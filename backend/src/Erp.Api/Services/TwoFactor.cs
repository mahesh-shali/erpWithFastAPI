using System.Security.Cryptography;
using System.Text;
using Erp.Api.Data;
using Erp.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

// ------------------------------------------------------------------------------------------------
// Two-factor sign-in with an authenticator app (Google/Microsoft Authenticator, 1Password, Authy...).
// Codes are TOTP (RFC 6238): HMAC-SHA1, 6 digits, 30-second steps, +-1 step of clock drift.
// Flow: setup (secret stored, still off) -> enable with a first code (returns recovery codes) -> every
// login then needs a code. A code is accepted once (TwoFactorLastStep); recovery codes are single use.
// ------------------------------------------------------------------------------------------------

/// <summary>TOTP (RFC 6238) and Base32 (RFC 4648), the format authenticator apps expect.</summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    /// <summary>160-bit random secret (the RFC 4226 recommendation), Base32 without padding.</summary>
    public static string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / PeriodSeconds;

    public static string Code(string secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--, step >>= 8) counter[i] = (byte)(step & 0xff);
        var hash = HMACSHA1.HashData(Base32Decode(secret), counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>The matching time step (within +-<paramref name="window"/>) newer than <paramref name="lastStep"/>, else null.</summary>
    public static long? Verify(string secret, string code, DateTimeOffset now, long? lastStep, int window = 1)
    {
        code = new string(code.Where(char.IsDigit).ToArray());
        if (code.Length != Digits) return null;
        var current = StepAt(now);
        for (var step = current - window; step <= current + window; step++)
        {
            if (lastStep is not null && step <= lastStep) continue; // already used: no replay
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(code)))
                return step;
        }
        return null;
    }

    /// <summary>otpauth:// URI that authenticator apps read from the QR code.</summary>
    public static string ProvisioningUri(string issuer, string account, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string text)
    {
        var clean = text.TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Invalid Base32 character.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}

public sealed record TwoFactorSetup(string Secret, string OtpAuthUri);

public interface ITwoFactorService
{
    /// <summary>New secret for the user (2FA stays off until <see cref="EnableAsync"/> confirms a code).</summary>
    Task<TwoFactorSetup> BeginSetupAsync(Guid userId, CancellationToken ct);
    /// <summary>Turns 2FA on after a valid code from the new secret. Returns the recovery codes (shown once).</summary>
    Task<IReadOnlyList<string>> EnableAsync(Guid userId, string code, CancellationToken ct);
    /// <summary>Turns 2FA off; needs the password and a current code (or a recovery code).</summary>
    Task DisableAsync(Guid userId, string password, string code, CancellationToken ct);
    Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid userId, string code, CancellationToken ct);
    /// <summary>Login check: a TOTP code or an unused recovery code. Marks it used (caller saves).</summary>
    bool VerifyAndConsume(User user, string code);
    /// <summary>Clears all 2FA data (turned off, or reset by a superior for a user who lost the phone).</summary>
    void Clear(User user);

    // ---- "Remember this device" (skip the code on a trusted browser/app for Auth:TwoFactorRememberDays) ----
    /// <summary>True if <paramref name="token"/> is an unexpired trusted-device token of this user.</summary>
    Task<bool> IsTrustedDeviceAsync(Guid userId, string? token, CancellationToken ct);
    /// <summary>Remembers the device the user just verified on; null when the feature is off (0 days).</summary>
    Task<TrustedDeviceToken?> TrustDeviceAsync(Guid userId, ClientInfo client, CancellationToken ct);
    /// <summary>Every device must enter a code again (2FA off/reset, password changed, or "Forget devices").</summary>
    Task<int> ForgetDevicesAsync(Guid userId, CancellationToken ct);
    Task<int> CountTrustedDevicesAsync(Guid userId, CancellationToken ct);
}

/// <summary>The raw token goes into the device's HttpOnly cookie; the database only has its hash.</summary>
public sealed record TrustedDeviceToken(string Token, DateTime ExpiresAt);

public sealed class TwoFactorService(ErpDbContext db, IPasswordHasher hasher, TimeProvider clock, IConfiguration cfg,
    Microsoft.Extensions.Options.IOptions<Infrastructure.LoginSessionOptions> sessionOptions)
    : ITwoFactorService
{
    public const int RecoveryCodeCount = 10;
    private string Issuer => cfg["Auth:TwoFactorIssuer"] ?? "ERP Platform";
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<bool> IsTrustedDeviceAsync(Guid userId, string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128 || sessionOptions.Value.TwoFactorRememberDays <= 0) return false;
        var hash = HashToken(token);
        return await db.TrustedDevices.AnyAsync(d => d.TokenHash == hash && d.UserId == userId && d.ExpiresAt > Now, ct);
    }

    public async Task<TrustedDeviceToken?> TrustDeviceAsync(Guid userId, ClientInfo client, CancellationToken ct)
    {
        var days = sessionOptions.Value.TwoFactorRememberDays;
        if (days <= 0) return null;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var device = new TrustedDevice
        {
            Id = Guid.NewGuid(), UserId = userId, TokenHash = HashToken(token), CreatedAt = Now, ExpiresAt = Now.AddDays(days),
            IpAddress = Cut(client.IpAddress, 64), UserAgent = Cut(client.UserAgent, 512),
        };
        db.TrustedDevices.Add(device);
        await db.SaveChangesAsync(ct);
        return new TrustedDeviceToken(token, device.ExpiresAt);
    }

    public Task<int> ForgetDevicesAsync(Guid userId, CancellationToken ct) =>
        db.TrustedDevices.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);

    public Task<int> CountTrustedDevicesAsync(Guid userId, CancellationToken ct) =>
        db.TrustedDevices.CountAsync(d => d.UserId == userId && d.ExpiresAt > Now, ct);

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string? Cut(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    public async Task<TwoFactorSetup> BeginSetupAsync(Guid userId, CancellationToken ct)
    {
        var user = await UserAsync(userId, ct);
        if (user.TwoFactorEnabled) throw new ConflictException("Two-factor sign-in is already on. Turn it off first to use a new device.");
        user.TwoFactorSecret = Totp.NewSecret();
        user.TwoFactorLastStep = null;
        await db.SaveChangesAsync(ct);
        return new TwoFactorSetup(user.TwoFactorSecret, Totp.ProvisioningUri(Issuer, user.Email, user.TwoFactorSecret));
    }

    public async Task<IReadOnlyList<string>> EnableAsync(Guid userId, string code, CancellationToken ct)
    {
        var user = await UserAsync(userId, ct);
        if (user.TwoFactorEnabled) throw new ConflictException("Two-factor sign-in is already on.");
        if (user.TwoFactorSecret is null) throw new DomainException("Start the setup first.");
        var step = Totp.Verify(user.TwoFactorSecret, code, clock.GetUtcNow(), user.TwoFactorLastStep)
                   ?? throw new DomainException("That code is not valid. Check the time on your phone and try again.");
        user.TwoFactorEnabled = true;
        user.TwoFactorLastStep = step;
        var codes = NewRecoveryCodes(user);
        await db.SaveChangesAsync(ct);
        return codes;
    }

    public async Task DisableAsync(Guid userId, string password, string code, CancellationToken ct)
    {
        var user = await UserAsync(userId, ct);
        if (!user.TwoFactorEnabled) return;
        if (!hasher.Verify(password, user.PasswordHash)) throw new DomainException("Wrong password.");
        if (!VerifyAndConsume(user, code)) throw new DomainException("That code is not valid.");
        Clear(user);
        await db.SaveChangesAsync(ct);
        await ForgetDevicesAsync(user.Id, ct);
    }

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid userId, string code, CancellationToken ct)
    {
        var user = await UserAsync(userId, ct);
        if (!user.TwoFactorEnabled) throw new DomainException("Two-factor sign-in is off.");
        if (!VerifyAndConsume(user, code)) throw new DomainException("That code is not valid.");
        var codes = NewRecoveryCodes(user);
        await db.SaveChangesAsync(ct);
        return codes;
    }

    public bool VerifyAndConsume(User user, string code)
    {
        if (!user.TwoFactorEnabled || user.TwoFactorSecret is null || string.IsNullOrWhiteSpace(code)) return false;
        var step = Totp.Verify(user.TwoFactorSecret, code, clock.GetUtcNow(), user.TwoFactorLastStep);
        if (step is not null)
        {
            user.TwoFactorLastStep = step;
            return true;
        }
        var hash = HashRecoveryCode(code);
        if (hash is null || !user.TwoFactorRecoveryCodes.Contains(hash)) return false;
        user.TwoFactorRecoveryCodes = user.TwoFactorRecoveryCodes.Where(h => h != hash).ToList(); // new list: EF sees the change
        return true;
    }

    public void Clear(User user)
    {
        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.TwoFactorLastStep = null;
        user.TwoFactorRecoveryCodes = [];
    }

    /// <summary>10 codes like "k7f3-9xq2" (40 random bits each); only their hashes are stored.</summary>
    private static List<string> NewRecoveryCodes(User user)
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789"; // no 0/o, 1/l/i
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ =>
        {
            var s = RandomNumberGenerator.GetString(chars, 8);
            return $"{s[..4]}-{s[4..]}";
        }).ToList();
        user.TwoFactorRecoveryCodes = codes.Select(c => HashRecoveryCode(c)!).ToList();
        return codes;
    }

    /// <summary>Case/spacing-insensitive; null for input that can't be a recovery code.</summary>
    internal static string? HashRecoveryCode(string code)
    {
        var norm = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return norm.Length == 8 ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(norm))).ToLowerInvariant() : null;
    }

    private async Task<User> UserAsync(Guid id, CancellationToken ct) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("User not found.");
}
