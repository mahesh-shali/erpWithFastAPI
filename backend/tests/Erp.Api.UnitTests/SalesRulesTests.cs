using Erp.Api.Services;
using FluentAssertions;

namespace Erp.Api.UnitTests;

/// <summary>Pure rules of the sales flow: production cost, invoice maths, amount in words, GST type, stock status.</summary>
public class SalesRulesTests
{
    [Fact]
    public void Produced_rate_is_consumed_value_plus_other_cost_per_unit()
    {
        Valuation.ProducedRate(1000m, 200m, 4).Should().Be(300m);
        Valuation.ProducedRate(100m, 0m, 3).Should().Be(33.3333m);
        Valuation.ProducedRate(0m, 0m, 5).Should().Be(0m);              // no materials, no cost: comes in at zero
        FluentActions.Invoking(() => Valuation.ProducedRate(10m, 0m, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => Valuation.ProducedRate(-1m, 0m, 1)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Invoice_line_applies_discount_before_tax()
    {
        // 3 × 99.99 = 299.97, less 10% = 269.97 (299.973 rounded), GST 18% = 48.59
        var l = Money.Line(3, 99.99m, 10m, 18m);
        l.Gross.Should().Be(299.97m);
        l.Amount.Should().Be(269.97m);
        l.Discount.Should().Be(30.00m);
        l.Tax.Should().Be(48.59m);
        Money.Line(1, 100m, 0m, 0m).Should().Be((100m, 0m, 100m, 0m));
    }

    [Theory]
    [InlineData("1234.49", "1234", "-0.49")]
    [InlineData("1234.50", "1235", "0.50")]
    [InlineData("100.00", "100", "0.00")]
    public void Grand_total_is_rounded_to_the_rupee(string exact, string grand, string roundOff)
    {
        var (g, r) = Money.RoundToRupee(decimal.Parse(exact, System.Globalization.CultureInfo.InvariantCulture));
        g.Should().Be(decimal.Parse(grand, System.Globalization.CultureInfo.InvariantCulture));
        r.Should().Be(decimal.Parse(roundOff, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("0", "Rupees Zero Only")]
    [InlineData("7", "Rupees Seven Only")]
    [InlineData("15", "Rupees Fifteen Only")]
    [InlineData("101", "Rupees One Hundred One Only")]
    [InlineData("1180", "Rupees One Thousand One Hundred Eighty Only")]
    [InlineData("120050.5", "Rupees One Lakh Twenty Thousand Fifty and Fifty Paise Only")]
    [InlineData("25000000", "Rupees Two Crore Fifty Lakh Only")]
    [InlineData("1234567890.99", "Rupees One Hundred Twenty Three Crore Forty Five Lakh Sixty Seven Thousand Eight Hundred Ninety and Ninety Nine Paise Only")]
    public void Amount_in_words_uses_indian_numbering(string amount, string words) =>
        AmountInWords.Rupees(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(words);

    [Theory]
    [InlineData("Maharashtra", "Maharashtra", false)]
    [InlineData("Maharashtra", " maharashtra ", false)]
    [InlineData("Maharashtra", "Karnataka", true)]
    [InlineData(null, "Karnataka", false)]     // company state unknown: treat as within the state
    [InlineData("Maharashtra", null, false)]
    public void Different_states_mean_igst(string? company, string? customer, bool inter) =>
        SalesInvoiceService.IsInterState(company, customer).Should().Be(inter);

    [Theory]
    [InlineData(-1, 0, "Short")]
    [InlineData(0, 0, "Reorder")]
    [InlineData(4, 5, "Reorder")]
    [InlineData(5, 5, "OK")]
    [InlineData(1, 0, "OK")]
    public void Stock_status_compares_available_with_the_reorder_level(int available, int reorder, string status) =>
        StockDetailsService.StatusOf(available, reorder).Should().Be(status);
}
