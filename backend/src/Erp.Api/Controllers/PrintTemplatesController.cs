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
/// Settings › Print templates: an organization's own print layouts. SuperAdmin only (a layout is printed on every
/// invoice the organization issues, so it is owner-level, unlike Settings › Company which Admins may edit).
/// Printing itself only needs read on the document's page (GET /api/sales-invoices/{id}/print).
/// </summary>
[ApiController]
[Route("api/print-templates")]
[Authorize(Roles = Roles.SuperAdmin)]
public class PrintTemplatesController(IPrintTemplateService templates, ISalesInvoiceService invoices, ErpDbContext db, ITenantContext tenant)
    : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<PrintTemplateSummaryDto>> List([FromQuery] string? documentType, [FromQuery] bool includeArchived = false,
        CancellationToken ct = default) => templates.ListAsync(documentType, includeArchived, ct);

    /// <summary>The built-in layout as a template: the starting point for a new one.</summary>
    [HttpGet("starter")]
    public ActionResult<PrintTemplateStarterDto> Starter([FromQuery] string documentType = PrintDocumentType.SalesInvoice) =>
        documentType == PrintDocumentType.SalesInvoice
            ? new PrintTemplateStarterDto(documentType, PrintTemplateStarter.SalesInvoice)
            : throw new DomainException($"Document type must be one of {string.Join(", ", PrintDocumentType.All)}.");

    [HttpGet("{id:guid}")]
    public Task<PrintTemplateDto> Get(Guid id, CancellationToken ct) => templates.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<PrintTemplateDto>> Create(CreatePrintTemplateRequest req, CancellationToken ct)
    {
        var t = await templates.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = t.Id }, t);
    }

    /// <summary>Saves a new version; returns it (with a new id).</summary>
    [HttpPut("{id:guid}")]
    public Task<PrintTemplateDto> Update(Guid id, UpdatePrintTemplateRequest req, CancellationToken ct) => templates.UpdateAsync(id, req, ct);

    [HttpPost("{id:guid}/default")]
    public Task<PrintTemplateDto> SetDefault(Guid id, CancellationToken ct) => templates.SetDefaultAsync(id, ct);

    [HttpPost("use-built-in")]
    public async Task<IActionResult> UseBuiltIn([FromQuery] string documentType = PrintDocumentType.SalesInvoice, CancellationToken ct = default)
    {
        await templates.UseBuiltInAsync(documentType, ct);
        return NoContent();
    }

    /// <summary>Retires the template. Documents that froze one of its versions still print with it.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct)
    {
        await templates.ArchiveAsync(id, ct);
        return NoContent();
    }

    /// <summary>Renders a template that isn't saved yet, with sample data or one of this organization's invoices.</summary>
    [HttpPost("preview")]
    public async Task<RenderedTemplateDto> Preview(PreviewPrintTemplateRequest req, CancellationToken ct)
    {
        if (req.DocumentType != PrintDocumentType.SalesInvoice)
            throw new DomainException($"Document type must be one of {string.Join(", ", PrintDocumentType.All)}.");
        CompanyDto company;
        SalesInvoiceDto invoice;
        if (req.InvoiceId is { } invoiceId)
        {
            var print = await invoices.PrintAsync(invoiceId, ct);        // tenant-filtered: another organization's id is 404
            (company, invoice) = (print.Company, print.Invoice);
        }
        else
        {
            var co = await db.Organizations.AsNoTracking().Where(o => o.Id == tenant.OrganizationId)
                .Select(o => new CompanyDto(o.Name, o.Address, o.State, o.Phone, o.Email, o.TaxId)).SingleAsync(ct);
            (company, invoice) = PrintTemplateRenderer.Sample(co);
        }
        var r = PrintTemplateRenderer.Render(req.Body, PrintTemplateRenderer.InvoiceModel(company, invoice));
        return new RenderedTemplateDto(null, "Preview", 0, r.Html, r.Css);
    }
}
