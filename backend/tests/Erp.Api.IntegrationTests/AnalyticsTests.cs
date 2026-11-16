using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using FluentAssertions;

namespace Erp.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AnalyticsTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!";

    private async Task<HttpClient> Login(string email, string password)
    {
        var c = factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return c;
    }

    [Theory]
    [InlineData(30, "day", 30)]
    [InlineData(90, "day", 90)]
    [InlineData(365, "month", 12)]
    public async Task Report_covers_the_range_for_the_demo_tenant(int days, string bucket, int points)
    {
        var admin = await Login("admin@demo.com", "Admin@123");

        var report = (await admin.GetFromJsonAsync<AnalyticsReport>($"/api/analytics?days={days}"))!;

        report.Bucket.Should().Be(bucket);
        report.Trend.Should().HaveCount(points);
        report.Current.Revenue.Should().Be(report.Trend.Sum(p => p.Revenue));
        report.Current.Orders.Should().Be(report.Trend.Sum(p => p.Orders));
        report.Statuses.Select(s => s.Status).Should().Equal("Pending", "Paid", "Shipped", "Cancelled");
        report.TopProducts.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(6)
            .And.BeInDescendingOrder(r => r.Revenue);
        // Tenant isolation: the other tenant's customer (Paris) never shows up.
        report.TopCustomers.Should().NotContain(r => r.Name.Contains("OTHER TENANT"));
        report.Cities.Should().NotContain(r => r.Name == "Paris");
    }

    [Fact]
    public async Task Unsupported_range_is_400()
    {
        var admin = await Login("admin@demo.com", "Admin@123");
        (await admin.GetAsync("/api/analytics?days=7")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Default_rights_allow_Admin_but_not_User()
    {
        var owner = factory.CreateClient();
        var reg = await owner.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Org " + Guid.NewGuid().ToString("N")[..6], $"owner{Guid.NewGuid():N}@x.com", Pw, "Owner"));
        reg.EnsureSuccessStatusCode();
        var auth = (await reg.Content.ReadFromJsonAsync<AuthResponse>())!;
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        async Task<HttpClient> Create(string role)
        {
            var email = $"{role.ToLowerInvariant()}{Guid.NewGuid():N}@x.com";
            (await owner.PostAsJsonAsync("/api/users", new CreateUserRequest(email, role, Pw, role)))
                .StatusCode.Should().Be(HttpStatusCode.Created);
            return await Login(email, Pw);
        }

        var empty = (await owner.GetFromJsonAsync<AnalyticsReport>("/api/analytics"))!;   // new org: no orders yet
        empty.Current.Revenue.Should().Be(0);
        empty.Trend.Should().HaveCount(30).And.OnlyContain(p => p.Revenue == 0);

        (await (await Create("Admin")).GetAsync("/api/analytics")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await Create("User")).GetAsync("/api/analytics")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
