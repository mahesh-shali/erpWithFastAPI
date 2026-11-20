using System.Data.Common;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Api.Services;

/// <summary>One change to the stock of a product in a store.</summary>
/// <param name="Quantity">Positive = in, negative = out.</param>
/// <param name="Rate">
/// Incoming: the rate the stock comes in at (required). Outgoing: null = the store's moving-average rate,
/// or an explicit rate (used when reversing a receipt, so exactly the value that came in goes out).
/// </param>
/// <param name="RateFromPrevious">Incoming at the rate of the previous movement in the batch (transfers).</param>
public sealed record StockMovement(Guid ProductId, Guid StoreId, int Quantity, decimal? Rate = null, bool RateFromPrevious = false);

/// <summary>The result of applying one movement: what it was valued at and the balance afterwards.</summary>
public sealed record PostedMovement(StockMovement Movement, decimal Rate, decimal ValueChange, int QtyAfter, decimal ValuationRate, decimal ValueAfter);

public sealed record Voucher(string Type, Guid? Id, string No, DateOnly PostingDate, bool IsReversal = false);

/// <summary>Moving-average valuation (the pure maths, unit-tested on its own).</summary>
public static class Valuation
{
    /// <summary>
    /// Incoming: new rate = (value before + qty × rate) / (qty before + qty).
    /// Outgoing: value leaves at the current rate (or the given one); the rate itself does not change.
    /// A balance that reaches zero quantity has zero value, so rounding never leaves a residue.
    /// </summary>
    public static (decimal Rate, decimal ValueChange, int QtyAfter, decimal ValuationRate, decimal ValueAfter) Apply(
        int qty, decimal valuationRate, decimal value, int change, decimal? rate)
    {
        var qtyAfter = qty + change;
        if (qtyAfter < 0) throw new DomainException("Insufficient stock.");
        if (change >= 0)
        {
            var r = rate ?? throw new ArgumentException("An incoming movement needs a rate.", nameof(rate));
            var valueChange = Math.Round(change * r, 2);
            var valueAfter = value + valueChange;
            var newRate = qtyAfter == 0 ? valuationRate : Math.Round(valueAfter / qtyAfter, 4);
            return (r, valueChange, qtyAfter, newRate, valueAfter);
        }
        else
        {
            var r = rate ?? valuationRate;
            var valueChange = Math.Round(change * r, 2);
            var valueAfter = qtyAfter == 0 ? 0 : Math.Max(0, value + valueChange);
            if (qtyAfter == 0) valueChange = -value;
            var newRate = qtyAfter == 0 ? valuationRate : rate is null ? valuationRate : Math.Round(valueAfter / qtyAfter, 4);
            return (r, valueChange, qtyAfter, newRate, valueAfter);
        }
    }
}

public interface IStockPosting
{
    /// <summary>The organization's default store, created on first use together with opening balances.</summary>
    Task<Guid> EnsureDefaultStoreAsync(CancellationToken ct);
    /// <summary>
    /// Applies movements inside the caller's transaction: locks the balances (in a fixed order, so concurrent
    /// postings can't deadlock), values each movement, writes ledger rows and keeps products.stock_quantity in step.
    /// Throws DomainException if any store would go below zero.
    /// </summary>
    Task<IReadOnlyList<PostedMovement>> PostAsync(IReadOnlyList<StockMovement> movements, Voucher voucher, CancellationToken ct);
    /// <summary>Next number for a document prefix, e.g. ("SE") -> "SE-00001". Call inside the document's transaction.</summary>
    Task<string> NextNumberAsync(string prefix, CancellationToken ct);
}

public sealed class StockPosting(ErpDbContext db, ITenantContext tenant, TimeProvider clock) : IStockPosting
{
    private Guid Org => tenant.OrganizationId;

    public async Task<Guid> EnsureDefaultStoreAsync(CancellationToken ct)
    {
        var existing = await db.Stores.AsNoTracking().Where(s => s.IsDefault).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var store = new Store { Id = Guid.NewGuid(), Code = "MAIN", Name = "Main Store", IsDefault = true };
                // A store with code MAIN may already exist (made by hand): keep codes unique.
                if (await db.Stores.AnyAsync(s => s.Code == "MAIN", ct)) store.Code = "MAIN-" + store.Id.ToString("N")[..4].ToUpperInvariant();
                db.Stores.Add(store);
                await db.SaveChangesAsync(ct);

                // Opening balances: stock that existed before inventory was switched on (seed data, older databases).
                // There is no purchase cost yet, so it is valued at the product's unit price.
                var opening = await db.Products.AsNoTracking().Where(p => p.StockQuantity > 0)
                    .Select(p => new { p.Id, p.StockQuantity, p.UnitPrice }).ToListAsync(ct);
                if (opening.Count > 0)
                {
                    var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                    await PostAsync(opening.Select(p => new StockMovement(p.Id, store.Id, p.StockQuantity, p.UnitPrice)).ToList(),
                        new Voucher(VoucherType.Opening, null, "OPENING", today), ct, updateProducts: false);
                }
                await tx.CommitAsync(ct);
                return store.Id;
            });
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another request created it at the same moment.
            db.ChangeTracker.Clear();
            return await db.Stores.AsNoTracking().Where(s => s.IsDefault).Select(s => s.Id).SingleAsync(ct);
        }
    }

    public Task<IReadOnlyList<PostedMovement>> PostAsync(IReadOnlyList<StockMovement> movements, Voucher voucher, CancellationToken ct) =>
        PostAsync(movements, voucher, ct, updateProducts: true);

    private async Task<IReadOnlyList<PostedMovement>> PostAsync(IReadOnlyList<StockMovement> movements, Voucher voucher,
        CancellationToken ct, bool updateProducts)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Stock must be posted inside a transaction.");
        if (movements.Count == 0) return [];

        // 1) Lock every balance row this batch touches, always in (product, store) order.
        var balances = new Dictionary<(Guid, Guid), StockBalance>();
        foreach (var key in movements.Select(m => (m.ProductId, m.StoreId)).Distinct().OrderBy(k => k.ProductId).ThenBy(k => k.StoreId))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO stock_balances (organization_id, product_id, store_id, quantity, valuation_rate, stock_value, updated_at)
                VALUES ({Org}, {key.ProductId}, {key.StoreId}, 0, 0, 0, now()) ON CONFLICT DO NOTHING
                """, ct);
            var row = (await db.StockBalances.FromSqlInterpolated($"""
                SELECT * FROM stock_balances
                WHERE organization_id = {Org} AND product_id = {key.ProductId} AND store_id = {key.StoreId}
                FOR UPDATE
                """).IgnoreQueryFilters().AsTracking().ToListAsync(ct)).Single();
            balances[key] = row;
        }

        // 2) Apply in the given order (a transfer's "in" uses the rate its "out" just left at).
        var now = clock.GetUtcNow().UtcDateTime;
        var posted = new List<PostedMovement>();
        foreach (var m in movements)
        {
            var b = balances[(m.ProductId, m.StoreId)];
            var rate = m.RateFromPrevious ? posted[^1].Rate : m.Rate;
            (decimal Rate, decimal ValueChange, int QtyAfter, decimal ValuationRate, decimal ValueAfter) v;
            try { v = Valuation.Apply(b.Quantity, b.ValuationRate, b.StockValue, m.Quantity, rate); }
            catch (DomainException)
            {
                var info = await db.Products.AsNoTracking().Where(p => p.Id == m.ProductId).Select(p => p.Sku).FirstOrDefaultAsync(ct);
                var store = await db.Stores.AsNoTracking().Where(s => s.Id == m.StoreId).Select(s => s.Name).FirstOrDefaultAsync(ct);
                throw new DomainException($"Insufficient stock for {info} in {store}: {b.Quantity} available, {-m.Quantity} needed.");
            }
            (b.Quantity, b.ValuationRate, b.StockValue, b.UpdatedAt) = (v.QtyAfter, v.ValuationRate, v.ValueAfter, now);
            db.StockLedger.Add(new StockLedgerEntry
            {
                ProductId = m.ProductId, StoreId = m.StoreId, PostingDate = voucher.PostingDate, PostedAt = now,
                QtyChange = m.Quantity, QtyAfter = v.QtyAfter, Rate = v.Rate, ValuationRate = v.ValuationRate,
                ValueChange = v.ValueChange, ValueAfter = v.ValueAfter,
                VoucherType = voucher.Type, VoucherId = voucher.Id, VoucherNo = voucher.No, IsReversal = voucher.IsReversal,
            });
            posted.Add(new PostedMovement(m, v.Rate, v.ValueChange, v.QtyAfter, v.ValuationRate, v.ValueAfter));
        }
        await db.SaveChangesAsync(ct);

        // 3) Keep products.stock_quantity = sum of its balances (transfers net to zero).
        if (updateProducts)
            foreach (var g in movements.GroupBy(m => m.ProductId).OrderBy(g => g.Key))
            {
                var change = g.Sum(m => m.Quantity);
                if (change == 0) continue;
                await db.Products.Where(p => p.Id == g.Key)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, p => p.StockQuantity + change), ct);
            }
        return posted;
    }

    public async Task<string> NextNumberAsync(string prefix, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        await using DbCommand cmd = conn.CreateCommand();
        cmd.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = """
            INSERT INTO document_series (organization_id, prefix, next_number) VALUES (@org, @prefix, 2)
            ON CONFLICT (organization_id, prefix) DO UPDATE SET next_number = document_series.next_number + 1
            RETURNING next_number - 1
            """;
        cmd.Parameters.Add(new NpgsqlParameter("org", Org));
        cmd.Parameters.Add(new NpgsqlParameter("prefix", prefix));
        var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return $"{prefix}-{n:00000}";
    }
}
