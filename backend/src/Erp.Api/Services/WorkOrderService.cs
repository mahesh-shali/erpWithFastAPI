using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

public interface IWorkOrderService
{
    Task<PagedResult<WorkOrderSummaryDto>> ListAsync(string? status, Guid? customerPoId, string? search, int page, int pageSize, CancellationToken ct);
    Task<WorkOrderDto> GetAsync(Guid id, CancellationToken ct);
    Task<WorkOrderDto> CreateAsync(UpsertWorkOrderRequest req, CancellationToken ct);
    Task<WorkOrderDto> UpdateAsync(Guid id, UpsertWorkOrderRequest req, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<WorkOrderDto> SubmitAsync(Guid id, CancellationToken ct);
    Task<WorkOrderDto> CancelAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Production: a work order makes <c>Quantity</c> of one item, usually for a Customer P.O. line.
/// Submit posts, in one locked batch: each raw material out of the material store (moving average), then the finished
/// item into the output store at (materials consumed + other cost) / quantity. Cancel reverses those ledger rows.
/// </summary>
public sealed class WorkOrderService(ErpDbContext db, IStockPosting posting, ITenantContext tenant, TimeProvider clock) : IWorkOrderService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<PagedResult<WorkOrderSummaryDto>> ListAsync(string? status, Guid? customerPoId, string? search,
        int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.WorkOrders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(w => w.Status == status);
        if (customerPoId is not null) q = q.Where(w => w.CustomerPoId == customerPoId);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(w => EF.Functions.ILike(w.Number, $"%{search}%") || EF.Functions.ILike(w.Product!.Name, $"%{search}%")
                             || EF.Functions.ILike(w.Product!.Sku, $"%{search}%") || EF.Functions.ILike(w.CustomerPo!.Number, $"%{search}%"));
        var total = await q.LongCountAsync(ct);
        var items = await q.OrderByDescending(w => w.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(w => new WorkOrderSummaryDto(w.Id, w.Number, w.Status, w.PostingDate, w.CustomerPo!.Number, w.CustomerPo.Customer!.Name,
                w.Product!.Sku, w.Product.Name, w.Quantity, w.Store!.Name, w.TotalCost, w.CreatedAt))
            .ToListAsync(ct);
        return new PagedResult<WorkOrderSummaryDto>(items, page, pageSize, total);
    }

    public async Task<WorkOrderDto> GetAsync(Guid id, CancellationToken ct)
    {
        var w = await db.WorkOrders.AsNoTracking()
            .Include(x => x.Product!).ThenInclude(p => p.Uom)
            .Include(x => x.CustomerPo!).ThenInclude(p => p.Customer)
            .Include(x => x.Store).Include(x => x.MaterialStore)
            .Include(x => x.Materials.OrderBy(m => m.LineNo)).ThenInclude(m => m.Product!).ThenInclude(p => p.Uom)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work order not found.");
        return ToDto(w);
    }

    public async Task<WorkOrderDto> CreateAsync(UpsertWorkOrderRequest req, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        var (productId, poId) = await ValidateAsync(req, ct);
        var id = await db.InTransactionAsync(async () =>
        {
            var w = new WorkOrder
            {
                Id = Guid.NewGuid(), Number = await posting.NextNumberAsync("WO", ct),
                CreatedByUserId = tenant.UserId, Status = DocStatus.Draft,
            };
            Apply(w, req, productId, poId);
            db.WorkOrders.Add(w);
            await db.SaveChangesAsync(ct);
            return w.Id;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<WorkOrderDto> UpdateAsync(Guid id, UpsertWorkOrderRequest req, CancellationToken ct)
    {
        var (productId, poId) = await ValidateAsync(req, ct);
        var w = await db.WorkOrders.Include(x => x.Materials).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException("Work order not found.");
        if (w.Status != DocStatus.Draft) throw new DomainException($"A {w.Status.ToLowerInvariant()} work order can't be edited.");
        db.WorkOrderMaterials.RemoveRange(w.Materials);
        w.Materials = [];
        Apply(w, req, productId, poId);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var w = await db.WorkOrders.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Work order not found.");
        if (w.Status != DocStatus.Draft) throw new DomainException("Only drafts can be deleted; cancel a submitted work order instead.");
        db.WorkOrders.Remove(w);
        await db.SaveChangesAsync(ct);
    }

    public async Task<WorkOrderDto> SubmitAsync(Guid id, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        await db.InTransactionAsync(async () =>
        {
            var w = await db.LockAsync<WorkOrder>("work_orders", id, "Work order not found.", ct);
            if (w.Status != DocStatus.Draft) throw new DomainException($"This work order is already {w.Status.ToLowerInvariant()}.");
            await db.Entry(w).Collection(x => x.Materials).LoadAsync(ct);
            await EnsureStoresActiveAsync(ct, w.StoreId, w.MaterialStoreId);

            var line = await LockPoLineAsync(w, ct);
            if (line is not null)
            {
                var left = line.Quantity - line.ProducedQty;
                if (w.Quantity > left)
                    throw new DomainException($"Only {Math.Max(0, left)} more can be produced for this P.O. line ({line.ProducedQty} of {line.Quantity} done).");
                line.ProducedQty += w.Quantity;
            }

            var materials = w.Materials.OrderBy(m => m.LineNo).ToList();
            var movements = materials.Select(m => new StockMovement(m.ProductId, w.MaterialStoreId, -m.Quantity))
                .Append(new StockMovement(w.ProductId, w.StoreId, w.Quantity, RateFromConsumed: true, AddedCost: w.OtherCost))
                .ToList();
            var posted = await posting.PostAsync(movements, new Voucher(VoucherType.WorkOrder, w.Id, w.Number, w.PostingDate), ct);

            for (var i = 0; i < materials.Count; i++)
            {
                materials[i].Rate = posted[i].Rate;
                materials[i].Amount = Math.Abs(posted[i].ValueChange);
            }
            var output = posted[^1];
            w.MaterialCost = materials.Sum(m => m.Amount);
            w.UnitCost = output.Rate;
            w.TotalCost = output.ValueChange;
            w.Status = DocStatus.Submitted;
            w.SubmittedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<WorkOrderDto> CancelAsync(Guid id, CancellationToken ct)
    {
        await db.InTransactionAsync(async () =>
        {
            var w = await db.LockAsync<WorkOrder>("work_orders", id, "Work order not found.", ct);
            if (w.Status != DocStatus.Submitted) throw new DomainException("Only submitted work orders can be cancelled.");
            var line = await LockPoLineAsync(w, ct);
            if (line is not null) line.ProducedQty = Math.Max(0, line.ProducedQty - w.Quantity);

            // Finished goods go back out and materials come back in, at the rates they moved at.
            // Fails (insufficient stock) if the produced items have already been invoiced or issued.
            var rows = await db.StockLedger.AsNoTracking()
                .Where(l => l.VoucherId == w.Id && l.VoucherType == VoucherType.WorkOrder && !l.IsReversal)
                .OrderByDescending(l => l.Id).ToListAsync(ct);
            await posting.PostAsync(rows.Select(r => new StockMovement(r.ProductId, r.StoreId, -r.QtyChange, r.Rate)).ToList(),
                new Voucher(VoucherType.WorkOrder, w.Id, w.Number, Today, IsReversal: true), ct);

            w.Status = DocStatus.Cancelled;
            w.CancelledAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Locks the work order's P.O. (serializes everything that changes its quantities) and returns the line.</summary>
    private async Task<CustomerPoLine?> LockPoLineAsync(WorkOrder w, CancellationToken ct)
    {
        if (w.CustomerPoId is not { } poId || w.CustomerPoLineId is not { } lineId) return null;
        var po = await db.LockAsync<CustomerPo>("customer_pos", poId, "Customer P.O. not found.", ct);
        if (w.Status == DocStatus.Draft && po.Status != CustomerPoStatus.Confirmed)
            throw new DomainException($"P.O. {po.Number} is {po.Status.ToLowerInvariant()}; work orders need a confirmed P.O.");
        return await db.CustomerPoLines.SingleAsync(l => l.Id == lineId, ct);
    }

    private void Apply(WorkOrder w, UpsertWorkOrderRequest req, Guid productId, Guid? poId)
    {
        w.CustomerPoId = poId;
        w.CustomerPoLineId = req.CustomerPoLineId;
        w.ProductId = productId;
        w.Quantity = req.Quantity;
        w.StoreId = req.StoreId;
        w.MaterialStoreId = req.MaterialStoreId ?? req.StoreId;
        w.PostingDate = req.PostingDate ?? Today;
        w.OtherCost = Money.R2(req.OtherCost);
        w.Remarks = string.IsNullOrWhiteSpace(req.Remarks) ? null : req.Remarks.Trim();
        w.Materials = (req.Materials ?? []).Select((m, i) => new WorkOrderMaterial
        {
            LineNo = i + 1, ProductId = m.ProductId, Quantity = m.Quantity,
        }).ToList();
        w.MaterialCost = 0;
        w.UnitCost = 0;
        w.TotalCost = 0;
    }

    /// <summary>Returns the item to produce and the P.O. it belongs to (if any).</summary>
    private async Task<(Guid ProductId, Guid? PoId)> ValidateAsync(UpsertWorkOrderRequest req, CancellationToken ct)
    {
        if (req.PostingDate is { } d && d > Today) throw new DomainException("Production date can't be in the future.");
        Guid productId;
        Guid? poId = null;
        if (req.CustomerPoLineId is { } lineId)
        {
            var line = await db.CustomerPoLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct)
                       ?? throw new NotFoundException("P.O. line not found.");
            var po = await db.CustomerPos.AsNoTracking().SingleAsync(p => p.Id == line.CustomerPoId, ct);
            if (po.Status != CustomerPoStatus.Confirmed)
                throw new DomainException($"P.O. {po.Number} is {po.Status.ToLowerInvariant()}; work orders need a confirmed P.O.");
            if (req.ProductId is { } p && p != line.ProductId) throw new DomainException("The item must be the P.O. line's item.");
            productId = line.ProductId;
            poId = po.Id;
        }
        else productId = req.ProductId ?? throw new DomainException("Choose the item to produce, or a P.O. line.");

        var materials = req.Materials ?? [];
        if (materials.GroupBy(m => m.ProductId).Any(g => g.Count() > 1)) throw new DomainException("Each material can appear only once.");
        if (materials.Any(m => m.ProductId == productId)) throw new DomainException("An item can't be a material of itself.");

        var ids = materials.Select(m => m.ProductId).Append(productId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        if (products.Count != ids.Count) throw new NotFoundException("One or more items not found.");
        if (products.FirstOrDefault(p => !p.IsActive) is { } inactive) throw new DomainException($"Item {inactive.Sku} is inactive.");
        await EnsureStoresActiveAsync(ct, req.StoreId, req.MaterialStoreId ?? req.StoreId);
        return (productId, poId);
    }

    private async Task EnsureStoresActiveAsync(CancellationToken ct, params Guid[] ids)
    {
        foreach (var id in ids.Distinct())
        {
            var s = await db.Stores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Store not found.");
            if (!s.IsActive) throw new DomainException($"Store {s.Name} is inactive.");
        }
    }

    private static WorkOrderDto ToDto(WorkOrder w) => new(w.Id, w.Number, w.Status, w.PostingDate,
        w.CustomerPoId, w.CustomerPo?.Number, w.CustomerPo?.Customer?.Name, w.CustomerPoLineId,
        w.ProductId, w.Product!.Sku, w.Product.Name, w.Product.Uom?.Code, w.Quantity,
        w.StoreId, w.Store!.Name, w.MaterialStoreId, w.MaterialStore!.Name,
        w.OtherCost, w.MaterialCost, w.UnitCost, w.TotalCost, w.Remarks, w.CreatedAt, w.SubmittedAt, w.CancelledAt,
        w.Materials.OrderBy(m => m.LineNo).Select(m => new WorkOrderMaterialDto(m.ProductId, m.Product!.Sku, m.Product.Name,
            m.Product.Uom?.Code, m.Quantity, m.Rate, m.Amount, m.Product.StockQuantity)).ToList());
}
