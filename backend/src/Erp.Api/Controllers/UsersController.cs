using System.Security.Claims;
using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Erp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Controllers;

/// <summary>The caller's own effective rights: drives the menu, route guards and buttons in the UI.</summary>
[ApiController]
[Route("api/me")]
[Authorize]
public class MeController(IPermissionService permissions, ErpDbContext db, ITenantContext tenant) : ControllerBase
{
    [HttpGet("permissions")]
    public async Task<MyPermissionsDto> Permissions(CancellationToken ct)
    {
        var rights = (await permissions.GetEffectiveAsync(tenant.UserId, tenant.OrganizationId, ct)).ToDictionary(r => r.PageId);
        var pages = await db.MenuPages().ToListAsync(ct);
        var list = pages.Select(p =>
        {
            var r = rights.GetValueOrDefault(p.Id);
            return new MyPagePermissionDto(p.Key, p.Name, p.Route, p.Menu(), p.IsImplemented,
                r?.IsVisible ?? false, r?.IsRestricted ?? false, r?.CanRead ?? false,
                r?.CanWrite ?? false, r?.CanUpdate ?? false, r?.CanDelete ?? false);
        }).ToList();
        return new MyPermissionsDto(User.FindFirstValue(AuthClaims.Role) ?? "", list);
    }
}

/// <summary>Roles stored in the database (roles table).</summary>
[ApiController]
[Route("api/roles")]
[Authorize]
public class RolesController(ErpDbContext db, IMasterService master) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<RoleDto>> List(CancellationToken ct) =>
        await db.Roles.AsNoTracking().OrderByDescending(r => r.Level)
            .Select(r => new RoleDto(r.Id, r.Name, r.Description, r.Level)).ToListAsync(ct);

    /// <summary>Roles the caller may give to others (strictly below the caller's own level).</summary>
    [HttpGet("assignable")]
    public Task<IReadOnlyList<RoleDto>> Assignable(CancellationToken ct) => master.AssignableRolesAsync(ct);
}

/// <summary>Master > Users. Page rights decide access to the screen; MasterService enforces the hierarchy.</summary>
[ApiController]
[Route("api/users")]
public class UsersController(IMasterService master) : ControllerBase
{
    [HttpGet, RequirePage(PageKeys.MasterUsers, PageAction.Read)]
    public Task<IReadOnlyList<OrgUserDto>> List(CancellationToken ct) => master.ListUsersAsync(ct);

    [HttpPost, RequirePage(PageKeys.MasterUsers, PageAction.Write)]
    public async Task<ActionResult<OrgUserDto>> Create(CreateUserRequest req, CancellationToken ct)
    {
        var user = await master.CreateUserAsync(req, ct);
        return CreatedAtAction(nameof(List), null, user);
    }

    /// <summary>Changes a user's role and signs them out everywhere so the new role applies immediately.</summary>
    [HttpPut("{id:guid}/role"), RequirePage(PageKeys.MasterUsers, PageAction.Update)]
    public Task<OrgUserDto> UpdateRole(Guid id, UpdateUserRoleRequest req, CancellationToken ct)
        => master.ChangeRoleAsync(id, req.Role, ct);

    [HttpDelete("{id:guid}"), RequirePage(PageKeys.MasterUsers, PageAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await master.DeleteUserAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Master > Pages, User Rights, Role Rights.</summary>
[ApiController]
[Route("api/master")]
public class MasterController(IMasterService master, ErpDbContext db) : ControllerBase
{
    [HttpGet("pages"), RequirePage(PageKeys.MasterPages, PageAction.Read)]
    public async Task<IReadOnlyList<PageDto>> Pages(CancellationToken ct) =>
        (await db.Pages.AsNoTracking().Include(p => p.Group!).ThenInclude(g => g.Module)
            .OrderBy(p => p.GroupId == null ? 0 : 1).ThenBy(p => p.Group!.Module!.SortOrder)
            .ThenBy(p => p.Group!.SortOrder).ThenBy(p => p.SortOrder).ToListAsync(ct))
        .Select(p => new PageDto(p.Id, p.Key, p.Name, p.Route, p.Menu(), p.IsActive, p.IsImplemented)).ToList();

    [HttpGet("users/{id:guid}/rights"), RequirePage(PageKeys.MasterUserRights, PageAction.Read)]
    public Task<UserRightsDto> UserRights(Guid id, CancellationToken ct) => master.GetUserRightsAsync(id, ct);

    [HttpPut("users/{id:guid}/rights"), RequirePage(PageKeys.MasterUserRights, PageAction.Update)]
    public Task<UserRightsDto> SetUserRights(Guid id, SetRightsRequest req, CancellationToken ct)
        => master.SetUserRightsAsync(id, req, ct);

    [HttpGet("roles/{roleId:int}/rights"), RequirePage(PageKeys.MasterRoleRights, PageAction.Read)]
    public Task<RoleRightsDto> RoleRights(int roleId, CancellationToken ct) => master.GetRoleRightsAsync(roleId, ct);

    [HttpPut("roles/{roleId:int}/rights"), RequirePage(PageKeys.MasterRoleRights, PageAction.Update)]
    public Task<RoleRightsDto> SetRoleRights(int roleId, SetRightsRequest req, CancellationToken ct)
        => master.SetRoleRightsAsync(roleId, req, ct);
}
