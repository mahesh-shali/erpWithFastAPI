using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Erp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Controllers;

// Sales flow: Customer P.O. -> Work Order (production) -> Sales Invoice (+ print).
// Rights (like Stock Entries): Read = list/view · Write = create · Update = edit, confirm/submit · Delete = delete draft, cancel.

[ApiController]
[Route("api/customer-pos")]
public class CustomerPosController(ICustomerPoService pos) : ControllerBase
{
    [HttpGet, RequirePage(PageKeys.CustomerPos)]
    public Task<PagedResult<CustomerPoSummaryDto>> List([FromQuery] string? status, [FromQuery] Guid? customerId, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        pos.ListAsync(status, customerId, search, page, pageSize, ct);

    /// <summary>Also readable from the Work Orders and Sales Invoices screens, which are raised against a P.O.</summary>
    [HttpGet("{id:guid}"), RequireAnyPage(PageKeys.CustomerPos, PageKeys.WorkOrders, PageKeys.SalesInvoices)]
    public Task<CustomerPoDto> Get(Guid id, CancellationToken ct) => pos.GetAsync(id, ct);

    [HttpPost, RequirePage(PageKeys.CustomerPos, PageAction.Write)]
    public async Task<ActionResult<CustomerPoDto>> Create(UpsertCustomerPoRequest req, CancellationToken ct)
    {
        var po = await pos.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = po.Id }, po);
    }

    [HttpPut("{id:guid}"), RequirePage(PageKeys.CustomerPos, PageAction.Update)]
    public Task<CustomerPoDto> Update(Guid id, UpsertCustomerPoRequest req, CancellationToken ct) => pos.UpdateAsync(id, req, ct);

    [HttpDelete("{id:guid}"), RequirePage(PageKeys.CustomerPos, PageAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await pos.DeleteAsync(id, ct); return NoContent(); }

    /// <summary>The P.O. rendered with a print format. format = "built-in" or a print template id (null = the page's default).</summary>
    [HttpGet("{id:guid}/print"), RequirePage(PageKeys.CustomerPos)]
    public Task<DocumentPrintDto> Print(Guid id, [FromQuery] string? format, [FromServices] IDocumentPrintService printing,
        CancellationToken ct) => printing.CustomerPoAsync(id, format, ct);

    [HttpPost("{id:guid}/confirm"), RequirePage(PageKeys.CustomerPos, PageAction.Update)]
    public Task<CustomerPoDto> Confirm(Guid id, CancellationToken ct) => pos.ConfirmAsync(id, ct);

    [HttpPost("{id:guid}/cancel"), RequirePage(PageKeys.CustomerPos, PageAction.Delete)]
    public Task<CustomerPoDto> Cancel(Guid id, CancellationToken ct) => pos.CancelAsync(id, ct);
}

[ApiController]
[Route("api/work-orders")]
public class WorkOrdersController(IWorkOrderService wos) : ControllerBase
{
    [HttpGet, RequirePage(PageKeys.WorkOrders)]
    public Task<PagedResult<WorkOrderSummaryDto>> List([FromQuery] string? status, [FromQuery] Guid? customerPoId, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        wos.ListAsync(status, customerPoId, search, page, pageSize, ct);

    [HttpGet("{id:guid}"), RequirePage(PageKeys.WorkOrders)]
    public Task<WorkOrderDto> Get(Guid id, CancellationToken ct) => wos.GetAsync(id, ct);

    /// <summary>The work order (job sheet) rendered with a print format. format = "built-in" or a print template id.</summary>
    [HttpGet("{id:guid}/print"), RequirePage(PageKeys.WorkOrders)]
    public Task<DocumentPrintDto> Print(Guid id, [FromQuery] string? format, [FromServices] IDocumentPrintService printing,
        CancellationToken ct) => printing.WorkOrderAsync(id, format, ct);

    [HttpPost, RequirePage(PageKeys.WorkOrders, PageAction.Write)]
    public async Task<ActionResult<WorkOrderDto>> Create(UpsertWorkOrderRequest req, CancellationToken ct)
    {
        var w = await wos.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = w.Id }, w);
    }

    [HttpPut("{id:guid}"), RequirePage(PageKeys.WorkOrders, PageAction.Update)]
    public Task<WorkOrderDto> Update(Guid id, UpsertWorkOrderRequest req, CancellationToken ct) => wos.UpdateAsync(id, req, ct);

    [HttpDelete("{id:guid}"), RequirePage(PageKeys.WorkOrders, PageAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await wos.DeleteAsync(id, ct); return NoContent(); }

    /// <summary>Completes production: materials out, finished item in (needs Update).</summary>
    [HttpPost("{id:guid}/submit"), RequirePage(PageKeys.WorkOrders, PageAction.Update)]
    public Task<WorkOrderDto> Submit(Guid id, CancellationToken ct) => wos.SubmitAsync(id, ct);

    [HttpPost("{id:guid}/cancel"), RequirePage(PageKeys.WorkOrders, PageAction.Delete)]
    public Task<WorkOrderDto> Cancel(Guid id, CancellationToken ct) => wos.CancelAsync(id, ct);
}

[ApiController]
[Route("api/sales-invoices")]
public class SalesInvoicesController(ISalesInvoiceService invoices) : ControllerBase
{
    [HttpGet, RequirePage(PageKeys.SalesInvoices)]
    public Task<PagedResult<SalesInvoiceSummaryDto>> List([FromQuery] string? status, [FromQuery] Guid? customerId,
        [FromQuery] Guid? customerPoId, [FromQuery] string? search,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        invoices.ListAsync(status, customerId, customerPoId, search, page, pageSize, ct);

    [HttpGet("{id:guid}"), RequirePage(PageKeys.SalesInvoices)]
    public Task<SalesInvoiceDto> Get(Guid id, CancellationToken ct) => invoices.GetAsync(id, ct);

    /// <summary>Company + invoice, for the print layout. format = "built-in" or a print template id (null = the usual one).</summary>
    [HttpGet("{id:guid}/print"), RequirePage(PageKeys.SalesInvoices)]
    public Task<InvoicePrintDto> Print(Guid id, [FromQuery] string? format, CancellationToken ct) => invoices.PrintAsync(id, format, ct);

    [HttpPost, RequirePage(PageKeys.SalesInvoices, PageAction.Write)]
    public async Task<ActionResult<SalesInvoiceDto>> Create(UpsertSalesInvoiceRequest req, CancellationToken ct)
    {
        var inv = await invoices.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = inv.Id }, inv);
    }

    [HttpPut("{id:guid}"), RequirePage(PageKeys.SalesInvoices, PageAction.Update)]
    public Task<SalesInvoiceDto> Update(Guid id, UpsertSalesInvoiceRequest req, CancellationToken ct) => invoices.UpdateAsync(id, req, ct);

    [HttpDelete("{id:guid}"), RequirePage(PageKeys.SalesInvoices, PageAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await invoices.DeleteAsync(id, ct); return NoContent(); }

    /// <summary>Issues the invoice: stock goes out, the P.O. is updated (needs Update).</summary>
    [HttpPost("{id:guid}/submit"), RequirePage(PageKeys.SalesInvoices, PageAction.Update)]
    public Task<SalesInvoiceDto> Submit(Guid id, CancellationToken ct) => invoices.SubmitAsync(id, ct);

    [HttpPost("{id:guid}/cancel"), RequirePage(PageKeys.SalesInvoices, PageAction.Delete)]
    public Task<SalesInvoiceDto> Cancel(Guid id, CancellationToken ct) => invoices.CancelAsync(id, ct);
}

[ApiController]
[Route("api/stock/details")]
public class StockDetailsController(IStockDetailsService details) : ControllerBase
{
    /// <param name="attention">Only items whose available stock is below the reorder level (or none / short).</param>
    [HttpGet, RequirePage(PageKeys.StockDetails)]
    public Task<StockDetailsResult> List([FromQuery] string? search, [FromQuery] string? itemType, [FromQuery] bool attention = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        details.ListAsync(search, itemType, attention, page, pageSize, ct);

    [HttpGet("{productId:guid}"), RequirePage(PageKeys.StockDetails)]
    public Task<StockItemDetailsDto> Item(Guid productId, CancellationToken ct) => details.ItemAsync(productId, ct);
}

/// <summary>Pickers for the sales screens: whoever can open one of them may list customers and P.O.s to choose from.</summary>
[ApiController]
[Route("api/sales/lookups")]
public class SalesLookupsController(ErpDbContext db) : ControllerBase
{
    [HttpGet("customers"), RequireAnyPage(PageKeys.CustomerPos, PageKeys.SalesInvoices, PageKeys.Customers)]
    public async Task<IReadOnlyList<CustomerDto>> Customers(CancellationToken ct) =>
        await db.Customers.AsNoTracking().OrderBy(c => c.Name).Take(1000)
            .Select(c => new CustomerDto(c.Id, c.Name, c.Email, c.City, c.Country, c.Phone, c.ContactPerson, c.TaxId, c.Address,
                c.State, c.PostalCode, c.PaymentTermsDays))
            .ToListAsync(ct);

    /// <summary>Confirmed P.O.s (the ones work orders and invoices can be raised against).</summary>
    [HttpGet("open-pos"), RequireAnyPage(PageKeys.CustomerPos, PageKeys.WorkOrders, PageKeys.SalesInvoices)]
    public async Task<IReadOnlyList<DocumentLinkDto>> OpenPos([FromQuery] Guid? customerId, CancellationToken ct) =>
        await db.CustomerPos.AsNoTracking().Where(p => p.Status == CustomerPoStatus.Confirmed && (customerId == null || p.CustomerId == customerId))
            .OrderByDescending(p => p.PoDate).ThenByDescending(p => p.Number).Take(500)
            .Select(p => new DocumentLinkDto(p.Id, p.Number, p.Status, p.PoDate, p.GrandTotal, p.Customer!.Name + " · " + p.CustomerPoNo))
            .ToListAsync(ct);
}

/// <summary>
/// Settings › Company: the organization's name, address and tax number, printed on invoices.
/// Everyone in the organization may read it; only SuperAdmin and Admin may change it.
/// </summary>
[ApiController]
[Route("api/company")]
[Authorize]
public class CompanyController(ErpDbContext db, ITenantContext tenant) : ControllerBase
{
    [HttpGet]
    public async Task<CompanyDto> Get(CancellationToken ct) =>
        await db.Organizations.AsNoTracking().Where(o => o.Id == tenant.OrganizationId)
            .Select(o => new CompanyDto(o.Name, o.Address, o.State, o.Phone, o.Email, o.TaxId)).SingleAsync(ct);

    [HttpPut, Authorize(Roles = $"{Roles.SuperAdmin},{Roles.Admin}")]
    public async Task<CompanyDto> Update(UpdateCompanyRequest req, CancellationToken ct)
    {
        var o = await db.Organizations.SingleAsync(x => x.Id == tenant.OrganizationId, ct);
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        o.Name = req.Name.Trim();
        (o.Address, o.State, o.Phone, o.Email, o.TaxId) = (Clean(req.Address), Clean(req.State), Clean(req.Phone), Clean(req.Email),
            Clean(req.TaxId)?.ToUpperInvariant());
        await db.SaveChangesAsync(ct);
        return new CompanyDto(o.Name, o.Address, o.State, o.Phone, o.Email, o.TaxId);
    }
}
