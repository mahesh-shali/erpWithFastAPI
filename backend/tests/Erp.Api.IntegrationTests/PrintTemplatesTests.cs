using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using Erp.Api.Data;
using FluentAssertions;

namespace Erp.Api.IntegrationTests;

/// <summary>
/// Print formats per page: CRUD, versions, default, freezing on issue, the print screens' format picker, Customer P.O. and
/// Work Order prints, fall-back, rights, tenants and the platform admin who manages every organization.
/// </summary>
[Collection(ApiCollection.Name)]
public class PrintTemplatesTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!";
    private const string Type = "sales-invoices";

    private async Task<HttpClient> NewOrg() => (await NewOrgWithOwner()).Client;

    private async Task<(HttpClient Client, string Email, Guid OrgId)> NewOrgWithOwner()
    {
        var c = factory.CreateClient();
        var email = $"owner{Guid.NewGuid():N}@x.com";
        var res = await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Org " + Guid.NewGuid().ToString("N")[..6], email, Pw, "Owner"));
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var setup = (await c.GetFromJsonAsync<PrintSetupDto>("/api/print-templates/setup"))!;
        return (c, email, setup.CurrentOrganizationId);
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

    private static async Task<PrintTemplateDto> Create(HttpClient c, string name, string body, bool makeDefault = false,
        string page = Type, Guid? org = null) =>
        await Ok<PrintTemplateDto>(await c.PostAsJsonAsync($"/api/print-templates{(org is { } o ? $"?organizationId={o}" : "")}",
            new CreatePrintTemplateRequest(page, name, body, makeDefault)));

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

    private static Task<InvoicePrintDto> Print(HttpClient c, Guid invoiceId, string? format = null) =>
        c.GetFromJsonAsync<InvoicePrintDto>($"/api/sales-invoices/{invoiceId}/print{(format is null ? "" : $"?format={format}")}")!;

    /// <summary>A confirmed P.O. for 2 of a new item, and a draft work order producing it.</summary>
    private static async Task<(CustomerPoDto Po, WorkOrderDto Wo)> PoAndWorkOrder(HttpClient c)
    {
        var item = await Ok<ProductDto>(await c.PostAsJsonAsync("/api/products",
            new UpsertProductRequest("FG-" + Guid.NewGuid().ToString("N")[..5], "Pump <b>", null, 100m, 0, TaxRate: 18m)));
        var cust = await Ok<CustomerDto>(await c.PostAsJsonAsync("/api/customers", new UpsertCustomerRequest("Buyer & Co", null, null, null)));
        var po = await Ok<CustomerPoDto>(await c.PostAsJsonAsync("/api/customer-pos",
            new UpsertCustomerPoRequest(cust.Id, "THEIR/1", null, null, null, [new CustomerPoLineRequest(item.Id, 2, 500m)])));
        var main = (await c.GetFromJsonAsync<List<StoreDto>>("/api/stores"))!.Single(s => s.IsDefault);
        var wo = await Ok<WorkOrderDto>(await c.PostAsJsonAsync("/api/work-orders",
            new UpsertWorkOrderRequest(null, item.Id, 2, main.Id, null, null, 50m, "Test <i>", null)));
        return (po, wo);
    }

    [Fact]
    public async Task Starter_preview_and_validation()
    {
        var c = await NewOrg();
        var starter = await c.GetFromJsonAsync<PrintTemplateStarterDto>($"/api/print-templates/starter?pageKey={Type}");
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
        (await c.PostAsync($"/api/print-templates/use-built-in?pageKey={Type}", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
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

    [Fact]
    public async Task The_print_screens_format_picker_switches_between_formats()
    {
        var c = await NewOrg();
        var a = await Create(c, "Plain", "<p>PLAIN {{ invoice.number }}</p>", makeDefault: true);
        var b = await Create(c, "Letterhead", "<p>LETTER {{ invoice.number }}</p>");
        var inv = await DraftInvoice(c);

        var usual = await Print(c, inv.Id);
        usual.Template!.Html.Should().Contain("PLAIN");
        usual.Formats!.Select(f => f.Name).Should().Equal("Built-in layout", "Plain", "Letterhead");
        usual.Formats!.Single(f => f.IsDefault).Id.Should().Be(a.Id);

        (await Print(c, inv.Id, b.Id.ToString())).Template!.Html.Should().Contain("LETTER");
        (await Print(c, inv.Id, "built-in")).Template.Should().BeNull();
        (await c.GetAsync($"/api/sales-invoices/{inv.Id}/print?format={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await c.GetAsync($"/api/sales-invoices/{inv.Id}/print?format=nonsense")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Issued with "Plain" v1; after an edit the picker offers both the issued version and the current one
        await Ok<SalesInvoiceDto>(await c.PostAsync($"/api/sales-invoices/{inv.Id}/submit", null));
        var a2 = await Ok<PrintTemplateDto>(await c.PutAsJsonAsync($"/api/print-templates/{a.Id}",
            new UpdatePrintTemplateRequest("Plain", "<p>PLAIN2 {{ invoice.number }}</p>")));
        var issued = await Print(c, inv.Id);
        issued.Template!.Html.Should().Contain("PLAIN ").And.NotContain("PLAIN2");
        issued.Formats!.Should().Contain(f => f.Id == a.Id && f.IsIssuedWith && f.Version == 1);
        issued.Formats!.Should().Contain(f => f.Id == a2.Id && f.IsDefault && !f.IsIssuedWith);
        (await Print(c, inv.Id, a2.Id.ToString())).Template!.Html.Should().Contain("PLAIN2");
        (await Print(c, inv.Id, a.Id.ToString())).Template!.Html.Should().Contain("PLAIN ");
    }

    [Fact]
    public async Task Customer_po_and_work_order_print_with_built_in_or_their_own_formats()
    {
        var c = await NewOrg();
        var (po, wo) = await PoAndWorkOrder(c);

        var poBuiltIn = (await c.GetFromJsonAsync<DocumentPrintDto>($"/api/customer-pos/{po.Id}/print"))!;
        poBuiltIn.Template.TemplateId.Should().BeNull();
        poBuiltIn.Template.Html.Should().Contain(po.Number).And.Contain("THEIR/1").And.Contain("Buyer &amp; Co").And.Contain("Pump &lt;b&gt;");
        poBuiltIn.Formats.Should().ContainSingle().Which.IsDefault.Should().BeTrue();
        var woBuiltIn = (await c.GetFromJsonAsync<DocumentPrintDto>($"/api/work-orders/{wo.Id}/print"))!;
        woBuiltIn.Template.Html.Should().Contain(wo.Number).And.Contain("Test &lt;i&gt;");

        // Formats are per page: an invoice format is not offered on the P.O. and vice versa
        await Create(c, "Invoice only", "<p>INV</p>", makeDefault: true);
        var poFormat = await Create(c, "OA", "<h1>OA {{ po.number }} {{ customer.name }} {{ po.line_count }}</h1>", true, "customer-pos");
        var jobSheet = await Create(c, "Job sheet", "<h1>JOB {{ work_order.number }} {{ work_order.item_name }}</h1>", false, "work-orders");
        var poPrint = (await c.GetFromJsonAsync<DocumentPrintDto>($"/api/customer-pos/{po.Id}/print"))!;
        poPrint.Template.Html.Should().Contain($"OA {po.Number} Buyer &amp; Co 1");
        poPrint.Formats.Select(f => f.Name).Should().Equal("Built-in layout", "OA");
        (await c.GetFromJsonAsync<DocumentPrintDto>($"/api/customer-pos/{po.Id}/print?format=built-in"))!.Template.TemplateId.Should().BeNull();
        (await c.GetAsync($"/api/customer-pos/{po.Id}/print?format={jobSheet.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var woPrint = (await c.GetFromJsonAsync<DocumentPrintDto>($"/api/work-orders/{wo.Id}/print"))!;
        woPrint.Template.TemplateId.Should().BeNull();                                     // job sheet isn't the default
        (await c.GetFromJsonAsync<DocumentPrintDto>($"/api/work-orders/{wo.Id}/print?format={jobSheet.Id}"))!
            .Template.Html.Should().Contain($"JOB {wo.Number} Pump &lt;b&gt;");

        // A broken format falls back to the built-in layout with a warning
        await Ok<PrintTemplateDto>(await c.PutAsJsonAsync($"/api/print-templates/{poFormat.Id}",
            new UpdatePrintTemplateRequest("OA", "<p>{{ for i in 1..5000 }}x{{ end }}</p>")));
        var broken = (await c.GetFromJsonAsync<DocumentPrintDto>($"/api/customer-pos/{po.Id}/print"))!;
        broken.Template.TemplateId.Should().BeNull();
        broken.TemplateWarning.Should().Contain("\"OA\"");

        // Previews with sample data and with a real document; the starter of each page
        foreach (var page in new[] { "customer-pos", "work-orders" })
        {
            var starter = (await c.GetFromJsonAsync<PrintTemplateStarterDto>($"/api/print-templates/starter?pageKey={page}"))!;
            var sample = await Ok<RenderedTemplateDto>(await c.PostAsJsonAsync("/api/print-templates/preview",
                new PreviewPrintTemplateRequest(page, starter.Body)));
            sample.Html.Should().Contain(page == "customer-pos" ? "CPO-00017" : "WO-00008");
            var real = await Ok<RenderedTemplateDto>(await c.PostAsJsonAsync("/api/print-templates/preview",
                new PreviewPrintTemplateRequest(page, starter.Body, page == "customer-pos" ? po.Id : wo.Id)));
            real.Html.Should().Contain(page == "customer-pos" ? po.Number : wo.Number);
        }
        (await c.GetAsync("/api/print-templates/starter?pageKey=purchase-orders")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Printing needs read on the document's page only
        var staff = await NewUserIn(c, "User");
        (await staff.GetAsync($"/api/work-orders/{wo.Id}/print")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await NewOrg()).GetAsync($"/api/customer-pos/{po.Id}/print").Result.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Setup_lists_the_menu_as_module_group_page_with_printable_pages()
    {
        var c = await NewOrg();
        await Create(c, "OA", "<p>x</p>", page: "customer-pos");
        var setup = (await c.GetFromJsonAsync<PrintSetupDto>("/api/print-templates/setup"))!;
        setup.IsPlatformAdmin.Should().BeFalse();
        setup.Organizations.Should().ContainSingle().Which.Id.Should().Be(setup.CurrentOrganizationId);
        var pages = setup.Modules.SelectMany(m => m.Groups.SelectMany(g => g.Pages.Select(p => (Module: m.Key, Group: g.Key, Page: p)))).ToList();
        var cpo = pages.Single(p => p.Page.Key == "customer-pos");
        (cpo.Module, cpo.Group, cpo.Page.Printable, cpo.Page.FormatCount).Should().Be(("sales", "sales-orders", true, 1));
        pages.Where(p => p.Page.Printable).Select(p => p.Page.Key).Should().BeEquivalentTo("customer-pos", "sales-invoices", "work-orders");
        pages.Single(p => p.Page.Key == "purchase-orders").Page.Printable.Should().BeFalse();
    }

    [Fact]
    public async Task A_platform_admin_manages_the_formats_of_any_organization()
    {
        var (pa, paEmail, paOrg) = await NewOrgWithOwner();
        var (owner, _, org) = await NewOrgWithOwner();
        var (po, _) = await PoAndWorkOrder(owner);

        // Not yet a platform admin: another organization is off limits
        (await pa.GetAsync($"/api/print-templates?organizationId={org}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await pa.GetAsync($"/api/print-templates/setup?organizationId={org}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await DatabaseInitializer.SetPlatformAdminAsync(factory.Services, paEmail, true);
        var setup = (await pa.GetFromJsonAsync<PrintSetupDto>($"/api/print-templates/setup?organizationId={org}"))!;
        setup.IsPlatformAdmin.Should().BeTrue();
        setup.CurrentOrganizationId.Should().Be(org);
        setup.Organizations.Select(o => o.Id).Should().Contain([org, paOrg]);

        // Creates a default P.O. format in the other organization: its documents print with it, the admin's own org doesn't
        var t = await Create(pa, "Their OA", "<h1>THEIRS {{ po.number }} {{ company.name }}</h1>", true, "customer-pos", org);
        (await owner.GetFromJsonAsync<List<PrintTemplateSummaryDto>>("/api/print-templates"))!.Should().ContainSingle(x => x.Id == t.Id);
        (await owner.GetFromJsonAsync<DocumentPrintDto>($"/api/customer-pos/{po.Id}/print"))!.Template.Html.Should().Contain($"THEIRS {po.Number}");
        (await pa.GetFromJsonAsync<List<PrintTemplateSummaryDto>>("/api/print-templates"))!.Should().BeEmpty();
        (await pa.GetAsync($"/api/print-templates/{t.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);     // not in own org
        (await pa.GetFromJsonAsync<PrintTemplateDto>($"/api/print-templates/{t.Id}?organizationId={org}"))!.Name.Should().Be("Their OA");
        var v2 = await Ok<PrintTemplateDto>(await pa.PutAsJsonAsync($"/api/print-templates/{t.Id}?organizationId={org}",
            new UpdatePrintTemplateRequest("Their OA", "<h1>V2</h1>")));
        v2.Version.Should().Be(2);
        (await pa.PostAsync($"/api/print-templates/use-built-in?pageKey=customer-pos&organizationId={org}", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Previews of another organization use sample data only (with that organization's company name)
        var sample = await Ok<RenderedTemplateDto>(await pa.PostAsJsonAsync($"/api/print-templates/preview?organizationId={org}",
            new PreviewPrintTemplateRequest("customer-pos", "<p>{{ company.name }}</p>")));
        sample.Html.Should().StartWith("<div class=\"print-root\"><p>Org ");
        (await pa.PostAsJsonAsync($"/api/print-templates/preview?organizationId={org}",
            new PreviewPrintTemplateRequest("customer-pos", "<p>x</p>", po.Id))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await pa.GetAsync($"/api/print-templates?organizationId={Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The owner of the other organization is no platform admin; revoking takes effect at once
        (await owner.GetAsync($"/api/print-templates?organizationId={paOrg}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await DatabaseInitializer.SetPlatformAdminAsync(factory.Services, paEmail, false);
        (await pa.GetAsync($"/api/print-templates?organizationId={org}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
