using Erp.Api.Domain;
using Erp.Api.Services;
using FluentAssertions;

namespace Erp.Api.UnitTests;

public class RefreshTokenTests
{
    [Fact]
    public void Tokens_are_random_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 1000).Select(_ => RefreshTokens.Generate()).ToList();
        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().AllSatisfy(t =>
        {
            t.Length.Should().Be(43); // 32 bytes, base64url, no padding
            t.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        });
    }

    [Fact]
    public void Hash_is_deterministic_sha256_hex_and_not_the_token()
    {
        var t = RefreshTokens.Generate();
        var h = RefreshTokens.Hash(t);
        h.Should().Be(RefreshTokens.Hash(t));
        h.Should().MatchRegex("^[0-9a-f]{64}$");
        h.Should().NotContain(t);
        RefreshTokens.Hash(RefreshTokens.Generate()).Should().NotBe(h);
    }

    [Fact]
    public void Session_is_active_only_until_expiry_or_revocation()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var s = new UserSession { ExpiresAt = now.AddMinutes(1) };
        s.IsActive(now).Should().BeTrue();
        s.IsActive(now.AddMinutes(2)).Should().BeFalse();
        s.RevokedAt = now;
        s.IsActive(now).Should().BeFalse();
    }
}
