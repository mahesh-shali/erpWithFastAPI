using System.Security.Claims;
using System.Text.Json;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Erp.Api.Services;

public interface IPermissionService
{
    /// <summary>Effective rights of a user on every active page (cached; invalidated on any rights change in the org).</summary>
    Task<IReadOnlyList<EffectivePagePermission>> GetEffectiveAsync(Guid userId, Guid organizationId, CancellationToken ct);
    Task<bool> HasAsync(Guid userId, Guid organizationId, string pageKey, PageAction action, CancellationToken ct);
    /// <summary>Call after any change to role or user rights (or a user's role) in the organization.</summary>
    Task InvalidateOrganizationAsync(Guid organizationId, CancellationToken ct);
}

public sealed class PermissionService(ErpDbContext db, IDistributedCache cache) : IPermissionService
{
    private static readonly DistributedCacheEntryOptions CacheFor = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };
    private static string VersionKey(Guid org) => $"perms-ver:{org:N}";

    public async Task<IReadOnlyList<EffectivePagePermission>> GetEffectiveAsync(Guid userId, Guid organizationId, CancellationToken ct)
    {
        // Org-versioned cache key: bumping the version invalidates every user of the org in O(1).
        var version = await cache.GetStringAsync(VersionKey(organizationId), ct) ?? "0";
        var key = $"perms:{organizationId:N}:{version}:{userId:N}";
        if (await cache.GetStringAsync(key, ct) is { } hit)
            return JsonSerializer.Deserialize<List<EffectivePagePermission>>(hit)!;

        var rows = await db.EffectivePermissions(userId).AsNoTracking().ToListAsync(ct);
        await cache.SetStringAsync(key, JsonSerializer.Serialize(rows), CacheFor, ct);
        return rows;
    }

    public async Task<bool> HasAsync(Guid userId, Guid organizationId, string pageKey, PageAction action, CancellationToken ct)
    {
        var rights = await GetEffectiveAsync(userId, organizationId, ct);
        return rights.FirstOrDefault(r => r.PageKey == pageKey)?.Allows(action) ?? false;
    }

    public Task InvalidateOrganizationAsync(Guid organizationId, CancellationToken ct) =>
        cache.SetStringAsync(VersionKey(organizationId), Guid.NewGuid().ToString("N"),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) }, ct);
}

// ---------------- Authorization: [RequirePage(PageKeys.Products, PageAction.Write)] ----------------

/// <summary>Requires the given right on a page (from the Pages master), evaluated against the database rights.</summary>
public sealed class RequirePageAttribute : AuthorizeAttribute
{
    public const string Prefix = "page:";

    public RequirePageAttribute(string pageKey, PageAction action = PageAction.Read)
        => Policy = $"{Prefix}{pageKey}:{action}";
}

public sealed record PageRequirement(string PageKey, PageAction Action) : IAuthorizationRequirement;

/// <summary>Builds "page:{key}:{action}" policies on demand, so no policy has to be registered per page.</summary>
public sealed class PagePolicyProvider(Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(RequirePageAttribute.Prefix, StringComparison.Ordinal))
        {
            var parts = policyName[RequirePageAttribute.Prefix.Length..].Split(':');
            if (parts.Length == 2 && Enum.TryParse<PageAction>(parts[1], out var action))
                return new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
                    .AddRequirements(new PageRequirement(parts[0], action)).Build();
        }
        return await base.GetPolicyAsync(policyName);
    }
}

public sealed class PageRequirementHandler(IPermissionService permissions, IHttpContextAccessor http)
    : AuthorizationHandler<PageRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext ctx, PageRequirement req)
    {
        if (!Guid.TryParse(ctx.User.FindFirstValue("sub"), out var userId) ||
            !Guid.TryParse(ctx.User.FindFirstValue(CustomClaims.OrgId), out var orgId))
            return;
        var ct = http.HttpContext?.RequestAborted ?? CancellationToken.None;
        if (await permissions.HasAsync(userId, orgId, req.PageKey, req.Action, ct))
            ctx.Succeed(req);
    }
}
