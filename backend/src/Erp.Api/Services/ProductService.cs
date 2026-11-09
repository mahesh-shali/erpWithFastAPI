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
public sealed class ProductService(ErpDbContext db, IDistributedCache cache, ITenantContext tenant) : IProductService
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
            .Select(p => new ProductDto(p.Id, p.Sku, p.Name, p.Category, p.UnitPrice, p.StockQuantity))
            .ToListAsync(ct);

        var result = new PagedResult<ProductDto>(items, page, pageSize, total);
        await cache.SetStringAsync(key, JsonSerializer.Serialize(result), Ttl, ct);
        return result;
    }

    public async Task<ProductDto> CreateAsync(UpsertProductRequest req, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(p => p.Sku == req.Sku, ct))
            throw new ConflictException($"SKU {req.Sku} already exists.");
        var p = new Product
        {
            Id = Guid.NewGuid(), Sku = req.Sku, Name = req.Name, Category = req.Category,
            UnitPrice = req.UnitPrice, StockQuantity = req.StockQuantity
        };
        db.Products.Add(p);
        await db.SaveChangesAsync(ct);
        await InvalidateAsync(ct);
        return ToDto(p);
    }

    public async Task<ProductDto> UpdateAsync(Guid id, UpsertProductRequest req, CancellationToken ct)
    {
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Product not found.");
        (p.Sku, p.Name, p.Category, p.UnitPrice, p.StockQuantity) = (req.Sku, req.Name, req.Category, req.UnitPrice, req.StockQuantity);
        await db.SaveChangesAsync(ct);
        await InvalidateAsync(ct);
        return ToDto(p);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var n = await db.Products.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        if (n == 0) throw new NotFoundException("Product not found.");
        await InvalidateAsync(ct);
    }

    private Task InvalidateAsync(CancellationToken ct) =>
        cache.SetStringAsync(VersionKey, Guid.NewGuid().ToString("N"), ct);

    private static ProductDto ToDto(Product p) => new(p.Id, p.Sku, p.Name, p.Category, p.UnitPrice, p.StockQuantity);
}
