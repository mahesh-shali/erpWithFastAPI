using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using FluentAssertions;

namespace Erp.Api.IntegrationTests;

/// <summary>Custom print templates: CRUD, versions, default, freezing on issue, fall-back, rights and tenants.</summary>
[Collection(ApiCollection.Name)]
public class PrintTemplatesTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!";
    private const string Type = "sales-invoice";

    private async Task<HttpClient> NewOrg()
    {
        var c = factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Org " + Guid.NewGuid().ToString("N")[..6], $"owner{Guid.NewGuid():N}@x.com", Pw, "Owner"));
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return c;
    }

    private async Task<HttpClient> NewUserIn(HttpClient owner, string role)
    {
        var email = $"{role.ToLowerInvariant()}{Guid.NewGuid():N}@x.com";
        (await owner.PostAsJsonAsync("/api/users", new CreateUserRequest(email, role, Pw, role))).EnsureSuccessStatusCode();
        var c = factory.CreateClient();
        var auth = (await (await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Pw))).Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return c;
    }

    private static async Task<T> Ok<T>(HttpResponseMessage res)
    {
        if (!res.IsSuccessStatusCode) throw new Exception($"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<PrintTemplateDto> Create(HttpClient c, string name, string body, bool makeDefault = false) =>
        c.PostAsJsonAsync("/api/print-templates", new CreatePrintTemplateRequest(Type, name, body, makeDefault))
            .ContinueWith(t => Ok<PrintTemplateDto>(t.Result)).Unwrap();

    /// <summary>A draft invoice for 1 item from opening stock (no P.O.).</summary>
    private static async Task<SalesInvoiceDto> DraftInvoice(HttpClient c)
    {
        var item = await Ok<ProductDto>(await c.PostAsJsonAsync("/api/products",
            new UpsertProductRequest("PT-" + Guid.NewGuid().ToString("N")[..5], "Valve", null, 100m, 10, TaxRate: 18m)));
        var cust = await Ok<CustomerDto>(await c.PostAsJsonAsync("/api/customers", new UpsertCustomerRequest("Acme <script>", null, null, null)));
        var main = (await c.GetFromJsonAsync<List<StoreDto>>("/api/stores"))!.Single(s => s.IsDefault);
        return await Ok<SalesInvoiceDto>(await c.PostAsJsonAsync("/api/sales-invoices", new UpsertSalesInvoiceRequest(cust.Id, null, null, null,
            main.Id, null, null, [new SalesInvoiceLineRequest(null, item.Id, 1, 100m)])));
    }

    private static Task<InvoicePrintDto> Print(HttpClient c, Guid invoiceId) =>
        c.GetFromJsonAsync<InvoicePrintDto>($"/api/sales-invoices/{invoiceId}/print")!;

    [Fact]
    public async Task Starter_preview_and_validation()
    {
        var c = await NewOrg();
        var starter = await c.GetFromJsonAsync<PrintTemplateStarterDto>($"/api/print-templates/starter?documentType={Type}");
        starter!.Body.Should().Contain("{{ invoice.number }}");

        var preview = await Ok<RenderedTemplateDto>(await c.PostAsJsonAsync("/api/print-templates/preview",
            new PreviewPrintTemplateRequest(Type, starter.Body)));
        preview.Html.Should().Contain("SI-00042").And.Contain("Tax Invoice");            // sample data
        preview.Css.Should().Contain(".invoice");

        var bad = await c.PostAsJsonAsync("/api/print-templates", new CreatePrintTemplateRequest(Type, "Broken", "{{ for x in lines }}"));
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync()).Should().Contain("Template error");
        (await c.PostAsJsonAsync("/api/print-templates", new CreatePrintTemplateRequest("purchase-order", "X", "<p>x</p>")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invoices_print_with_the_default_template_and_issued_ones_keep_their_version()
    {
        var c = await NewOrg();
        var inv = await DraftInvoice(c);
        (await Print(c, inv.Id)).Template.Should().BeNull();                               // no template yet: built-in

        var v1 = await Create(c, "Letterhead", "<h1>V1 {{ invoice.number }}</h1><p>{{ customer.name }}</p>", makeDefault: true);
        v1.Should().BeEquivalentTo(new { Version = 1, IsDefault = true, IsArchived = false });
        var draftPrint = await Print(c, inv.Id);
        draftPrint.Template!.Html.Should().Contain($"V1 {inv.Number}").And.Contain("Acme &lt;script&gt;");
        draftPrint.Template.Version.Should().Be(1);

        // Issue it: version 1 is frozen on the invoice
        await Ok<SalesInvoiceDto>(await c.PostAsync($"/api/sales-invoices/{inv.Id}/submit", null));

        // Edit: version 2 replaces version 1 as the default; the issued invoice still prints version 1
        var v2 = await Ok<PrintTemplateDto>(await c.PutAsJsonAsync($"/api/print-templates/{v1.Id}",
            new UpdatePrintTemplateRequest("Letterhead", "<h1>V2 {{ invoice.number }}</h1>")));
        v2.Should().BeEquivalentTo(new { Version = 2, IsDefault = true, FamilyId = v1.FamilyId });
        v2.Id.Should().NotBe(v1.Id);
        (await Print(c, inv.Id)).Template!.Html.Should().Contain("V1");
        (await c.PutAsJsonAsync($"/api/print-templates/{v1.Id}", new UpdatePrintTemplateRequest("X", "<p>x</p>")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);                         // old versions are read-only

        var list = (await c.GetFromJsonAsync<List<PrintTemplateSummaryDto>>("/api/print-templates"))!;
        list.Should().ContainSingle().Which.Id.Should().Be(v2.Id);
        (await c.GetFromJsonAsync<List<PrintTemplateSummaryDto>>("/api/print-templates?includeArchived=true"))!.Should().HaveCount(2);

        // New drafts use version 2; switching back to the built-in layout affects drafts only
        var inv2 = await DraftInvoice(c);
        (await Print(c, inv2.Id)).Template!.Html.Should().Contain("V2");
        (await c.PostAsync($"/api/print-templates/use-built-in?documentType={Type}", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Print(c, inv2.Id)).Template.Should().BeNull();
        (await Print(c, inv.Id)).Template!.Html.Should().Contain("V1");

        // Archiving keeps frozen versions printable
        (await c.DeleteAsync($"/api/print-templates/{v2.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await c.GetFromJsonAsync<List<PrintTemplateSummaryDto>>("/api/print-templates"))!.Should().BeEmpty();
        (await Print(c, inv.Id)).Template!.Html.Should().Contain("V1");
    }

    [Fact]
    public async Task Only_one_default_and_a_broken_template_falls_back_to_built_in()
    {
        var c = await NewOrg();
        var a = await Create(c, "A", "<p>A</p>", makeDefault: true);
        var b = await Create(c, "B", "<p>{{ for i in 1..5000 }}x{{ end }}</p>");          // parses, but hits the loop limit
        await Ok<PrintTemplateDto>(await c.PostAsync($"/api/print-templates/{b.Id}/default", null));
        var list = (await c.GetFromJsonAsync<List<PrintTemplateSummaryDto>>("/api/print-templates"))!;
        list.Single(t => t.Id == a.Id).IsDefault.Should().BeFalse();
        list.Single(t => t.Id == b.Id).IsDefault.Should().BeTrue();

        var inv = await DraftInvoice(c);
        var print = await Print(c, inv.Id);
        print.Template.Should().BeNull();
        print.TemplateWarning.Should().Contain("\"B\"").And.Contain("built-in layout");
    }

    [Fact]
    public async Task Only_the_superadmin_manages_templates_and_other_organizations_see_nothing()
    {
        var owner = await NewOrg();
        var t = await Create(owner, "Mine", "<p>{{ invoice.number }}</p>", makeDefault: true);
        var inv = await DraftInvoice(owner);

        var user = await NewUserIn(owner, "User");
        (await user.GetAsync("/api/print-templates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PostAsJsonAsync("/api/print-templates/preview", new PreviewPrintTemplateRequest(Type, "<p>x</p>")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var manager = await NewUserIn(owner, "Manager");
        (await manager.PostAsJsonAsync("/api/print-templates", new CreatePrintTemplateRequest(Type, "M", "<p>x</p>")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var admin = await NewUserIn(owner, "Admin");                                    // Admins edit Company, not layouts
        (await admin.GetAsync("/api/print-templates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync("/api/print-templates/preview", new PreviewPrintTemplateRequest(Type, "<p>x</p>")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsync($"/api/print-templates/{t.Id}/default", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Print(admin, inv.Id)).Template!.Html.Should().Contain(inv.Number);         // …but their prints use it

        var other = await NewOrg();
        (await other.GetAsync($"/api/print-templates/{t.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsync($"/api/print-templates/{t.Id}/default", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync("/api/print-templates/preview", new PreviewPrintTemplateRequest(Type, "<p>{{ invoice.number }}</p>", inv.Id)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);                              // can't preview someone else's invoice
        var otherInv = await DraftInvoice(other);
        (await Print(other, otherInv.Id)).Template.Should().BeNull();                     // owner's default doesn't leak

        var real = await Ok<RenderedTemplateDto>(await owner.PostAsJsonAsync("/api/print-templates/preview",
            new PreviewPrintTemplateRequest(Type, "<p>{{ invoice.number }}</p>", inv.Id)));
        real.Html.Should().Contain(inv.Number);
    }
}
