using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Erp.Api.Contracts;
using Ganss.Xss;
using Scriban;
using Scriban.Runtime;
using Scriban.Syntax;

namespace Erp.Api.Services;

public static class PrintTemplateRules
{
    public const int MaxBodyLength = 200_000;
    public const int MaxOutputLength = 2_000_000;
    /// <summary>Every rendered template is wrapped in this element; "html"/"body" selectors in its CSS are mapped to it.</summary>
    public const string RootClass = "print-root";
}

/// <summary>Rendered, sanitized template: HTML for a shadow root and the CSS for a constructed stylesheet.</summary>
public sealed record RenderedPrint(string Html, string Css);

/// <summary>
/// Turns an organization's print template into safe HTML + CSS.
///
/// 1. Scriban renders the template against a plain data model (dictionaries only, no .NET objects): every text
///    value is HTML-escaped up front, so data such as a customer name can never inject markup. No includes
///    (no template loader), loop/recursion/output limits and a time limit.
/// 2. <c>&lt;style&gt;</c> blocks are taken out; the rest goes through HtmlSanitizer: no scripts, event handlers,
///    frames, forms or external URLs (images only as data:image/… URIs).
/// 3. CSS is sanitized, stripped of @import and non-data url(), and "html"/"body" selectors are pointed at the root.
///    Inline style="" attributes become generated classes, because the web CSP (style-src 'self') blocks inline
///    styles; the client applies the CSS as a constructed stylesheet inside a shadow root, which the CSP allows.
/// </summary>
public static class PrintTemplateRenderer
{
    private static readonly string[] BlockedTags =
        ["script", "iframe", "frame", "frameset", "object", "embed", "form", "input", "button", "textarea", "select",
         "option", "link", "meta", "base", "svg", "math", "template", "audio", "video", "source", "track", "canvas"];
    private static readonly string[] ExtraTags =
        ["header", "footer", "section", "article", "main", "aside", "figure", "figcaption", "caption", "colgroup", "col", "style"];
    private static readonly Regex DataImage = new(@"^data:image/(png|jpe?g|gif|webp);base64,[A-Za-z0-9+/=\s]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses the template and throws a DomainException with the first errors (line, column) if it is invalid.</summary>
    public static Template Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new DomainException("The template is empty.");
        if (body.Length > PrintTemplateRules.MaxBodyLength)
            throw new DomainException($"The template is too long (max {PrintTemplateRules.MaxBodyLength:N0} characters).");
        var template = Template.Parse(body);
        if (template.HasErrors)
            throw new DomainException("Template error: " + string.Join("; ", template.Messages.Take(3).Select(m => m.ToString())));
        return template;
    }

    public static RenderedPrint Render(string body, ScriptObject model)
    {
        var template = Parse(body);
        string raw;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var context = new TemplateContext
        {
            LoopLimit = 2_000, RecursiveLimit = 20, LimitToString = PrintTemplateRules.MaxOutputLength,
            ObjectRecursionLimit = 20, RegexTimeOut = TimeSpan.FromMilliseconds(500), TemplateLoader = null,
            StrictVariables = false, CancellationToken = timeout.Token,
        };
        context.PushGlobal(model);
        try { raw = template.Render(context); }
        catch (ScriptRuntimeException e) { throw new DomainException("Template error: " + e.OriginalMessage); }
        catch (OperationCanceledException) { throw new DomainException("Template error: it took too long to render."); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { throw new DomainException("Template error: " + e.Message); }
        if (raw.Length > PrintTemplateRules.MaxOutputLength) throw new DomainException("Template error: the output is too large.");
        return Clean(raw);
    }

    /// <summary>Sanitizes rendered HTML and returns it with its scoped CSS (also used directly by tests).</summary>
    public static RenderedPrint Clean(string raw)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(raw);
        var css = new StringBuilder();
        foreach (var style in doc.QuerySelectorAll("style").ToList())
        {
            css.AppendLine(style.TextContent);
            style.Remove();
        }
        var bodyHtml = doc.Body?.InnerHtml ?? "";

        var sanitizer = CreateSanitizer();
        var safeHtml = sanitizer.Sanitize(bodyHtml);
        var safeCss = CleanCss(sanitizer, css.ToString());

        // Inline style="" -> generated classes (inline styles are blocked by the web CSP).
        var fragment = parser.ParseDocument("<div class=\"" + PrintTemplateRules.RootClass + "\">" + safeHtml + "</div>");
        var n = 0;
        var extra = new StringBuilder();
        foreach (var el in fragment.QuerySelectorAll("[style]").ToList())
        {
            var decl = el.GetAttribute("style") ?? "";
            el.RemoveAttribute("style");
            if (string.IsNullOrWhiteSpace(decl)) continue;
            var cls = $"pt-s{++n}";
            el.ClassList.Add(cls);
            extra.Append('.').Append(cls).Append('{').Append(decl.Replace("}", "")).AppendLine("}");
        }
        return new RenderedPrint(fragment.Body!.InnerHtml, safeCss + extra);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();
        foreach (var t in ExtraTags) s.AllowedTags.Add(t);
        foreach (var t in BlockedTags) s.AllowedTags.Remove(t);
        foreach (var a in new[] { "class", "colspan", "rowspan", "style", "src", "alt", "width", "height", "align", "valign", "title" })
            s.AllowedAttributes.Add(a);
        s.AllowedAttributes.Remove("href");      // no links on a printout: nothing to click, nothing to leak
        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("data");
        // Only embedded raster images; every other URL (http, relative, javascript:, data:text/html…) is dropped.
        s.FilterUrl += (_, e) => { if (!DataImage.IsMatch(e.OriginalUrl.Trim())) e.SanitizedUrl = null; };
        s.AllowedAtRules.Clear();
        s.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Style);
        s.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        return s;
    }

    private static readonly Regex Import = new(@"@import[^;]*;?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Url = new(@"url\(\s*(['""]?)(?<u>[^)'""]*)\1\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlBody = new(@"(^|[\s,{}>+~])(html|body|:root)(?=[\s,{.:#\[>+~]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    private static string CleanCss(HtmlSanitizer sanitizer, string css)
    {
        if (string.IsNullOrWhiteSpace(css)) return "";
        css = Import.Replace(css, "");
        css = Url.Replace(css, m => DataImage.IsMatch(m.Groups["u"].Value.Trim()) ? m.Value : "none");
        css = css.Replace("expression(", "", StringComparison.OrdinalIgnoreCase).Replace("</", "");
        // Let the sanitizer parse and filter the stylesheet (properties and at-rules), then read it back.
        var cleaned = new HtmlParser().ParseDocument(sanitizer.Sanitize("<style>" + css + "</style>"))
            .QuerySelector("style")?.TextContent ?? "";
        return HtmlBody.Replace(cleaned, m => m.Groups[1].Value + "." + PrintTemplateRules.RootClass) + Environment.NewLine;
    }

    // ------------------------------------------------------------------ data model

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string Lines(string? s) => string.Join("<br>", (s ?? "").Split('\n').Select(l => E(l.TrimEnd('\r'))));

    /// <summary>The variables a sales-invoice template can use (snake_case, all text already HTML-escaped).</summary>
    public static ScriptObject InvoiceModel(CompanyDto co, SalesInvoiceDto inv)
    {
        decimal Half(decimal v) => Math.Round(v / 2, 2, MidpointRounding.AwayFromZero);
        var inter = inv.InterState;
        var company = new ScriptObject
        {
            ["name"] = E(co.Name), ["address"] = E(co.Address), ["address_html"] = Lines(co.Address), ["state"] = E(co.State),
            ["phone"] = E(co.Phone), ["email"] = E(co.Email), ["gstin"] = E(co.TaxId),
        };
        var customer = new ScriptObject
        {
            ["name"] = E(inv.BillingName), ["address"] = E(inv.BillingAddress), ["address_html"] = Lines(inv.BillingAddress),
            ["gstin"] = E(inv.BillingTaxId), ["state"] = E(inv.BillingState),
            ["ship_to_html"] = Lines(inv.ShippingAddress ?? inv.BillingAddress ?? inv.BillingName),
        };
        var lines = new ScriptArray();
        foreach (var l in inv.Lines)
            lines.Add(new ScriptObject
            {
                ["no"] = l.LineNo, ["sku"] = E(l.Sku), ["name"] = E(l.ProductName), ["hsn"] = E(l.HsnCode), ["unit"] = E(l.Uom),
                ["quantity"] = l.Quantity, ["rate"] = l.Rate, ["discount_percent"] = l.DiscountPercent, ["amount"] = l.Amount,
                ["tax_rate"] = l.TaxRate, ["tax_amount"] = l.TaxAmount, ["total"] = l.Total,
                ["cgst"] = inter ? 0m : Half(l.TaxAmount), ["sgst"] = inter ? 0m : l.TaxAmount - Half(l.TaxAmount),
                ["igst"] = inter ? l.TaxAmount : 0m,
            });
        var taxes = new ScriptArray();
        foreach (var t in inv.TaxSummary)
            taxes.Add(new ScriptObject
            {
                ["rate"] = t.TaxRate, ["taxable"] = t.Taxable, ["tax"] = t.Tax,
                ["cgst"] = inter ? 0m : Half(t.Tax), ["sgst"] = inter ? 0m : t.Tax - Half(t.Tax), ["igst"] = inter ? t.Tax : 0m,
            });
        var cgst = inter ? 0m : inv.TaxSummary.Sum(t => Half(t.Tax));
        var invoice = new ScriptObject
        {
            ["number"] = E(inv.Number), ["status"] = E(inv.Status), ["is_draft"] = inv.Status == "Draft",
            ["is_cancelled"] = inv.Status == "Cancelled", ["date"] = inv.InvoiceDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            ["due_date"] = inv.DueDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
            ["customer_po_ref"] = E(inv.CustomerPoRef), ["our_ref"] = E(inv.CustomerPoNumber), ["place_of_supply"] = E(inv.BillingState),
            ["is_inter_state"] = inter, ["remarks"] = E(inv.Remarks),
            ["sub_total"] = inv.SubTotal, ["discount_total"] = inv.DiscountTotal, ["taxable_total"] = inv.SubTotal - inv.DiscountTotal,
            ["tax_total"] = inv.TaxTotal, ["cgst_total"] = cgst, ["sgst_total"] = inter ? 0m : inv.TaxTotal - cgst,
            ["igst_total"] = inter ? inv.TaxTotal : 0m, ["round_off"] = inv.RoundOff, ["grand_total"] = inv.GrandTotal,
            ["amount_in_words"] = E(inv.AmountInWords), ["line_count"] = inv.Lines.Count,
        };
        var fmt = new ScriptObject();
        fmt.Import("money", new Func<object?, string>(v => "₹" + IndianNumber(ToDecimal(v), 2)));
        fmt.Import("number", new Func<object?, string>(v => IndianNumber(ToDecimal(v), ToDecimal(v) % 1 == 0 ? 0 : 2)));

        var model = new ScriptObject
        {
            ["company"] = company, ["customer"] = customer, ["invoice"] = invoice, ["lines"] = lines, ["tax_summary"] = taxes,
            ["fmt"] = fmt,
        };
        return model;
    }

    private static decimal ToDecimal(object? v) => v switch
    {
        null => 0m,
        decimal d => d,
        IConvertible c => Convert.ToDecimal(c, CultureInfo.InvariantCulture),
        _ => 0m,
    };

    /// <summary>12,34,567.89 (Indian grouping), independent of the server's culture data.</summary>
    public static string IndianNumber(decimal value, int decimals)
    {
        var neg = value < 0;
        var s = Math.Round(Math.Abs(value), decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture);
        var parts = s.Split('.');
        var whole = parts[0];
        if (whole.Length > 3)
        {
            var head = whole[..^3];
            var groups = new List<string>();
            while (head.Length > 2) { groups.Insert(0, head[^2..]); head = head[..^2]; }
            if (head.Length > 0) groups.Insert(0, head);
            whole = string.Join(",", groups) + "," + whole[^3..];
        }
        return (neg ? "-" : "") + whole + (parts.Length > 1 ? "." + parts[1] : "");
    }

    /// <summary>A realistic invoice for previews when no real invoice is chosen.</summary>
    public static (CompanyDto Company, SalesInvoiceDto Invoice) Sample(CompanyDto company)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var lines = new List<SalesInvoiceLineDto>
        {
            new(1, Guid.Empty, "FG-1001", "Ball valve 2\" forged brass", "NOS", "8481", null, 10, 1250m, 0m, 12500m, 18m, 2250m, 14750m, 0m, 0),
            new(2, Guid.Empty, "FG-2040", "Gate valve 4\" cast iron", "NOS", "8481", null, 4, 3200m, 5m, 12160m, 18m, 2188.80m, 14348.80m, 0m, 0),
            new(3, Guid.Empty, "SP-0007", "Gasket set", "SET", "4016", null, 20, 45m, 0m, 900m, 12m, 108m, 1008m, 0m, 0),
        };
        decimal sub = 12500m + 12800m + 900m, disc = 640m, tax = 2250m + 2188.80m + 108m, exact = sub - disc + tax;
        var (grand, roundOff) = Money.RoundToRupee(exact);
        var inv = new SalesInvoiceDto(Guid.Empty, "SI-00042", "Submitted", today, today.AddDays(30), Guid.Empty, "Sample Customer Pvt Ltd",
            Guid.Empty, "CPO-00017", "PO/2026/0815", Guid.Empty, "Main Store", "Sample Customer Pvt Ltd",
            "Plot 7, MIDC Industrial Area\nPune 411019\nMaharashtra\nIndia", "27ABCDE1234F1Z5", "Maharashtra", null, "Thank you for your business.",
            sub, disc, tax, roundOff, grand, 0m, SalesInvoiceService.IsInterState(company.State, "Maharashtra"),
            AmountInWords.Rupees(grand), [new TaxSummaryRow(12m, 900m, 108m), new TaxSummaryRow(18m, 24660m, 4438.80m)],
            DateTime.UtcNow, DateTime.UtcNow, null, lines);
        var co = company with
        {
            Address = company.Address ?? "Your company address", State = company.State ?? "Maharashtra",
            TaxId = company.TaxId ?? "27AAAAA0000A1Z5",
        };
        return (co, inv);
    }
}
