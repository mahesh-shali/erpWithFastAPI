using System.Security.Claims;
using Erp.Api.Contracts;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Erp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Erp.Api.Controllers;

/// <summary>
/// Settings > My account: profile, password and two-factor sign-in. Every signed-in user may use it for
/// their own account (no page right needed).
/// </summary>
[ApiController]
[Route("api/account")]
[Authorize]
public class AccountController(IAccountService account, ITwoFactorService twoFactor, ITenantContext tenant) : ControllerBase
{
    [HttpGet]
    public Task<AccountDto> Get(CancellationToken ct) => account.GetAsync(ct);

    [HttpPut("profile")]
    public Task<AccountDto> UpdateProfile(UpdateProfileRequest req, CancellationToken ct) => account.UpdateProfileAsync(req, ct);

    /// <summary>Changes the password; other devices are signed out, this one stays signed in.</summary>
    [HttpPost("password"), EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req, CancellationToken ct)
    {
        await account.ChangePasswordAsync(req, CurrentSessionId(), ct);
        return NoContent();
    }

    /// <summary>Step 1: a new secret for the authenticator app (QR code). 2FA stays off until step 2.</summary>
    [HttpPost("two-factor/setup")]
    public async Task<TwoFactorSetupResponse> SetupTwoFactor(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var s = await twoFactor.BeginSetupAsync(tenant.UserId, ct);
        return new TwoFactorSetupResponse(s.Secret, s.OtpAuthUri);
    }

    /// <summary>Step 2: confirm with a code from the app. Returns the one-time recovery codes (shown once).</summary>
    [HttpPost("two-factor/enable"), EnableRateLimiting("auth")]
    public async Task<RecoveryCodesResponse> EnableTwoFactor(TwoFactorCodeRequest req, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return new RecoveryCodesResponse(await twoFactor.EnableAsync(tenant.UserId, req.Code, ct));
    }

    [HttpPost("two-factor/disable"), EnableRateLimiting("auth")]
    public async Task<IActionResult> DisableTwoFactor(DisableTwoFactorRequest req, CancellationToken ct)
    {
        await twoFactor.DisableAsync(tenant.UserId, req.Password, req.Code, ct);
        return NoContent();
    }

    /// <summary>New recovery codes (the old ones stop working).</summary>
    [HttpPost("two-factor/recovery-codes"), EnableRateLimiting("auth")]
    public async Task<RecoveryCodesResponse> RegenerateRecoveryCodes(TwoFactorCodeRequest req, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return new RecoveryCodesResponse(await twoFactor.RegenerateRecoveryCodesAsync(tenant.UserId, req.Code, ct));
    }

    /// <summary>Every remembered device (browser/app) has to enter a two-factor code again.</summary>
    [HttpDelete("two-factor/trusted-devices")]
    public async Task<ActionResult<object>> ForgetTrustedDevices(CancellationToken ct) =>
        Ok(new { forgotten = await twoFactor.ForgetDevicesAsync(tenant.UserId, ct) });

    private Guid? CurrentSessionId() =>
        Guid.TryParse(User.FindFirstValue(AuthClaims.SessionId), out var id) ? id : null;
}

/// <summary>
/// Settings > Users: edit the accounts of people in LOWER roles (SuperAdmin: everyone; Admin: Managers and Users;
/// Manager: Users). Needs Update on the rights-only page "settings-users"; MasterService enforces the hierarchy.
/// </summary>
[ApiController]
[Route("api/settings/users")]
public class SettingsUsersController(IMasterService master) : ControllerBase
{
    [HttpGet, RequirePage(PageKeys.SettingsUsers, PageAction.Update)]
    public Task<IReadOnlyList<OrgUserDto>> List(CancellationToken ct) => master.ListUsersAsync(ct);

    [HttpGet("roles"), RequirePage(PageKeys.SettingsUsers, PageAction.Update)]
    public Task<IReadOnlyList<RoleDto>> AssignableRoles(CancellationToken ct) => master.AssignableRolesAsync(ct);

    [HttpPut("{id:guid}"), RequirePage(PageKeys.SettingsUsers, PageAction.Update)]
    public Task<OrgUserDto> Update(Guid id, UpdateUserRequest req, CancellationToken ct) => master.UpdateUserAsync(id, req, ct);

    [HttpDelete("{id:guid}/two-factor"), RequirePage(PageKeys.SettingsUsers, PageAction.Update)]
    public Task<OrgUserDto> ResetTwoFactor(Guid id, CancellationToken ct) => master.ResetTwoFactorAsync(id, ct);
}
