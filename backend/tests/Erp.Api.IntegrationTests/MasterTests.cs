using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;

namespace Erp.Api.IntegrationTests;

/// <summary>Master module: hierarchy SuperAdmin > Admin > User, page rights, overrides, no escalation.</summary>
[Collection(ApiCollection.Name)]
public class MasterTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!";

    private async Task<(HttpClient Client, AuthResponse Auth)> NewOrgSuperAdmin()
    {
        var c = factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Org " + Guid.NewGuid().ToString("N")[..6], $"owner{Guid.NewGuid():N}@x.com", Pw, "Owner"));
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (c, auth);
    }

    private async Task<(HttpClient Client, OrgUserDto User)> CreateAndLogin(HttpClient creator, string role)
    {
        var email = $"{role.ToLowerInvariant()}{Guid.NewGuid():N}@x.com";
        var res = await creator.PostAsJsonAsync("/api/users", new CreateUserRequest(email, role + " Person", Pw, role));
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var user = (await res.Content.ReadFromJsonAsync<OrgUserDto>())!;
        return (await Login(email), user);
    }

    private async Task<HttpClient> Login(string email)
    {
        var c = factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Pw));
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return c;
    }

    private static async Task<int> PageId(HttpClient superAdmin, string key) =>
        (await superAdmin.GetFromJsonAsync<List<PageDto>>("/api/master/pages"))!.Single(p => p.Key == key).Id;

    private static PageRightsDto R(bool v = false, bool restricted = false, bool r = false, bool w = false, bool u = false, bool d = false)
        => new(v, restricted, r, w, u, d);

    [Fact]
    public async Task Registering_an_org_makes_the_owner_SuperAdmin_with_every_right()
    {
        var (c, auth) = await NewOrgSuperAdmin();
        auth.User.Role.Should().Be("SuperAdmin");
        var me = (await c.GetFromJsonAsync<MyPermissionsDto>("/api/me/permissions"))!;
        me.Pages.Should().OnlyContain(p => p.IsVisible && p.CanRead && p.CanWrite && p.CanUpdate && p.CanDelete);
    }

    [Fact]
    public async Task SuperAdmin_creates_Admins_Admin_creates_Users_only()
    {
        var (super, _) = await NewOrgSuperAdmin();
        var (admin, _) = await CreateAndLogin(super, "Admin");

        (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest($"a{Guid.NewGuid():N}@x.com", "X", Pw, "Admin")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest($"u{Guid.NewGuid():N}@x.com", "X", Pw, "User")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.GetFromJsonAsync<List<RoleDto>>("/api/roles/assignable"))!.Select(r => r.Name).Should().Equal("User");
    }

    [Fact]
    public async Task Admin_cannot_manage_SuperAdmin_or_other_Admins()
    {
        var (super, superAuth) = await NewOrgSuperAdmin();
        var (admin, _) = await CreateAndLogin(super, "Admin");
        var (_, otherAdmin) = await CreateAndLogin(super, "Admin");

        (await admin.DeleteAsync($"/api/users/{superAuth.User.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.DeleteAsync($"/api/users/{otherAdmin.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var rights = await admin.GetFromJsonAsync<UserRightsDto>($"/api/master/users/{otherAdmin.Id}/rights");
        rights!.CanEdit.Should().BeFalse();
    }

    [Fact]
    public async Task Default_User_rights_are_enforced_by_the_API()
    {
        var (super, _) = await NewOrgSuperAdmin();
        var (user, _) = await CreateAndLogin(super, "User");

        (await user.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);              // canRead
        (await user.PostAsJsonAsync("/api/products", new UpsertProductRequest("S1", "P", null, 1, 1)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);                                        // no canWrite
        (await user.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);         // no Master access
    }

    [Fact]
    public async Task User_override_grants_and_restrict_blocks_immediately()
    {
        var (super, _) = await NewOrgSuperAdmin();
        var (user, created) = await CreateAndLogin(super, "User");
        var products = await PageId(super, "products");

        // Grant write on Products to this one user
        (await super.PutAsJsonAsync($"/api/master/users/{created.Id}/rights",
            new SetRightsRequest([new SetRightsItem(products, false, R(v: true, r: true, w: true))]))).EnsureSuccessStatusCode();
        (await user.PostAsJsonAsync("/api/products", new UpsertProductRequest($"S{Guid.NewGuid():N}"[..10], "P", null, 1, 1)))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Restrict: hard block, even reads, without changing the other flags
        (await super.PutAsJsonAsync($"/api/master/users/{created.Id}/rights",
            new SetRightsRequest([new SetRightsItem(products, false, R(v: true, restricted: true, r: true, w: true))]))).EnsureSuccessStatusCode();
        (await user.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Back to the role default
        var reset = await super.PutAsJsonAsync($"/api/master/users/{created.Id}/rights",
            new SetRightsRequest([new SetRightsItem(products, true, null)]));
        var rights = (await reset.Content.ReadFromJsonAsync<UserRightsDto>())!;
        rights.Pages.Single(p => p.PageId == products).Should().Match<UserRightsRowDto>(p => !p.IsOverridden && p.Source == "system");
        (await user.GetAsync("/api/products")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_cannot_grant_rights_they_do_not_have()
    {
        var (super, _) = await NewOrgSuperAdmin();
        var (admin, _) = await CreateAndLogin(super, "Admin");
        var (_, user) = await CreateAndLogin(admin, "User");
        var roleRights = await PageId(super, "master-role-rights"); // Admin has no rights here by default

        (await admin.PutAsJsonAsync($"/api/master/users/{user.Id}/rights",
            new SetRightsRequest([new SetRightsItem(roleRights, false, R(v: true, r: true))])))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var customers = await PageId(super, "customers"); // Admin has full rights here
        (await admin.PutAsJsonAsync($"/api/master/users/{user.Id}/rights",
            new SetRightsRequest([new SetRightsItem(customers, false, R(v: true, r: true, d: true))])))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Role_defaults_are_per_organization_and_only_for_lower_roles()
    {
        var (superA, _) = await NewOrgSuperAdmin();
        var (superB, _) = await NewOrgSuperAdmin();
        var (userA, _) = await CreateAndLogin(superA, "User");
        var (userB, _) = await CreateAndLogin(superB, "User");
        var products = await PageId(superA, "products");
        var roles = (await superA.GetFromJsonAsync<List<RoleDto>>("/api/roles"))!;
        var userRole = roles.Single(r => r.Name == "User").Id;
        var superRole = roles.Single(r => r.Name == "SuperAdmin").Id;

        // Org A: Users may create products
        (await superA.PutAsJsonAsync($"/api/master/roles/{userRole}/rights",
            new SetRightsRequest([new SetRightsItem(products, false, R(v: true, r: true, w: true))]))).EnsureSuccessStatusCode();

        (await userA.PostAsJsonAsync("/api/products", new UpsertProductRequest($"S{Guid.NewGuid():N}"[..10], "P", null, 1, 1)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await userB.PostAsJsonAsync("/api/products", new UpsertProductRequest($"S{Guid.NewGuid():N}"[..10], "P", null, 1, 1)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden); // org B unaffected

        (await superA.PutAsJsonAsync($"/api/master/roles/{superRole}/rights",
            new SetRightsRequest([new SetRightsItem(products, false, R())]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Nobody_can_edit_their_own_rights()
    {
        var (super, superAuth) = await NewOrgSuperAdmin();
        var products = await PageId(super, "products");
        (await super.PutAsJsonAsync($"/api/master/users/{superAuth.User.Id}/rights",
            new SetRightsRequest([new SetRightsItem(products, false, R())]))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Production_style_setup_registration_off_and_bootstrap_owner_can_log_in()
    {
        var email = $"owner{Guid.NewGuid():N}@prod.example";
        using var prod = factory.WithWebHostBuilder(b => b
            .UseSetting("Auth:AllowRegistration", "false")
            .UseSetting("Bootstrap:OrganizationName", "Prod Co")
            .UseSetting("Bootstrap:Email", email)
            .UseSetting("Bootstrap:Password", "Very-Strong-Pass-1")
            .UseSetting("Bootstrap:FullName", "Prod Owner"));
        var c = prod.CreateClient();

        (await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Hacker Org", $"x{Guid.NewGuid():N}@x.com", Pw, "X"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var login = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "Very-Strong-Pass-1"));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<AuthResponse>())!.User.Role.Should().Be("SuperAdmin");
    }
}
