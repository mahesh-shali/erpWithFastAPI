using System.Security.Claims;
using Erp.Api.Contracts;
using Erp.Api.Infrastructure;
using Erp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Erp.Api.Controllers;

/// <summary>
/// Session-based login:
///  - login/register create a row in user_sessions and set an HttpOnly refresh cookie (path /api/auth);
///  - the body carries a short-lived access token (JWT with a "sid" claim) for the Authorization header;
///  - refresh rotates the cookie and issues a new access token (role re-read from the database);
///  - logout / session revocation makes every access token of that session invalid immediately.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService auth, ISessionService sessions, IOptions<LoginSessionOptions> options,
    IConfiguration cfg) : ControllerBase
{
    private readonly LoginSessionOptions _opt = options.Value;

    /// <summary>Self-service sign-up of a new organization. Off in production (Auth:AllowRegistration=false).</summary>
    [HttpPost("register"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req, CancellationToken ct)
    {
        if (!cfg.GetValue("Auth:AllowRegistration", true))
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails { Title = "Registration is disabled." });
        return Issue(await auth.RegisterAsync(req, Client(), ct));
    }

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req, CancellationToken ct)
    {
        var res = await auth.LoginAsync(req, Client(), ct);
        return res is null ? Unauthorized(new ProblemDetails { Title = "Invalid email or password." }) : Issue(res);
    }

    /// <summary>New access token from the refresh cookie. The cookie is rotated on every call.</summary>
    [HttpPost("refresh"), AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Refresh(CancellationToken ct)
    {
        var token = Request.Cookies[_opt.CookieName];
        var res = string.IsNullOrEmpty(token) ? null : await auth.RefreshAsync(token, Client(), ct);
        if (res is null)
        {
            ClearCookie();
            return Unauthorized(new ProblemDetails { Title = "Session expired. Please sign in again." });
        }
        return Issue(res);
    }

    /// <summary>Ends the current session (by access token if present, else by refresh cookie).</summary>
    [HttpPost("logout"), AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await auth.LogoutAsync(CurrentSessionId(), Request.Cookies[_opt.CookieName], ct);
        ClearCookie();
        return NoContent();
    }

    [HttpGet("me"), Authorize]
    public ActionResult<UserDto> Me() => new UserDto(
        Guid.Parse(User.FindFirstValue("sub")!), User.FindFirstValue("email") ?? "",
        User.FindFirstValue("name") ?? "", User.FindFirstValue(AuthClaims.Role) ?? "",
        Guid.Parse(User.FindFirstValue(CustomClaims.OrgId)!));

    /// <summary>The caller's active sessions (devices).</summary>
    [HttpGet("sessions"), Authorize]
    public async Task<IReadOnlyList<SessionDto>> Sessions(CancellationToken ct)
    {
        var current = CurrentSessionId();
        return (await sessions.ListActiveAsync(UserId(), ct))
            .Select(s => new SessionDto(s.Id, s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.IpAddress, s.UserAgent, s.Id == current))
            .ToList();
    }

    /// <summary>Sign out one of the caller's own sessions (e.g. a lost device).</summary>
    [HttpDelete("sessions/{id:guid}"), Authorize]
    public async Task<IActionResult> RevokeSession(Guid id, CancellationToken ct)
        => await sessions.RevokeAsync(id, UserId(), "revoked_by_user", ct) ? NoContent() : NotFound();

    /// <summary>Sign out everywhere except this session.</summary>
    [HttpPost("logout-others"), Authorize]
    public async Task<ActionResult<object>> LogoutOthers(CancellationToken ct)
        => Ok(new { revoked = await sessions.RevokeAllForUserAsync(UserId(), "logout_others", ct, CurrentSessionId()) });

    // ---------- helpers ----------
    private ActionResult<AuthResponse> Issue(AuthResult r)
    {
        Response.Cookies.Append(_opt.CookieName, r.RefreshToken, CookieOptions(r.SessionExpiresAt));
        Response.Headers.CacheControl = "no-store";
        return Ok(r.Response);
    }

    private void ClearCookie() => Response.Cookies.Delete(_opt.CookieName, CookieOptions(null));

    private CookieOptions CookieOptions(DateTime? expires) => new()
    {
        HttpOnly = true,                 // not readable by JavaScript (XSS can't steal it)
        Secure = Request.IsHttps,        // HTTPS in production (X-Forwarded-Proto from the proxy)
        SameSite = SameSiteMode.Strict,  // not sent on cross-site requests (CSRF)
        Path = "/api/auth",              // only sent to the auth endpoints
        Expires = expires,
        IsEssential = true,
    };

    private ClientInfo Client() => new(HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);

    private Guid? CurrentSessionId() =>
        Guid.TryParse(User.FindFirstValue(AuthClaims.SessionId), out var id) ? id : null;
}
