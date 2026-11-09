using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Erp.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class SessionAndRoleTests(ApiFactory factory)
{
    private const string Cookie = "erp_refresh";

    /// <summary>Client without automatic cookie handling, so tests can see and replay raw refresh tokens.</summary>
    private HttpClient RawClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static string? RefreshCookie(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).Where(v => v.StartsWith(Cookie + "=")).Select(v => v[(Cookie.Length + 1)..])
                .FirstOrDefault(v => v.Length > 0)
            : null;

    private static async Task<HttpResponseMessage> Refresh(HttpClient c, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req.Headers.Add("Cookie", $"{Cookie}={token}");
        return await c.SendAsync(req);
    }

    private static void UseToken(HttpClient c, AuthResponse a) =>
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", a.AccessToken);

    private async Task<(HttpClient Client, AuthResponse Auth)> RegisterNewOrg()
    {
        var c = factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Org " + Guid.NewGuid().ToString("N")[..6], $"owner{Guid.NewGuid():N}@x.com", "Passw0rd!", "Owner"));
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(c, auth);
        return (c, auth);
    }

    [Fact]
    public async Task Login_sets_httponly_refresh_cookie_and_returns_session()
    {
        var res = await RawClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@demo.com", "Admin@123"));
        res.EnsureSuccessStatusCode();
        var setCookie = string.Join("\n", res.Headers.GetValues("Set-Cookie"));
        setCookie.Should().Contain($"{Cookie}=").And.Contain("httponly").And.Contain("path=/api/auth").And.Contain("samesite=strict");
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        body!.SessionId.Should().NotBeEmpty();
        body.User.Role.Should().Be("SuperAdmin");
        (await res.Content.ReadAsStringAsync()).Should().NotContain(RefreshCookie(res)!); // token only in the cookie
    }

    [Fact]
    public async Task Refresh_rotates_the_cookie_and_issues_a_new_access_token()
    {
        var c = RawClient();
        var login = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@demo.com", "Admin@123"));
        var t1 = RefreshCookie(login)!;

        var r1 = await Refresh(c, t1);
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        var t2 = RefreshCookie(r1)!;
        t2.Should().NotBe(t1);

        var auth = (await r1.Content.ReadFromJsonAsync<AuthResponse>())!;
        UseToken(c, auth);
        (await c.GetAsync("/api/products?pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Replaying the rotated-out token is refused (within the grace window the session survives)
        (await Refresh(c, t1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Refresh(c, t2)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_revokes_the_session_immediately()
    {
        var c = RawClient();
        var login = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@demo.com", "Admin@123"));
        var refresh = RefreshCookie(login)!;
        UseToken(c, (await login.Content.ReadFromJsonAsync<AuthResponse>())!);
        (await c.GetAsync("/api/products?pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await c.PostAsync("/api/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The access token is still unexpired, but its session is gone
        (await c.GetAsync("/api/products?pageSize=1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Refresh(c, refresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sessions_can_be_listed_and_other_devices_signed_out()
    {
        var (c, auth) = await RegisterNewOrg();
        var other = factory.CreateClient();
        var login2 = await other.PostAsJsonAsync("/api/auth/login", new LoginRequest(auth.User.Email, "Passw0rd!"));
        UseToken(other, (await login2.Content.ReadFromJsonAsync<AuthResponse>())!);

        var list = await c.GetFromJsonAsync<List<SessionDto>>("/api/auth/sessions");
        list.Should().HaveCount(2).And.ContainSingle(s => s.Current && s.Id == auth.SessionId);

        (await c.PostAsync("/api/auth/logout-others", null)).EnsureSuccessStatusCode();
        (await other.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await c.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Roles_come_from_the_database()
    {
        var c = await LoginAs("admin@demo.com", "Admin@123");
        var roles = await c.GetFromJsonAsync<List<RoleDto>>("/api/roles");
        roles!.Select(r => r.Name).Should().BeEquivalentTo("SuperAdmin", "Admin", "User");
    }

    [Fact]
    public async Task Admin_manages_user_roles_and_role_change_takes_effect_immediately()
    {
        var (admin, _) = await RegisterNewOrg();
        var email = $"staff{Guid.NewGuid():N}@x.com";
        var created = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Staff", "Passw0rd!", "User"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var staffUser = (await created.Content.ReadFromJsonAsync<OrgUserDto>())!;

        var staff = await LoginAs(email, "Passw0rd!");
        (await staff.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await admin.PutAsJsonAsync($"/api/users/{staffUser.Id}/role", new UpdateUserRoleRequest("Admin")))
            .EnsureSuccessStatusCode();

        // Old token (role=User) is dead: the role change signed the user out everywhere
        (await staff.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var promoted = await LoginAs(email, "Passw0rd!");
        (await promoted.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_cannot_see_or_change_users_of_another_tenant()
    {
        var (orgA, _) = await RegisterNewOrg();
        var (_, authB) = await RegisterNewOrg();
        var users = await orgA.GetFromJsonAsync<List<OrgUserDto>>("/api/users");
        users.Should().NotContain(u => u.Id == authB.User.Id);
        (await orgA.PutAsJsonAsync($"/api/users/{authB.User.Id}/role", new UpdateUserRoleRequest("User")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<HttpClient> LoginAs(string email, string password)
    {
        var c = factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        res.EnsureSuccessStatusCode();
        UseToken(c, (await res.Content.ReadFromJsonAsync<AuthResponse>())!);
        return c;
    }
}
