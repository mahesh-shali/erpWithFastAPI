using Erp.Api.Services;
using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Erp.Api.Controllers;

[ApiController]
[Route("api/customers")]
public class CustomersController(ErpDbContext db) : ControllerBase
{
    private static readonly Expression<Func<Customer, CustomerDto>> Projection = c => new CustomerDto(c.Id, c.Name, c.Email, c.City,
        c.Country, c.Phone, c.ContactPerson, c.TaxId, c.Address, c.State, c.PostalCode, c.PaymentTermsDays);
    private static readonly Func<Customer, CustomerDto> Map = Projection.Compile();

    [HttpGet, RequirePage(PageKeys.Customers)]
    public async Task<PagedResult<CustomerDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(c => EF.Functions.ILike(c.Name, $"%{search}%"));
        var total = await q.LongCountAsync(ct);
        var items = await q.OrderBy(c => c.Name).Skip((page - 1) * pageSize).Take(pageSize).Select(Projection).ToListAsync(ct);
        return new PagedResult<CustomerDto>(items, page, pageSize, total);
    }

    [HttpGet("{id:guid}"), RequirePage(PageKeys.Customers)]
    public async Task<ActionResult<CustomerDto>> Get(Guid id, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().Where(x => x.Id == id).Select(Projection).FirstOrDefaultAsync(ct);
        if (c is null) return NotFound();
        return c;
    }

    [HttpPost, RequirePage(PageKeys.Customers, PageAction.Write)]
    public async Task<ActionResult<CustomerDto>> Create(UpsertCustomerRequest req, CancellationToken ct)
    {
        var c = new Customer { Id = Guid.NewGuid() };
        Apply(c, req);
        db.Customers.Add(c);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = c.Id }, Map(c));
    }

    [HttpPut("{id:guid}"), RequirePage(PageKeys.Customers, PageAction.Update)]
    public async Task<ActionResult<CustomerDto>> Update(Guid id, UpsertCustomerRequest req, CancellationToken ct)
    {
        var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        Apply(c, req);
        await db.SaveChangesAsync(ct);
        return Map(c);
    }

    /// <summary>A customer with P.O.s or invoices can't be deleted (the documents must keep their customer).</summary>
    [HttpDelete("{id:guid}"), RequirePage(PageKeys.Customers, PageAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (await db.CustomerPos.AnyAsync(p => p.CustomerId == id, ct) || await db.SalesInvoices.AnyAsync(i => i.CustomerId == id, ct))
            throw new ConflictException("This customer has P.O.s or invoices and can't be deleted.");
        var deleted = await db.Customers.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        if (deleted == 0) return NotFound();
        return NoContent();
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void Apply(Customer c, UpsertCustomerRequest req)
    {
        c.Name = req.Name.Trim();
        c.Email = Clean(req.Email);
        c.City = Clean(req.City);
        c.Country = Clean(req.Country);
        c.Phone = Clean(req.Phone);
        c.ContactPerson = Clean(req.ContactPerson);
        c.TaxId = Clean(req.TaxId)?.ToUpperInvariant();
        c.Address = Clean(req.Address);
        c.State = Clean(req.State);
        c.PostalCode = Clean(req.PostalCode);
        c.PaymentTermsDays = req.PaymentTermsDays;
    }
}
