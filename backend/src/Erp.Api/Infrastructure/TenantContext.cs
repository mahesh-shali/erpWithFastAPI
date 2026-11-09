using System.Security.Claims;

namespace Erp.Api.Infrastructure;

public interface ITenantContext
{
    Guid OrganizationId { get; }
    Guid UserId { get; }
}

public static class CustomClaims
{
    public const string OrgId = "org_id";
}

/// <summary>A fixed tenant, for background work with no HTTP request (seeding, design-time tooling).</summary>
public sealed class FixedTenantContext(Guid organizationId, Guid userId = default) : ITenantContext
{
    public Guid OrganizationId { get; } = organizationId;
    public Guid UserId { get; } = userId;
}

/// <summary>Resolves tenant + user from the JWT of the current request.</summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public Guid OrganizationId =>
        Guid.TryParse(User?.FindFirstValue(CustomClaims.OrgId), out var id) ? id : Guid.Empty;

    public Guid UserId =>
        Guid.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? User?.FindFirstValue("sub"), out var id)
            ? id : Guid.Empty;
}
