using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Api.Services;

public interface IInventoryService
{
    Task<IReadOnlyList<UomDto>> UomsAsync(bool includeInactive, CancellationToken ct);
    Task<UomDto> SaveUomAsync(Guid? id, UpsertUomRequest req, CancellationToken ct);
    Task DeleteUomAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<StoreDto>> StoresAsync(bool includeInactive, CancellationToken ct);
    Task<StoreDto> SaveStoreAsync(Guid? id, UpsertStoreRequest req, CancellationToken ct);
    Task DeleteStoreAsync(Guid id, CancellationToken ct);

    Task<PagedResult<StockEntrySummaryDto>> EntriesAsync(string? status, string? type, int page, int pageSize, CancellationToken ct);
    Task<StockEntryDto> EntryAsync(Guid id, CancellationToken ct);
    Task<StockEntryDto> CreateEntryAsync(UpsertStockEntryRequest req, CancellationToken ct);
    Task<StockEntryDto> UpdateEntryAsync(Guid id, UpsertStockEntryRequest req, CancellationToken ct);
    Task DeleteEntryAsync(Guid id, CancellationToken ct);
    Task<StockEntryDto> SubmitEntryAsync(Guid id, CancellationToken ct);
    Task<StockEntryDto> CancelEntryAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<ItemLookupDto>> ItemsAsync(string? search, CancellationToken ct);
    Task<StockPositionResult> PositionAsync(Guid? storeId, string? search, bool lowOnly, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<StockLedgerRow>> LedgerAsync(Guid? productId, Guid? storeId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken ct);
}

/// <summary>Inventory masters, stock entries (Draft → Submitted → Cancelled) and stock reports.</summary>
public sealed class InventoryService(ErpDbContext db, IStockPosting posting, ITenantContext tenant, TimeProvider clock) : IInventoryService
{
    public const int LowStockLevel = 10;
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    // ---------------------------------------------------------------- UoM
    public async Task<IReadOnlyList<UomDto>> UomsAsync(bool includeInactive, CancellationToken ct) =>
        await db.Uoms.AsNoTracking().Where(u => includeInactive || u.IsActive).OrderBy(u => u.Code)
            .Select(u => new UomDto(u.Id, u.Code, u.Name, u.IsActive)).ToListAsync(ct);

    public async Task<UomDto> SaveUomAsync(Guid? id, UpsertUomRequest req, CancellationToken ct)
    {
        var code = req.Code.Trim().ToUpperInvariant();
        if (await db.Uoms.AnyAsync(u => u.Code == code && u.Id != id, ct))
            throw new ConflictException($"Unit {code} already exists.");
        var u = id is null ? db.Uoms.Add(new Uom { Id = Guid.NewGuid() }).Entity
            : await db.Uoms.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Unit not found.");
        (u.Code, u.Name, u.IsActive) = (code, req.Name.Trim(), req.IsActive);
        await db.SaveChangesAsync(ct);
        return new UomDto(u.Id, u.Code, u.Name, u.IsActive);
    }

    public async Task DeleteUomAsync(Guid id, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(p => p.UomId == id, ct))
            throw new ConflictException("This unit is used by products. Deactivate it instead.");
        if (await db.Uoms.Where(u => u.Id == id).ExecuteDeleteAsync(ct) == 0) throw new NotFoundException("Unit not found.");
    }

    // ---------------------------------------------------------------- Stores
    public async Task<IReadOnlyList<StoreDto>> StoresAsync(bool includeInactive, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        return await db.Stores.AsNoTracking().Where(s => includeInactive || s.IsActive)
            .OrderByDescending(s => s.IsDefault).ThenBy(s => s.Code)
            .Select(s => new StoreDto(s.Id, s.Code, s.Name, s.Location, s.IsDefault, s.IsActive)).ToListAsync(ct);
    }

    public async Task<StoreDto> SaveStoreAsync(Guid? id, UpsertStoreRequest req, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        var code = req.Code.Trim().ToUpperInvariant();
        if (await db.Stores.AnyAsync(s => s.Code == code && s.Id != id, ct))
            throw new ConflictException($"Store {code} already exists.");
        var s = id is null ? db.Stores.Add(new Store { Id = Guid.NewGuid() }).Entity
            : await db.Stores.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Store not found.");
        if (s.IsDefault && !req.IsActive) throw new DomainException("The default store can't be deactivated.");
        if (s.IsDefault && !req.IsDefault) throw new DomainException("Make another store the default instead.");
        if (req.IsDefault && !req.IsActive) throw new DomainException("An inactive store can't be the default.");
        (s.Code, s.Name, s.Location, s.IsActive) = (code, req.Name.Trim(), req.Location?.Trim(), req.IsActive);

        if (req.IsDefault && !s.IsDefault)
        {
            // Move the default flag (partial unique index: clear the old one first, in one transaction).
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Stores.Where(x => x.IsDefault).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsDefault, false), ct);
                s.IsDefault = true;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
        }
        else await db.SaveChangesAsync(ct);
        return new StoreDto(s.Id, s.Code, s.Name, s.Location, s.IsDefault, s.IsActive);
    }

    public async Task DeleteStoreAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Stores.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Store not found.");
        if (s.IsDefault) throw new DomainException("The default store can't be deleted.");
        if (await db.StockLedger.AnyAsync(l => l.StoreId == id, ct)
            || await db.StockEntries.AnyAsync(e => e.SourceStoreId == id || e.TargetStoreId == id, ct))
            throw new ConflictException("This store has stock history. Deactivate it instead.");
        await db.StockBalances.Where(b => b.StoreId == id).ExecuteDeleteAsync(ct);
        db.Stores.Remove(s);
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- Stock entries
    public async Task<PagedResult<StockEntrySummaryDto>> EntriesAsync(string? status, string? type, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.StockEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(e => e.Status == status);
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(e => e.Type == type);
        var total = await q.LongCountAsync(ct);
        var items = await q.OrderByDescending(e => e.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new StockEntrySummaryDto(e.Id, e.Number, e.Type, e.Status, e.PostingDate,
                e.SourceStore!.Name, e.TargetStore!.Name, e.Lines.Count, e.TotalValue, e.CreatedAt))
            .ToListAsync(ct);
        return new PagedResult<StockEntrySummaryDto>(items, page, pageSize, total);
    }

    public async Task<StockEntryDto> EntryAsync(Guid id, CancellationToken ct)
    {
        var e = await db.StockEntries.AsNoTracking()
            .Include(x => x.SourceStore).Include(x => x.TargetStore)
            .Include(x => x.Lines.OrderBy(l => l.LineNo)).ThenInclude(l => l.Product!).ThenInclude(p => p.Uom)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Stock entry not found.");
        return ToDto(e);
    }

    public async Task<StockEntryDto> CreateEntryAsync(UpsertStockEntryRequest req, CancellationToken ct)
    {
        await ValidateAsync(req, ct);
        var strategy = db.Database.CreateExecutionStrategy();
        var id = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var e = new StockEntry
            {
                Id = Guid.NewGuid(), Number = await posting.NextNumberAsync("SE", ct),
                CreatedByUserId = tenant.UserId, Status = DocStatus.Draft,
            };
            Apply(e, req);
            db.StockEntries.Add(e);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return e.Id;
        });
        return await EntryAsync(id, ct);
    }

    public async Task<StockEntryDto> UpdateEntryAsync(Guid id, UpsertStockEntryRequest req, CancellationToken ct)
    {
        await ValidateAsync(req, ct);
        var e = await db.StockEntries.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Stock entry not found.");
        if (e.Status != DocStatus.Draft) throw new DomainException($"A {e.Status.ToLowerInvariant()} entry can't be edited.");
        db.StockEntryLines.RemoveRange(e.Lines);
        e.Lines = [];
        Apply(e, req);
        await db.SaveChangesAsync(ct);
        return await EntryAsync(id, ct);
    }

    public async Task DeleteEntryAsync(Guid id, CancellationToken ct)
    {
        var e = await db.StockEntries.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Stock entry not found.");
        if (e.Status != DocStatus.Draft) throw new DomainException("Only drafts can be deleted; cancel a submitted entry instead.");
        db.StockEntries.Remove(e);
        await db.SaveChangesAsync(ct);
    }

    public async Task<StockEntryDto> SubmitEntryAsync(Guid id, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var e = await LockAsync(id, ct);
            if (e.Status != DocStatus.Draft) throw new DomainException($"This entry is already {e.Status.ToLowerInvariant()}.");
            await EnsureStoresActiveAsync(e.SourceStoreId, e.TargetStoreId, ct);

            var lines = e.Lines.OrderBy(l => l.LineNo).ToList();
            var movements = lines.SelectMany(l => e.Type switch
            {
                StockEntryType.Receipt => [new StockMovement(l.ProductId, e.TargetStoreId!.Value, l.Quantity, l.Rate)],
                StockEntryType.Issue => [new StockMovement(l.ProductId, e.SourceStoreId!.Value, -l.Quantity)],
                _ => new[]
                {
                    new StockMovement(l.ProductId, e.SourceStoreId!.Value, -l.Quantity),
                    new StockMovement(l.ProductId, e.TargetStoreId!.Value, l.Quantity, RateFromPrevious: true),
                },
            }).ToList();
            var posted = await posting.PostAsync(movements, new Voucher(VoucherType.StockEntry, e.Id, e.Number, e.PostingDate), ct);

            // Lines record the rate actually used (Issue/Transfer: the valuation rate at submit time).
            var step = e.Type == StockEntryType.Transfer ? 2 : 1;
            for (var i = 0; i < lines.Count; i++)
            {
                var p = posted[i * step];
                lines[i].Rate = p.Rate;
                lines[i].Amount = Math.Abs(p.ValueChange);
            }
            e.TotalValue = lines.Sum(l => l.Amount);
            e.Status = DocStatus.Submitted;
            e.SubmittedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return await EntryAsync(id, ct);
    }

    public async Task<StockEntryDto> CancelEntryAsync(Guid id, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var e = await LockAsync(id, ct);
            if (e.Status != DocStatus.Submitted) throw new DomainException("Only submitted entries can be cancelled.");

            // Reverse every ledger row of this entry, newest first, at the rate it was posted at, so exactly
            // the quantity and value that moved goes back. Fails if the stock has since been used.
            var rows = await db.StockLedger.AsNoTracking()
                .Where(l => l.VoucherId == e.Id && !l.IsReversal).OrderByDescending(l => l.Id).ToListAsync(ct);
            var reversal = rows.Select(r => new StockMovement(r.ProductId, r.StoreId, -r.QtyChange, r.Rate)).ToList();
            await posting.PostAsync(reversal, new Voucher(VoucherType.StockEntry, e.Id, e.Number, Today, IsReversal: true), ct);

            e.Status = DocStatus.Cancelled;
            e.CancelledAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        return await EntryAsync(id, ct);
    }

    /// <summary>Row-locks the entry so two people can't submit or cancel it at the same time.</summary>
    private async Task<StockEntry> LockAsync(Guid id, CancellationToken ct)
    {
        var org = tenant.OrganizationId;
        var found = await db.StockEntries.FromSqlInterpolated(
                $"SELECT * FROM stock_entries WHERE id = {id} AND organization_id = {org} FOR UPDATE")
            .IgnoreQueryFilters().AsTracking().ToListAsync(ct);
        var e = found.SingleOrDefault() ?? throw new NotFoundException("Stock entry not found.");
        await db.Entry(e).Collection(x => x.Lines).LoadAsync(ct);
        return e;
    }

    private void Apply(StockEntry e, UpsertStockEntryRequest req)
    {
        e.Type = req.Type;
        e.PostingDate = req.PostingDate ?? Today;
        e.SourceStoreId = req.Type == StockEntryType.Receipt ? null : req.SourceStoreId;
        e.TargetStoreId = req.Type == StockEntryType.Issue ? null : req.TargetStoreId;
        e.Remarks = string.IsNullOrWhiteSpace(req.Remarks) ? null : req.Remarks.Trim();
        e.Lines = req.Lines.Select((l, i) => new StockEntryLine
        {
            LineNo = i + 1, ProductId = l.ProductId, Quantity = l.Quantity,
            Rate = req.Type == StockEntryType.Receipt ? l.Rate!.Value : 0,
            Amount = req.Type == StockEntryType.Receipt ? Math.Round(l.Quantity * l.Rate!.Value, 2) : 0,
        }).ToList();
        e.TotalValue = e.Lines.Sum(l => l.Amount);
    }

    private async Task ValidateAsync(UpsertStockEntryRequest req, CancellationToken ct)
    {
        if (!StockEntryType.All.Contains(req.Type)) throw new DomainException($"Type must be one of {string.Join(", ", StockEntryType.All)}.");
        if (req.PostingDate is { } d && d > Today) throw new DomainException("Posting date can't be in the future.");
        if (req.Lines.Count == 0) throw new DomainException("Add at least one item.");
        if (req.Lines.GroupBy(l => l.ProductId).Any(g => g.Count() > 1)) throw new DomainException("Each item can appear only once.");

        var needsSource = req.Type != StockEntryType.Receipt;
        var needsTarget = req.Type != StockEntryType.Issue;
        if (needsSource && req.SourceStoreId is null) throw new DomainException("Choose the store the stock comes from.");
        if (needsTarget && req.TargetStoreId is null) throw new DomainException("Choose the store the stock goes to.");
        if (req.Type == StockEntryType.Transfer && req.SourceStoreId == req.TargetStoreId)
            throw new DomainException("Source and target store must be different.");
        if (req.Type == StockEntryType.Receipt && req.Lines.Any(l => l.Rate is null))
            throw new DomainException("Enter a rate for every item received.");

        await EnsureStoresActiveAsync(needsSource ? req.SourceStoreId : null, needsTarget ? req.TargetStoreId : null, ct);
        var ids = req.Lines.Select(l => l.ProductId).ToList();
        if (await db.Products.CountAsync(p => ids.Contains(p.Id), ct) != ids.Count)
            throw new NotFoundException("One or more items not found.");
    }

    private async Task EnsureStoresActiveAsync(Guid? a, Guid? b, CancellationToken ct)
    {
        foreach (var id in new[] { a, b }.OfType<Guid>())
        {
            var s = await db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Store not found.");
            if (!s.IsActive) throw new DomainException($"Store {s.Name} is inactive.");
        }
    }

    private static StockEntryDto ToDto(StockEntry e) => new(e.Id, e.Number, e.Type, e.Status, e.PostingDate,
        e.SourceStoreId, e.SourceStore?.Name, e.TargetStoreId, e.TargetStore?.Name, e.Remarks, e.TotalValue,
        e.CreatedAt, e.SubmittedAt, e.CancelledAt,
        e.Lines.OrderBy(l => l.LineNo).Select(l => new StockEntryLineDto(l.ProductId, l.Product!.Sku, l.Product.Name,
            l.Product.Uom?.Code, l.Quantity, l.Rate, l.Amount)).ToList());

    // ---------------------------------------------------------------- Reports
    public async Task<IReadOnlyList<ItemLookupDto>> ItemsAsync(string? search, CancellationToken ct)
    {
        // Inactive items can't go on new documents, so they are left out of the pickers.
        var q = db.Products.AsNoTracking().Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(p => EF.Functions.ILike(p.Name, $"%{search}%") || EF.Functions.ILike(p.Sku, $"%{search}%"));
        return await q.OrderBy(p => p.Name).Take(500)
            .Select(p => new ItemLookupDto(p.Id, p.Sku, p.Name, p.Uom!.Code, p.StockQuantity, p.UnitPrice, p.ItemType, p.TaxRate, p.HsnCode))
            .ToListAsync(ct);
    }

    public async Task<StockPositionResult> PositionAsync(Guid? storeId, string? search, bool lowOnly, int page, int pageSize, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var q = db.StockBalances.AsNoTracking().Where(b => b.Quantity > 0 || b.StockValue > 0 || lowOnly);
        if (storeId is not null) q = q.Where(b => b.StoreId == storeId);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(b => EF.Functions.ILike(b.Product!.Name, $"%{search}%") || EF.Functions.ILike(b.Product!.Sku, $"%{search}%"));
        if (lowOnly) q = q.Where(b => b.Quantity < LowStockLevel);

        var total = await q.LongCountAsync(ct);
        var totalQty = await q.SumAsync(b => (long?)b.Quantity, ct) ?? 0;
        var totalValue = await q.SumAsync(b => (decimal?)b.StockValue, ct) ?? 0;
        var items = await q.OrderBy(b => b.Product!.Name).ThenBy(b => b.Store!.Name)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new StockPositionRow(b.ProductId, b.Product!.Sku, b.Product.Name, b.Product.Category, b.Product.Uom!.Code,
                b.StoreId, b.Store!.Name, b.Quantity, b.ValuationRate, b.StockValue))
            .ToListAsync(ct);
        return new StockPositionResult(items, page, pageSize, total, totalQty, totalValue);
    }

    public async Task<PagedResult<StockLedgerRow>> LedgerAsync(Guid? productId, Guid? storeId, DateOnly? from, DateOnly? to,
        int page, int pageSize, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var q = db.StockLedger.AsNoTracking();
        if (productId is not null) q = q.Where(l => l.ProductId == productId);
        if (storeId is not null) q = q.Where(l => l.StoreId == storeId);
        if (from is not null) q = q.Where(l => l.PostingDate >= from);
        if (to is not null) q = q.Where(l => l.PostingDate <= to);
        var total = await q.LongCountAsync(ct);
        var items = await q.OrderByDescending(l => l.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new StockLedgerRow(l.Id, l.PostingDate, l.PostedAt, l.ProductId, l.Product!.Sku, l.Product.Name,
                l.StoreId, l.Store!.Name, l.VoucherType, l.VoucherId, l.VoucherNo, l.IsReversal,
                l.QtyChange, l.QtyAfter, l.Rate, l.ValuationRate, l.ValueChange, l.ValueAfter))
            .ToListAsync(ct);
        return new PagedResult<StockLedgerRow>(items, page, pageSize, total);
    }
}
