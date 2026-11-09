using System.Security.Cryptography;
using System.Text;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace Erp.Api.Services;

/// <summary>Where a request came from; stored on the session so users can recognise their devices.</summary>
public record ClientInfo(string? IpAddress, string? UserAgent);

public record RefreshOutcome(UserSession Session, User User, string RefreshToken);

public interface ISessionService
{
    Task<(UserSession Session, string RefreshToken)> CreateAsync(User user, ClientInfo client, CancellationToken ct);
    /// <summary>Validates and rotates a refresh token. Null if invalid, expired, revoked or reused.</summary>
    Task<RefreshOutcome?> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken ct);
    Task<Guid?> FindActiveSessionIdAsync(string refreshToken, CancellationToken ct);
    Task<bool> RevokeAsync(Guid sessionId, Guid? userId, string reason, CancellationToken ct);
    Task<int> RevokeAllForUserAsync(Guid userId, string reason, CancellationToken ct, Guid? exceptSessionId = null);
    Task<bool> IsActiveAsync(Guid sessionId, CancellationToken ct);
    Task<IReadOnlyList<UserSession>> ListActiveAsync(Guid userId, CancellationToken ct);
}

public static class RefreshTokens
{
    /// <summary>256 random bits, base64url. Never stored; only its hash is.</summary>
    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class SessionService(
    ErpDbContext db, IDistributedCache cache, IOptions<LoginSessionOptions> options, TimeProvider clock,
    ILogger<SessionService> log) : ISessionService
{
    private readonly LoginSessionOptions _opt = options.Value;
    // A rotated-out token seen again shortly after rotation is most likely a benign race
    // (two tabs refreshing at once), not theft. Reject it without killing the session.
    private static readonly TimeSpan ReuseGrace = TimeSpan.FromSeconds(30);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static string CacheKey(Guid id) => $"session-active:{id:N}";

    public async Task<(UserSession, string)> CreateAsync(User user, ClientInfo client, CancellationToken ct)
    {
        var token = RefreshTokens.Generate();
        var session = new UserSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, OrganizationId = user.OrganizationId,
            RefreshTokenHash = RefreshTokens.Hash(token),
            CreatedAt = Now, LastSeenAt = Now, ExpiresAt = Now.AddDays(_opt.SessionDays),
            IpAddress = Truncate(client.IpAddress, 64), UserAgent = Truncate(client.UserAgent, 512),
        };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return (session, token);
    }

    public async Task<RefreshOutcome?> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken ct)
    {
        var hash = RefreshTokens.Hash(refreshToken);
        var session = await db.UserSessions
            .Include(s => s.User).ThenInclude(u => u!.Role)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash || s.PreviousRefreshTokenHash == hash, ct);
        if (session is null || !session.IsActive(Now)) return null;

        if (session.RefreshTokenHash != hash)
        {
            if (Now - session.LastSeenAt > ReuseGrace)
            {
                log.LogWarning("Refresh token reuse detected for session {SessionId}; revoking", session.Id);
                await RevokeAsync(session.Id, null, "refresh_token_reuse", ct);
            }
            return null;
        }

        // Rotate. The WHERE on the current hash makes concurrent refreshes safe: only one wins.
        var newToken = RefreshTokens.Generate();
        var newHash = RefreshTokens.Hash(newToken);
        var now = Now;
        var ip = Truncate(client.IpAddress, 64);
        var ua = Truncate(client.UserAgent, 512);
        var updated = await db.UserSessions
            .Where(s => s.Id == session.Id && s.RefreshTokenHash == hash && s.RevokedAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.PreviousRefreshTokenHash, hash)
                .SetProperty(s => s.RefreshTokenHash, newHash)
                .SetProperty(s => s.LastSeenAt, now)
                .SetProperty(s => s.IpAddress, ip)
                .SetProperty(s => s.UserAgent, ua), ct);
        if (updated == 0) return null;

        return new RefreshOutcome(session, session.User!, newToken);
    }

    public async Task<Guid?> FindActiveSessionIdAsync(string refreshToken, CancellationToken ct)
    {
        var hash = RefreshTokens.Hash(refreshToken);
        var now = Now;
        return await db.UserSessions
            .Where(s => s.RefreshTokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<bool> RevokeAsync(Guid sessionId, Guid? userId, string reason, CancellationToken ct)
    {
        var now = Now;
        var n = await db.UserSessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null && (userId == null || s.UserId == userId))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, reason), ct);
        await MarkInactiveAsync(sessionId, ct);
        return n > 0;
    }

    public async Task<int> RevokeAllForUserAsync(Guid userId, string reason, CancellationToken ct, Guid? exceptSessionId = null)
    {
        var now = Now;
        var ids = await db.UserSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.Id != exceptSessionId)
            .Select(s => s.Id).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        await db.UserSessions.Where(s => ids.Contains(s.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedReason, reason), ct);
        foreach (var id in ids) await MarkInactiveAsync(id, ct);
        return ids.Count;
    }

    /// <summary>
    /// Called on every authenticated request (JwtBearer OnTokenValidated). Cached so that most requests
    /// don't hit Postgres; revocation overwrites the cache entry, so it takes effect immediately
    /// wherever the cache is shared (Redis), and within ActiveCheckCacheSeconds otherwise.
    /// </summary>
    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken ct)
    {
        var cached = await cache.GetStringAsync(CacheKey(sessionId), ct);
        if (cached is not null) return cached == "1";

        var now = Now;
        var active = await db.UserSessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.RevokedAt == null && s.ExpiresAt > now, ct);
        await cache.SetStringAsync(CacheKey(sessionId), active ? "1" : "0", new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_opt.ActiveCheckCacheSeconds)
        }, ct);
        return active;
    }

    public async Task<IReadOnlyList<UserSession>> ListActiveAsync(Guid userId, CancellationToken ct)
    {
        var now = Now;
        return await db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt).ToListAsync(ct);
    }

    private Task MarkInactiveAsync(Guid sessionId, CancellationToken ct) =>
        cache.SetStringAsync(CacheKey(sessionId), "0", new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(_opt.ActiveCheckCacheSeconds, 60))
        }, ct);

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

/// <summary>Deletes sessions that ended (expired or revoked) more than RetainEndedSessionsDays ago.</summary>
public sealed class SessionCleanupService(IServiceScopeFactory scopes, IOptions<LoginSessionOptions> options,
    TimeProvider clock, ILogger<SessionCleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
                var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-options.Value.RetainEndedSessionsDays);
                var n = await db.UserSessions
                    .Where(s => s.ExpiresAt < cutoff || (s.RevokedAt != null && s.RevokedAt < cutoff))
                    .ExecuteDeleteAsync(stoppingToken);
                if (n > 0) log.LogInformation("Deleted {Count} ended sessions", n);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Session cleanup failed; will retry");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
