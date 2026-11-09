using Erp.Api.Services;
using System.Text.Json;
using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Erp.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController(ErpDbContext db, IDistributedCache cache, ITenantContext tenant) : ControllerBase
{
    [HttpGet("stats"), RequirePage(PageKeys.Dashboard)]
    public async Task<DashboardStats> Stats(CancellationToken ct)
    {
        var key = $"dashboard:{tenant.OrganizationId}";
        if (await cache.GetStringAsync(key, ct) is { } hit)
            return JsonSerializer.Deserialize<DashboardStats>(hit)!;

        var stats = new DashboardStats(
            await db.Customers.CountAsync(ct),
            await db.Products.CountAsync(ct),
            await db.Orders.CountAsync(ct),
            await db.Orders.Where(o => o.Status != OrderStatus.Cancelled).SumAsync(o => (decimal?)o.TotalAmount, ct) ?? 0,
            await db.Products.CountAsync(p => p.StockQuantity < 10, ct));

        await cache.SetStringAsync(key, JsonSerializer.Serialize(stats),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) }, ct);
        return stats;
    }
}
