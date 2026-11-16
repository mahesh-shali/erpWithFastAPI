using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using FluentAssertions;

namespace Erp.Api.IntegrationTests;

/// <summary>
/// Customers, Products, Orders, Dashboard and the "who am I / what can I open" endpoints.
/// Each test registers its own organization, so counts are exact and tests don't affect each other.
/// </summary>
[Collection(ApiCollection.Name)]
public class BusinessApiTests(ApiFactory factory)
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
        (await owner.PostAsJsonAsync("/api/users", new CreateUserRequest(email, role, Pw, role)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var c = factory.CreateClient();
        var auth = (await (await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Pw)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return c;
    }

    private static async Task<CustomerDto> AddCustomer(HttpClient c, string name, string? city = "Pune")
    {
        var res = await c.PostAsJsonAsync("/api/customers", new UpsertCustomerRequest(name, null, city, "India"));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<CustomerDto>())!;
    }

    private static async Task<ProductDto> AddProduct(HttpClient c, string sku, decimal price = 100m, int stock = 10)
    {
        var res = await c.PostAsJsonAsync("/api/products", new UpsertProductRequest(sku, "Product " + sku, "Tools", price, stock));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await res.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    // ---------------- Customers ----------------

    [Fact]
    public async Task Customers_create_get_search_page_and_delete()
    {
        var c = await NewOrg();
        var acme = await AddCustomer(c, "Acme Traders");
        await AddCustomer(c, "Beta Stores");
        await AddCustomer(c, "Gamma Acme Works");

        (await c.GetFromJsonAsync<CustomerDto>($"/api/customers/{acme.Id}"))!.Name.Should().Be("Acme Traders");

        var search = (await c.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/customers?search=acme"))!;
        search.Items.Select(x => x.Name).Should().Equal("Acme Traders", "Gamma Acme Works"); // case-insensitive, sorted

        var page2 = (await c.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/customers?page=2&pageSize=2"))!;
        page2.Total.Should().Be(3);
        page2.Items.Should().ContainSingle().Which.Name.Should().Be("Gamma Acme Works");

        (await c.DeleteAsync($"/api/customers/{acme.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await c.GetAsync($"/api/customers/{acme.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await c.DeleteAsync($"/api/customers/{acme.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Customer_validation_rejects_missing_name_and_bad_email()
    {
        var c = await NewOrg();
        (await c.PostAsJsonAsync("/api/customers", new UpsertCustomerRequest("", null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.PostAsJsonAsync("/api/customers", new UpsertCustomerRequest("X", "not-an-email", null, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Customer_of_another_organization_is_not_found()
    {
        var a = await NewOrg();
        var b = await NewOrg();
        var mine = await AddCustomer(a, "Private Customer");
        (await b.GetAsync($"/api/customers/{mine.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.DeleteAsync($"/api/customers/{mine.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task User_role_can_add_customers_but_not_delete_them()
    {
        var owner = await NewOrg();
        var user = await NewUserIn(owner, "User");
        var created = await AddCustomer(user, "By User");
        (await user.DeleteAsync($"/api/customers/{created.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------- Products ----------------

    [Fact]
    public async Task Products_create_update_list_by_category_and_delete()
    {
        var c = await NewOrg();
        var p = await AddProduct(c, "SKU-1");
        await AddProduct(c, "SKU-2");

        var updated = await c.PutAsJsonAsync($"/api/products/{p.Id}", new UpsertProductRequest("SKU-1", "Renamed", "Spares", 150m, 4));
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await updated.Content.ReadFromJsonAsync<ProductDto>())!.Should()
            .BeEquivalentTo(new { Name = "Renamed", Category = "Spares", UnitPrice = 150m, StockQuantity = 4 });

        // The list cache is invalidated on every change, so the new values show at once.
        var spares = (await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products?category=Spares"))!;
        spares.Items.Should().ContainSingle().Which.Name.Should().Be("Renamed");

        (await c.DeleteAsync($"/api/products/{p.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products"))!.Total.Should().Be(1);
        (await c.DeleteAsync($"/api/products/{p.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Duplicate_sku_is_409_and_bad_values_are_400()
    {
        var c = await NewOrg();
        await AddProduct(c, "DUP-1");
        (await c.PostAsJsonAsync("/api/products", new UpsertProductRequest("DUP-1", "Again", null, 1, 1)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await c.PostAsJsonAsync("/api/products", new UpsertProductRequest("NEG-1", "Negative", null, -5, 1)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.PutAsJsonAsync($"/api/products/{Guid.NewGuid()}", new UpsertProductRequest("X", "X", null, 1, 1)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task User_role_can_read_products_but_not_change_them()
    {
        var owner = await NewOrg();
        var p = await AddProduct(owner, "RO-1");
        var user = await NewUserIn(owner, "User");
        (await user.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await user.PutAsJsonAsync($"/api/products/{p.Id}", new UpsertProductRequest("RO-1", "X", null, 1, 1)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.DeleteAsync($"/api/products/{p.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------- Orders ----------------

    [Fact]
    public async Task Order_lifecycle_Pending_to_Paid_to_Shipped()
    {
        var c = await NewOrg();
        var customer = await AddCustomer(c, "Buyer");
        var a = await AddProduct(c, "A-1", price: 10m, stock: 5);
        var b = await AddProduct(c, "B-1", price: 2.5m, stock: 5);

        var res = await c.PostAsJsonAsync("/api/orders",
            new CreateOrderRequest(customer.Id, [new OrderItemRequest(a.Id, 2), new OrderItemRequest(b.Id, 4)]));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = (await res.Content.ReadFromJsonAsync<OrderDto>())!;
        order.Status.Should().Be("Pending");
        order.TotalAmount.Should().Be(30m);
        order.Items.Should().HaveCount(2);

        (await c.GetFromJsonAsync<OrderDto>($"/api/orders/{order.Id}"))!.TotalAmount.Should().Be(30m);
        (await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products"))!.Items
            .Single(p => p.Id == a.Id).StockQuantity.Should().Be(3);

        foreach (var next in new[] { "Paid", "Shipped" })
        {
            var r = await c.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateOrderStatusRequest(next));
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            (await r.Content.ReadFromJsonAsync<OrderDto>())!.Status.Should().Be(next);
        }
        (await c.PatchAsJsonAsync($"/api/orders/{order.Id}/status", new UpdateOrderStatusRequest("Cancelled")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Order_validation_errors()
    {
        var c = await NewOrg();
        var customer = await AddCustomer(c, "Buyer");
        var p = await AddProduct(c, "V-1");

        (await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest(customer.Id, [])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);                                          // no items
        (await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest(customer.Id,
            [new OrderItemRequest(p.Id, 1), new OrderItemRequest(p.Id, 1)])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);                                          // duplicate product
        (await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest(Guid.NewGuid(), [new OrderItemRequest(p.Id, 1)])))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);                                            // unknown customer
        (await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest(customer.Id, [new OrderItemRequest(Guid.NewGuid(), 1)])))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);                                            // unknown product
        (await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest(customer.Id, [new OrderItemRequest(p.Id, 0)])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);                                          // quantity < 1
        (await c.GetAsync($"/api/orders/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Orders_list_is_newest_first_with_keyset_paging_and_status_filter()
    {
        var c = await NewOrg();
        var customer = await AddCustomer(c, "Buyer");
        var p = await AddProduct(c, "K-1", stock: 100);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var o = await (await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest(customer.Id, [new OrderItemRequest(p.Id, 1)])))
                .Content.ReadFromJsonAsync<OrderDto>();
            ids.Add(o!.Id);
        }
        await c.PatchAsJsonAsync($"/api/orders/{ids[0]}/status", new UpdateOrderStatusRequest("Paid"));

        var first = (await c.GetFromJsonAsync<List<OrderDto>>("/api/orders?limit=2"))!;
        first.Select(o => o.Id).Should().Equal(ids[2], ids[1]);
        var before = Uri.EscapeDataString(first[^1].OrderedAt.ToString("O"));
        var next = (await c.GetFromJsonAsync<List<OrderDto>>($"/api/orders?limit=2&before={before}"))!;
        next.Select(o => o.Id).Should().Equal(ids[0]);

        (await c.GetFromJsonAsync<List<OrderDto>>("/api/orders?status=Paid"))!
            .Should().ContainSingle().Which.Id.Should().Be(ids[0]);
    }

    // ---------------- Dashboard, permissions, roles, pages ----------------

    [Fact]
    public async Task Dashboard_stats_count_this_organization_only()
    {
        var c = await NewOrg();
        var customer = await AddCustomer(c, "Buyer");
        var p = await AddProduct(c, "D-1", price: 50m, stock: 12);
        await AddProduct(c, "D-2", price: 1m, stock: 3);                       // low stock (< 10)
        var o = await (await c.PostAsJsonAsync("/api/orders", new CreateOrderRequest(customer.Id, [new OrderItemRequest(p.Id, 4)])))
            .Content.ReadFromJsonAsync<OrderDto>();

        var stats = (await c.GetFromJsonAsync<DashboardStats>("/api/dashboard/stats"))!;
        stats.Should().Be(new DashboardStats(Customers: 1, Products: 2, Orders: 1, Revenue: 200m, LowStockProducts: 2));
        o!.TotalAmount.Should().Be(200m);
    }

    [Fact]
    public async Task My_permissions_list_every_page_SuperAdmin_has_all_User_has_defaults()
    {
        var owner = await NewOrg();
        var mine = (await owner.GetFromJsonAsync<MyPermissionsDto>("/api/me/permissions"))!;
        mine.Role.Should().Be("SuperAdmin");
        mine.Pages.Should().Contain(p => p.PageKey == "analytics").And.OnlyContain(p => p.CanRead && p.CanDelete);

        var user = await NewUserIn(owner, "User");
        var theirs = (await user.GetFromJsonAsync<MyPermissionsDto>("/api/me/permissions"))!;
        theirs.Role.Should().Be("User");
        theirs.Pages.Single(p => p.PageKey == "dashboard").CanRead.Should().BeTrue();
        theirs.Pages.Single(p => p.PageKey == "analytics").CanRead.Should().BeFalse();
        theirs.Pages.Single(p => p.PageKey == "master-users").CanRead.Should().BeFalse();
    }

    [Fact]
    public async Task Assignable_roles_are_only_those_below_your_own()
    {
        var owner = await NewOrg();
        (await owner.GetFromJsonAsync<List<RoleDto>>("/api/roles/assignable"))!.Select(r => r.Name)
            .Should().BeEquivalentTo("Admin", "User");
        var admin = await NewUserIn(owner, "Admin");
        (await admin.GetFromJsonAsync<List<RoleDto>>("/api/roles/assignable"))!.Select(r => r.Name)
            .Should().Equal("User");
    }

    [Fact]
    public async Task Pages_master_lists_the_seeded_menu_with_unique_keys()
    {
        var owner = await NewOrg();
        var pages = (await owner.GetFromJsonAsync<List<PageDto>>("/api/master/pages"))!;
        pages.Should().Contain(p => p.Key == "dashboard" && p.Route == "/" && p.Menu.Module == null);
        pages.Should().Contain(p => p.Key == "analytics" && p.IsImplemented);
        pages.Should().Contain(p => p.Key == "vendors" && !p.IsImplemented && p.Menu.Module == "Master");
        pages.Select(p => p.Key).Should().OnlyHaveUniqueItems();
        pages.Select(p => p.Route).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Delete_user_removes_them_and_their_sessions()
    {
        var owner = await NewOrg();
        var email = $"gone{Guid.NewGuid():N}@x.com";
        var created = await (await owner.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Gone", Pw, "User")))
            .Content.ReadFromJsonAsync<OrgUserDto>();
        var login = factory.CreateClient();
        var auth = (await (await login.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Pw)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        login.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        (await owner.DeleteAsync($"/api/users/{created!.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<List<OrgUserDto>>("/api/users"))!.Should().NotContain(u => u.Id == created.Id);
        (await login.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
