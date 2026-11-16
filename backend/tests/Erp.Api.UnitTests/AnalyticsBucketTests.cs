using Erp.Api.Controllers;
using FluentAssertions;

namespace Erp.Api.UnitTests;

public class AnalyticsBucketTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);

    [Fact]
    public void Days_has_one_point_per_day_with_zeros_for_gaps()
    {
        var start = Today.AddDays(-29);
        var rows = new[] { (new DateTime(2026, 11, 10), 150m, 2), (new DateTime(2026, 11, 16), 50m, 1) };

        var points = AnalyticsController.Days(start, Today, rows);

        points.Should().HaveCount(30);
        points[0].Date.Should().Be(start);
        points[^1].Should().Be(new Erp.Api.Contracts.AnalyticsPoint(Today, 50m, 1));
        points.Single(p => p.Date == new DateOnly(2026, 11, 10)).Revenue.Should().Be(150m);
        points.Sum(p => p.Orders).Should().Be(3);
    }

    [Fact]
    public void Months_rolls_days_up_into_twelve_calendar_months()
    {
        var start = new DateOnly(2025, 12, 1);
        var rows = new[]
        {
            (new DateTime(2025, 12, 3), 100m, 1),
            (new DateTime(2025, 12, 28), 40m, 2),
            (new DateTime(2026, 11, 1), 10m, 1),
        };

        var points = AnalyticsController.Months(start, Today, rows);

        points.Should().HaveCount(12);
        points[0].Should().Be(new Erp.Api.Contracts.AnalyticsPoint(new DateOnly(2025, 12, 1), 140m, 3));
        points[^1].Date.Should().Be(new DateOnly(2026, 11, 1));
        points.Where(p => p.Date.Month is > 0 and < 11 && p.Date.Year == 2026).Should().OnlyContain(p => p.Revenue == 0);
    }
}
