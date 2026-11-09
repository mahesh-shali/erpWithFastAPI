using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

/// <summary>
/// Master module: users and page rights inside one organization.
///
/// Hierarchy SuperAdmin (100) > Admin (50) > User (10). Rules, enforced here (not only in the UI):
///  - you can only create, edit, delete, or change rights of users whose role level is BELOW yours;
///  - you can only assign roles below your own level (SuperAdmin creates Admins and Users; Admin creates Users);
///  - nobody can change their own role or rights;
///  - no escalation: a non-SuperAdmin can only grant rights (visible/read/write/update/delete) they hold themselves;
///  - role defaults can be edited only for roles below your level (normally: SuperAdmin edits Admin and User).
/// Page-level access to the Master screens themselves is checked by [RequirePage] on the controllers.
/// </summary>
public interface IMasterService
{
    Task<IReadOnlyList<OrgUserDto>> ListUsersAsync(CancellationToken ct);
    Task<OrgUserDto> CreateUserAsync(CreateUserRequest req, CancellationToken ct);
    Task<OrgUserDto> ChangeRoleAsync(Guid userId, string role, CancellationToken ct);
    Task DeleteUserAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<RoleDto>> AssignableRolesAsync(CancellationToken ct);

    Task<UserRightsDto> GetUserRightsAsync(Guid userId, CancellationToken ct);
    Task<UserRightsDto> SetUserRightsAsync(Guid userId, SetRightsRequest req, CancellationToken ct);
    Task<RoleRightsDto> GetRoleRightsAsync(int roleId, CancellationToken ct);
    Task<RoleRightsDto> SetRoleRightsAsync(int roleId, SetRightsRequest req, CancellationToken ct);
}

public sealed class MasterService(
    ErpDbContext db, ITenantContext tenant, IPasswordHasher hasher, ISessionService sessions,
    IPermissionService permissions, TimeProvider clock) : IMasterService
{
    private Guid Org => tenant.OrganizationId;

    // ---------------- Users ----------------

    public async Task<IReadOnlyList<OrgUserDto>> ListUsersAsync(CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var users = await db.Users.AsNoTracking().Include(u => u.Role)
            .Where(u => u.OrganizationId == Org)
            .OrderByDescending(u => u.Role!.Level).ThenBy(u => u.FullName)
            .ToListAsync(ct);
        return users.Select(u => ToDto(u, actor)).ToList();
    }

    public async Task<IReadOnlyList<RoleDto>> AssignableRolesAsync(CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        return await db.Roles.AsNoTracking().Where(r => r.Level < actor.Role!.Level)
            .OrderByDescending(r => r.Level).Select(r => new RoleDto(r.Id, r.Name, r.Description, r.Level)).ToListAsync(ct);
    }

    public async Task<OrgUserDto> CreateUserAsync(CreateUserRequest req, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var role = await RoleByNameAsync(req.Role, ct);
        if (role.Level >= actor.Role!.Level)
            throw new ForbiddenException($"A {actor.Role.Name} can't create a {role.Name}.");

        var email = req.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ConflictException("Email already registered.");

        var user = new User
        {
            Id = Guid.NewGuid(), OrganizationId = Org, Email = email, FullName = req.FullName.Trim(),
            PasswordHash = hasher.Hash(req.Password), RoleId = role.Id, CreatedAt = Now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        user.Role = role;
        return ToDto(user, actor);
    }

    public async Task<OrgUserDto> ChangeRoleAsync(Guid userId, string roleName, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var target = await ManageableUserAsync(actor, userId, ct);
        var role = await RoleByNameAsync(roleName, ct);
        if (role.Level >= actor.Role!.Level)
            throw new ForbiddenException($"A {actor.Role.Name} can't assign the {role.Name} role.");

        if (target.RoleId != role.Id)
        {
            target.RoleId = role.Id;
            await db.SaveChangesAsync(ct);
            await sessions.RevokeAllForUserAsync(target.Id, "role_changed", ct); // new role applies immediately
            await permissions.InvalidateOrganizationAsync(Org, ct);
        }
        target.Role = role;
        return ToDto(target, actor);
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var target = await ManageableUserAsync(actor, userId, ct);
        db.Users.Remove(target); // sessions and rights overrides cascade
        await db.SaveChangesAsync(ct);
        await permissions.InvalidateOrganizationAsync(Org, ct);
    }

    // ---------------- User rights ----------------

    public async Task<UserRightsDto> GetUserRightsAsync(Guid userId, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var target = await db.Users.AsNoTracking().Include(u => u.Role)
                         .FirstOrDefaultAsync(u => u.Id == userId && u.OrganizationId == Org, ct)
                     ?? throw new NotFoundException("User not found.");

        var pages = await ActivePagesAsync(ct);
        var effective = (await db.EffectivePermissions(target.Id).AsNoTracking().ToListAsync(ct)).ToDictionary(e => e.PageId);
        var overrides = await db.UserPagePermissions.AsNoTracking().Where(o => o.UserId == target.Id)
            .Select(o => o.PageId).ToListAsync(ct);
        var roleDefaults = await RoleDefaultsAsync(target.RoleId, ct);

        var rows = pages.Select(p =>
        {
            var e = effective.GetValueOrDefault(p.Id);
            return new UserRightsRowDto(p.Id, p.Key, p.Name, p.Menu(),
                e is null ? None : Rights(e), e?.Source ?? "none",
                roleDefaults.TryGetValue(p.Id, out var d) ? Rights(d.Rights) : None, overrides.Contains(p.Id));
        }).ToList();

        return new UserRightsDto(target.Id, target.FullName, target.Email, target.Role!.Name,
            CanManage(actor, target), rows);
    }

    public async Task<UserRightsDto> SetUserRightsAsync(Guid userId, SetRightsRequest req, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var target = await ManageableUserAsync(actor, userId, ct);
        var pageIds = (await ActivePagesAsync(ct)).Select(p => p.Id).ToHashSet();
        var actorRights = await ActorRightsAsync(actor, ct);

        var existing = await db.UserPagePermissions.Where(o => o.UserId == target.Id).ToDictionaryAsync(o => o.PageId, ct);
        foreach (var item in req.Items)
        {
            if (!pageIds.Contains(item.PageId)) throw new DomainException($"Unknown page {item.PageId}.");
            existing.TryGetValue(item.PageId, out var row);
            if (item.UseDefault)
            {
                if (row is not null) db.UserPagePermissions.Remove(row);
                continue;
            }
            var r = item.Rights ?? throw new DomainException("Rights are required unless UseDefault is true.");
            EnsureNoEscalation(actor, actorRights, item.PageId, r);
            if (row is null)
            {
                row = new UserPagePermission { UserId = target.Id, PageId = item.PageId, OrganizationId = Org };
                db.UserPagePermissions.Add(row);
            }
            Apply(row, r);
            row.UpdatedAt = Now;
            row.UpdatedByUserId = actor.Id;
        }
        await db.SaveChangesAsync(ct);
        await permissions.InvalidateOrganizationAsync(Org, ct);
        return await GetUserRightsAsync(userId, ct);
    }

    // ---------------- Role rights (defaults per role for this organization) ----------------

    public async Task<RoleRightsDto> GetRoleRightsAsync(int roleId, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId, ct)
                   ?? throw new NotFoundException("Role not found.");
        var pages = await ActivePagesAsync(ct);
        var defaults = await RoleDefaultsAsync(roleId, ct);
        var system = await db.RolePagePermissions.AsNoTracking()
            .Where(r => r.OrganizationId == null && r.RoleId == roleId).ToDictionaryAsync(r => r.PageId, ct);
        var isSuper = role.Id == Roles.SuperAdminId;

        var rows = pages.Select(p =>
        {
            var current = isSuper ? All : defaults.TryGetValue(p.Id, out var d) ? Rights(d.Rights) : None;
            var sys = isSuper ? All : system.TryGetValue(p.Id, out var s) ? Rights(s) : None;
            return new RoleRightsRowDto(p.Id, p.Key, p.Name, p.Menu(), current,
                !isSuper && defaults.TryGetValue(p.Id, out var d2) && d2.IsOrganization, sys);
        }).ToList();
        return new RoleRightsDto(role.Id, role.Name, !isSuper && role.Level < actor.Role!.Level, rows);
    }

    public async Task<RoleRightsDto> SetRoleRightsAsync(int roleId, SetRightsRequest req, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId, ct)
                   ?? throw new NotFoundException("Role not found.");
        if (role.Id == Roles.SuperAdminId || role.Level >= actor.Role!.Level)
            throw new ForbiddenException($"A {actor.Role!.Name} can't change the default rights of {role.Name}.");

        var pageIds = (await ActivePagesAsync(ct)).Select(p => p.Id).ToHashSet();
        var actorRights = await ActorRightsAsync(actor, ct);
        var existing = await db.RolePagePermissions.Where(r => r.OrganizationId == Org && r.RoleId == roleId)
            .ToDictionaryAsync(r => r.PageId, ct);

        foreach (var item in req.Items)
        {
            if (!pageIds.Contains(item.PageId)) throw new DomainException($"Unknown page {item.PageId}.");
            existing.TryGetValue(item.PageId, out var row);
            if (item.UseDefault)
            {
                if (row is not null) db.RolePagePermissions.Remove(row);
                continue;
            }
            var r = item.Rights ?? throw new DomainException("Rights are required unless UseDefault is true.");
            EnsureNoEscalation(actor, actorRights, item.PageId, r);
            if (row is null)
            {
                row = new RolePagePermission { OrganizationId = Org, RoleId = roleId, PageId = item.PageId };
                db.RolePagePermissions.Add(row);
            }
            Apply(row, r);
            row.UpdatedAt = Now;
            row.UpdatedByUserId = actor.Id;
        }
        await db.SaveChangesAsync(ct);
        await permissions.InvalidateOrganizationAsync(Org, ct);
        return await GetRoleRightsAsync(roleId, ct);
    }

    // ---------------- helpers ----------------

    private static readonly PageRightsDto None = new(false, false, false, false, false, false);
    private static readonly PageRightsDto All = new(true, false, true, true, true, true);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<User> ActorAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Id == tenant.UserId && u.OrganizationId == Org, ct)
        ?? throw new ForbiddenException("Unknown caller.");

    private static bool CanManage(User actor, User target) =>
        actor.Id != target.Id && target.Role!.Level < actor.Role!.Level;

    private async Task<User> ManageableUserAsync(User actor, Guid userId, CancellationToken ct)
    {
        var target = await db.Users.Include(u => u.Role)
                         .FirstOrDefaultAsync(u => u.Id == userId && u.OrganizationId == Org, ct)
                     ?? throw new NotFoundException("User not found.");
        if (target.Id == actor.Id) throw new ForbiddenException("You can't change your own account here.");
        if (!CanManage(actor, target))
            throw new ForbiddenException($"A {actor.Role!.Name} can't manage a {target.Role!.Name}.");
        return target;
    }

    private async Task<Role> RoleByNameAsync(string name, CancellationToken ct) =>
        await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == name, ct)
        ?? throw new DomainException($"Unknown role '{name}'.");

    private Task<List<Page>> ActivePagesAsync(CancellationToken ct) => db.MenuPages().ToListAsync(ct);

    /// <summary>The role's default per page for this org: the org's customised row, else the system row.</summary>
    private async Task<Dictionary<int, (IPageRights Rights, bool IsOrganization)>> RoleDefaultsAsync(int roleId, CancellationToken ct)
    {
        var rows = await db.RolePagePermissions.AsNoTracking()
            .Where(r => r.RoleId == roleId && (r.OrganizationId == null || r.OrganizationId == Org)).ToListAsync(ct);
        return rows.GroupBy(r => r.PageId).ToDictionary(g => g.Key, g =>
        {
            var org = g.FirstOrDefault(r => r.OrganizationId != null);
            return ((IPageRights)(org ?? g.First()), org is not null);
        });
    }

    private async Task<Dictionary<int, EffectivePagePermission>> ActorRightsAsync(User actor, CancellationToken ct) =>
        (await permissions.GetEffectiveAsync(actor.Id, Org, ct)).ToDictionary(r => r.PageId);

    private static void EnsureNoEscalation(User actor, Dictionary<int, EffectivePagePermission> mine, int pageId, PageRightsDto r)
    {
        if (actor.RoleId == Roles.SuperAdminId) return;
        mine.TryGetValue(pageId, out var m);
        var have = m is null || m.IsRestricted ? None : Rights(m);
        if ((r.IsVisible && !have.IsVisible) || (r.CanRead && !have.CanRead) || (r.CanWrite && !have.CanWrite) ||
            (r.CanUpdate && !have.CanUpdate) || (r.CanDelete && !have.CanDelete))
            throw new ForbiddenException($"You can't grant rights on '{m?.PageKey ?? pageId.ToString()}' that you don't have yourself.");
    }

    private static PageRightsDto Rights(IPageRights r) =>
        new(r.IsVisible, r.IsRestricted, r.CanRead, r.CanWrite, r.CanUpdate, r.CanDelete);

    private static PageRightsDto Rights(EffectivePagePermission r) =>
        new(r.IsVisible, r.IsRestricted, r.CanRead, r.CanWrite, r.CanUpdate, r.CanDelete);

    private static void Apply(IPageRights row, PageRightsDto r)
    {
        row.IsVisible = r.IsVisible;
        row.IsRestricted = r.IsRestricted;
        row.CanRead = r.CanRead;
        row.CanWrite = r.CanWrite;
        row.CanUpdate = r.CanUpdate;
        row.CanDelete = r.CanDelete;
    }

    private OrgUserDto ToDto(User u, User actor) =>
        new(u.Id, u.Email, u.FullName, u.Role!.Name, u.CreatedAt, u.LastLoginAt, CanManage(actor, u));
}
