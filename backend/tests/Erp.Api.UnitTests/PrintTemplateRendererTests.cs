using Erp.Api.Contracts;
using Erp.Api.Services;
using FluentAssertions;

namespace Erp.Api.UnitTests;

/// <summary>Custom print templates: rendering, the data model, and everything a hostile template or value must not get through.</summary>
public class PrintTemplateRendererTests
{
    private static readonly CompanyDto Company = new("Maker <b>Ltd</b>", "Gat 12\nChakan", "Maharashtra", null, null, "27AAACM0000A1Z1");

    private static RenderedPrint Render(string body, SalesInvoiceDto? invoice = null)
    {
        var (co, sample) = PrintTemplateRenderer.Sample(Company);
        return PrintTemplateRenderer.Render(body, PrintTemplateRenderer.InvoiceModel(co, invoice ?? sample));
    }

    [Fact]
    public void Renders_variables_loops_conditions_and_money_format()
    {
        var r = Render("""
            <h1>{{ company.name }}</h1><p>{{ invoice.number }} · {{ invoice.grand_total | fmt.money }}</p>
            <ul>{{ for l in lines }}<li>{{ l.no }}. {{ l.name }} × {{ l.quantity }}</li>{{ end }}</ul>
            {{ if invoice.is_inter_state }}<p>IGST</p>{{ else }}<p>CGST {{ invoice.cgst_total | fmt.money }}</p>{{ end }}
            """);
        r.Html.Should().StartWith("<div class=\"print-root\">");
        r.Html.Should().Contain("SI-00042 · ₹30,107.00");
        r.Html.Should().Contain("1. Ball valve 2\" forged brass × 10");
        r.Html.Should().Contain("CGST ₹2,273.40");                               // 4546.80 split in half
    }

    [Fact]
    public void Data_values_are_escaped_so_they_cannot_inject_markup()
    {
        var r = Render("<p>{{ company.name }}</p><div>{{ company.address_html }}</div>");
        r.Html.Should().Contain("Maker &lt;b&gt;Ltd&lt;/b&gt;").And.NotContain("<b>");
        r.Html.Should().Contain("Gat 12<br>Chakan");                              // the only markup a value gets: line breaks
    }

    [Theory]
    [InlineData("<script>alert(1)</script><p>ok</p>", "<script")]
    [InlineData("<img src=x onerror=alert(1)><p>ok</p>", "onerror")]
    [InlineData("<p onclick=\"steal()\">ok</p>", "onclick")]
    [InlineData("<iframe src=\"https://evil.test\"></iframe><p>ok</p>", "iframe")]
    [InlineData("<a href=\"javascript:alert(1)\">ok</a>", "javascript:")]
    [InlineData("<form action=\"https://evil.test\"><input name=x></form><p>ok</p>", "<form")]
    [InlineData("<img src=\"https://tracker.test/p.gif\"><p>ok</p>", "tracker.test")]
    [InlineData("<svg onload=alert(1)></svg><p>ok</p>", "<svg")]
    public void Dangerous_markup_is_removed(string body, string forbidden) =>
        Render(body).Html.Should().NotContainEquivalentOf(forbidden).And.Contain("ok");

    [Fact]
    public void Embedded_images_are_kept()
    {
        const string png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
        Render($"<img src=\"{png}\" alt=\"logo\">").Html.Should().Contain(png);
        Render("<img src=\"data:text/html;base64,PHNjcmlwdD4=\">").Html.Should().NotContain("data:text");
    }

    [Fact]
    public void Style_blocks_become_scoped_css_without_imports_or_remote_urls()
    {
        var r = Render("""
            <style>
              @import url("https://evil.test/x.css");
              body { font-family: Arial; }
              .t { color: red; background: url(https://tracker.test/bg.png); }
            </style>
            <p class="t">hi</p>
            """);
        r.Html.Should().NotContain("<style");
        r.Css.Should().NotContain("@import").And.NotContain("evil.test").And.NotContain("tracker.test");
        r.Css.Should().Contain(".print-root").And.Contain("color: rgba(255, 0, 0, 1)");   // the CSS parser normalises colours
        r.Css.Should().NotMatchRegex(@"(^|[\s,}])body\s*\{");
    }

    [Fact]
    public void Inline_style_attributes_become_generated_classes()
    {
        var r = Render("<p style=\"color: blue; text-align: right\">x</p>");
        r.Html.Should().NotContain("style=").And.Contain("class=\"pt-s1\"");
        r.Css.Should().Contain(".pt-s1{").And.Contain("color: rgba(0, 0, 255, 1)");
    }

    [Fact]
    public void Syntax_errors_name_the_line()
    {
        var act = () => PrintTemplateRenderer.Parse("<p>{{ for l in lines }}{{ l.name }}</p>");   // missing end
        act.Should().Throw<DomainException>().WithMessage("Template error:*");
    }

    [Fact]
    public void Runaway_loops_and_includes_are_stopped()
    {
        FluentActions.Invoking(() => Render("{{ for i in 1..100000 }}x{{ end }}")).Should().Throw<DomainException>().WithMessage("Template error:*");
        FluentActions.Invoking(() => Render("{{ include 'secret' }}")).Should().Throw<DomainException>().WithMessage("Template error:*");
    }

    [Fact]
    public void Empty_and_oversized_templates_are_refused()
    {
        FluentActions.Invoking(() => PrintTemplateRenderer.Parse("  ")).Should().Throw<DomainException>();
        FluentActions.Invoking(() => PrintTemplateRenderer.Parse(new string('x', PrintTemplateRules.MaxBodyLength + 1)))
            .Should().Throw<DomainException>().WithMessage("*too long*");
    }

    [Fact]
    public void The_starter_template_renders_the_sample_invoice()
    {
        var r = Render(PrintTemplateStarter.SalesInvoice);
        r.Html.Should().Contain("Tax Invoice").And.Contain("PO/2026/0815").And.Contain("Thirty Thousand One Hundred Seven");
        r.Html.Should().Contain("₹14,348.80");
        r.Css.Should().Contain(".invoice");
    }

    [Fact]
    public void The_customer_po_starter_renders_the_sample_po_with_escaped_values()
    {
        var (co, party, po) = PrintTemplateRenderer.SampleCustomerPo(Company);
        var r = PrintTemplateRenderer.Render(PrintTemplateStarter.CustomerPo, PrintTemplateRenderer.CustomerPoModel(co, party, po));
        r.Html.Should().Contain("Order Acknowledgement").And.Contain("CPO-00017").And.Contain("PO/2026/0815")
            .And.Contain("₹29,854.00").And.Contain("Pune 411019<br>Maharashtra");
        var evil = PrintTemplateRenderer.Render("<p>{{ customer.name }}</p>",
            PrintTemplateRenderer.CustomerPoModel(co, party with { Name = "<img src=x onerror=alert(1)>" }, po));
        evil.Html.Should().Contain("&lt;img").And.NotContain("<img");
    }

    [Fact]
    public void The_work_order_starter_renders_the_sample_job_sheet()
    {
        var (co, wo) = PrintTemplateRenderer.SampleWorkOrder(Company);
        var r = PrintTemplateRenderer.Render(PrintTemplateStarter.WorkOrder, PrintTemplateRenderer.WorkOrderModel(co, wo));
        r.Html.Should().Contain("WO-00008").And.Contain("Brass bar 50 mm").And.Contain("₹11,560.00").And.Contain("Pressure-test");
        PrintTemplateRenderer.Render("<p>{{ work_order.status }}</p>", PrintTemplateRenderer.WorkOrderModel(co, wo with { Status = "Submitted" }))
            .Html.Should().Contain("Completed");
    }

    [Theory]
    [InlineData("customer-pos")]
    [InlineData("sales-invoices")]
    [InlineData("work-orders")]
    public void Every_printable_page_has_a_starter(string pageKey) =>
        PrintTemplateStarter.For(pageKey).Should().NotBeNullOrWhiteSpace();

    [Fact]
    public void Pages_without_print_formats_have_no_starter() =>
        FluentActions.Invoking(() => PrintTemplateStarter.For("purchase-orders")).Should().Throw<DomainException>();

    [Theory]
    [InlineData(0, 2, "0.00")]
    [InlineData(999, 0, "999")]
    [InlineData(1234.5, 2, "1,234.50")]
    [InlineData(123456.789, 2, "1,23,456.79")]
    [InlineData(-12345678, 0, "-1,23,45,678")]
    public void Indian_grouping(decimal value, int decimals, string expected) =>
        PrintTemplateRenderer.IndianNumber(value, decimals).Should().Be(expected);
}
