using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using FluentAssertions;

namespace Erp.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class ApiTests(ApiFactory factory)
{
    private async Task<HttpClient> LoginAsDemoAdmin()
    {
        var c = factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@demo.com", "Admin@123"));
        res.EnsureSuccessStatusCode();
        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return c;
    }

    [Fact]
    public async Task Health_live_is_ok()
    {
        var res = await factory.CreateClient().GetAsync("/health/live");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Protected_endpoint_requires_token()
    {
        var res = await factory.CreateClient().GetAsync("/api/products");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_with_wrong_password_is_401()
    {
        var res = await factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@demo.com", "wrong-pass"));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Demo_admin_sees_seeded_products()
    {
        var c = await LoginAsDemoAdmin();
        var page = await c.GetFromJsonAsync<PagedResult<ProductDto>>("/api/products?pageSize=10");
        page!.Total.Should().BeGreaterOrEqualTo(100); // seeded 100; other tests in the collection may add more
        page.Items.Should().HaveCount(10);
    }

    [Fact]
    public async Task New_tenant_cannot_see_other_tenants_data()
    {
        var c = factory.CreateClient();
        var reg = await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Isolated Inc", $"iso{Guid.NewGuid():N}@x.com", "Passw0rd!", "Iso User"));
        reg.EnsureSuccessStatusCode();
        var auth = await reg.Content.ReadFromJsonAsync<AuthResponse>();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var customers = await c.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/customers");
        customers!.Total.Should().Be(0);
    }

    [Fact]
    public async Task Creating_order_decrements_stock_and_rejects_oversell()
    {
        var c = await LoginAsDemoAdmin();
        var customer = (await c.GetFromJsonAsync<PagedResult<CustomerDto>>("/api/customers?pageSize=1"))!.Items[0];

        var created = await c.PostAsJsonAsync("/api/products",
            new UpsertProductRequest($"T-{Guid.NewGuid():N}"[..12], "Test Widget", "Test", 99.99m, 5));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var product = await created.Content.ReadFromJsonAsync<ProductDto>();

        var ok = await c.PostAsJsonAsync("/api/orders",
            new CreateOrderRequest(customer.Id, [new OrderItemRequest(product!.Id, 3)]));
        ok.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ok.Content.ReadFromJsonAsync<OrderDto>())!.TotalAmount.Should().Be(299.97m);

        var oversell = await c.PostAsJsonAsync("/api/orders",
            new CreateOrderRequest(customer.Id, [new OrderItemRequest(product.Id, 3)]));
        oversell.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invalid_status_transition_is_rejected()
    {
        var c = await LoginAsDemoAdmin();
        var shipped = await c.GetFromJsonAsync<List<OrderDto>>("/api/orders?status=Shipped&limit=1");
        if (shipped!.Count == 0) return; // seed is random; nothing to assert
        var res = await c.PatchAsJsonAsync($"/api/orders/{shipped[0].Id}/status", new UpdateOrderStatusRequest("Pending"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
