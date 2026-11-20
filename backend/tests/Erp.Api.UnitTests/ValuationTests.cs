using Erp.Api.Services;
using FluentAssertions;

namespace Erp.Api.UnitTests;

/// <summary>Moving-average valuation, checked against the worked example in the ERPNext manual.</summary>
public class ValuationTests
{
    [Fact]
    public void Receipts_average_the_rate_and_issues_go_out_at_it()
    {
        // 10 @ 100, then 20 @ 120  ->  (1000 + 2400) / 30 = 113.3333
        var a = Valuation.Apply(0, 0, 0, 10, 100m);
        a.Should().Be((100m, 1000m, 10, 100m, 1000m));
        var b = Valuation.Apply(a.QtyAfter, a.ValuationRate, a.ValueAfter, 20, 120m);
        b.ValuationRate.Should().Be(113.3333m);
        b.ValueAfter.Should().Be(3400m);

        // Issue 15 at the moving average: 15 × 113.3333 = 1700; rate unchanged
        var c = Valuation.Apply(b.QtyAfter, b.ValuationRate, b.ValueAfter, -15, null);
        c.Rate.Should().Be(113.3333m);
        c.ValueChange.Should().Be(-1700m);
        c.QtyAfter.Should().Be(15);
        c.ValuationRate.Should().Be(113.3333m);
        c.ValueAfter.Should().Be(1700m);
    }

    [Fact]
    public void Issuing_everything_leaves_zero_value_without_rounding_residue()
    {
        var r = Valuation.Apply(3, 33.3333m, 100m, -3, null);
        r.QtyAfter.Should().Be(0);
        r.ValueAfter.Should().Be(0);
        r.ValueChange.Should().Be(-100m);
    }

    [Fact]
    public void Going_below_zero_is_refused()
    {
        var act = () => Valuation.Apply(5, 10m, 50m, -6, null);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Reversing_a_receipt_at_its_own_rate_restores_the_previous_rate()
    {
        var first = Valuation.Apply(0, 0, 0, 10, 100m);                                   // 10 @ 100
        var second = Valuation.Apply(first.QtyAfter, first.ValuationRate, first.ValueAfter, 10, 200m); // avg 150
        second.ValuationRate.Should().Be(150m);
        var reversed = Valuation.Apply(second.QtyAfter, second.ValuationRate, second.ValueAfter, -10, 200m);
        reversed.Should().Be((200m, -2000m, 10, 100m, 1000m));
    }

    [Fact]
    public void Incoming_without_a_rate_is_a_programming_error()
    {
        var act = () => Valuation.Apply(0, 0, 0, 5, null);
        act.Should().Throw<ArgumentException>();
    }
}
