using System.Text.Json;
using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Erp.Api.Services;

public interface IProductService
{
    Task<PagedResult<ProductDto>> ListAsync(int page, int pageSize, string? category, CancellationToken ct);
    Task<ProductDto> CreateAsync(UpsertProductRequest req, CancellationToken ct);
    Task<ProductDto> UpdateAsync(Guid id, UpsertProductRequest req, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Cache-aside on Redis. Keys are tenant-scoped and versioned: a write bumps the
/// tenant's version so all list pages invalidate in O(1) without SCAN/KEYS.
/// </summary>
public sealed class ProductService(ErpDbContext db, IDistributedCache cache, ITenantContext tenant, IStockPosting posting, TimeProvider clock)
    : IProductService
{
    private static readonly DistributedCacheEntryOptions Ttl = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };
    private string VersionKey => $"products:v:{tenant.OrganizationId}";

    public async Task<PagedResult<ProductDto>> ListAsync(int page, int pageSize, string? category, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var version = await cache.GetStringAsync(VersionKey, ct) ?? "0";
        var key = $"products:{tenant.OrganizationId}:{version}:{category}:{page}:{pageSize}";

        var cached = await cache.GetStringAsync(key, ct);
        if (cached is not null) return JsonSerializer.Deserialize<PagedResult<ProductDto>>(cached)!;

        var q = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(p => p.Category == category);
        var total = await q.LongCountAsync(ct);
        var items = await q.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.Category, p.UnitPrice, p.StockQuantity, p.UomId, p.Uom!.Code))
            .ToListAsync(ct);

        var result = new PagedResult<ProductDto>(items, page, pageSize, total);
        await cache.SetStringAsync(key, JsonSerializer.Serialize(result), Ttl, ct);
        return result;
    }

    /// <summary>Opening stock goes into the default store through the stock ledger, valued at the unit price.</summary>
    public async Task<ProductDto> CreateAsync(UpsertProductRequest req, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(p => p.Sku == req.Sku, ct))
            throw new ConflictException($"SKU {req.Sku} already exists.");
        await CheckUomAsync(req.UomId, ct);
        var store = await posting.EnsureDefaultStoreAsync(ct);
        var id = Guid.NewGuid();
        await InTransactionAsync(async () =>
        {
            db.Products.Add(new Product
            {
                Id = id, Sku = req.Sku, Name = req.Name, Category = req.Category,
                UnitPrice = req.UnitPrice, StockQuantity = 0, UomId = req.UomId,
            });
            await db.SaveChangesAsync(ct);
            if (req.StockQuantity > 0)
                await posting.PostAsync([new StockMovement(id, store, req.StockQuantity, req.UnitPrice)],
                    new Voucher(VoucherType.Opening, id, req.Sku, Today), ct);
        }, ct);
        await InvalidateAsync(ct);
        return await GetDtoAsync(id, ct);
    }

    /// <summary>A changed stock quantity is posted to the default store as an adjustment.</summary>
    public async Task<ProductDto> UpdateAsync(Guid id, UpsertProductRequest req, CancellationToken ct)
    {
        await CheckUomAsync(req.UomId, ct);
        var store = await posting.EnsureDefaultStoreAsync(ct);
        await InTransactionAsync(async () =>
        {
            var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Product not found.");
            if (p.Sku != req.Sku && await db.Products.AnyAsync(x => x.Sku == req.Sku && x.Id != id, ct))
                throw new ConflictException($"SKU {req.Sku} already exists.");
            var change = req.StockQuantity - p.StockQuantity;
            (p.Sku, p.Name, p.Category, p.UnitPrice, p.UomId) = (req.Sku, req.Name, req.Category, req.UnitPrice, req.UomId);
            await db.SaveChangesAsync(ct);
            if (change != 0)
            {
                // In at the store's current rate (or the unit price if it has none); out at the moving average.
                var rate = change > 0
                    ? await db.StockBalances.Where(b => b.ProductId == id && b.StoreId == store && b.Quantity > 0)
                        .Select(b => (decimal?)b.ValuationRate).FirstOrDefaultAsync(ct) ?? req.UnitPrice
                    : (decimal?)null;
                await posting.PostAsync([new StockMovement(id, store, change, rate)],
                    new Voucher(VoucherType.ProductAdjustment, id, req.Sku, Today), ct);
            }
        }, ct);
        await InvalidateAsync(ct);
        return await GetDtoAsync(id, ct);
    }

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private async Task CheckUomAsync(Guid? uomId, CancellationToken ct)
    {
        if (uomId is { } u && !await db.Uoms.AnyAsync(x => x.Id == u && x.IsActive, ct))
            throw new NotFoundException("Unit of measure not found.");
    }

    private async Task InTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await work();
            await tx.CommitAsync(ct);
        });
    }

    private async Task<ProductDto> GetDtoAsync(Guid id, CancellationToken ct) =>
        await db.Products.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.Category, p.UnitPrice, p.StockQuantity, p.UomId, p.Uom!.Code))
            .SingleAsync(ct);

    /// <summary>Products used on orders or stock entries can't be deleted (their history must stay intact).</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (await db.OrderItems.AnyAsync(i => i.ProductId == id, ct) || await db.StockEntryLines.AnyAsync(l => l.ProductId == id, ct))
            throw new ConflictException("This product is used on orders or stock entries and can't be deleted.");
        var n = await db.Products.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        if (n == 0) throw new NotFoundException("Product not found.");
        await InvalidateAsync(ct);
    }

    private Task InvalidateAsync(CancellationToken ct) =>
        cache.SetStringAsync(VersionKey, Guid.NewGuid().ToString("N"), ct);

}
