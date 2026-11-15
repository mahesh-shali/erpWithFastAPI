using Microsoft.Extensions.Caching.Distributed;

namespace Erp.Api.Services;

/// <summary>
/// Per-account brute-force protection, on top of the per-IP "auth" rate limit (which an attacker can
/// sidestep by rotating IPs). After <c>Auth:LockoutThreshold</c> failed logins for one email within
/// <c>Auth:LockoutMinutes</c>, further attempts for that email are refused until the window passes.
/// Keyed by email whether or not the account exists, so it doesn't reveal which emails are registered.
/// State lives in the distributed cache (Redis in production), so it holds across API instances.
/// </summary>
public interface ILoginThrottle
{
    Task<bool> IsLockedOutAsync(string email, CancellationToken ct);
    Task RecordFailureAsync(string email, CancellationToken ct);
    Task ResetAsync(string email, CancellationToken ct);
    TimeSpan Window { get; }
}

public sealed class LoginThrottle(IDistributedCache cache, IConfiguration cfg) : ILoginThrottle
{
    private readonly int _threshold = cfg.GetValue("Auth:LockoutThreshold", 5);
    public TimeSpan Window { get; } = TimeSpan.FromMinutes(cfg.GetValue("Auth:LockoutMinutes", 15));

    private static string Key(string email) => "login-fail:" + email.Trim().ToLowerInvariant();

    public async Task<bool> IsLockedOutAsync(string email, CancellationToken ct) =>
        _threshold > 0 && await CountAsync(email, ct) >= _threshold;

    public async Task RecordFailureAsync(string email, CancellationToken ct)
    {
        if (_threshold <= 0) return;
        var count = await CountAsync(email, ct) + 1;
        // Each failure extends the window: a slow, steady guesser stays locked out.
        await cache.SetStringAsync(Key(email), count.ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Window }, ct);
    }

    public Task ResetAsync(string email, CancellationToken ct) => cache.RemoveAsync(Key(email), ct);

    private async Task<int> CountAsync(string email, CancellationToken ct) =>
        int.TryParse(await cache.GetStringAsync(Key(email), ct), out var n) ? n : 0;
}
