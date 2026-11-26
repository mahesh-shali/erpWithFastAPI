using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

public interface ISalesInvoiceService
{
    Task<PagedResult<SalesInvoiceSummaryDto>> ListAsync(string? status, Guid? customerId, Guid? customerPoId, string? search,
        int page, int pageSize, CancellationToken ct);
    Task<SalesInvoiceDto> GetAsync(Guid id, CancellationToken ct);
    Task<InvoicePrintDto> PrintAsync(Guid id, CancellationToken ct);
    Task<SalesInvoiceDto> CreateAsync(UpsertSalesInvoiceRequest req, CancellationToken ct);
    Task<SalesInvoiceDto> UpdateAsync(Guid id, UpsertSalesInvoiceRequest req, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<SalesInvoiceDto> SubmitAsync(Guid id, CancellationToken ct);
    Task<SalesInvoiceDto> CancelAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Sales invoice: Draft → Submitted (the items leave the store at the moving-average rate and the P.O. lines'
/// invoiced quantity goes up; the P.O. completes when everything is invoiced) → Cancelled (reversed).
/// </summary>
public sealed class SalesInvoiceService(ErpDbContext db, IStockPosting posting, ITenantContext tenant, TimeProvider clock,
    IPrintTemplateService templates) : ISalesInvoiceService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<PagedResult<SalesInvoiceSummaryDto>> ListAsync(string? status, Guid? customerId, Guid? customerPoId, string? search,
        int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.SalesInvoices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(i => i.Status == status);
        if (customerId is not null) q = q.Where(i => i.CustomerId == customerId);
        if (customerPoId is not null) q = q.Where(i => i.CustomerPoId == customerPoId);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(i => EF.Functions.ILike(i.Number, $"%{search}%") || EF.Functions.ILike(i.BillingName, $"%{search}%")
                             || EF.Functions.ILike(i.CustomerPo!.Number, $"%{search}%"));
        var total = await q.LongCountAsync(ct);
        var items = await q.OrderByDescending(i => i.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new SalesInvoiceSummaryDto(i.Id, i.Number, i.Status, i.InvoiceDate, i.DueDate, i.Customer!.Name,
                i.CustomerPo!.Number, i.Lines.Count, i.GrandTotal, i.CreatedAt))
            .ToListAsync(ct);
        return new PagedResult<SalesInvoiceSummaryDto>(items, page, pageSize, total);
    }

    public async Task<SalesInvoiceDto> GetAsync(Guid id, CancellationToken ct)
    {
        var company = await CompanyAsync(ct);
        return ToDto(await LoadAsync(id, ct), company.State);
    }

    /// <summary>
    /// Issued and cancelled invoices print with the template version frozen when they were issued (none = built-in);
    /// drafts with the organization's current default. A template that fails to render falls back to the built-in layout.
    /// </summary>
    public async Task<InvoicePrintDto> PrintAsync(Guid id, CancellationToken ct)
    {
        var company = await CompanyAsync(ct);
        var entity = await LoadAsync(id, ct);
        var dto = ToDto(entity, company.State);
        var template = entity.Status == DocStatus.Draft ? await templates.DefaultAsync(PrintDocumentType.SalesInvoice, ct)
            : entity.PrintTemplateId is { } frozen ? await templates.FindAsync(frozen, ct) : null;
        if (template is null) return new InvoicePrintDto(company, dto);
        try
        {
            var r = PrintTemplateRenderer.Render(template.Body, PrintTemplateRenderer.InvoiceModel(company, dto));
            return new InvoicePrintDto(company, dto, new RenderedTemplateDto(template.Id, template.Name, template.Version, r.Html, r.Css));
        }
        catch (DomainException e)
        {
            return new InvoicePrintDto(company, dto, null,
                $"Template \"{template.Name}\" (version {template.Version}) could not be used: {e.Message} Printed with the built-in layout.");
        }
    }

    public async Task<SalesInvoiceDto> CreateAsync(UpsertSalesInvoiceRequest req, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        var v = await ValidateAsync(req, ct);
        var id = await db.InTransactionAsync(async () =>
        {
            var inv = new SalesInvoice
            {
                Id = Guid.NewGuid(), Number = await posting.NextNumberAsync("SI", ct),
                CreatedByUserId = tenant.UserId, Status = DocStatus.Draft,
            };
            Apply(inv, req, v);
            db.SalesInvoices.Add(inv);
            await db.SaveChangesAsync(ct);
            return inv.Id;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<SalesInvoiceDto> UpdateAsync(Guid id, UpsertSalesInvoiceRequest req, CancellationToken ct)
    {
        var v = await ValidateAsync(req, ct);
        var inv = await db.SalesInvoices.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct)
                  ?? throw new NotFoundException("Invoice not found.");
        if (inv.Status != DocStatus.Draft) throw new DomainException($"A {inv.Status.ToLowerInvariant()} invoice can't be edited.");
        db.SalesInvoiceLines.RemoveRange(inv.Lines);
        inv.Lines = [];
        Apply(inv, req, v);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var inv = await db.SalesInvoices.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Invoice not found.");
        if (inv.Status != DocStatus.Draft) throw new DomainException("Only drafts can be deleted; cancel a submitted invoice instead.");
        db.SalesInvoices.Remove(inv);
        await db.SaveChangesAsync(ct);
    }

    public async Task<SalesInvoiceDto> SubmitAsync(Guid id, CancellationToken ct)
    {
        await posting.EnsureDefaultStoreAsync(ct);
        await db.InTransactionAsync(async () =>
        {
            var inv = await db.LockAsync<SalesInvoice>("sales_invoices", id, "Invoice not found.", ct);
            if (inv.Status != DocStatus.Draft) throw new DomainException($"This invoice is already {inv.Status.ToLowerInvariant()}.");
            await db.Entry(inv).Collection(x => x.Lines).LoadAsync(ct);
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == inv.StoreId, ct) ?? throw new NotFoundException("Store not found.");
            if (!store.IsActive) throw new DomainException($"Store {store.Name} is inactive.");

            // Customer details are copied again now, so the printed invoice shows what was true when it was issued.
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == inv.CustomerId, ct)
                           ?? throw new NotFoundException("Customer not found.");
            Snapshot(inv, customer);

            var lines = inv.Lines.OrderBy(l => l.LineNo).ToList();
            if (inv.CustomerPoId is { } poId)
            {
                var po = await db.LockAsync<CustomerPo>("customer_pos", poId, "Customer P.O. not found.", ct);
                if (po.Status != CustomerPoStatus.Confirmed)
                    throw new DomainException($"P.O. {po.Number} is {po.Status.ToLowerInvariant()}; only confirmed P.O.s can be invoiced.");
                var poLines = await db.CustomerPoLines.Where(l => l.CustomerPoId == poId).ToListAsync(ct);
                foreach (var l in lines)
                {
                    var pl = poLines.Single(x => x.Id == l.CustomerPoLineId);
                    var left = pl.Quantity - pl.InvoicedQty;
                    if (l.Quantity > left)
                        throw new DomainException($"Line {l.LineNo}: only {left} left to invoice on P.O. {po.Number} ({pl.InvoicedQty} of {pl.Quantity} invoiced).");
                    pl.InvoicedQty += l.Quantity;
                }
                if (poLines.All(pl => pl.InvoicedQty >= pl.Quantity))
                {
                    po.Status = CustomerPoStatus.Completed;
                    po.CompletedAt = clock.GetUtcNow().UtcDateTime;
                }
            }

            var posted = await posting.PostAsync(lines.Select(l => new StockMovement(l.ProductId, inv.StoreId, -l.Quantity)).ToList(),
                new Voucher(VoucherType.SalesInvoice, inv.Id, inv.Number, inv.InvoiceDate), ct);
            for (var i = 0; i < lines.Count; i++) lines[i].CostRate = posted[i].Rate;
            inv.CostOfGoods = posted.Sum(p => Math.Abs(p.ValueChange));
            // Freeze the print layout: later template edits never change how an issued invoice reprints.
            inv.PrintTemplateId = (await templates.DefaultAsync(PrintDocumentType.SalesInvoice, ct))?.Id;
            inv.Status = DocStatus.Submitted;
            inv.SubmittedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<SalesInvoiceDto> CancelAsync(Guid id, CancellationToken ct)
    {
        await db.InTransactionAsync(async () =>
        {
            var inv = await db.LockAsync<SalesInvoice>("sales_invoices", id, "Invoice not found.", ct);
            if (inv.Status != DocStatus.Submitted) throw new DomainException("Only submitted invoices can be cancelled.");
            await db.Entry(inv).Collection(x => x.Lines).LoadAsync(ct);

            if (inv.CustomerPoId is { } poId)
            {
                var po = await db.LockAsync<CustomerPo>("customer_pos", poId, "Customer P.O. not found.", ct);
                var poLines = await db.CustomerPoLines.Where(l => l.CustomerPoId == poId).ToListAsync(ct);
                foreach (var l in inv.Lines)
                {
                    var pl = poLines.Single(x => x.Id == l.CustomerPoLineId);
                    pl.InvoicedQty = Math.Max(0, pl.InvoicedQty - l.Quantity);
                }
                if (po.Status == CustomerPoStatus.Completed)
                {
                    po.Status = CustomerPoStatus.Confirmed;
                    po.CompletedAt = null;
                }
            }

            var rows = await db.StockLedger.AsNoTracking()
                .Where(l => l.VoucherId == inv.Id && l.VoucherType == VoucherType.SalesInvoice && !l.IsReversal)
                .OrderByDescending(l => l.Id).ToListAsync(ct);
            await posting.PostAsync(rows.Select(r => new StockMovement(r.ProductId, r.StoreId, -r.QtyChange, r.Rate)).ToList(),
                new Voucher(VoucherType.SalesInvoice, inv.Id, inv.Number, Today, IsReversal: true), ct);

            inv.Status = DocStatus.Cancelled;
            inv.CancelledAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Validated(Customer Customer, CustomerPo? Po, IReadOnlyList<(SalesInvoiceLineRequest Req, Product Product, CustomerPoLine? PoLine)> Lines);

    private async Task<Validated> ValidateAsync(UpsertSalesInvoiceRequest req, CancellationToken ct)
    {
        var date = req.InvoiceDate ?? Today;
        if (date > Today) throw new DomainException("Invoice date can't be in the future.");
        if (req.DueDate is { } due && due < date) throw new DomainException("Due date can't be before the invoice date.");
        if (req.Lines.Count == 0) throw new DomainException("Add at least one item.");

        CustomerPo? po = null;
        Dictionary<Guid, CustomerPoLine> poLines = [];
        Guid customerId;
        if (req.CustomerPoId is { } poId)
        {
            po = await db.CustomerPos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == poId, ct) ?? throw new NotFoundException("Customer P.O. not found.");
            if (po.Status != CustomerPoStatus.Confirmed)
                throw new DomainException($"P.O. {po.Number} is {po.Status.ToLowerInvariant()}; only confirmed P.O.s can be invoiced.");
            if (req.CustomerId is { } c && c != po.CustomerId) throw new DomainException("The customer must be the P.O.'s customer.");
            customerId = po.CustomerId;
            poLines = await db.CustomerPoLines.AsNoTracking().Where(l => l.CustomerPoId == poId).ToDictionaryAsync(l => l.Id, ct);
        }
        else customerId = req.CustomerId ?? throw new DomainException("Choose a customer or a P.O.");

        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, ct)
                       ?? throw new NotFoundException("Customer not found.");

        var resolved = new List<(SalesInvoiceLineRequest, Guid ProductId, CustomerPoLine?)>();
        foreach (var l in req.Lines)
        {
            if (po is not null)
            {
                if (l.CustomerPoLineId is not { } lid || !poLines.TryGetValue(lid, out var pl))
                    throw new DomainException($"Every line must be a line of P.O. {po.Number}.");
                if (l.ProductId is { } p && p != pl.ProductId) throw new DomainException("A line's item must be the P.O. line's item.");
                if (l.Quantity > pl.Quantity - pl.InvoicedQty)
                    throw new DomainException($"Only {pl.Quantity - pl.InvoicedQty} left to invoice on P.O. line {pl.LineNo}.");
                resolved.Add((l, pl.ProductId, pl));
            }
            else
            {
                if (l.CustomerPoLineId is not null) throw new DomainException("Choose the P.O. to invoice its lines.");
                resolved.Add((l, l.ProductId ?? throw new DomainException("Choose an item for every line."), null));
            }
        }
        if (resolved.GroupBy(r => r.ProductId).Any(g => g.Count() > 1)) throw new DomainException("Each item can appear only once.");

        var ids = resolved.Select(r => r.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        if (products.Count != ids.Count) throw new NotFoundException("One or more items not found.");
        if (products.Values.FirstOrDefault(p => !p.IsActive) is { } inactive) throw new DomainException($"Item {inactive.Sku} is inactive.");

        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.StoreId, ct) ?? throw new NotFoundException("Store not found.");
        if (!store.IsActive) throw new DomainException($"Store {store.Name} is inactive.");

        return new Validated(customer, po, resolved.Select(r => (r.Item1, products[r.ProductId], r.Item3)).ToList());
    }

    private void Apply(SalesInvoice inv, UpsertSalesInvoiceRequest req, Validated v)
    {
        inv.CustomerId = v.Customer.Id;
        inv.CustomerPoId = v.Po?.Id;
        inv.InvoiceDate = req.InvoiceDate ?? Today;
        inv.DueDate = req.DueDate ?? inv.InvoiceDate.AddDays(v.Customer.PaymentTermsDays);
        inv.StoreId = req.StoreId;
        inv.ShippingAddress = string.IsNullOrWhiteSpace(req.ShippingAddress) ? null : req.ShippingAddress.Trim();
        inv.Remarks = string.IsNullOrWhiteSpace(req.Remarks) ? null : req.Remarks.Trim();
        Snapshot(inv, v.Customer);

        decimal gross = 0, discount = 0;
        inv.Lines = v.Lines.Select((x, i) =>
        {
            var taxRate = x.Req.TaxRate ?? x.PoLine?.TaxRate ?? x.Product.TaxRate;
            var m = Money.Line(x.Req.Quantity, x.Req.Rate, x.Req.DiscountPercent, taxRate);
            gross += m.Gross;
            discount += m.Discount;
            return new SalesInvoiceLine
            {
                LineNo = i + 1, ProductId = x.Product.Id, CustomerPoLineId = x.PoLine?.Id,
                HsnCode = x.Product.HsnCode, Quantity = x.Req.Quantity, Rate = x.Req.Rate, DiscountPercent = x.Req.DiscountPercent,
                Amount = m.Amount, TaxRate = taxRate, TaxAmount = m.Tax, Total = m.Amount + m.Tax,
            };
        }).ToList();
        inv.SubTotal = gross;
        inv.DiscountTotal = discount;
        inv.TaxTotal = inv.Lines.Sum(l => l.TaxAmount);
        (inv.GrandTotal, inv.RoundOff) = Money.RoundToRupee(inv.SubTotal - inv.DiscountTotal + inv.TaxTotal);
    }

    private static void Snapshot(SalesInvoice inv, Customer c)
    {
        inv.BillingName = c.Name;
        var cityLine = string.Join(" ", new[] { c.City, c.PostalCode }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var parts = new[] { c.Address, cityLine, c.State, c.Country }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        inv.BillingAddress = parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
        inv.BillingTaxId = c.TaxId;
        inv.BillingState = c.State;
    }

    private async Task<SalesInvoice> LoadAsync(Guid id, CancellationToken ct) =>
        await db.SalesInvoices.AsNoTracking()
            .Include(x => x.Customer).Include(x => x.CustomerPo).Include(x => x.Store)
            .Include(x => x.Lines.OrderBy(l => l.LineNo)).ThenInclude(l => l.Product!).ThenInclude(p => p.Uom)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Invoice not found.");

    private async Task<CompanyDto> CompanyAsync(CancellationToken ct) =>
        await db.Organizations.AsNoTracking().Where(o => o.Id == tenant.OrganizationId)
            .Select(o => new CompanyDto(o.Name, o.Address, o.State, o.Phone, o.Email, o.TaxId)).SingleAsync(ct);

    /// <summary>Different states (both known) = inter-state supply: IGST. Otherwise CGST + SGST.</summary>
    public static bool IsInterState(string? companyState, string? customerState) =>
        !string.IsNullOrWhiteSpace(companyState) && !string.IsNullOrWhiteSpace(customerState)
        && !string.Equals(companyState.Trim(), customerState.Trim(), StringComparison.OrdinalIgnoreCase);

    private static SalesInvoiceDto ToDto(SalesInvoice i, string? companyState) => new(i.Id, i.Number, i.Status, i.InvoiceDate, i.DueDate,
        i.CustomerId, i.Customer!.Name, i.CustomerPoId, i.CustomerPo?.Number, i.CustomerPo?.CustomerPoNo,
        i.StoreId, i.Store!.Name, i.BillingName, i.BillingAddress, i.BillingTaxId, i.BillingState, i.ShippingAddress, i.Remarks,
        i.SubTotal, i.DiscountTotal, i.TaxTotal, i.RoundOff, i.GrandTotal, i.CostOfGoods,
        IsInterState(companyState, i.BillingState), AmountInWords.Rupees(i.GrandTotal),
        i.Lines.GroupBy(l => l.TaxRate).OrderBy(g => g.Key).Select(g => new TaxSummaryRow(g.Key, g.Sum(l => l.Amount), g.Sum(l => l.TaxAmount))).ToList(),
        i.CreatedAt, i.SubmittedAt, i.CancelledAt,
        i.Lines.OrderBy(l => l.LineNo).Select(l => new SalesInvoiceLineDto(l.LineNo, l.ProductId, l.Product!.Sku, l.Product.Name,
            l.Product.Uom?.Code, l.HsnCode, l.CustomerPoLineId, l.Quantity, l.Rate, l.DiscountPercent, l.Amount, l.TaxRate,
            l.TaxAmount, l.Total, l.CostRate, l.Product.StockQuantity)).ToList());
}
