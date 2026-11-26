using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Services;

public interface ICustomerPoService
{
    Task<PagedResult<CustomerPoSummaryDto>> ListAsync(string? status, Guid? customerId, string? search, int page, int pageSize, CancellationToken ct);
    Task<CustomerPoDto> GetAsync(Guid id, CancellationToken ct);
    Task<CustomerPoDto> CreateAsync(UpsertCustomerPoRequest req, CancellationToken ct);
    Task<CustomerPoDto> UpdateAsync(Guid id, UpsertCustomerPoRequest req, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
    Task<CustomerPoDto> ConfirmAsync(Guid id, CancellationToken ct);
    Task<CustomerPoDto> CancelAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Customer P.O.: Draft (editable) → Confirmed (work orders and invoices may be raised) → Completed when every line is
/// fully invoiced; Cancelled only while nothing has been produced or invoiced against it.
/// Produced/invoiced quantities are kept by the work order and invoice services under a row lock on the P.O.
/// </summary>
public sealed class CustomerPoService(ErpDbContext db, IStockPosting posting, ITenantContext tenant, TimeProvider clock) : ICustomerPoService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<PagedResult<CustomerPoSummaryDto>> ListAsync(string? status, Guid? customerId, string? search,
        int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.CustomerPos.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(p => p.Status == status);
        if (customerId is not null) q = q.Where(p => p.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(p => EF.Functions.ILike(p.Number, $"%{search}%") || EF.Functions.ILike(p.CustomerPoNo, $"%{search}%")
                             || EF.Functions.ILike(p.Customer!.Name, $"%{search}%"));
        var total = await q.LongCountAsync(ct);
        var items = await q.OrderByDescending(p => p.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new CustomerPoSummaryDto(p.Id, p.Number, p.Customer!.Name, p.CustomerPoNo, p.PoDate, p.DeliveryDate, p.Status,
                p.Lines.Count, p.GrandTotal, p.Lines.Sum(l => l.Quantity), p.Lines.Sum(l => l.ProducedQty), p.Lines.Sum(l => l.InvoicedQty),
                p.CreatedAt))
            .ToListAsync(ct);
        return new PagedResult<CustomerPoSummaryDto>(items, page, pageSize, total);
    }

    public async Task<CustomerPoDto> GetAsync(Guid id, CancellationToken ct)
    {
        var po = await db.CustomerPos.AsNoTracking().Include(p => p.Customer)
            .Include(p => p.Lines.OrderBy(l => l.LineNo)).ThenInclude(l => l.Product!).ThenInclude(p => p.Uom)
            .AsSplitQuery().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Customer P.O. not found.");
        var wos = await db.WorkOrders.AsNoTracking().Where(w => w.CustomerPoId == id).OrderBy(w => w.CreatedAt)
            .Select(w => new DocumentLinkDto(w.Id, w.Number, w.Status, w.PostingDate, w.TotalCost, w.Product!.Sku + " × " + w.Quantity))
            .ToListAsync(ct);
        var invoices = await db.SalesInvoices.AsNoTracking().Where(i => i.CustomerPoId == id).OrderBy(i => i.CreatedAt)
            .Select(i => new DocumentLinkDto(i.Id, i.Number, i.Status, i.InvoiceDate, i.GrandTotal, null)).ToListAsync(ct);
        return ToDto(po, wos, invoices);
    }

    public async Task<CustomerPoDto> CreateAsync(UpsertCustomerPoRequest req, CancellationToken ct)
    {
        var products = await ValidateAsync(null, req, ct);
        var id = await db.InTransactionAsync(async () =>
        {
            var po = new CustomerPo
            {
                Id = Guid.NewGuid(), Number = await posting.NextNumberAsync("CPO", ct),
                CreatedByUserId = tenant.UserId, Status = CustomerPoStatus.Draft,
            };
            Apply(po, req, products);
            db.CustomerPos.Add(po);
            await db.SaveChangesAsync(ct);
            return po.Id;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<CustomerPoDto> UpdateAsync(Guid id, UpsertCustomerPoRequest req, CancellationToken ct)
    {
        var products = await ValidateAsync(id, req, ct);
        var po = await db.CustomerPos.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id, ct)
                 ?? throw new NotFoundException("Customer P.O. not found.");
        if (po.Status != CustomerPoStatus.Draft) throw new DomainException($"A {po.Status.ToLowerInvariant()} P.O. can't be edited.");
        db.CustomerPoLines.RemoveRange(po.Lines);
        po.Lines = [];
        Apply(po, req, products);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var po = await db.CustomerPos.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Customer P.O. not found.");
        if (po.Status != CustomerPoStatus.Draft) throw new DomainException("Only drafts can be deleted; cancel a confirmed P.O. instead.");
        db.CustomerPos.Remove(po);
        await db.SaveChangesAsync(ct);
    }

    public async Task<CustomerPoDto> ConfirmAsync(Guid id, CancellationToken ct)
    {
        await db.InTransactionAsync(async () =>
        {
            var po = await db.LockAsync<CustomerPo>("customer_pos", id, "Customer P.O. not found.", ct);
            if (po.Status != CustomerPoStatus.Draft) throw new DomainException($"This P.O. is already {po.Status.ToLowerInvariant()}.");
            if (!await db.Customers.AnyAsync(c => c.Id == po.CustomerId, ct)) throw new NotFoundException("Customer not found.");
            po.Status = CustomerPoStatus.Confirmed;
            po.ConfirmedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<CustomerPoDto> CancelAsync(Guid id, CancellationToken ct)
    {
        await db.InTransactionAsync(async () =>
        {
            var po = await db.LockAsync<CustomerPo>("customer_pos", id, "Customer P.O. not found.", ct);
            if (po.Status != CustomerPoStatus.Confirmed) throw new DomainException("Only confirmed P.O.s can be cancelled.");
            // Work orders and invoices lock the P.O. too, so none can be raised between this check and the update.
            if (await db.WorkOrders.AnyAsync(w => w.CustomerPoId == id && w.Status != DocStatus.Cancelled, ct)
                || await db.SalesInvoices.AnyAsync(i => i.CustomerPoId == id && i.Status != DocStatus.Cancelled, ct))
                throw new DomainException("This P.O. has work orders or invoices. Delete or cancel them first.");
            po.Status = CustomerPoStatus.Cancelled;
            po.CancelledAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return await GetAsync(id, ct);
    }

    private void Apply(CustomerPo po, UpsertCustomerPoRequest req, IReadOnlyDictionary<Guid, Product> products)
    {
        po.CustomerId = req.CustomerId;
        po.CustomerPoNo = req.CustomerPoNo.Trim();
        po.PoDate = req.PoDate ?? Today;
        po.DeliveryDate = req.DeliveryDate;
        po.Remarks = string.IsNullOrWhiteSpace(req.Remarks) ? null : req.Remarks.Trim();
        po.Lines = req.Lines.Select((l, i) =>
        {
            var taxRate = l.TaxRate ?? products[l.ProductId].TaxRate;
            var (_, _, amount, tax) = Money.Line(l.Quantity, l.Rate, 0, taxRate);
            return new CustomerPoLine
            {
                LineNo = i + 1, ProductId = l.ProductId, Quantity = l.Quantity, Rate = l.Rate,
                TaxRate = taxRate, Amount = amount, TaxAmount = tax,
            };
        }).ToList();
        po.SubTotal = po.Lines.Sum(l => l.Amount);
        po.TaxTotal = po.Lines.Sum(l => l.TaxAmount);
        po.GrandTotal = po.SubTotal + po.TaxTotal;
    }

    private async Task<Dictionary<Guid, Product>> ValidateAsync(Guid? id, UpsertCustomerPoRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.CustomerPoNo)) throw new DomainException("Enter the customer's P.O. number.");
        if (req.Lines.Count == 0) throw new DomainException("Add at least one item.");
        if (req.Lines.GroupBy(l => l.ProductId).Any(g => g.Count() > 1)) throw new DomainException("Each item can appear only once.");
        var poDate = req.PoDate ?? Today;
        if (poDate > Today) throw new DomainException("P.O. date can't be in the future.");
        if (req.DeliveryDate is { } d && d < poDate) throw new DomainException("Delivery date can't be before the P.O. date.");
        if (!await db.Customers.AnyAsync(c => c.Id == req.CustomerId, ct)) throw new NotFoundException("Customer not found.");

        var poNo = req.CustomerPoNo.Trim();
        if (await db.CustomerPos.AnyAsync(p => p.CustomerId == req.CustomerId && p.CustomerPoNo == poNo
                                               && p.Status != CustomerPoStatus.Cancelled && p.Id != id, ct))
            throw new ConflictException($"P.O. {poNo} of this customer is already entered.");

        var ids = req.Lines.Select(l => l.ProductId).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        if (products.Count != ids.Count) throw new NotFoundException("One or more items not found.");
        if (products.Values.FirstOrDefault(p => !p.IsActive) is { } inactive) throw new DomainException($"Item {inactive.Sku} is inactive.");
        return products;
    }

    private static CustomerPoDto ToDto(CustomerPo p, IReadOnlyList<DocumentLinkDto> wos, IReadOnlyList<DocumentLinkDto> invoices) =>
        new(p.Id, p.Number, p.CustomerId, p.Customer!.Name, p.CustomerPoNo, p.PoDate, p.DeliveryDate, p.Status, p.Remarks,
            p.SubTotal, p.TaxTotal, p.GrandTotal, p.CreatedAt, p.ConfirmedAt, p.CompletedAt, p.CancelledAt,
            p.Lines.OrderBy(l => l.LineNo).Select(l => new CustomerPoLineDto(l.Id, l.LineNo, l.ProductId, l.Product!.Sku, l.Product.Name,
                l.Product.Uom?.Code, l.Product.HsnCode, l.Quantity, l.Rate, l.TaxRate, l.Amount, l.TaxAmount, l.ProducedQty, l.InvoicedQty,
                Math.Max(0, l.Quantity - l.ProducedQty), l.Quantity - l.InvoicedQty, l.Product.StockQuantity)).ToList(),
            wos, invoices);
}
