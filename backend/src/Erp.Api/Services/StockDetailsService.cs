using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

public interface IStockDetailsService
{
    Task<StockDetailsResult> ListAsync(string? search, string? itemType, bool attentionOnly, int page, int pageSize, CancellationToken ct);
    Task<StockItemDetailsDto> ItemAsync(Guid productId, CancellationToken ct);
}

/// <summary>
/// Inventory › Stock Position › Stock Details. Per item: on hand (all stores), committed (confirmed customer P.O.s not
/// yet invoiced), in production (draft work orders), available = on hand − committed, against the reorder level.
/// </summary>
public sealed class StockDetailsService(ErpDbContext db, IStockPosting posting) : IStockDetailsService
{
    /// <summary>Member-init (not a constructor) so EF can still filter and sort on the projected figures.</summary>
    private sealed class Raw
    {
        public Guid ProductId { get; init; }
        public string Sku { get; init; } = "";
        public string Name { get; init; } = "";
        public string ItemType { get; init; } = "";
        public string? Category { get; init; }
        public string? Uom { get; init; }
        public int OnHand { get; init; }
        public decimal StockValue { get; init; }
        public int Committed { get; init; }
        public int InProduction { get; init; }
        public int ReorderLevel { get; init; }
    }

    /// <summary>All the per-item figures as one SQL query (correlated sub-selects, tenant filters apply to each).</summary>
    private IQueryable<Raw> Figures(IQueryable<Product> products) => products.Select(p => new Raw
    {
        ProductId = p.Id, Sku = p.Sku, Name = p.Name, ItemType = p.ItemType, Category = p.Category, Uom = p.Uom!.Code,
        OnHand = p.StockQuantity,
        StockValue = db.StockBalances.Where(b => b.ProductId == p.Id).Sum(b => (decimal?)b.StockValue) ?? 0,
        Committed = db.CustomerPoLines.Where(l => l.ProductId == p.Id
                && db.CustomerPos.Any(po => po.Id == l.CustomerPoId && po.Status == CustomerPoStatus.Confirmed))
            .Sum(l => (int?)(l.Quantity - l.InvoicedQty)) ?? 0,
        InProduction = db.WorkOrders.Where(w => w.ProductId == p.Id && w.Status == DocStatus.Draft).Sum(w => (int?)w.Quantity) ?? 0,
        ReorderLevel = p.ReorderLevel,
    });

    public static string StatusOf(int available, int reorderLevel) =>
        available < 0 ? "Short" : available == 0 || available < reorderLevel ? "Reorder" : "OK";

    private static StockDetailsRow Row(Raw r)
    {
        var available = r.OnHand - r.Committed;
        return new StockDetailsRow(r.ProductId, r.Sku, r.Name, r.ItemType, r.Category, r.Uom, r.OnHand, r.StockValue,
            r.Committed, r.InProduction, available, r.ReorderLevel, StatusOf(available, r.ReorderLevel));
    }

    public async Task<StockDetailsResult> ListAsync(string? search, string? itemType, bool attentionOnly, int page, int pageSize, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var products = db.Products.AsNoTracking().Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
            products = products.Where(p => EF.Functions.ILike(p.Name, $"%{search}%") || EF.Functions.ILike(p.Sku, $"%{search}%"));
        if (!string.IsNullOrWhiteSpace(itemType)) products = products.Where(p => p.ItemType == itemType);

        var q = Figures(products);
        if (attentionOnly) q = q.Where(r => r.OnHand - r.Committed < r.ReorderLevel || r.OnHand - r.Committed <= 0);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderBy(r => r.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new StockDetailsResult(rows.Select(Row).ToList(), page, pageSize, total);
    }

    public async Task<StockItemDetailsDto> ItemAsync(Guid productId, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        var p = await db.Products.AsNoTracking().Include(x => x.Uom).FirstOrDefaultAsync(x => x.Id == productId, ct)
                ?? throw new NotFoundException("Item not found.");
        var raw = await Figures(db.Products.AsNoTracking().Where(x => x.Id == productId)).SingleAsync(ct);

        var stores = await db.StockBalances.AsNoTracking().Where(b => b.ProductId == productId && (b.Quantity > 0 || b.StockValue > 0))
            .OrderBy(b => b.Store!.Name)
            .Select(b => new StoreBalanceDto(b.StoreId, b.Store!.Name, b.Quantity, b.ValuationRate, b.StockValue)).ToListAsync(ct);

        var commitments = await db.CustomerPoLines.AsNoTracking()
            .Where(l => l.ProductId == productId)
            .Join(db.CustomerPos.Where(po => po.Status == CustomerPoStatus.Confirmed), l => l.CustomerPoId, po => po.Id, (l, po) => new { l, po })
            .Where(x => x.l.Quantity > x.l.InvoicedQty)
            .OrderBy(x => x.po.DeliveryDate ?? x.po.PoDate).ThenBy(x => x.po.Number)
            .Select(x => new ItemCommitmentDto(x.po.Id, x.po.Number, x.po.Customer!.Name, x.po.DeliveryDate,
                x.l.Quantity, x.l.ProducedQty, x.l.InvoicedQty, x.l.Quantity - x.l.InvoicedQty))
            .ToListAsync(ct);

        var openWorkOrders = await db.WorkOrders.AsNoTracking().Where(w => w.ProductId == productId && w.Status == DocStatus.Draft)
            .OrderBy(w => w.PostingDate)
            .Select(w => new DocumentLinkDto(w.Id, w.Number, w.Status, w.PostingDate, w.Quantity, w.CustomerPo!.Number))
            .ToListAsync(ct);

        var movements = await db.StockLedger.AsNoTracking().Where(l => l.ProductId == productId).OrderByDescending(l => l.Id).Take(20)
            .Select(l => new StockLedgerRow(l.Id, l.PostingDate, l.PostedAt, l.ProductId, l.Product!.Sku, l.Product.Name,
                l.StoreId, l.Store!.Name, l.VoucherType, l.VoucherId, l.VoucherNo, l.IsReversal,
                l.QtyChange, l.QtyAfter, l.Rate, l.ValuationRate, l.ValueChange, l.ValueAfter))
            .ToListAsync(ct);

        return new StockItemDetailsDto(ProductService.ToDto(p), Row(raw), stores, commitments, openWorkOrders, movements);
    }
}
