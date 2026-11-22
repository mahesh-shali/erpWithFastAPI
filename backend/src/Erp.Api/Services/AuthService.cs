using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

/// <summary>Response body plus the refresh token, which the controller puts in an HttpOnly cookie.</summary>
public record AuthResult(AuthResponse Response, string RefreshToken, DateTime SessionExpiresAt);

public enum LoginStatus { Success, InvalidCredentials, TwoFactorRequired, InvalidTwoFactorCode }

/// <summary>
/// Result of a login attempt; <see cref="Auth"/> is set only on success. <see cref="TrustedDevice"/> is set when the
/// user verified a code and asked to remember this device (the controller puts it in an HttpOnly cookie).
/// </summary>
public sealed record LoginOutcome(LoginStatus Status, AuthResult? Auth = null, TrustedDeviceToken? TrustedDevice = null)
{
    public static readonly LoginOutcome InvalidCredentials = new(LoginStatus.InvalidCredentials);
    public static readonly LoginOutcome TwoFactorRequired = new(LoginStatus.TwoFactorRequired);
    public static readonly LoginOutcome InvalidTwoFactorCode = new(LoginStatus.InvalidTwoFactorCode);
}

public interface IAuthService
{
    Task<AuthResult> RegisterAsync(RegisterRequest req, ClientInfo client, CancellationToken ct);
    /// <param name="trustedDeviceToken">The device's "remember this device" cookie, if any.</param>
    Task<LoginOutcome> LoginAsync(LoginRequest req, ClientInfo client, string? trustedDeviceToken, CancellationToken ct);
    Task<AuthResult?> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken ct);
    Task LogoutAsync(Guid? sessionId, string? refreshToken, CancellationToken ct);
}

public sealed class AuthService(ErpDbContext db, IPasswordHasher hasher, ITokenService tokens, ISessionService sessions,
    ILoginAlertService loginAlerts, ITwoFactorService twoFactor, TimeProvider clock) : IAuthService
{
    public async Task<AuthResult> RegisterAsync(RegisterRequest req, ClientInfo client, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("Email already registered.");

        var org = new Organization { Id = Guid.NewGuid(), Name = req.OrganizationName.Trim() };
        var user = new User
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            Email = email,
            FullName = req.FullName.Trim(),
            PasswordHash = hasher.Hash(req.Password),
            RoleId = Roles.SuperAdminId, // first user of an org owns it
            LastLoginAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Organizations.Add(org);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return await StartSessionAsync(user, Roles.SuperAdmin, client, ct);
    }

    public async Task<LoginOutcome> LoginAsync(LoginRequest req, ClientInfo client, string? trustedDeviceToken, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var user = await db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            hasher.Verify(req.Password, DummyHash); // same cost as a real check: don't leak which emails exist
            return LoginOutcome.InvalidCredentials;
        }
        if (!hasher.Verify(req.Password, user.PasswordHash)) return LoginOutcome.InvalidCredentials;

        // Two-factor: password alone is not enough, unless this device was remembered within the last 30 days.
        // Otherwise the client asks for the code and sends the login again with it.
        TrustedDeviceToken? trusted = null;
        if (user.TwoFactorEnabled && !await twoFactor.IsTrustedDeviceAsync(user.Id, trustedDeviceToken, ct))
        {
            if (string.IsNullOrWhiteSpace(req.Code)) return LoginOutcome.TwoFactorRequired;
            if (!twoFactor.VerifyAndConsume(user, req.Code)) return LoginOutcome.InvalidTwoFactorCode;
            if (req.RememberDevice) trusted = await twoFactor.TrustDeviceAsync(user.Id, client, ct);
        }

        user.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct); // also stores the used TOTP step / recovery code
        var result = await StartSessionAsync(user, user.Role!.Name, client, ct);
        await loginAlerts.NotifyIfNewDeviceAsync(user, result.Response.SessionId, client, ct); // email is queued, never blocks
        return new LoginOutcome(LoginStatus.Success, result, trusted);
    }

    public async Task<AuthResult?> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken ct)
    {
        var r = await sessions.RefreshAsync(refreshToken, client, ct);
        if (r is null) return null;
        // Role is re-read from the database on every refresh, so role changes reach the token.
        var (token, exp) = tokens.CreateAccessToken(r.User, r.User.Role!.Name, r.Session.Id);
        var response = new AuthResponse(token, exp, ToDto(r.User, r.User.Role!.Name), r.Session.Id, r.Session.ExpiresAt);
        return new AuthResult(response, r.RefreshToken, r.Session.ExpiresAt);
    }

    public async Task LogoutAsync(Guid? sessionId, string? refreshToken, CancellationToken ct)
    {
        var id = sessionId;
        if (id is null && !string.IsNullOrEmpty(refreshToken))
            id = await sessions.FindActiveSessionIdAsync(refreshToken, ct);
        if (id is not null) await sessions.RevokeAsync(id.Value, null, "logout", ct);
    }

    private async Task<AuthResult> StartSessionAsync(User user, string role, ClientInfo client, CancellationToken ct)
    {
        var (session, refresh) = await sessions.CreateAsync(user, client, ct);
        var (token, exp) = tokens.CreateAccessToken(user, role, session.Id);
        return new AuthResult(new AuthResponse(token, exp, ToDto(user, role), session.Id, session.ExpiresAt),
            refresh, session.ExpiresAt);
    }

    public static UserDto ToDto(User u, string role) => new(u.Id, u.Email, u.FullName, role, u.OrganizationId);

    // Any valid BCrypt hash with the same work factor (11); the verify result is ignored.
    // Only there so that unknown emails take as long as known ones.
    private const string DummyHash = "$2a$11$V76uTc8jRWnA6YTqhH2cveSrOfZ1roFle/sp8tWjHRJOtR/f.qBp.";
}
