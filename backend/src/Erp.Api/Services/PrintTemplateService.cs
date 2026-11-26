using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

/// <summary>A print screen's chosen format (null = built-in layout) and every format it may switch to.</summary>
public sealed record PrintChoice(PrintTemplate? Template, IReadOnlyList<PrintFormatDto> Formats);

/// <summary>
/// One organization's print formats. Works on the organization of the <see cref="ErpDbContext"/> it was made with:
/// the signed-in one (DI), or another one picked by a platform admin (<see cref="IPrintTemplateScope"/>).
/// </summary>
public interface IPrintTemplateService
{
    Task<IReadOnlyList<PrintTemplateSummaryDto>> ListAsync(string? pageKey, bool includeArchived, CancellationToken ct);
    Task<PrintTemplateDto> GetAsync(Guid id, CancellationToken ct);
    Task<PrintTemplateDto> CreateAsync(CreatePrintTemplateRequest req, CancellationToken ct);
    /// <summary>Saves a new version of the template (the old one is archived, the default moves along).</summary>
    Task<PrintTemplateDto> UpdateAsync(Guid id, UpdatePrintTemplateRequest req, CancellationToken ct);
    Task<PrintTemplateDto> SetDefaultAsync(Guid id, CancellationToken ct);
    /// <summary>No custom default for this page: the built-in layout is used.</summary>
    Task UseBuiltInAsync(string pageKey, CancellationToken ct);
    Task ArchiveAsync(Guid id, CancellationToken ct);
    /// <summary>The current default format of a page, or null (built-in).</summary>
    Task<PrintTemplate?> DefaultAsync(string pageKey, CancellationToken ct);
    /// <summary>
    /// The format a print screen uses. <paramref name="requested"/>: null = the document's usual one (the version it froze,
    /// else the page's default), "built-in", or a template id (a current format of the page, or the frozen version).
    /// </summary>
    /// <param name="frozen">The version an issued document froze; with <paramref name="hasFrozen"/> and a null id, the
    /// document was issued with the built-in layout.</param>
    Task<PrintChoice> ChooseAsync(string pageKey, string? requested, bool hasFrozen, Guid? frozen, CancellationToken ct);
    /// <summary>Current formats per page (for the page tree).</summary>
    Task<IReadOnlyDictionary<string, int>> CountsAsync(CancellationToken ct);
}

public sealed class PrintTemplateService(ErpDbContext db, ITenantContext tenant) : IPrintTemplateService
{
    public const string BuiltIn = "built-in";
    public const string BuiltInName = "Built-in layout";

    public async Task<IReadOnlyList<PrintTemplateSummaryDto>> ListAsync(string? pageKey, bool includeArchived, CancellationToken ct)
    {
        var q = db.PrintTemplates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(pageKey)) q = q.Where(t => t.PageKey == pageKey);
        if (!includeArchived) q = q.Where(t => !t.IsArchived);
        return await q.OrderBy(t => t.PageKey).ThenByDescending(t => t.IsDefault).ThenBy(t => t.Name).ThenByDescending(t => t.Version)
            .Select(t => new PrintTemplateSummaryDto(t.Id, t.FamilyId, t.PageKey, t.Name, t.Version, t.IsDefault, t.IsArchived, t.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<PrintTemplateDto> GetAsync(Guid id, CancellationToken ct) =>
        ToDto(await db.PrintTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
              ?? throw new NotFoundException("Print template not found."));

    public async Task<PrintTemplateDto> CreateAsync(CreatePrintTemplateRequest req, CancellationToken ct)
    {
        CheckPage(req.PageKey);
        PrintTemplateRenderer.Parse(req.Body);
        var id = await db.InTransactionAsync(async () =>
        {
            if (req.MakeDefault) await ClearDefaultAsync(req.PageKey, ct);
            var t = new PrintTemplate
            {
                Id = Guid.NewGuid(), FamilyId = Guid.NewGuid(), PageKey = req.PageKey, Name = req.Name.Trim(), Body = req.Body,
                Version = 1, IsDefault = req.MakeDefault, CreatedByUserId = tenant.UserId,
            };
            db.PrintTemplates.Add(t);
            await db.SaveChangesAsync(ct);
            return t.Id;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<PrintTemplateDto> UpdateAsync(Guid id, UpdatePrintTemplateRequest req, CancellationToken ct)
    {
        PrintTemplateRenderer.Parse(req.Body);
        var newId = await db.InTransactionAsync(async () =>
        {
            var current = await LockCurrentAsync(id, ct);
            // Unique (family, version) also catches two people saving the same version at once.
            var next = new PrintTemplate
            {
                Id = Guid.NewGuid(), FamilyId = current.FamilyId, PageKey = current.PageKey, Name = req.Name.Trim(), Body = req.Body,
                Version = current.Version + 1, IsDefault = false, CreatedByUserId = tenant.UserId,
            };
            var wasDefault = current.IsDefault;
            current.IsDefault = false;
            current.IsArchived = true;
            await db.SaveChangesAsync(ct);          // free the default slot first (partial unique index)
            next.IsDefault = wasDefault;
            db.PrintTemplates.Add(next);
            await db.SaveChangesAsync(ct);
            return next.Id;
        }, ct);
        return await GetAsync(newId, ct);
    }

    public async Task<PrintTemplateDto> SetDefaultAsync(Guid id, CancellationToken ct)
    {
        await db.InTransactionAsync(async () =>
        {
            var t = await LockCurrentAsync(id, ct);
            if (!t.IsDefault)
            {
                await ClearDefaultAsync(t.PageKey, ct);
                t.IsDefault = true;
                await db.SaveChangesAsync(ct);
            }
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task UseBuiltInAsync(string pageKey, CancellationToken ct)
    {
        CheckPage(pageKey);
        await ClearDefaultAsync(pageKey, ct);
    }

    public async Task ArchiveAsync(Guid id, CancellationToken ct)
    {
        await db.InTransactionAsync(async () =>
        {
            var t = await LockCurrentAsync(id, ct);
            (t.IsArchived, t.IsDefault) = (true, false);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    public Task<PrintTemplate?> DefaultAsync(string pageKey, CancellationToken ct) =>
        db.PrintTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.PageKey == pageKey && t.IsDefault, ct);

    public async Task<PrintChoice> ChooseAsync(string pageKey, string? requested, bool hasFrozen, Guid? frozen, CancellationToken ct)
    {
        CheckPage(pageKey);
        var current = await db.PrintTemplates.AsNoTracking().Where(t => t.PageKey == pageKey && !t.IsArchived)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name).ToListAsync(ct);
        var frozenRow = frozen is { } fid
            ? current.FirstOrDefault(t => t.Id == fid)
              ?? await db.PrintTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == fid && t.PageKey == pageKey, ct)
            : null;
        var def = current.FirstOrDefault(t => t.IsDefault);

        var formats = new List<PrintFormatDto> { new(null, BuiltInName, 0, def is null, hasFrozen && frozen is null) };
        formats.AddRange(current.Select(t => new PrintFormatDto(t.Id, t.Name, t.Version, t.IsDefault, frozenRow?.Id == t.Id)));
        if (frozenRow is not null && current.All(t => t.Id != frozenRow.Id))
            formats.Add(new PrintFormatDto(frozenRow.Id, frozenRow.Name, frozenRow.Version, false, true));

        PrintTemplate? chosen;
        if (string.IsNullOrWhiteSpace(requested)) chosen = hasFrozen ? frozenRow : def;
        else if (requested == BuiltIn) chosen = null;
        else if (Guid.TryParse(requested, out var id))
            chosen = current.FirstOrDefault(t => t.Id == id) ?? (frozenRow?.Id == id ? frozenRow : null)
                     ?? throw new NotFoundException("Print format not found.");
        else throw new DomainException($"Format must be \"{BuiltIn}\" or a print template id.");
        return new PrintChoice(chosen, formats);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountsAsync(CancellationToken ct) =>
        await db.PrintTemplates.AsNoTracking().Where(t => !t.IsArchived).GroupBy(t => t.PageKey)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    /// <summary>Locks the row; only the current (not archived) version of a template can be changed.</summary>
    private async Task<PrintTemplate> LockCurrentAsync(Guid id, CancellationToken ct)
    {
        var rows = await db.PrintTemplates.FromSqlInterpolated(
                $"SELECT * FROM print_templates WHERE id = {id} AND organization_id = {db.CurrentOrgId} FOR UPDATE")
            .IgnoreQueryFilters().AsTracking().ToListAsync(ct);
        var t = rows.SingleOrDefault() ?? throw new NotFoundException("Print template not found.");
        if (t.IsArchived) throw new DomainException("This is an old or archived version; edit the current version instead.");
        return t;
    }

    private Task ClearDefaultAsync(string pageKey, CancellationToken ct) =>
        db.PrintTemplates.Where(t => t.PageKey == pageKey && t.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, false), ct);

    public static void CheckPage(string pageKey)
    {
        if (!PrintablePages.All.Contains(pageKey))
            throw new DomainException($"Print formats exist only for these pages: {string.Join(", ", PrintablePages.All)}.");
    }

    private static PrintTemplateDto ToDto(PrintTemplate t) =>
        new(t.Id, t.FamilyId, t.PageKey, t.Name, t.Version, t.IsDefault, t.IsArchived, t.CreatedAt, t.Body);
}

/// <summary>
/// Which organization the print templates screen works on. Without an organization id (or with the caller's own), the
/// normal tenant-scoped service. Another organization only for a platform admin (<see cref="User.IsPlatformAdmin"/>,
/// read from the database on every call): the service then gets its own DbContext whose tenant is that organization,
/// so the global query filter and insert stamping keep every query inside it, exactly as for a member.
/// </summary>
public interface IPrintTemplateScope
{
    Task<bool> IsPlatformAdminAsync(CancellationToken ct);
    /// <summary>The organization to work on (validated), and its template service.</summary>
    Task<(Guid OrganizationId, IPrintTemplateService Templates)> ForAsync(Guid? organizationId, CancellationToken ct);
}

public sealed class PrintTemplateScope(ErpDbContext db, ITenantContext tenant, IPrintTemplateService own,
    DbContextOptions<ErpDbContext> options) : IPrintTemplateScope, IAsyncDisposable
{
    private readonly List<ErpDbContext> _created = [];
    private bool? _isPlatformAdmin;

    public async Task<bool> IsPlatformAdminAsync(CancellationToken ct) =>
        _isPlatformAdmin ??= tenant.UserId != Guid.Empty
            && await db.Users.AsNoTracking().AnyAsync(u => u.Id == tenant.UserId && u.IsPlatformAdmin, ct);

    public async Task<(Guid OrganizationId, IPrintTemplateService Templates)> ForAsync(Guid? organizationId, CancellationToken ct)
    {
        if (organizationId is not { } orgId || orgId == tenant.OrganizationId) return (tenant.OrganizationId, own);
        if (!await IsPlatformAdminAsync(ct))
            throw new ForbiddenException("Only a platform admin can manage another organization's print formats.");
        if (!await db.Organizations.AsNoTracking().AnyAsync(o => o.Id == orgId, ct))
            throw new NotFoundException("Organization not found.");
        var other = new FixedTenantContext(orgId, tenant.UserId);
        var ctx = new ErpDbContext(options, other);
        _created.Add(ctx);
        return (orgId, new PrintTemplateService(ctx, other));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _created) await c.DisposeAsync();
    }
}
