using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

public interface IPrintTemplateService
{
    Task<IReadOnlyList<PrintTemplateSummaryDto>> ListAsync(string? documentType, bool includeArchived, CancellationToken ct);
    Task<PrintTemplateDto> GetAsync(Guid id, CancellationToken ct);
    Task<PrintTemplateDto> CreateAsync(CreatePrintTemplateRequest req, CancellationToken ct);
    /// <summary>Saves a new version of the template (the old one is archived, the default moves along).</summary>
    Task<PrintTemplateDto> UpdateAsync(Guid id, UpdatePrintTemplateRequest req, CancellationToken ct);
    Task<PrintTemplateDto> SetDefaultAsync(Guid id, CancellationToken ct);
    /// <summary>No custom default for this document type: the built-in layout is used.</summary>
    Task UseBuiltInAsync(string documentType, CancellationToken ct);
    Task ArchiveAsync(Guid id, CancellationToken ct);
    /// <summary>The current default template of a document type, or null (built-in).</summary>
    Task<PrintTemplate?> DefaultAsync(string documentType, CancellationToken ct);
    /// <summary>Any version (archived too) of this organization's templates, or null.</summary>
    Task<PrintTemplate?> FindAsync(Guid id, CancellationToken ct);
}

public sealed class PrintTemplateService(ErpDbContext db, ITenantContext tenant) : IPrintTemplateService
{
    public async Task<IReadOnlyList<PrintTemplateSummaryDto>> ListAsync(string? documentType, bool includeArchived, CancellationToken ct)
    {
        var q = db.PrintTemplates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(documentType)) q = q.Where(t => t.DocumentType == documentType);
        if (!includeArchived) q = q.Where(t => !t.IsArchived);
        return await q.OrderBy(t => t.DocumentType).ThenByDescending(t => t.IsDefault).ThenBy(t => t.Name).ThenByDescending(t => t.Version)
            .Select(t => new PrintTemplateSummaryDto(t.Id, t.FamilyId, t.DocumentType, t.Name, t.Version, t.IsDefault, t.IsArchived, t.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<PrintTemplateDto> GetAsync(Guid id, CancellationToken ct) =>
        ToDto(await db.PrintTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
              ?? throw new NotFoundException("Print template not found."));

    public async Task<PrintTemplateDto> CreateAsync(CreatePrintTemplateRequest req, CancellationToken ct)
    {
        CheckType(req.DocumentType);
        PrintTemplateRenderer.Parse(req.Body);
        var id = await db.InTransactionAsync(async () =>
        {
            if (req.MakeDefault) await ClearDefaultAsync(req.DocumentType, ct);
            var t = new PrintTemplate
            {
                Id = Guid.NewGuid(), FamilyId = Guid.NewGuid(), DocumentType = req.DocumentType, Name = req.Name.Trim(), Body = req.Body,
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
                Id = Guid.NewGuid(), FamilyId = current.FamilyId, DocumentType = current.DocumentType, Name = req.Name.Trim(), Body = req.Body,
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
                await ClearDefaultAsync(t.DocumentType, ct);
                t.IsDefault = true;
                await db.SaveChangesAsync(ct);
            }
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task UseBuiltInAsync(string documentType, CancellationToken ct)
    {
        CheckType(documentType);
        await ClearDefaultAsync(documentType, ct);
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

    public Task<PrintTemplate?> DefaultAsync(string documentType, CancellationToken ct) =>
        db.PrintTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.DocumentType == documentType && t.IsDefault, ct);

    public Task<PrintTemplate?> FindAsync(Guid id, CancellationToken ct) =>
        db.PrintTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

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

    private Task ClearDefaultAsync(string documentType, CancellationToken ct) =>
        db.PrintTemplates.Where(t => t.DocumentType == documentType && t.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, false), ct);

    private static void CheckType(string documentType)
    {
        if (!PrintDocumentType.All.Contains(documentType))
            throw new DomainException($"Document type must be one of {string.Join(", ", PrintDocumentType.All)}.");
    }

    private static PrintTemplateDto ToDto(PrintTemplate t) =>
        new(t.Id, t.FamilyId, t.DocumentType, t.Name, t.Version, t.IsDefault, t.IsArchived, t.CreatedAt, t.Body);
}
