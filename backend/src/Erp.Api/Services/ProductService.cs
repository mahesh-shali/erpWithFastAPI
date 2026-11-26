using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
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
    Task<PagedResult<ProductDto>> ListAsync(int page, int pageSize, string? category, CancellationToken ct, string? search = null,
        string? itemType = null);
    Task<ProductDto> GetAsync(Guid id, CancellationToken ct);
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

    public async Task<PagedResult<ProductDto>> ListAsync(int page, int pageSize, string? category, CancellationToken ct,
        string? search = null, string? itemType = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var version = await cache.GetStringAsync(VersionKey, ct) ?? "0";
        // v2: item fields were added to ProductDto. Search text is hashed so the key stays short and printable.
        var searchKey = search is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(search)))[..16];
        var key = $"products:v2:{tenant.OrganizationId}:{version}:{category}:{itemType}:{searchKey}:{page}:{pageSize}";

        var cached = await cache.GetStringAsync(key, ct);
        if (cached is not null) return JsonSerializer.Deserialize<PagedResult<ProductDto>>(cached)!;

        var q = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(p => p.Category == category);
        if (!string.IsNullOrWhiteSpace(itemType)) q = q.Where(p => p.ItemType == itemType);
        if (search is not null)
            q = q.Where(p => EF.Functions.ILike(p.Name, $"%{search}%") || EF.Functions.ILike(p.Sku, $"%{search}%")
                             || EF.Functions.ILike(p.HsnCode!, $"%{search}%") || EF.Functions.ILike(p.DrawingNo!, $"%{search}%"));
        var total = await q.LongCountAsync(ct);
        var items = await q.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(Projection).ToListAsync(ct);

        var result = new PagedResult<ProductDto>(items, page, pageSize, total);
        await cache.SetStringAsync(key, JsonSerializer.Serialize(result), Ttl, ct);
        return result;
    }

    /// <summary>Opening stock goes into the default store through the stock ledger, valued at the unit price.</summary>
    public async Task<ProductDto> CreateAsync(UpsertProductRequest req, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(p => p.Sku == req.Sku, ct))
            throw new ConflictException($"SKU {req.Sku} already exists.");
        CheckItemType(req.ItemType);
        await CheckUomAsync(req.UomId, ct);
        var store = await posting.EnsureDefaultStoreAsync(ct);
        var id = Guid.NewGuid();
        await InTransactionAsync(async () =>
        {
            var product = new Product { Id = id, StockQuantity = 0 };
            Apply(product, req);
            db.Products.Add(product);
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
        CheckItemType(req.ItemType);
        await CheckUomAsync(req.UomId, ct);
        var store = await posting.EnsureDefaultStoreAsync(ct);
        await InTransactionAsync(async () =>
        {
            var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Product not found.");
            if (p.Sku != req.Sku && await db.Products.AnyAsync(x => x.Sku == req.Sku && x.Id != id, ct))
                throw new ConflictException($"SKU {req.Sku} already exists.");
            var change = req.StockQuantity - p.StockQuantity;
            Apply(p, req);
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

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void Apply(Product p, UpsertProductRequest req)
    {
        (p.Sku, p.Name, p.Category, p.UnitPrice, p.UomId) = (req.Sku, req.Name, Clean(req.Category), req.UnitPrice, req.UomId);
        p.ItemType = req.ItemType ?? ItemType.FinishedGood;
        p.Description = Clean(req.Description);
        p.HsnCode = Clean(req.HsnCode);
        p.TaxRate = req.TaxRate;
        p.StandardCost = req.StandardCost;
        p.ReorderLevel = req.ReorderLevel;
        p.LeadTimeDays = req.LeadTimeDays;
        p.DrawingNo = Clean(req.DrawingNo);
        p.IsActive = req.IsActive;
    }

    private static void CheckItemType(string? itemType)
    {
        if (itemType is not null && !ItemType.All.Contains(itemType))
            throw new DomainException($"Item type must be one of {string.Join(", ", ItemType.All)}.");
    }

    public static readonly Expression<Func<Product, ProductDto>> Projection = p => new ProductDto(
        p.Id, p.Sku, p.Name, p.Category, p.UnitPrice, p.StockQuantity, p.UomId, p.Uom!.Code,
        p.ItemType, p.Description, p.HsnCode, p.TaxRate, p.StandardCost, p.ReorderLevel, p.LeadTimeDays, p.DrawingNo, p.IsActive);

    /// <summary>Maps a loaded product (with its Uom included).</summary>
    public static ProductDto ToDto(Product p) => new(p.Id, p.Sku, p.Name, p.Category, p.UnitPrice, p.StockQuantity, p.UomId, p.Uom?.Code,
        p.ItemType, p.Description, p.HsnCode, p.TaxRate, p.StandardCost, p.ReorderLevel, p.LeadTimeDays, p.DrawingNo, p.IsActive);

    public async Task<ProductDto> GetAsync(Guid id, CancellationToken ct) =>
        await db.Products.AsNoTracking().Where(p => p.Id == id).Select(Projection).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Item not found.");

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

    private Task<ProductDto> GetDtoAsync(Guid id, CancellationToken ct) => GetAsync(id, ct);

    /// <summary>Items used on any document can't be deleted (their history must stay intact): deactivate them instead.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (await db.OrderItems.AnyAsync(i => i.ProductId == id, ct) || await db.StockEntryLines.AnyAsync(l => l.ProductId == id, ct)
            || await db.CustomerPoLines.AnyAsync(l => l.ProductId == id, ct) || await db.SalesInvoiceLines.AnyAsync(l => l.ProductId == id, ct)
            || await db.WorkOrders.AnyAsync(w => w.ProductId == id, ct) || await db.WorkOrderMaterials.AnyAsync(m => m.ProductId == id, ct))
            throw new ConflictException("This product is used on orders or stock entries and can't be deleted. Deactivate it instead.");
        var n = await db.Products.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        if (n == 0) throw new NotFoundException("Product not found.");
        await InvalidateAsync(ct);
    }

    private Task InvalidateAsync(CancellationToken ct) =>
        cache.SetStringAsync(VersionKey, Guid.NewGuid().ToString("N"), ct);

}
