using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Erp.Api.Contracts;
using Erp.Api.Services;
using FluentAssertions;

namespace Erp.Api.IntegrationTests;

/// <summary>Two-factor sign-in (TOTP + recovery codes) and Settings: my account, and editing lower-role users.</summary>
[Collection(ApiCollection.Name)]
public class TwoFactorAndSettingsTests(ApiFactory factory)
{
    private const string Pw = "Passw0rd!";

    private static string CodeNow(string secret, int stepOffset = 0) =>
        Totp.Code(secret, Totp.StepAt(DateTimeOffset.UtcNow) + stepOffset);

    private async Task<(HttpClient Client, string Email)> NewOrgOwner()
    {
        var c = factory.CreateClient();
        var email = $"owner{Guid.NewGuid():N}@x.com";
        var res = await c.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("2FA Org " + Guid.NewGuid().ToString("N")[..6], email, Pw, "Owner"));
        res.EnsureSuccessStatusCode();
        Authorize(c, (await res.Content.ReadFromJsonAsync<AuthResponse>())!);
        return (c, email);
    }

    private static void Authorize(HttpClient c, AuthResponse auth) =>
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

    private async Task<(HttpClient Client, OrgUserDto User)> Create(HttpClient creator, string role)
    {
        var email = $"{role.ToLowerInvariant()}{Guid.NewGuid():N}@x.com";
        var res = await creator.PostAsJsonAsync("/api/users", new CreateUserRequest(email, role + " Person", Pw, role));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var user = (await res.Content.ReadFromJsonAsync<OrgUserDto>())!;
        var c = factory.CreateClient();
        Authorize(c, (await (await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Pw))).Content
            .ReadFromJsonAsync<AuthResponse>())!);
        return (c, user);
    }

    private static async Task<(string Secret, IReadOnlyList<string> RecoveryCodes)> TurnOn2Fa(HttpClient c)
    {
        var setup = await c.PostAsync("/api/account/two-factor/setup", null);
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        var s = (await setup.Content.ReadFromJsonAsync<TwoFactorSetupResponse>())!;
        s.OtpAuthUri.Should().StartWith("otpauth://totp/").And.Contain(s.Secret);

        (await c.PostAsJsonAsync("/api/account/two-factor/enable", new TwoFactorCodeRequest("000000")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var enable = await c.PostAsJsonAsync("/api/account/two-factor/enable", new TwoFactorCodeRequest(CodeNow(s.Secret)));
        enable.StatusCode.Should().Be(HttpStatusCode.OK, await enable.Content.ReadAsStringAsync());
        var codes = (await enable.Content.ReadFromJsonAsync<RecoveryCodesResponse>())!.RecoveryCodes;
        codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        return (s.Secret, codes);
    }

    private Task<HttpResponseMessage> Login(string email, string password, string? code = null) =>
        factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password, code));

    [Fact]
    public async Task Login_asks_for_the_code_once_2FA_is_on_and_accepts_each_code_only_once()
    {
        var (owner, email) = await NewOrgOwner();
        var (secret, _) = await TurnOn2Fa(owner);
        (await owner.GetFromJsonAsync<AccountDto>("/api/account"))!.Should()
            .Match<AccountDto>(a => a.TwoFactorEnabled && a.RecoveryCodesLeft == 10);

        // Right password, no code: a challenge, no session and no refresh cookie
        var challenge = await Login(email, Pw);
        challenge.StatusCode.Should().Be(HttpStatusCode.OK);
        (await challenge.Content.ReadFromJsonAsync<TwoFactorChallengeResponse>())!.TwoFactorRequired.Should().BeTrue();
        challenge.Headers.Contains("Set-Cookie").Should().BeFalse();

        // Wrong password never reveals that 2FA is on
        (await Login(email, "wrong-pass")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(email, Pw, "123456")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The enable call used the current step; the next one is within the allowed drift
        var code = CodeNow(secret, 1);
        var ok = await Login(email, Pw, code);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken.Should().NotBeNullOrEmpty();

        (await Login(email, Pw, code)).StatusCode.Should().Be(HttpStatusCode.Unauthorized); // replay
    }

    private static async Task<bool> IsChallenge(HttpResponseMessage res) =>
        res.StatusCode == HttpStatusCode.OK && (await res.Content.ReadAsStringAsync()).Contains("twoFactorRequired");

    private static Task<HttpResponseMessage> LoginOn(HttpClient device, string email, string password, string? code = null,
        bool remember = false) =>
        device.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password, code, remember));

    [Fact]
    public async Task Remember_this_device_skips_the_code_on_that_device_only_until_forgotten()
    {
        var (owner, email) = await NewOrgOwner();
        var (secret, _) = await TurnOn2Fa(owner);
        var device = factory.CreateClient(); // keeps cookies, like a browser

        var first = await LoginOn(device, email, Pw, CodeNow(secret, 1), remember: true);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        first.Headers.GetValues("Set-Cookie").Should().Contain(c =>
            c.StartsWith("erp_2fa_trust=") && c.Contains("httponly") && c.Contains("path=/api/auth") && c.Contains("expires="));

        // Same device: password only. Another device: still asked for the code.
        var again = await LoginOn(device, email, Pw);
        (await again.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken.Should().NotBeNullOrEmpty();
        (await IsChallenge(await Login(email, Pw))).Should().BeTrue();
        (await LoginOn(device, email, "wrong-pass")).StatusCode.Should().Be(HttpStatusCode.Unauthorized); // password still checked
        (await owner.GetFromJsonAsync<AccountDto>("/api/account"))!.TrustedDevices.Should().Be(1);

        // "Forget all devices": the remembered device is asked again
        (await owner.DeleteAsync("/api/account/two-factor/trusted-devices")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await IsChallenge(await LoginOn(device, email, Pw))).Should().BeTrue();
    }

    [Fact]
    public async Task A_remembered_device_is_tied_to_its_user_and_forgotten_on_password_change()
    {
        var (owner, ownerEmail) = await NewOrgOwner();
        var (ownerSecret, _) = await TurnOn2Fa(owner);
        var (manager, managerUser) = await Create(owner, "Manager");
        await TurnOn2Fa(manager);
        var device = factory.CreateClient();

        (await LoginOn(device, ownerEmail, Pw, CodeNow(ownerSecret, 1), remember: true)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await IsChallenge(await LoginOn(device, managerUser.Email, Pw))).Should().BeTrue(); // owner's trust doesn't cover others
        (await IsChallenge(await LoginOn(device, ownerEmail, Pw))).Should().BeFalse();

        (await owner.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest(Pw, "Another#123")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await IsChallenge(await LoginOn(device, ownerEmail, "Another#123"))).Should().BeTrue();
    }

    [Fact]
    public async Task Recovery_codes_work_once_and_2FA_can_be_turned_off_with_password_and_code()
    {
        var (owner, email) = await NewOrgOwner();
        var (secret, recovery) = await TurnOn2Fa(owner);

        (await Login(email, Pw, recovery[0].ToUpperInvariant())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Login(email, Pw, recovery[0])).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await owner.GetFromJsonAsync<AccountDto>("/api/account"))!.RecoveryCodesLeft.Should().Be(9);

        // Setup again while on is refused (would replace the device silently)
        (await owner.PostAsync("/api/account/two-factor/setup", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await owner.PostAsJsonAsync("/api/account/two-factor/disable", new DisableTwoFactorRequest("wrong-pass", recovery[1])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/account/two-factor/disable", new DisableTwoFactorRequest(Pw, recovery[1])))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Off again: the password alone signs in, and the old secret is gone
        var plain = await Login(email, Pw);
        (await plain.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken.Should().NotBeNullOrEmpty();
        (await Login(email, Pw, CodeNow(secret))).StatusCode.Should().Be(HttpStatusCode.OK); // code ignored when off
    }

    [Fact]
    public async Task Users_change_their_own_profile_and_password()
    {
        var (owner, email) = await NewOrgOwner();

        var renamed = await owner.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest("New Name", email, null));
        (await renamed.Content.ReadFromJsonAsync<AccountDto>())!.FullName.Should().Be("New Name");

        var newEmail = $"moved{Guid.NewGuid():N}@x.com";
        (await owner.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest("New Name", newEmail, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest); // email change needs the password
        (await owner.PutAsJsonAsync("/api/account/profile", new UpdateProfileRequest("New Name", newEmail, Pw)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await owner.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest("wrong-pass", "Another#123")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync("/api/account/password", new ChangePasswordRequest(Pw, "Another#123")))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await owner.GetAsync("/api/account")).StatusCode.Should().Be(HttpStatusCode.OK); // this session survives

        (await Login(newEmail, Pw)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(newEmail, "Another#123")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SuperAdmin_edits_everyone_Admin_edits_Managers_and_Users_Manager_edits_Users()
    {
        var (owner, ownerEmail) = await NewOrgOwner();
        var (admin, adminUser) = await Create(owner, "Admin");
        var (manager, managerUser) = await Create(owner, "Manager");
        var (user, userUser) = await Create(owner, "User");
        var ownerId = (await owner.GetFromJsonAsync<AccountDto>("/api/account"))!.Id;

        Task<HttpResponseMessage> Edit(HttpClient actor, OrgUserDto target, string? role = null, string? pw = null) =>
            actor.PutAsJsonAsync($"/api/settings/users/{target.Id}",
                new UpdateUserRequest(target.FullName + " (edited)", target.Email, role, pw));

        // SuperAdmin: Admin, Manager and User
        (await Edit(owner, adminUser)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Edit(owner, managerUser)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Edit(owner, userUser)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Admin: Manager and User, not another Admin, not the owner, no promotion to Admin
        (await Edit(admin, managerUser)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Edit(admin, userUser)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Edit(admin, adminUser)).StatusCode.Should().Be(HttpStatusCode.Forbidden); // own account: use My account
        (await admin.PutAsJsonAsync($"/api/settings/users/{ownerId}", new UpdateUserRequest("X", ownerEmail, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Edit(admin, userUser, role: "Admin")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Manager: only Users
        (await Edit(manager, userUser)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Edit(manager, adminUser)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.GetFromJsonAsync<List<RoleDto>>("/api/settings/users/roles"))!.Select(r => r.Name).Should().Equal("User");

        // User: no access to Settings > Users at all
        (await user.GetAsync("/api/settings/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Edit(user, managerUser)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_password_reset_signs_the_user_out_and_a_2FA_reset_lets_them_in_with_the_password()
    {
        var (owner, _) = await NewOrgOwner();
        var (manager, managerUser) = await Create(owner, "Manager");
        await TurnOn2Fa(manager);

        var users = (await owner.GetFromJsonAsync<List<OrgUserDto>>("/api/settings/users"))!;
        users.Single(u => u.Id == managerUser.Id).TwoFactorEnabled.Should().BeTrue();

        var challenge = await Login(managerUser.Email, Pw);
        (await challenge.Content.ReadFromJsonAsync<TwoFactorChallengeResponse>())!.TwoFactorRequired.Should().BeTrue();
        var reset = await owner.DeleteAsync($"/api/settings/users/{managerUser.Id}/two-factor");
        (await reset.Content.ReadFromJsonAsync<OrgUserDto>())!.TwoFactorEnabled.Should().BeFalse();
        (await manager.GetAsync("/api/account")).StatusCode.Should().Be(HttpStatusCode.Unauthorized); // signed out

        var relogin = await Login(managerUser.Email, Pw);
        var auth = (await relogin.Content.ReadFromJsonAsync<AuthResponse>())!;
        auth.AccessToken.Should().NotBeNullOrEmpty();
        Authorize(manager, auth);

        (await owner.PutAsJsonAsync($"/api/settings/users/{managerUser.Id}",
            new UpdateUserRequest(managerUser.FullName, managerUser.Email, null, "short"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync($"/api/settings/users/{managerUser.Id}",
            new UpdateUserRequest(managerUser.FullName, managerUser.Email, null, "Fresh#Pass1"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.GetAsync("/api/account")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(managerUser.Email, "Fresh#Pass1")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admins_and_Managers_get_the_settings_users_right_by_default_Users_do_not()
    {
        var (owner, _) = await NewOrgOwner();
        var (admin, _) = await Create(owner, "Admin");
        var (manager, _) = await Create(owner, "Manager");
        var (user, _) = await Create(owner, "User");

        async Task<MyPagePermissionDto> Right(HttpClient c) =>
            (await c.GetFromJsonAsync<MyPermissionsDto>("/api/me/permissions"))!.Pages.Single(p => p.PageKey == "settings-users");

        (await Right(admin)).Should().Match<MyPagePermissionDto>(p => p.CanUpdate && !p.IsVisible);
        (await Right(manager)).CanUpdate.Should().BeTrue();
        (await Right(user)).CanUpdate.Should().BeFalse();
    }
}
