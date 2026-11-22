namespace Erp.Api.Infrastructure;

public sealed class JwtOptions
{
    public string Secret { get; set; } = default!;
    public string Issuer { get; set; } = "erp-api";
    public string Audience { get; set; } = "erp-clients";
    /// <summary>Short-lived: clients renew via the session's refresh token (POST /api/auth/refresh).</summary>
    public int AccessTokenMinutes { get; set; } = 15;
}

/// <summary>Login-session settings (section "Auth").</summary>
public sealed class LoginSessionOptions
{
    /// <summary>Absolute session lifetime; after this the user must sign in again.</summary>
    public int SessionDays { get; set; } = 14;
    /// <summary>HttpOnly cookie holding the refresh token. Scoped to /api/auth.</summary>
    public string CookieName { get; set; } = "erp_refresh";
    /// <summary>How long an "is this session active?" answer may be cached per session id.</summary>
    public int ActiveCheckCacheSeconds { get; set; } = 30;
    /// <summary>Revoked/expired sessions are deleted after this many days (cleanup job).</summary>
    public int RetainEndedSessionsDays { get; set; } = 30;
    /// <summary>"Remember this device" on the two-factor step: days a device may skip the code (0 = feature off).</summary>
    public int TwoFactorRememberDays { get; set; } = 30;
    /// <summary>HttpOnly cookie holding the trusted-device token. Scoped to /api/auth, survives sign-out.</summary>
    public string TrustedDeviceCookieName { get; set; } = "erp_2fa_trust";
}

public static class AuthClaims
{
    public const string SessionId = "sid";
    public const string Role = "role";
}
