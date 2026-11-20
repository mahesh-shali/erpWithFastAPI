using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using FluentAssertions;

namespace Erp.Api.IntegrationTests;

/// <summary>Inventory module end to end: masters, stock entries, valuation, ledger, orders, rights, tenants.</summary>
[Collection(ApiCollection.Name)]
public class InventoryTests(ApiFactory factory)
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

    private static async Task<ProductDto> Product(HttpClient c, string sku, int stock = 0, decimal price = 100m) =>
        await Ok<ProductDto>(await c.PostAsJsonAsync("/api/products", new UpsertProductRequest(sku, "Item " + sku, "Tools", price, stock)));

    private static async Task<StoreDto> Store(HttpClient c, string code) =>
        await Ok<StoreDto>(await c.PostAsJsonAsync("/api/stores", new UpsertStoreRequest(code, "Store " + code, "Pune")));

    private static async Task<StoreDto> MainStore(HttpClient c) =>
        (await c.GetFromJsonAsync<List<StoreDto>>("/api/stores"))!.Single(s => s.IsDefault);

    private static Task<StockEntryDto> Draft(HttpClient c, string type, Guid? source, Guid? target, params StockEntryLineRequest[] lines) =>
        c.PostAsJsonAsync("/api/stock-entries", new UpsertStockEntryRequest(type, null, source, target, "test", [.. lines]))
            .ContinueWith(t => Ok<StockEntryDto>(t.Result)).Unwrap();

    private static async Task<StockEntryDto> Submit(HttpClient c, Guid id) =>
        await Ok<StockEntryDto>(await c.PostAsync($"/api/stock-entries/{id}/submit", null));

    private static async Task<StockPositionRow?> Balance(HttpClient c, Guid product, Guid store) =>
        (await c.GetFromJsonAsync<StockPositionResult>($"/api/stock/position?storeId={store}&pageSize=200"))!
            .Items.SingleOrDefault(r => r.ProductId == product);

    // ---------------- Masters ----------------

    [Fact]
    public async Task Every_organization_gets_a_Main_Store_that_cannot_be_removed()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        main.Should().BeEquivalentTo(new { Code = "MAIN", Name = "Main Store", IsDefault = true, IsActive = true });
        (await c.DeleteAsync($"/api/stores/{main.Id}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.PutAsJsonAsync($"/api/stores/{main.Id}", new UpsertStoreRequest("MAIN", "Main", null, IsActive: false, IsDefault: true)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stores_codes_are_unique_default_can_move_and_unused_stores_can_be_deleted()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var wh = await Store(c, "wh-2");
        wh.Code.Should().Be("WH-2");                                                  // codes are upper-cased
        (await c.PostAsJsonAsync("/api/stores", new UpsertStoreRequest("WH-2", "Dup", null))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await Ok<StoreDto>(await c.PutAsJsonAsync($"/api/stores/{wh.Id}", new UpsertStoreRequest("WH-2", "Warehouse 2", null, true, IsDefault: true)));
        var stores = (await c.GetFromJsonAsync<List<StoreDto>>("/api/stores"))!;
        stores.Single(s => s.IsDefault).Id.Should().Be(wh.Id);
        stores.Single(s => s.Id == main.Id).IsDefault.Should().BeFalse();

        var spare = await Store(c, "SPARE");
        (await c.DeleteAsync($"/api/stores/{spare.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Units_of_measure_crud_and_products_carry_their_unit()
    {
        var c = await NewOrg();
        var nos = await Ok<UomDto>(await c.PostAsJsonAsync("/api/uoms", new UpsertUomRequest("nos", "Numbers")));
        nos.Code.Should().Be("NOS");
        (await c.PostAsJsonAsync("/api/uoms", new UpsertUomRequest("NOS", "Again"))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var p = await Ok<ProductDto>(await c.PostAsJsonAsync("/api/products",
            new UpsertProductRequest("U-1", "Bolt", null, 5m, 0, nos.Id)));
        p.Uom.Should().Be("NOS");
        (await c.DeleteAsync($"/api/uoms/{nos.Id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);  // in use

        await Ok<UomDto>(await c.PutAsJsonAsync($"/api/uoms/{nos.Id}", new UpsertUomRequest("NOS", "Numbers", IsActive: false)));
        (await c.GetFromJsonAsync<List<UomDto>>("/api/uoms"))!.Should().BeEmpty();                   // active only
        (await c.GetFromJsonAsync<List<UomDto>>("/api/uoms?all=true"))!.Should().ContainSingle();
    }

    // ---------------- Stock entries & valuation ----------------

    [Fact]
    public async Task Receipts_average_the_rate_issues_go_out_at_it_and_the_ledger_records_both()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var p = await Product(c, "MA-1");

        var r1 = await Draft(c, "Receipt", null, main.Id, new StockEntryLineRequest(p.Id, 10, 100m));
        r1.Status.Should().Be("Draft");
        r1.Number.Should().MatchRegex(@"^SE-\d{5}$");
        (await Balance(c, p.Id, main.Id)).Should().BeNull();                       // a draft moves nothing
        await Submit(c, r1.Id);

        var r2 = await Draft(c, "Receipt", null, main.Id, new StockEntryLineRequest(p.Id, 20, 120m));
        (await Submit(c, r2.Id)).TotalValue.Should().Be(2400m);
        var after = (await Balance(c, p.Id, main.Id))!;
        after.Quantity.Should().Be(30);
        after.ValuationRate.Should().Be(113.3333m);
        after.StockValue.Should().Be(3400m);

        var issue = await Submit(c, (await Draft(c, "Issue", main.Id, null, new StockEntryLineRequest(p.Id, 15, null))).Id);
        issue.Lines.Single().Rate.Should().Be(113.3333m);
        issue.TotalValue.Should().Be(1700m);
        (await Balance(c, p.Id, main.Id))!.Should().BeEquivalentTo(new { Quantity = 15, StockValue = 1700m });

        // products.stock_quantity follows the stores
        (await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products"))!.Items.Single().StockQuantity.Should().Be(15);

        var ledger = (await c.GetFromJsonAsync<PagedResult<StockLedgerRow>>($"/api/stock/ledger?productId={p.Id}"))!;
        ledger.Items.Select(l => (l.QtyChange, l.QtyAfter)).Should().Equal((-15, 15), (20, 30), (10, 10)); // newest first
        ledger.Items.Should().OnlyContain(l => l.VoucherType == "Stock Entry");
    }

    [Fact]
    public async Task Transfer_moves_stock_and_value_between_stores()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var wh = await Store(c, "WH");
        var p = await Product(c, "TR-1", stock: 10, price: 50m);                    // opening 10 @ 50 in Main Store

        var t = await Submit(c, (await Draft(c, "Transfer", main.Id, wh.Id, new StockEntryLineRequest(p.Id, 4, null))).Id);
        t.TotalValue.Should().Be(200m);
        (await Balance(c, p.Id, main.Id))!.Should().BeEquivalentTo(new { Quantity = 6, StockValue = 300m });
        (await Balance(c, p.Id, wh.Id))!.Should().BeEquivalentTo(new { Quantity = 4, ValuationRate = 50m, StockValue = 200m });
        (await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products"))!.Items.Single().StockQuantity.Should().Be(10);
    }

    [Fact]
    public async Task Issuing_more_than_the_store_holds_is_refused_and_nothing_moves()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var a = await Product(c, "NG-A", stock: 5);
        var b = await Product(c, "NG-B", stock: 1);
        var draft = await Draft(c, "Issue", main.Id, null, new StockEntryLineRequest(a.Id, 2, null), new StockEntryLineRequest(b.Id, 3, null));

        var res = await c.PostAsync($"/api/stock-entries/{draft.Id}/submit", null);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("Insufficient stock for NG-B");
        (await Balance(c, a.Id, main.Id))!.Quantity.Should().Be(5);                 // whole entry rolled back
        (await c.GetFromJsonAsync<StockEntryDto>($"/api/stock-entries/{draft.Id}"))!.Status.Should().Be("Draft");
    }

    [Fact]
    public async Task Cancel_reverses_exactly_and_is_refused_once_the_stock_is_used()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var p = await Product(c, "CX-1", stock: 10, price: 100m);
        var receipt = await Submit(c, (await Draft(c, "Receipt", null, main.Id, new StockEntryLineRequest(p.Id, 10, 200m))).Id);
        (await Balance(c, p.Id, main.Id))!.ValuationRate.Should().Be(150m);

        var cancelled = await Ok<StockEntryDto>(await c.PostAsync($"/api/stock-entries/{receipt.Id}/cancel", null));
        cancelled.Status.Should().Be("Cancelled");
        (await Balance(c, p.Id, main.Id))!.Should().BeEquivalentTo(new { Quantity = 10, ValuationRate = 100m, StockValue = 1000m });
        (await c.PostAsync($"/api/stock-entries/{receipt.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A receipt whose stock was already issued can't be cancelled
        var r2 = await Submit(c, (await Draft(c, "Receipt", null, main.Id, new StockEntryLineRequest(p.Id, 5, 100m))).Id);
        await Submit(c, (await Draft(c, "Issue", main.Id, null, new StockEntryLineRequest(p.Id, 15, null))).Id);
        (await c.PostAsync($"/api/stock-entries/{r2.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Drafts_can_be_edited_and_deleted_submitted_entries_cannot()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var p = await Product(c, "DR-1");
        var d = await Draft(c, "Receipt", null, main.Id, new StockEntryLineRequest(p.Id, 1, 10m));

        var edited = await Ok<StockEntryDto>(await c.PutAsJsonAsync($"/api/stock-entries/{d.Id}",
            new UpsertStockEntryRequest("Receipt", null, null, main.Id, "edited", [new StockEntryLineRequest(p.Id, 3, 10m)])));
        edited.Lines.Single().Quantity.Should().Be(3);
        edited.TotalValue.Should().Be(30m);

        await Submit(c, d.Id);
        (await c.PutAsJsonAsync($"/api/stock-entries/{d.Id}", new UpsertStockEntryRequest("Receipt", null, null, main.Id, null,
            [new StockEntryLineRequest(p.Id, 1, 1m)]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.DeleteAsync($"/api/stock-entries/{d.Id}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.PostAsync($"/api/stock-entries/{d.Id}/submit", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var d2 = await Draft(c, "Receipt", null, main.Id, new StockEntryLineRequest(p.Id, 1, 10m));
        (await c.DeleteAsync($"/api/stock-entries/{d2.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await c.GetAsync($"/api/stock-entries/{d2.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Receipt", false, true, false, "rate")]          // receipt line without a rate
    [InlineData("Issue", false, false, true, "comes from")]       // issue without a source store
    [InlineData("Transfer", true, true, true, "different")]       // same source and target
    [InlineData("Scrap", true, false, true, "Type must be")]      // unknown type
    public async Task Invalid_entries_are_rejected(string type, bool source, bool target, bool withRate, string message)
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var p = await Product(c, "IV-1", stock: 5);
        var res = await c.PostAsJsonAsync("/api/stock-entries", new UpsertStockEntryRequest(type, null,
            source ? main.Id : null, target ? main.Id : null, null, [new StockEntryLineRequest(p.Id, 1, withRate ? 1m : null)]));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain(message);
    }

    [Fact]
    public async Task Future_posting_dates_and_duplicate_items_are_rejected()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var p = await Product(c, "FD-1");
        (await c.PostAsJsonAsync("/api/stock-entries", new UpsertStockEntryRequest("Receipt", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
            null, main.Id, null, [new StockEntryLineRequest(p.Id, 1, 1m)]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.PostAsJsonAsync("/api/stock-entries", new UpsertStockEntryRequest("Receipt", null, null, main.Id, null,
            [new StockEntryLineRequest(p.Id, 1, 1m), new StockEntryLineRequest(p.Id, 2, 1m)]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Inactive_store_cannot_receive_stock()
    {
        var c = await NewOrg();
        var wh = await Store(c, "OLD");
        await Ok<StoreDto>(await c.PutAsJsonAsync($"/api/stores/{wh.Id}", new UpsertStoreRequest("OLD", "Old", null, IsActive: false)));
        var p = await Product(c, "IN-1");
        (await c.PostAsJsonAsync("/api/stock-entries", new UpsertStockEntryRequest("Receipt", null, null, wh.Id, null,
            [new StockEntryLineRequest(p.Id, 1, 1m)]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Entry_numbers_are_sequential_per_organization()
    {
        var a = await NewOrg();
        var b = await NewOrg();
        var pa = await Product(a, "SQ-1");
        var pb = await Product(b, "SQ-1");
        var ma = (await MainStore(a)).Id;
        var mb = (await MainStore(b)).Id;
        (await Draft(a, "Receipt", null, ma, new StockEntryLineRequest(pa.Id, 1, 1m))).Number.Should().Be("SE-00001");
        (await Draft(a, "Receipt", null, ma, new StockEntryLineRequest(pa.Id, 1, 1m))).Number.Should().Be("SE-00002");
        (await Draft(b, "Receipt", null, mb, new StockEntryLineRequest(pb.Id, 1, 1m))).Number.Should().Be("SE-00001");
    }

    // ---------------- Integration with products and sales orders ----------------

    [Fact]
    public async Task Product_opening_stock_and_edits_go_through_the_ledger()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var p = await Product(c, "OP-1", stock: 8, price: 25m);
        (await Balance(c, p.Id, main.Id))!.Should().BeEquivalentTo(new { Quantity = 8, ValuationRate = 25m, StockValue = 200m });

        await Ok<ProductDto>(await c.PutAsJsonAsync($"/api/products/{p.Id}", new UpsertProductRequest("OP-1", "Item", null, 25m, 5)));
        (await Balance(c, p.Id, main.Id))!.Quantity.Should().Be(5);
        var ledger = (await c.GetFromJsonAsync<PagedResult<StockLedgerRow>>($"/api/stock/ledger?productId={p.Id}"))!.Items;
        ledger.Select(l => (l.VoucherType, l.QtyChange)).Should().Equal(("Product Adjustment", -3), ("Opening", 8));
    }

    [Fact]
    public async Task Sales_orders_issue_from_the_default_store_at_the_moving_average()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var p = await Product(c, "SO-1", stock: 10, price: 40m);
        var customer = await Ok<CustomerDto>(await c.PostAsJsonAsync("/api/customers", new UpsertCustomerRequest("Buyer", null, null, null)));

        await Ok<OrderDto>(await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest(customer.Id, [new OrderItemRequest(p.Id, 4)])));
        (await Balance(c, p.Id, main.Id))!.Should().BeEquivalentTo(new { Quantity = 6, StockValue = 240m });
        var row = (await c.GetFromJsonAsync<PagedResult<StockLedgerRow>>($"/api/stock/ledger?productId={p.Id}"))!.Items[0];
        row.Should().BeEquivalentTo(new { VoucherType = "Sales Order", QtyChange = -4, Rate = 40m });

        // Stock held in another store does not count for orders
        var wh = await Store(c, "WH");
        await Submit(c, (await Draft(c, "Transfer", main.Id, wh.Id, new StockEntryLineRequest(p.Id, 6, null))).Id);
        (await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest(customer.Id, [new OrderItemRequest(p.Id, 1)])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Products_with_stock_history_from_entries_cannot_be_deleted()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var p = await Product(c, "DL-1");
        await Submit(c, (await Draft(c, "Receipt", null, main.Id, new StockEntryLineRequest(p.Id, 1, 1m))).Id);
        (await c.DeleteAsync($"/api/products/{p.Id}")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var fresh = await Product(c, "DL-2", stock: 3);                         // opening stock only: can go
        (await c.DeleteAsync($"/api/products/{fresh.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Concurrent_issues_never_oversell()
    {
        var c = await NewOrg();
        var main = await MainStore(c);
        var p = await Product(c, "CC-1", stock: 10);
        var drafts = new List<StockEntryDto>();
        for (var i = 0; i < 8; i++) drafts.Add(await Draft(c, "Issue", main.Id, null, new StockEntryLineRequest(p.Id, 2, null)));

        var results = await Task.WhenAll(drafts.Select(d => c.PostAsync($"/api/stock-entries/{d.Id}/submit", null)));
        results.Count(r => r.IsSuccessStatusCode).Should().Be(5);                 // 5 × 2 = 10
        results.Where(r => !r.IsSuccessStatusCode).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.BadRequest);
        (await Balance(c, p.Id, main.Id)).Should().BeNull();                       // empty rows are hidden…
        (await c.GetFromJsonAsync<StockPositionResult>("/api/stock/position?lowOnly=true"))!   // …except under low stock
            .Items.Single(r => r.ProductId == p.Id).Quantity.Should().Be(0);
        (await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products"))!.Items.Single().StockQuantity.Should().Be(0);
    }

    // ---------------- Reports ----------------

    [Fact]
    public async Task Stock_position_filters_by_store_search_and_low_stock()
    {
        var c = await NewOrg();
        await Product(c, "PS-LOW", stock: 3);
        await Product(c, "PS-HIGH", stock: 50, price: 10m);
        var all = (await c.GetFromJsonAsync<StockPositionResult>("/api/stock/position"))!;
        all.Total.Should().Be(2);
        all.TotalQuantity.Should().Be(53);
        all.TotalValue.Should().Be(800m);                                          // 3 × 100 + 50 × 10
        (await c.GetFromJsonAsync<StockPositionResult>("/api/stock/position?lowOnly=true"))!.Items.Single().Sku.Should().Be("PS-LOW");
        (await c.GetFromJsonAsync<StockPositionResult>("/api/stock/position?search=high"))!.Items.Single().Sku.Should().Be("PS-HIGH");
    }

    // ---------------- Rights & tenants ----------------

    [Fact]
    public async Task User_role_reads_stock_but_cannot_create_entries_or_stores()
    {
        var owner = await NewOrg();
        var p = await Product(owner, "RB-1", stock: 2);
        var user = await NewUserIn(owner, "User");
        (await user.GetAsync("/api/stock/position")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.GetAsync("/api/stock/ledger")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.GetAsync("/api/stores")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.GetAsync("/api/stock-entries")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PostAsJsonAsync("/api/stores", new UpsertStoreRequest("X", "X", null))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.PostAsJsonAsync("/api/stock-entries", new UpsertStockEntryRequest("Receipt", null, null, Guid.NewGuid(), null,
            [new StockEntryLineRequest(p.Id, 1, 1m)]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_has_full_inventory_rights_and_lookups_work()
    {
        var owner = await NewOrg();
        await Product(owner, "AD-1", stock: 1);
        var admin = await NewUserIn(owner, "Admin");
        (await admin.GetFromJsonAsync<List<StoreDto>>("/api/stock/stores"))!.Should().ContainSingle(s => s.IsDefault);
        (await admin.GetFromJsonAsync<List<ItemLookupDto>>("/api/stock/items?search=ad"))!.Should().ContainSingle(i => i.Sku == "AD-1");
        (await admin.PostAsJsonAsync("/api/stores", new UpsertStoreRequest("A2", "Admin store", null))).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Another_organization_sees_none_of_it()
    {
        var a = await NewOrg();
        var b = await NewOrg();
        var main = await MainStore(a);
        var p = await Product(a, "TN-1", stock: 5);
        var entry = await Draft(a, "Receipt", null, main.Id, new StockEntryLineRequest(p.Id, 1, 1m));

        (await b.GetAsync($"/api/stock-entries/{entry.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.PostAsync($"/api/stock-entries/{entry.Id}/submit", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.GetFromJsonAsync<StockPositionResult>("/api/stock/position"))!.Total.Should().Be(0);
        (await b.GetFromJsonAsync<PagedResult<StockLedgerRow>>("/api/stock/ledger"))!.Total.Should().Be(0);
        (await b.PostAsJsonAsync("/api/stock-entries", new UpsertStockEntryRequest("Receipt", null, null, main.Id, null,
            [new StockEntryLineRequest(p.Id, 1, 1m)]))).StatusCode.Should().Be(HttpStatusCode.NotFound);   // a's store/product
    }
}
