using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Erp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Controllers;

/// <summary>
/// Settings › Print templates: print formats per Organization › Module › Group › Page (e.g. Demo Corp › Sales &amp; Labour
/// Work › Orders › Customer P.O.). SuperAdmin only (a format is printed on every document of the page, so it is
/// owner-level). Every call works on the caller's own organization unless <c>organizationId</c> names another, which
/// only a platform admin may do (<see cref="IPrintTemplateScope"/>). Printing itself only needs read on the document's
/// page (GET /api/{customer-pos|work-orders|sales-invoices}/{id}/print).
/// </summary>
[ApiController]
[Route("api/print-templates")]
[Authorize(Roles = Roles.SuperAdmin)]
public class PrintTemplatesController(IPrintTemplateScope scope, IDocumentPrintService printing, ErpDbContext db, ITenantContext tenant)
    : ControllerBase
{
    /// <summary>The tree to pick from: organizations (all of them for a platform admin), then the menu's modules › groups › pages.</summary>
    [HttpGet("setup")]
    public async Task<PrintSetupDto> Setup([FromQuery] Guid? organizationId, CancellationToken ct)
    {
        var isPlatformAdmin = await scope.IsPlatformAdminAsync(ct);
        var (orgId, templates) = await scope.ForAsync(organizationId, ct);
        var orgs = await db.Organizations.AsNoTracking()
            .Where(o => isPlatformAdmin || o.Id == tenant.OrganizationId)
            .OrderBy(o => o.Name).Select(o => new PrintOrganizationDto(o.Id, o.Name)).ToListAsync(ct);
        var counts = await templates.CountsAsync(ct);
        var pages = await db.Pages.AsNoTracking().Where(p => p.IsActive && p.GroupId != null)
            .Select(p => new { p.Key, p.Name, p.SortOrder, GroupId = p.GroupId!.Value }).ToListAsync(ct);
        var groups = await db.MenuGroups.AsNoTracking().ToListAsync(ct);
        var modules = await db.MenuModules.AsNoTracking().OrderBy(m => m.SortOrder).ToListAsync(ct);
        var tree = modules.Select(m => new PrintModuleDto(m.Key, m.Name, groups.Where(g => g.ModuleId == m.Id).OrderBy(g => g.SortOrder)
                .Select(g => new PrintGroupDto(g.Key, g.Name, pages.Where(p => p.GroupId == g.Id).OrderBy(p => p.SortOrder)
                    .Select(p => new PrintPageDto(p.Key, p.Name, PrintablePages.All.Contains(p.Key), counts.GetValueOrDefault(p.Key)))
                    .ToList()))
                .Where(g => g.Pages.Count > 0).ToList()))
            .Where(m => m.Groups.Count > 0).ToList();
        return new PrintSetupDto(isPlatformAdmin, orgId, orgs, tree);
    }

    [HttpGet]
    public async Task<IReadOnlyList<PrintTemplateSummaryDto>> List([FromQuery] string? pageKey, [FromQuery] bool includeArchived = false,
        [FromQuery] Guid? organizationId = null, CancellationToken ct = default) =>
        await (await scope.ForAsync(organizationId, ct)).Templates.ListAsync(pageKey, includeArchived, ct);

    /// <summary>The page's built-in layout as a template: the starting point for a new format.</summary>
    [HttpGet("starter")]
    public PrintTemplateStarterDto Starter([FromQuery] string pageKey = PageKeys.SalesInvoices) =>
        new(pageKey, PrintTemplateStarter.For(pageKey));

    [HttpGet("{id:guid}")]
    public async Task<PrintTemplateDto> Get(Guid id, [FromQuery] Guid? organizationId, CancellationToken ct) =>
        await (await scope.ForAsync(organizationId, ct)).Templates.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<PrintTemplateDto>> Create(CreatePrintTemplateRequest req, [FromQuery] Guid? organizationId,
        CancellationToken ct)
    {
        var (orgId, templates) = await scope.ForAsync(organizationId, ct);
        var t = await templates.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = t.Id, organizationId = orgId }, t);
    }

    /// <summary>Saves a new version; returns it (with a new id).</summary>
    [HttpPut("{id:guid}")]
    public async Task<PrintTemplateDto> Update(Guid id, UpdatePrintTemplateRequest req, [FromQuery] Guid? organizationId,
        CancellationToken ct) =>
        await (await scope.ForAsync(organizationId, ct)).Templates.UpdateAsync(id, req, ct);

    [HttpPost("{id:guid}/default")]
    public async Task<PrintTemplateDto> SetDefault(Guid id, [FromQuery] Guid? organizationId, CancellationToken ct) =>
        await (await scope.ForAsync(organizationId, ct)).Templates.SetDefaultAsync(id, ct);

    [HttpPost("use-built-in")]
    public async Task<IActionResult> UseBuiltIn([FromQuery] string pageKey, [FromQuery] Guid? organizationId, CancellationToken ct)
    {
        await (await scope.ForAsync(organizationId, ct)).Templates.UseBuiltInAsync(pageKey, ct);
        return NoContent();
    }

    /// <summary>Retires the format. Documents that froze one of its versions still print with it.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Archive(Guid id, [FromQuery] Guid? organizationId, CancellationToken ct)
    {
        await (await scope.ForAsync(organizationId, ct)).Templates.ArchiveAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Renders a template that isn't saved yet: with sample data (and the target organization's company details), or with
    /// one of the caller's own organization's documents. Real documents of another organization are never read here.
    /// </summary>
    [HttpPost("preview")]
    public async Task<RenderedTemplateDto> Preview(PreviewPrintTemplateRequest req, [FromQuery] Guid? organizationId, CancellationToken ct)
    {
        PrintTemplateService.CheckPage(req.PageKey);
        var (orgId, _) = await scope.ForAsync(organizationId, ct);
        if (req.DocumentId is not null && orgId != tenant.OrganizationId)
            throw new DomainException("Another organization's formats can only be previewed with sample data.");
        var company = await db.Organizations.AsNoTracking().Where(o => o.Id == orgId)
            .Select(o => new CompanyDto(o.Name, o.Address, o.State, o.Phone, o.Email, o.TaxId)).SingleAsync(ct);
        var model = await printing.PreviewModelAsync(req.PageKey, req.DocumentId, company, ct);
        var r = PrintTemplateRenderer.Render(req.Body, model);
        return new RenderedTemplateDto(null, "Preview", 0, r.Html, r.Css);
    }
}
