using System.Text;
using Erp.Api.Services;
using FluentAssertions;

namespace Erp.Api.UnitTests;

public class TotpTests
{
    // RFC 6238 appendix B: SHA-1 key "12345678901234567890"; the 6-digit codes are the last 6 of the 8-digit vectors.
    private static readonly string RfcSecret = Totp.Base32Encode(Encoding.ASCII.GetBytes("12345678901234567890"));

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void Matches_the_RFC_6238_test_vectors(long unixSeconds, string expected) =>
        Totp.Code(RfcSecret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))).Should().Be(expected);

    [Fact]
    public void Base32_round_trips_and_uses_the_RFC_alphabet()
    {
        RfcSecret.Should().Be("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");
        var bytes = new byte[] { 0, 1, 2, 250, 255, 7, 9 };
        Totp.Base32Decode(Totp.Base32Encode(bytes)).Should().Equal(bytes);
        Totp.Base32Decode("gezd gnbv").Should().Equal(Totp.Base32Decode("GEZDGNBV")); // lower case and spaces as typed
    }

    [Fact]
    public void Accepts_one_step_of_clock_drift_but_not_more()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);
        Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 1), now, null).Should().Be(step - 1);
        Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step + 1), now, null).Should().Be(step + 1);
        Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 2), now, null).Should().BeNull();
    }

    [Fact]
    public void Rejects_a_code_that_was_already_used()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);
        var code = Totp.Code(RfcSecret, step);
        Totp.Verify(RfcSecret, code, now, lastStep: step).Should().BeNull();
        Totp.Verify(RfcSecret, code, now, lastStep: step - 1).Should().Be(step);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abcdef")]
    public void Rejects_malformed_codes(string code) =>
        Totp.Verify(RfcSecret, code, DateTimeOffset.UtcNow, null).Should().BeNull();

    [Fact]
    public void Ignores_spaces_in_a_typed_code()
    {
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Code(RfcSecret, Totp.StepAt(now));
        Totp.Verify(RfcSecret, $"{code[..3]} {code[3..]}", now, null).Should().NotBeNull();
    }

    [Fact]
    public void Provisioning_uri_has_what_authenticator_apps_need()
    {
        var uri = Totp.ProvisioningUri("ERP Platform", "a@b.com", "ABC");
        uri.Should().StartWith("otpauth://totp/ERP%20Platform:a%40b.com?secret=ABC&issuer=ERP%20Platform");
        uri.Should().Contain("digits=6").And.Contain("period=30");
    }

    [Fact]
    public void New_secrets_are_160_bit_and_unique()
    {
        var a = Totp.NewSecret();
        Totp.Base32Decode(a).Should().HaveCount(20);
        a.Should().NotBe(Totp.NewSecret());
    }

    [Fact]
    public void Recovery_codes_hash_case_and_dash_insensitively()
    {
        TwoFactorService.HashRecoveryCode("AB3D-EF7H").Should().Be(TwoFactorService.HashRecoveryCode("ab3def7h"));
        TwoFactorService.HashRecoveryCode("short").Should().BeNull();
    }
}
