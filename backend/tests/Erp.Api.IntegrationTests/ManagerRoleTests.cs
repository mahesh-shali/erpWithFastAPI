using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using FluentAssertions;

namespace Erp.Api.IntegrationTests;

/// <summary>Manager: between Admin and User. Operational rights without deletes or rights administration.</summary>
[Collection(ApiCollection.Name)]
public class ManagerRoleTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!";

    private async Task<HttpClient> NewOrgOwner()
    {
        var c = factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Mgr Org " + Guid.NewGuid().ToString("N")[..6], $"owner{Guid.NewGuid():N}@x.com", Pw, "Owner"));
        res.EnsureSuccessStatusCode();
        var auth = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return c;
    }

    private async Task<(HttpClient Client, OrgUserDto User)> Create(HttpClient creator, string role)
    {
        var email = $"{role.ToLowerInvariant()}{Guid.NewGuid():N}@x.com";
        var res = await creator.PostAsJsonAsync("/api/users", new CreateUserRequest(email, role + " Person", Pw, role));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var user = (await res.Content.ReadFromJsonAsync<OrgUserDto>())!;
        var c = factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Pw));
        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>())!;
        auth.User.Role.Should().Be(role);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (c, user);
    }

    [Fact]
    public async Task Admins_create_Managers_and_Managers_sit_below_Admins()
    {
        var owner = await NewOrgOwner();
        var (admin, _) = await Create(owner, "Admin");
        var (manager, managerUser) = await Create(admin, "Manager");
        var (_, otherAdmin) = await Create(owner, "Admin");

        // Admin can demote the Manager to User but not promote them to Admin
        (await admin.PutAsJsonAsync($"/api/users/{managerUser.Id}/role", new UpdateUserRoleRequest("Admin")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A Manager can't create accounts by default, and can never act on an Admin
        (await manager.PostAsJsonAsync("/api/users", new CreateUserRequest($"u{Guid.NewGuid():N}@x.com", "X", Pw, "User")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.DeleteAsync($"/api/users/{otherAdmin.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.GetFromJsonAsync<List<RoleDto>>("/api/roles/assignable"))!.Select(r => r.Name).Should().Equal("User");
    }

    [Fact]
    public async Task Manager_defaults_are_operational_without_deletes_or_rights_admin()
    {
        var owner = await NewOrgOwner();
        var (manager, _) = await Create(owner, "Manager");

        var pages = (await manager.GetFromJsonAsync<MyPermissionsDto>("/api/me/permissions"))!.Pages.ToDictionary(p => p.PageKey);
        pages["orders"].Should().Match<MyPagePermissionDto>(p => p.CanRead && p.CanWrite && p.CanUpdate && !p.CanDelete);
        pages["customers"].CanDelete.Should().BeFalse();
        pages["master-users"].Should().Match<MyPagePermissionDto>(p => p.CanRead && !p.CanWrite && !p.CanUpdate && !p.CanDelete);
        pages["master-user-rights"].CanRead.Should().BeFalse();
        pages["master-role-rights"].CanRead.Should().BeFalse();
        pages["messages"].CanWrite.Should().BeTrue();

        (await manager.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.GetAsync("/api/customers")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.DeleteAsync($"/api/customers/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.GetAsync("/api/master/pages")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Managers_reach_every_role_by_default_and_member_chats_stay_private_to_members()
    {
        var owner = await NewOrgOwner();
        var (admin, adminUser) = await Create(owner, "Admin");
        var (manager, _) = await Create(owner, "Manager");
        var (_, staff) = await Create(owner, "User");

        var contacts = (await manager.GetFromJsonAsync<List<ChatContactDto>>("/api/chat/contacts"))!;
        // Managers have the Message Senior Roles right by default
        contacts.Select(c => c.Role).Should().BeEquivalentTo("SuperAdmin", "Admin", "User");

        var group = await manager.PostAsJsonAsync("/api/chat/conversations",
            new CreateConversationRequest([adminUser.Id, staff.Id], "Floor team"));
        group.StatusCode.Should().Be(HttpStatusCode.OK);
        var conv = (await group.Content.ReadFromJsonAsync<ChatConversationDto>())!;
        conv.Members.Select(m => m.Role).Should().BeEquivalentTo("Admin", "Manager", "User");

        // The Admin sees it; the SuperAdmin isn't a member, so doesn't
        (await admin.GetFromJsonAsync<List<ChatConversationDto>>("/api/chat/conversations"))!.Should().Contain(c => c.Id == conv.Id);
        (await owner.GetFromJsonAsync<List<ChatConversationDto>>("/api/chat/conversations"))!.Should().NotContain(c => c.Id == conv.Id);
    }
}
