using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using FluentAssertions;

namespace Erp.Api.IntegrationTests;

/// <summary>
/// The make-to-order sales flow end to end: item and customer masters, Customer P.O. → Work Order (production) →
/// Sales Invoice (+ print), stock details, cancellations, rights and tenant isolation.
/// </summary>
[Collection(ApiCollection.Name)]
public class SalesFlowTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!";

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

    private static async Task<string> Error(HttpResponseMessage res, HttpStatusCode expected)
    {
        res.StatusCode.Should().Be(expected, await res.Content.ReadAsStringAsync());
        return await res.Content.ReadAsStringAsync();
    }

    private static Task<ProductDto> Item(HttpClient c, string sku, string type = "Finished Good", decimal price = 500m, decimal tax = 18m,
        int reorder = 0) =>
        c.PostAsJsonAsync("/api/products", new UpsertProductRequest(sku, "Item " + sku, null, price, 0, null, type, "desc", "8481", tax,
            100m, reorder)).ContinueWith(t => Ok<ProductDto>(t.Result)).Unwrap();

    private static Task<CustomerDto> Customer(HttpClient c, string? state = "Maharashtra") =>
        c.PostAsJsonAsync("/api/customers", new UpsertCustomerRequest("Acme " + Guid.NewGuid().ToString("N")[..4], "buy@acme.test", "Pune", "India",
            "+91 98000 00000", "Ravi", "27abcde1234f1z5", "Plot 7, MIDC", state, "411019", PaymentTermsDays: 30))
            .ContinueWith(t => Ok<CustomerDto>(t.Result)).Unwrap();

    private static async Task<StoreDto> MainStore(HttpClient c) =>
        (await c.GetFromJsonAsync<List<StoreDto>>("/api/stores"))!.Single(s => s.IsDefault);

    private static async Task Receive(HttpClient c, Guid store, Guid product, int qty, decimal rate)
    {
        var e = await Ok<StockEntryDto>(await c.PostAsJsonAsync("/api/stock-entries",
            new UpsertStockEntryRequest("Receipt", null, null, store, null, [new StockEntryLineRequest(product, qty, rate)])));
        await Ok<StockEntryDto>(await c.PostAsync($"/api/stock-entries/{e.Id}/submit", null));
    }

    private static async Task<CustomerPoDto> ConfirmedPo(HttpClient c, Guid customer, params CustomerPoLineRequest[] lines)
    {
        var po = await Ok<CustomerPoDto>(await c.PostAsJsonAsync("/api/customer-pos",
            new UpsertCustomerPoRequest(customer, "PO-" + Guid.NewGuid().ToString("N")[..6], null, null, null, [.. lines])));
        return await Ok<CustomerPoDto>(await c.PostAsync($"/api/customer-pos/{po.Id}/confirm", null));
    }

    private static Task<HttpResponseMessage> DraftWo(HttpClient c, Guid poLine, int qty, Guid store, decimal otherCost = 0,
        params WorkOrderMaterialRequest[] materials) =>
        c.PostAsJsonAsync("/api/work-orders", new UpsertWorkOrderRequest(poLine, null, qty, store, null, null, otherCost, null, [.. materials]));

    private static async Task<WorkOrderDto> ProducedWo(HttpClient c, Guid poLine, int qty, Guid store, decimal otherCost = 0,
        params WorkOrderMaterialRequest[] materials)
    {
        var wo = await Ok<WorkOrderDto>(await DraftWo(c, poLine, qty, store, otherCost, materials));
        return await Ok<WorkOrderDto>(await c.PostAsync($"/api/work-orders/{wo.Id}/submit", null));
    }

    private static Task<HttpResponseMessage> DraftInvoice(HttpClient c, CustomerPoDto po, Guid store, params (Guid Line, int Qty)[] lines) =>
        c.PostAsJsonAsync("/api/sales-invoices", new UpsertSalesInvoiceRequest(null, po.Id, null, null, store, null, null,
            lines.Select(l => new SalesInvoiceLineRequest(l.Line, null, l.Qty, po.Lines.Single(x => x.Id == l.Line).Rate)).ToList()));

    private static async Task<StockDetailsRow> Details(HttpClient c, Guid product) =>
        (await c.GetFromJsonAsync<StockItemDetailsDto>($"/api/stock/details/{product}"))!.Summary;

    // ---------------- Masters ----------------

    [Fact]
    public async Task Items_keep_every_detail_and_inactive_items_leave_the_pickers()
    {
        var c = await NewOrg();
        var created = await Ok<ProductDto>(await c.PostAsJsonAsync("/api/products", new UpsertProductRequest("VAL-1", "Ball valve 2in", "Valves",
            1250m, 0, null, "Finished Good", "Forged brass", "8481", 18m, 700m, 5, 7, "DRG-101")));
        var got = await c.GetFromJsonAsync<ProductDto>($"/api/products/{created.Id}");
        got.Should().BeEquivalentTo(new
        {
            Sku = "VAL-1", ItemType = "Finished Good", Description = "Forged brass", HsnCode = "8481", TaxRate = 18m,
            StandardCost = 700m, ReorderLevel = 5, LeadTimeDays = 7, DrawingNo = "DRG-101", IsActive = true,
        });
        (await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products?search=DRG-101"))!.Items.Should().ContainSingle();
        (await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products?itemType=Raw%20Material"))!.Items.Should().BeEmpty();

        await Error(await c.PostAsJsonAsync("/api/products", new UpsertProductRequest("BAD", "Bad", null, 1m, 0, null, "Gadget")),
            HttpStatusCode.BadRequest);

        await Ok<ProductDto>(await c.PutAsJsonAsync($"/api/products/{created.Id}", new UpsertProductRequest("VAL-1", "Ball valve 2in", "Valves",
            1250m, 0, null, "Finished Good", IsActive: false)));
        (await c.GetFromJsonAsync<List<ItemLookupDto>>("/api/stock/items"))!.Should().NotContain(i => i.Id == created.Id);
    }

    [Fact]
    public async Task Customers_can_be_edited_and_with_documents_cannot_be_deleted()
    {
        var c = await NewOrg();
        var cust = await Customer(c);
        cust.TaxId.Should().Be("27ABCDE1234F1Z5");                                  // GSTIN upper-cased
        var edited = await Ok<CustomerDto>(await c.PutAsJsonAsync($"/api/customers/{cust.Id}",
            new UpsertCustomerRequest(cust.Name, null, "Mumbai", "India", State: "Maharashtra", PaymentTermsDays: 45)));
        edited.Should().BeEquivalentTo(new { City = "Mumbai", PaymentTermsDays = 45, Email = (string?)null });

        var item = await Item(c, "C-1");
        await ConfirmedPo(c, cust.Id, new CustomerPoLineRequest(item.Id, 1, 10m));
        (await Error(await c.DeleteAsync($"/api/customers/{cust.Id}"), HttpStatusCode.Conflict)).Should().Contain("P.O.s or invoices");
    }

    [Fact]
    public async Task Company_details_are_read_by_everyone_and_changed_only_by_admins()
    {
        var owner = await NewOrg();
        var company = await Ok<CompanyDto>(await owner.PutAsJsonAsync("/api/company",
            new UpdateCompanyRequest("Shali Valves Pvt Ltd", "Gat 12, Chakan", "Maharashtra", "020-123456", "accounts@shali.test", "27aaacs1234a1z9")));
        company.TaxId.Should().Be("27AAACS1234A1Z9");
        var user = await NewUserIn(owner, "User");
        (await user.GetFromJsonAsync<CompanyDto>("/api/company"))!.Name.Should().Be("Shali Valves Pvt Ltd");
        (await user.PutAsJsonAsync("/api/company", new UpdateCompanyRequest("Hacked", null, null, null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------- The flow ----------------

    [Fact]
    public async Task Po_to_production_to_invoice_moves_stock_and_completes_the_po()
    {
        var c = await NewOrg();
        await Ok<CompanyDto>(await c.PutAsJsonAsync("/api/company", new UpdateCompanyRequest("Maker Ltd", "Chakan", "Maharashtra", null, null, "27AAACM0000A1Z1")));
        var main = await MainStore(c);
        var fg = await Item(c, "FG-1", price: 500m, tax: 18m, reorder: 2);
        var rm = await Item(c, "RM-1", "Raw Material", price: 60m, tax: 18m);
        await Receive(c, main.Id, rm.Id, 100, 50m);
        var cust = await Customer(c, "Karnataka");                                     // other state: IGST

        // 1) Customer P.O.: 10 × 500 + 18% GST
        var draft = await Ok<CustomerPoDto>(await c.PostAsJsonAsync("/api/customer-pos",
            new UpsertCustomerPoRequest(cust.Id, "ACME/77", null, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10), "urgent",
                [new CustomerPoLineRequest(fg.Id, 10, 500m)])));
        draft.Should().BeEquivalentTo(new { Status = "Draft", SubTotal = 5000m, TaxTotal = 900m, GrandTotal = 5900m });
        draft.Number.Should().MatchRegex(@"^CPO-\d{5}$");
        await Error(await c.PostAsJsonAsync("/api/customer-pos",                       // same customer P.O. number twice
            new UpsertCustomerPoRequest(cust.Id, "ACME/77", null, null, null, [new CustomerPoLineRequest(fg.Id, 1, 1m)])), HttpStatusCode.Conflict);
        var line = draft.Lines.Single();
        (await Error(await DraftWo(c, line.Id, 1, main.Id), HttpStatusCode.BadRequest)).Should().Contain("confirmed P.O.");

        var po = await Ok<CustomerPoDto>(await c.PostAsync($"/api/customer-pos/{draft.Id}/confirm", null));
        po.Status.Should().Be("Confirmed");
        (await Details(c, fg.Id)).Should().BeEquivalentTo(new { OnHand = 0, Committed = 10, Available = -10, Status = "Short" });

        // 2) Production: 6 made from 12 RM (@50 = 600) + 300 labour = 900 → 150 each
        var wo = await ProducedWo(c, line.Id, 6, main.Id, 300m, new WorkOrderMaterialRequest(rm.Id, 12));
        wo.Should().BeEquivalentTo(new { Status = "Submitted", MaterialCost = 600m, UnitCost = 150m, TotalCost = 900m });
        wo.Number.Should().MatchRegex(@"^WO-\d{5}$");
        (await Error(await DraftWo(c, line.Id, 5, main.Id), HttpStatusCode.Created)).Should().NotBeEmpty();   // a draft for 5 is fine…
        var tooMany = (await c.GetFromJsonAsync<PagedResult<WorkOrderSummaryDto>>($"/api/work-orders?customerPoId={po.Id}&status=Draft"))!.Items.Single();
        (await Error(await c.PostAsync($"/api/work-orders/{tooMany.Id}/submit", null), HttpStatusCode.BadRequest))
            .Should().Contain("Only 4 more can be produced");                         // …but only 4 are left to make
        (await c.DeleteAsync($"/api/work-orders/{tooMany.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var stock = await Details(c, fg.Id);
        stock.Should().BeEquivalentTo(new { OnHand = 6, StockValue = 900m, Committed = 10, Available = -4 });
        (await Details(c, rm.Id)).OnHand.Should().Be(88);
        po = (await c.GetFromJsonAsync<CustomerPoDto>($"/api/customer-pos/{po.Id}"))!;
        po.Lines.Single().Should().BeEquivalentTo(new { ProducedQty = 6, ToProduce = 4, InvoicedQty = 0, ToInvoice = 10 });
        po.WorkOrders.Should().ContainSingle(w => w.Status == "Submitted");

        // 3) Invoice the 6 made so far
        var inv1 = await Ok<SalesInvoiceDto>(await DraftInvoice(c, po, main.Id, (line.Id, 6)));
        inv1.Should().BeEquivalentTo(new { Status = "Draft", SubTotal = 3000m, TaxTotal = 540m, GrandTotal = 3540m, InterState = true,
            BillingName = cust.Name, BillingTaxId = "27ABCDE1234F1Z5", CustomerPoRef = "ACME/77" });
        inv1.DueDate.Should().Be(inv1.InvoiceDate.AddDays(30));                        // customer's payment terms
        inv1.Number.Should().MatchRegex(@"^SI-\d{5}$");
        await Error(await DraftInvoice(c, po, main.Id, (line.Id, 11)), HttpStatusCode.BadRequest);   // more than ordered
        inv1 = await Ok<SalesInvoiceDto>(await c.PostAsync($"/api/sales-invoices/{inv1.Id}/submit", null));
        inv1.Should().BeEquivalentTo(new { Status = "Submitted", CostOfGoods = 900m });
        inv1.Lines.Single().CostRate.Should().Be(150m);
        (await Details(c, fg.Id)).Should().BeEquivalentTo(new { OnHand = 0, Committed = 4, Available = -4 });

        var print = await c.GetFromJsonAsync<InvoicePrintDto>($"/api/sales-invoices/{inv1.Id}/print");
        print!.Company.Name.Should().Be("Maker Ltd");
        print.Invoice.AmountInWords.Should().Be("Rupees Three Thousand Five Hundred Forty Only");
        print.Invoice.TaxSummary.Should().ContainSingle().Which.Should().Be(new TaxSummaryRow(18m, 3000m, 540m));

        // 4) Make and invoice the remaining 4: the P.O. completes
        await ProducedWo(c, line.Id, 4, main.Id, 0m, new WorkOrderMaterialRequest(rm.Id, 8));   // 8 × 50 / 4 = 100 each
        var inv2 = await Ok<SalesInvoiceDto>(await DraftInvoice(c, po, main.Id, (line.Id, 4)));
        await Ok<SalesInvoiceDto>(await c.PostAsync($"/api/sales-invoices/{inv2.Id}/submit", null));
        po = (await c.GetFromJsonAsync<CustomerPoDto>($"/api/customer-pos/{po.Id}"))!;
        po.Status.Should().Be("Completed");
        po.Invoices.Should().HaveCount(2);
        (await Details(c, fg.Id)).Should().BeEquivalentTo(new { OnHand = 0, Committed = 0, Available = 0 });

        // 5) Cancelling an invoice puts the stock back and re-opens the P.O.
        await Ok<SalesInvoiceDto>(await c.PostAsync($"/api/sales-invoices/{inv2.Id}/cancel", null));
        po = (await c.GetFromJsonAsync<CustomerPoDto>($"/api/customer-pos/{po.Id}"))!;
        po.Status.Should().Be("Confirmed");
        po.Lines.Single().InvoicedQty.Should().Be(6);
        (await Details(c, fg.Id)).Should().BeEquivalentTo(new { OnHand = 4, Committed = 4 });

        var ledger = (await c.GetFromJsonAsync<PagedResult<StockLedgerRow>>($"/api/stock/ledger?productId={fg.Id}"))!.Items;
        ledger.Select(l => l.VoucherType).Distinct().Should().BeEquivalentTo("Work Order", "Sales Invoice");
        ledger.First().IsReversal.Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_a_work_order_returns_materials_and_is_refused_once_the_goods_are_sold()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var fg = await Item(c, "FG-2");
        var rm = await Item(c, "RM-2", "Raw Material");
        await Receive(c, main.Id, rm.Id, 10, 20m);
        var cust = await Customer(c);
        var po = await ConfirmedPo(c, cust.Id, new CustomerPoLineRequest(fg.Id, 5, 100m));
        var line = po.Lines.Single();

        // Materials can't go below zero
        var big = await Ok<WorkOrderDto>(await DraftWo(c, line.Id, 5, main.Id, 0m, new WorkOrderMaterialRequest(rm.Id, 11)));
        (await Error(await c.PostAsync($"/api/work-orders/{big.Id}/submit", null), HttpStatusCode.BadRequest)).Should().Contain("Insufficient stock for RM-2");
        await c.DeleteAsync($"/api/work-orders/{big.Id}");

        var wo = await ProducedWo(c, line.Id, 5, main.Id, 0m, new WorkOrderMaterialRequest(rm.Id, 10));
        (await Error(await c.PostAsync($"/api/customer-pos/{po.Id}/cancel", null), HttpStatusCode.BadRequest)).Should().Contain("work orders or invoices");

        await Ok<WorkOrderDto>(await c.PostAsync($"/api/work-orders/{wo.Id}/cancel", null));
        (await Details(c, rm.Id)).OnHand.Should().Be(10);
        (await Details(c, fg.Id)).OnHand.Should().Be(0);
        (await c.GetFromJsonAsync<CustomerPoDto>($"/api/customer-pos/{po.Id}"))!.Lines.Single().ProducedQty.Should().Be(0);

        // Sold goods can't be "un-produced"
        var wo2 = await ProducedWo(c, line.Id, 5, main.Id, 0m, new WorkOrderMaterialRequest(rm.Id, 10));
        var inv = await Ok<SalesInvoiceDto>(await DraftInvoice(c, po, main.Id, (line.Id, 5)));
        await Ok<SalesInvoiceDto>(await c.PostAsync($"/api/sales-invoices/{inv.Id}/submit", null));
        (await Error(await c.PostAsync($"/api/work-orders/{wo2.Id}/cancel", null), HttpStatusCode.BadRequest)).Should().Contain("Insufficient stock");

        // With everything cancelled the P.O. itself can be cancelled
        await Ok<SalesInvoiceDto>(await c.PostAsync($"/api/sales-invoices/{inv.Id}/cancel", null));
        await Ok<WorkOrderDto>(await c.PostAsync($"/api/work-orders/{wo2.Id}/cancel", null));
        (await Ok<CustomerPoDto>(await c.PostAsync($"/api/customer-pos/{po.Id}/cancel", null))).Status.Should().Be("Cancelled");
        (await Error(await DraftInvoice(c, po, main.Id, (line.Id, 1)), HttpStatusCode.BadRequest)).Should().Contain("cancelled");
    }

    [Fact]
    public async Task Invoices_without_a_po_and_insufficient_stock_is_refused()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var item = await Ok<ProductDto>(await c.PostAsJsonAsync("/api/products", new UpsertProductRequest("TR-9", "Trading item", null, 200m, 3,
            TaxRate: 12m)));
        var cust = await Customer(c);
        var inv = await Ok<SalesInvoiceDto>(await c.PostAsJsonAsync("/api/sales-invoices", new UpsertSalesInvoiceRequest(cust.Id, null, null, null,
            main.Id, "Site 4", null, [new SalesInvoiceLineRequest(null, item.Id, 5, 200m, DiscountPercent: 10m)])));
        inv.Should().BeEquivalentTo(new { SubTotal = 1000m, DiscountTotal = 100m, TaxTotal = 108m, GrandTotal = 1008m, InterState = false });
        (await Error(await c.PostAsync($"/api/sales-invoices/{inv.Id}/submit", null), HttpStatusCode.BadRequest)).Should().Contain("Insufficient stock for TR-9");

        var fixedInv = await Ok<SalesInvoiceDto>(await c.PutAsJsonAsync($"/api/sales-invoices/{inv.Id}", new UpsertSalesInvoiceRequest(cust.Id, null,
            null, null, main.Id, null, null, [new SalesInvoiceLineRequest(null, item.Id, 3, 200m)])));
        (await Ok<SalesInvoiceDto>(await c.PostAsync($"/api/sales-invoices/{fixedInv.Id}/submit", null))).Status.Should().Be("Submitted");
        (await c.PutAsJsonAsync($"/api/sales-invoices/{inv.Id}", new UpsertSalesInvoiceRequest(cust.Id, null, null, null, main.Id, null, null,
            [new SalesInvoiceLineRequest(null, item.Id, 1, 1m)]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);   // submitted: read-only
        (await c.DeleteAsync($"/api/sales-invoices/{inv.Id}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------------- Rights & tenants ----------------

    [Fact]
    public async Task Users_raise_pos_but_not_invoices_or_work_orders_and_other_tenants_see_nothing()
    {
        var owner = await NewOrg();
        var main = await MainStore(owner);
        var fg = await Item(owner, "RT-1");
        var cust = await Customer(owner);
        var user = await NewUserIn(owner, "User");

        var po = await Ok<CustomerPoDto>(await user.PostAsJsonAsync("/api/customer-pos",
            new UpsertCustomerPoRequest(cust.Id, "U-1", null, null, null, [new CustomerPoLineRequest(fg.Id, 2, 10m)])));
        (await user.GetAsync("/api/sales/lookups/customers")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.DeleteAsync($"/api/customer-pos/{po.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);   // no Delete right
        (await user.GetAsync("/api/sales-invoices")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PostAsJsonAsync("/api/work-orders", new UpsertWorkOrderRequest(null, fg.Id, 1, main.Id, null, null, 0m, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.GetAsync("/api/stock/details")).StatusCode.Should().Be(HttpStatusCode.OK);

        var other = await NewOrg();
        (await other.GetAsync($"/api/customer-pos/{po.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetFromJsonAsync<PagedResult<CustomerPoSummaryDto>>("/api/customer-pos"))!.Items.Should().BeEmpty();
        (await other.PostAsync($"/api/customer-pos/{po.Id}/confirm", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.PostAsJsonAsync("/api/customer-pos", new UpsertCustomerPoRequest(cust.Id, "X", null, null, null,
            [new CustomerPoLineRequest(fg.Id, 1, 1m)]))).StatusCode.Should().Be(HttpStatusCode.NotFound);   // the customer is not theirs
    }

    [Fact]
    public async Task Production_for_stock_without_a_po_values_the_item_at_material_cost()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var wh = await Ok<StoreDto>(await c.PostAsJsonAsync("/api/stores", new UpsertStoreRequest("RMS", "RM Store", null)));
        var fg = await Item(c, "MS-1");
        var rm1 = await Item(c, "MS-R1", "Raw Material");
        var rm2 = await Item(c, "MS-R2", "Consumable");
        await Receive(c, wh.Id, rm1.Id, 10, 30m);
        await Receive(c, wh.Id, rm2.Id, 10, 5m);

        var wo = await Ok<WorkOrderDto>(await c.PostAsJsonAsync("/api/work-orders", new UpsertWorkOrderRequest(null, fg.Id, 3, main.Id, wh.Id, null, 15m,
            "batch 1", [new WorkOrderMaterialRequest(rm1.Id, 3), new WorkOrderMaterialRequest(rm2.Id, 6)])));
        (await Details(c, fg.Id)).InProduction.Should().Be(3);
        wo = await Ok<WorkOrderDto>(await c.PostAsync($"/api/work-orders/{wo.Id}/submit", null));
        wo.Should().BeEquivalentTo(new { MaterialCost = 120m, UnitCost = 45m, TotalCost = 135m, CustomerPoId = (Guid?)null });   // (90 + 30 + 15) / 3
        var detail = await c.GetFromJsonAsync<StockItemDetailsDto>($"/api/stock/details/{fg.Id}");
        detail!.Stores.Should().ContainSingle().Which.Should().BeEquivalentTo(new { StoreId = main.Id, Quantity = 3, ValuationRate = 45m });
        detail.RecentMovements.Should().ContainSingle(m => m.VoucherNo == wo.Number);
        await Error(await c.PostAsJsonAsync("/api/work-orders", new UpsertWorkOrderRequest(null, fg.Id, 1, main.Id, null, null, 0m, null,
            [new WorkOrderMaterialRequest(fg.Id, 1)])), HttpStatusCode.BadRequest);  // an item can't consume itself
    }
}
