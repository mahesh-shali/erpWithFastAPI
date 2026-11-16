using System.Text.Json;
using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Erp.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Erp.Api.Controllers;

/// <summary>
/// Sales analytics for the Analytics page. Everything except the status breakdown excludes cancelled
/// orders. All queries go through the tenant query filter; the result is cached per organization and range.
/// </summary>
[ApiController]
[Route("api/analytics")]
public class AnalyticsController(ErpDbContext db, IDistributedCache cache, ITenantContext tenant, TimeProvider clock)
    : ControllerBase
{
    /// <summary>Allowed ranges: 30 and 90 days (daily points) or 365 = the last 12 calendar months (monthly points).</summary>
    public static readonly int[] AllowedDays = [30, 90, 365];
    private const int TopN = 6;

    [HttpGet, RequirePage(PageKeys.Analytics)]
    public async Task<ActionResult<AnalyticsReport>> Get([FromQuery] int days = 30, CancellationToken ct = default)
    {
        if (!AllowedDays.Contains(days))
            return Problem(statusCode: 400, title: $"days must be one of {string.Join(", ", AllowedDays)}.");

        var key = $"analytics:{tenant.OrganizationId}:{days}";
        if (await cache.GetStringAsync(key, ct) is { } hit)
            return JsonSerializer.Deserialize<AnalyticsReport>(hit)!;

        var report = await BuildAsync(days, ct);
        await cache.SetStringAsync(key, JsonSerializer.Serialize(report),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60) }, ct);
        return report;
    }

    private async Task<AnalyticsReport> BuildAsync(int days, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var monthly = days == 365;

        // Current period [from, to) and the previous period of the same length, for the deltas.
        DateOnly start, prevStart;
        if (monthly)
        {
            start = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);
            prevStart = start.AddMonths(-12);
        }
        else
        {
            start = today.AddDays(-(days - 1));
            prevStart = start.AddDays(-days);
        }
        var from = Utc(start);
        var to = now;
        var prevFrom = Utc(prevStart);

        var current = await TotalsAsync(from, to, ct);
        var previous = await TotalsAsync(prevFrom, from, ct);

        var orders = db.Orders.AsNoTracking().Where(o => o.OrderedAt >= from && o.OrderedAt < to);
        var valid = orders.Where(o => o.Status != OrderStatus.Cancelled);

        var daily = await valid
            .GroupBy(o => o.OrderedAt.Date)
            .Select(g => new { Day = g.Key, Revenue = g.Sum(o => o.TotalAmount), Orders = g.Count() })
            .ToListAsync(ct);
        var trend = monthly ? Months(start, today, daily.Select(d => (d.Day, d.Revenue, d.Orders)))
                            : Days(start, today, daily.Select(d => (d.Day, d.Revenue, d.Orders)));

        var statusRows = await orders
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Orders = g.Count(), Amount = g.Sum(o => o.TotalAmount) })
            .ToListAsync(ct);
        // Fixed order (the lifecycle), with zero rows for statuses that have no orders in the range.
        var statuses = OrderStatus.All.Select(s => statusRows.FirstOrDefault(r => r.Status == s) is { } r
            ? new AnalyticsStatus(s, r.Orders, r.Amount)
            : new AnalyticsStatus(s, 0, 0)).ToList();

        var lines =
            from i in db.OrderItems.AsNoTracking()
            join o in valid on i.OrderId equals o.Id
            join p in db.Products on i.ProductId equals p.Id
            select new { p.Id, p.Name, Category = p.Category ?? "Uncategorised", i.Quantity, Amount = i.Quantity * i.UnitPrice };

        var topProducts = await lines
            .GroupBy(l => new { l.Id, l.Name })
            .Select(g => new { g.Key.Name, Revenue = g.Sum(l => l.Amount), Count = g.Sum(l => l.Quantity) })
            .OrderByDescending(r => r.Revenue).Take(TopN)
            .Select(r => new AnalyticsRanked(r.Name, r.Revenue, r.Count))
            .ToListAsync(ct);

        var categories = await lines
            .GroupBy(l => l.Category)
            .Select(g => new { Name = g.Key, Revenue = g.Sum(l => l.Amount), Count = g.Sum(l => l.Quantity) })
            .OrderByDescending(r => r.Revenue).Take(TopN)
            .Select(r => new AnalyticsRanked(r.Name, r.Revenue, r.Count))
            .ToListAsync(ct);

        var byCustomer =
            from o in valid
            join c in db.Customers on o.CustomerId equals c.Id
            select new { c.Id, c.Name, City = c.City ?? "Unknown", o.TotalAmount };

        var topCustomers = await byCustomer
            .GroupBy(x => new { x.Id, x.Name })
            .Select(g => new { g.Key.Name, Revenue = g.Sum(x => x.TotalAmount), Count = g.Count() })
            .OrderByDescending(r => r.Revenue).Take(TopN)
            .Select(r => new AnalyticsRanked(r.Name, r.Revenue, r.Count))
            .ToListAsync(ct);

        var cities = await byCustomer
            .GroupBy(x => x.City)
            .Select(g => new { Name = g.Key, Revenue = g.Sum(x => x.TotalAmount), Count = g.Count() })
            .OrderByDescending(r => r.Revenue).Take(TopN)
            .Select(r => new AnalyticsRanked(r.Name, r.Revenue, r.Count))
            .ToListAsync(ct);

        return new AnalyticsReport(days, monthly ? "month" : "day", from, to, current, previous,
            trend, statuses, topProducts, topCustomers, cities, categories);
    }

    private async Task<AnalyticsTotals> TotalsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var valid = db.Orders.AsNoTracking()
            .Where(o => o.OrderedAt >= from && o.OrderedAt < to && o.Status != OrderStatus.Cancelled);
        var revenue = await valid.SumAsync(o => (decimal?)o.TotalAmount, ct) ?? 0;
        var count = await valid.CountAsync(ct);
        var newCustomers = await db.Customers.CountAsync(c => c.CreatedAt >= from && c.CreatedAt < to, ct);
        return new AnalyticsTotals(revenue, count, count == 0 ? 0 : Math.Round(revenue / count, 2), newCustomers);
    }

    /// <summary>One point per day from start to today, with zeros for days without orders.</summary>
    public static List<AnalyticsPoint> Days(DateOnly start, DateOnly today,
        IEnumerable<(DateTime Day, decimal Revenue, int Orders)> rows)
    {
        var byDay = rows.ToDictionary(r => DateOnly.FromDateTime(r.Day));
        var points = new List<AnalyticsPoint>();
        for (var d = start; d <= today; d = d.AddDays(1))
            points.Add(byDay.TryGetValue(d, out var r) ? new AnalyticsPoint(d, r.Revenue, r.Orders) : new AnalyticsPoint(d, 0, 0));
        return points;
    }

    /// <summary>One point per calendar month (dated the 1st) from start's month to today's month.</summary>
    public static List<AnalyticsPoint> Months(DateOnly start, DateOnly today,
        IEnumerable<(DateTime Day, decimal Revenue, int Orders)> rows)
    {
        var byMonth = rows.GroupBy(r => new DateOnly(r.Day.Year, r.Day.Month, 1))
            .ToDictionary(g => g.Key, g => (Revenue: g.Sum(r => r.Revenue), Orders: g.Sum(r => r.Orders)));
        var points = new List<AnalyticsPoint>();
        for (var m = new DateOnly(start.Year, start.Month, 1); m <= today; m = m.AddMonths(1))
            points.Add(byMonth.TryGetValue(m, out var r) ? new AnalyticsPoint(m, r.Revenue, r.Orders) : new AnalyticsPoint(m, 0, 0));
        return points;
    }

    private static DateTime Utc(DateOnly d) => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
