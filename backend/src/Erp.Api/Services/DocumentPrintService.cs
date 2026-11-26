using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Scriban.Runtime;

namespace Erp.Api.Services;

/// <summary>
/// Print screens of Customer P.O. and Work Order, and the data behind template previews of every printable page.
/// Unlike invoices (which have a built-in React layout), these always come back rendered: the built-in layout is the
/// page's starter template. A chosen format that fails to render falls back to the built-in one with a warning.
/// </summary>
public interface IDocumentPrintService
{
    Task<DocumentPrintDto> CustomerPoAsync(Guid id, string? format, CancellationToken ct);
    Task<DocumentPrintDto> WorkOrderAsync(Guid id, string? format, CancellationToken ct);
    /// <summary>
    /// Data for a template preview: one of the signed-in organization's documents of <paramref name="pageKey"/>
    /// (tenant-filtered, so another organization's id is 404), or sample data with <paramref name="company"/>'s details.
    /// </summary>
    Task<ScriptObject> PreviewModelAsync(string pageKey, Guid? documentId, CompanyDto company, CancellationToken ct);
}

public sealed class DocumentPrintService(ErpDbContext db, ITenantContext tenant, ICustomerPoService pos, IWorkOrderService wos,
    ISalesInvoiceService invoices, IPrintTemplateService templates) : IDocumentPrintService
{
    public async Task<DocumentPrintDto> CustomerPoAsync(Guid id, string? format, CancellationToken ct)
    {
        var po = await pos.GetAsync(id, ct);
        var model = PrintTemplateRenderer.CustomerPoModel(await CompanyAsync(ct), await PartyAsync(po.CustomerId, ct), po);
        return await RenderAsync(PageKeys.CustomerPos, po.Id, po.Number, $"{po.Number} · {po.CustomerName}", format, model, ct);
    }

    public async Task<DocumentPrintDto> WorkOrderAsync(Guid id, string? format, CancellationToken ct)
    {
        var wo = await wos.GetAsync(id, ct);
        var model = PrintTemplateRenderer.WorkOrderModel(await CompanyAsync(ct), wo);
        return await RenderAsync(PageKeys.WorkOrders, wo.Id, wo.Number, $"{wo.Number} · {wo.ProductName}", format, model, ct);
    }

    public async Task<ScriptObject> PreviewModelAsync(string pageKey, Guid? documentId, CompanyDto company, CancellationToken ct)
    {
        PrintTemplateService.CheckPage(pageKey);
        switch (pageKey)
        {
            case PageKeys.SalesInvoices when documentId is { } id:
            {
                var p = await invoices.PrintAsync(id, PrintTemplateService.BuiltIn, ct);
                return PrintTemplateRenderer.InvoiceModel(p.Company, p.Invoice);
            }
            case PageKeys.SalesInvoices:
            {
                var (co, inv) = PrintTemplateRenderer.Sample(company);
                return PrintTemplateRenderer.InvoiceModel(co, inv);
            }
            case PageKeys.CustomerPos when documentId is { } id:
            {
                var po = await pos.GetAsync(id, ct);
                return PrintTemplateRenderer.CustomerPoModel(await CompanyAsync(ct), await PartyAsync(po.CustomerId, ct), po);
            }
            case PageKeys.CustomerPos:
            {
                var (co, party, po) = PrintTemplateRenderer.SampleCustomerPo(company);
                return PrintTemplateRenderer.CustomerPoModel(co, party, po);
            }
            case PageKeys.WorkOrders when documentId is { } id:
                return PrintTemplateRenderer.WorkOrderModel(await CompanyAsync(ct), await wos.GetAsync(id, ct));
            default:
            {
                var (co, wo) = PrintTemplateRenderer.SampleWorkOrder(company);
                return PrintTemplateRenderer.WorkOrderModel(co, wo);
            }
        }
    }

    private async Task<DocumentPrintDto> RenderAsync(string pageKey, Guid id, string number, string title, string? format,
        ScriptObject model, CancellationToken ct)
    {
        var choice = await templates.ChooseAsync(pageKey, format, false, null, ct);
        string? warning = null;
        if (choice.Template is { } t)
        {
            try
            {
                var r = PrintTemplateRenderer.Render(t.Body, model);
                return new DocumentPrintDto(pageKey, id, number, title, new RenderedTemplateDto(t.Id, t.Name, t.Version, r.Html, r.Css), choice.Formats);
            }
            catch (DomainException e)
            {
                warning = $"Template \"{t.Name}\" (version {t.Version}) could not be used: {e.Message} Printed with the built-in layout.";
            }
        }
        var b = PrintTemplateRenderer.Render(PrintTemplateStarter.For(pageKey), model);
        return new DocumentPrintDto(pageKey, id, number, title,
            new RenderedTemplateDto(null, PrintTemplateService.BuiltInName, 0, b.Html, b.Css), choice.Formats, warning);
    }

    private Task<CompanyDto> CompanyAsync(CancellationToken ct) =>
        db.Organizations.AsNoTracking().Where(o => o.Id == tenant.OrganizationId)
            .Select(o => new CompanyDto(o.Name, o.Address, o.State, o.Phone, o.Email, o.TaxId)).SingleAsync(ct);

    private async Task<PrintPartyDto> PartyAsync(Guid customerId, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId, ct)
                ?? throw new NotFoundException("Customer not found.");
        return new PrintPartyDto(c.Name, SalesInvoiceService.PostalAddress(c), c.TaxId, c.State, c.Phone, c.Email);
    }
}
