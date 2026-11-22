using System.Net;
using Erp.Api.Data;
using Erp.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

/// <summary>
/// Emails a user when they sign in from a device they haven't used recently.
///
/// "Device" = app/browser family + operating system derived from the User-Agent (e.g. "Chrome on Windows",
/// "ERP Desktop app on Windows"), so browser updates don't count as a new device. The new sign-in is compared
/// with the user's other sessions still on record (at most <see cref="LookbackDays"/> days; ended sessions are
/// deleted after Auth:RetainEndedSessionsDays, default 30). No alert on an account's very first sign-in
/// (nothing to compare with: the account was just created). Failures never affect the login.
/// </summary>
public interface ILoginAlertService
{
    Task NotifyIfNewDeviceAsync(User user, Guid newSessionId, ClientInfo client, CancellationToken ct);
}

public sealed class LoginAlertService(ErpDbContext db, IEmailQueue email, IConfiguration cfg, TimeProvider clock,
    ILogger<LoginAlertService> log) : ILoginAlertService
{
    public const int LookbackDays = 180;
    private const int MaxSessionsCompared = 200;

    public async Task NotifyIfNewDeviceAsync(User user, Guid newSessionId, ClientInfo client, CancellationToken ct)
    {
        if (!cfg.GetValue("LOGIN_ALERTS_ENABLED", cfg.GetValue("LoginAlerts:Enabled", true))) return;
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var since = now.AddDays(-LookbackDays);
            var previous = await db.UserSessions.AsNoTracking()
                .Where(s => s.UserId == user.Id && s.Id != newSessionId && s.CreatedAt >= since)
                .OrderByDescending(s => s.CreatedAt).Take(MaxSessionsCompared)
                .Select(s => s.UserAgent).ToListAsync(ct);
            if (previous.Count == 0) return; // first sign-in of this account

            var device = DeviceInfo.Describe(client.UserAgent);
            if (previous.Any(ua => DeviceInfo.Describe(ua) == device)) return;

            email.Enqueue(NewDeviceEmail(user, device, client.IpAddress, now));
            log.LogInformation("New-device sign-in alert queued for user {UserId} ({Device})", user.Id, device);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not check sign-in device for user {UserId}", user.Id);
        }
    }

    internal static EmailMessage NewDeviceEmail(User user, string device, string? ip, DateTime atUtc)
    {
        var when = atUtc.ToString("dddd d MMMM yyyy, HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
        var where = string.IsNullOrWhiteSpace(ip) ? "unknown" : ip;
        const string subject = "New sign-in to your ERP account";

        var text = $"""
            Hi {user.FullName},

            Your ERP account ({user.Email}) was just signed in to from a new device.

              Device:     {device}
              IP address: {where}
              Time:       {when}

            If this was you, you don't need to do anything.

            If you don't recognise this sign-in, change your password right away and ask your
            administrator to sign out your other sessions.

            This is an automatic security message from ERP Platform.
            """;

        string H(string s) => WebUtility.HtmlEncode(s);
        var html = $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2937;max-width:520px">
              <h2 style="font-size:18px;margin:0 0 12px">New sign-in to your account</h2>
              <p>Hi {H(user.FullName)},</p>
              <p>Your ERP account (<strong>{H(user.Email)}</strong>) was just signed in to from a new device.</p>
              <table style="border-collapse:collapse;margin:12px 0">
                <tr><td style="padding:4px 16px 4px 0;color:#6b7280">Device</td><td style="padding:4px 0"><strong>{H(device)}</strong></td></tr>
                <tr><td style="padding:4px 16px 4px 0;color:#6b7280">IP address</td><td style="padding:4px 0">{H(where)}</td></tr>
                <tr><td style="padding:4px 16px 4px 0;color:#6b7280">Time</td><td style="padding:4px 0">{H(when)}</td></tr>
              </table>
              <p>If this was you, you don't need to do anything.</p>
              <p><strong>If you don't recognise this sign-in</strong>, change your password right away and ask your
                 administrator to sign out your other sessions.</p>
              <p style="color:#6b7280;font-size:12px;margin-top:24px">This is an automatic security message from ERP Platform.</p>
            </div>
            """;
        return new EmailMessage(user.Email, user.FullName, subject, text, html);
    }
}

/// <summary>Human-readable device from a User-Agent: app/browser family + OS, without versions.</summary>
public static class DeviceInfo
{
    public static string Describe(string? userAgent)
    {
        var ua = userAgent ?? "";
        if (ua.Length == 0) return "Unknown device";

        // Our desktop app: "ERP-Desktop/1.2.0 (win32) Electron/..."
        if (ua.StartsWith("ERP-Desktop/", StringComparison.OrdinalIgnoreCase))
        {
            var os = Contains(ua, "(win32)") ? "Windows" : Contains(ua, "(darwin)") ? "macOS" : Contains(ua, "(linux)") ? "Linux" : "unknown OS";
            return $"ERP Desktop app on {os}";
        }

        var browser =
            Contains(ua, "Edg/") || Contains(ua, "EdgA/") || Contains(ua, "EdgiOS/") ? "Edge" :
            Contains(ua, "OPR/") || Contains(ua, "Opera") ? "Opera" :
            Contains(ua, "SamsungBrowser/") ? "Samsung Internet" :
            Contains(ua, "Firefox/") || Contains(ua, "FxiOS/") ? "Firefox" :
            Contains(ua, "Chrome/") || Contains(ua, "CriOS/") ? "Chrome" :
            Contains(ua, "Safari/") ? "Safari" :
            null;

        var system =
            Contains(ua, "Windows") ? "Windows" :
            Contains(ua, "Android") ? "Android" :
            Contains(ua, "iPhone") || Contains(ua, "iPad") || Contains(ua, "iPod") ? "iOS" :
            Contains(ua, "CrOS") ? "ChromeOS" :
            Contains(ua, "Mac OS X") || Contains(ua, "Macintosh") ? "macOS" :
            Contains(ua, "Linux") ? "Linux" :
            null;

        return (browser, system) switch
        {
            (not null, not null) => $"{browser} on {system}",
            (not null, null) => browser,
            (null, not null) => $"App on {system}",
            _ => ua.Split(' ', 2)[0].Split('/')[0] is { Length: > 0 and <= 40 } name ? name : "Unknown device",
        };
    }

    private static bool Contains(string s, string part) => s.Contains(part, StringComparison.OrdinalIgnoreCase);
}
