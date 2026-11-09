using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims; // Claim
using System.Text;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Erp.Api.Services;

public interface ITokenService
{
    /// <param name="role">Role name, loaded from the roles table.</param>
    /// <param name="sessionId">Login session the token belongs to ("sid"); revoking it kills the token.</param>
    (string Token, DateTime ExpiresAt) CreateAccessToken(User user, string role, Guid sessionId);
}

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _opt = options.Value;

    public (string Token, DateTime ExpiresAt) CreateAccessToken(User user, string role, Guid sessionId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_opt.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(AuthClaims.Role, role),
            new Claim(AuthClaims.SessionId, sessionId.ToString()),
            new Claim(CustomClaims.OrgId, user.OrganizationId.ToString()),
            new Claim("name", user.FullName),
        };

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
