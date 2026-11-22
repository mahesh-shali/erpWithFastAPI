using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

/// <summary>
/// Settings > My account: the caller's own profile and password. Open to every signed-in user (no page right);
/// changing your role or rights is not possible here (that needs someone above you).
/// </summary>
public interface IAccountService
{
    Task<AccountDto> GetAsync(CancellationToken ct);
    Task<AccountDto> UpdateProfileAsync(UpdateProfileRequest req, CancellationToken ct);
    /// <summary>Changes the password and signs out every other session of the caller.</summary>
    Task ChangePasswordAsync(ChangePasswordRequest req, Guid? currentSessionId, CancellationToken ct);
}

public sealed class AccountService(ErpDbContext db, ITenantContext tenant, IPasswordHasher hasher, ISessionService sessions,
    ITwoFactorService twoFactor) : IAccountService
{
    public async Task<AccountDto> GetAsync(CancellationToken ct)
    {
        var me = await db.Users.AsNoTracking().Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == tenant.UserId, ct)
                 ?? throw new NotFoundException("User not found.");
        return ToDto(me) with { TrustedDevices = await twoFactor.CountTrustedDevicesAsync(me.Id, ct) };
    }

    public async Task<AccountDto> UpdateProfileAsync(UpdateProfileRequest req, CancellationToken ct)
    {
        var me = await MeAsync(ct);
        var email = req.Email.Trim().ToLowerInvariant();
        if (email != me.Email)
        {
            // The email is the login name: changing it needs the password, so an unlocked screen isn't enough.
            if (string.IsNullOrEmpty(req.CurrentPassword) || !hasher.Verify(req.CurrentPassword, me.PasswordHash))
                throw new DomainException("Enter your current password to change your email.");
            if (await db.Users.AnyAsync(u => u.Email == email && u.Id != me.Id, ct))
                throw new ConflictException("Email already registered.");
            me.Email = email;
        }
        me.FullName = req.FullName.Trim();
        await db.SaveChangesAsync(ct);
        return ToDto(me);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest req, Guid? currentSessionId, CancellationToken ct)
    {
        var me = await MeAsync(ct);
        if (!hasher.Verify(req.CurrentPassword, me.PasswordHash)) throw new DomainException("Your current password is wrong.");
        me.PasswordHash = hasher.Hash(req.NewPassword);
        await db.SaveChangesAsync(ct);
        await sessions.RevokeAllForUserAsync(me.Id, "password_changed", ct, currentSessionId);
        await twoFactor.ForgetDevicesAsync(me.Id, ct); // a new password: every device proves the second factor again
    }

    private async Task<Domain.User> MeAsync(CancellationToken ct) =>
        await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == tenant.UserId, ct)
        ?? throw new NotFoundException("User not found.");

    private static AccountDto ToDto(Domain.User u) =>
        new(u.Id, u.Email, u.FullName, u.Role!.Name, u.TwoFactorEnabled, u.TwoFactorRecoveryCodes.Count);
}
