using System.IdentityModel.Tokens.Jwt;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Erp.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Erp.Api.UnitTests;

public class TokenServiceTests
{
    private static readonly JwtOptions Opts = new()
    {
        Secret = "unit-test-secret-unit-test-secret-1234567890", Issuer = "erp-api", Audience = "erp-clients",
        AccessTokenMinutes = 60
    };

    private static User SampleUser() => new()
    {
        Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Email = "a@b.com", FullName = "A B", RoleId = Roles.AdminId
    };

    private static readonly Guid SessionId = Guid.NewGuid();

    [Fact]
    public void Token_contains_tenant_role_and_subject_claims()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var user = SampleUser();
        var (token, _) = new TokenService(Options.Create(Opts), clock).CreateAccessToken(user, Roles.Admin, SessionId);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Subject.Should().Be(user.Id.ToString());
        jwt.Claims.Should().Contain(c => c.Type == CustomClaims.OrgId && c.Value == user.OrganizationId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "role" && c.Value == Roles.Admin);
        jwt.Claims.Should().Contain(c => c.Type == AuthClaims.SessionId && c.Value == SessionId.ToString());
        jwt.Issuer.Should().Be("erp-api");
        jwt.Audiences.Should().Contain("erp-clients");
    }

    [Fact]
    public void Token_expires_after_configured_minutes()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (_, expires) = new TokenService(Options.Create(Opts), new FakeTimeProvider(now)).CreateAccessToken(SampleUser(), Roles.User, SessionId);
        expires.Should().Be(now.UtcDateTime.AddMinutes(60));
    }

    [Fact]
    public void Token_never_contains_password_hash()
    {
        var u = SampleUser();
        u.PasswordHash = "SUPER_SECRET_HASH";
        var (token, _) = new TokenService(Options.Create(Opts), TimeProvider.System).CreateAccessToken(u, Roles.User, SessionId);
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Should().NotContain(c => c.Value.Contains("SUPER_SECRET"));
    }
}
