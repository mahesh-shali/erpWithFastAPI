using Erp.Api.Services;
using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Controllers;

[ApiController]
[Route("api/customers")]
public class CustomersController(ErpDbContext db) : ControllerBase
{
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
        var items = await q.OrderBy(c => c.Name).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new CustomerDto(c.Id, c.Name, c.Email, c.City, c.Country)).ToListAsync(ct);
        return new PagedResult<CustomerDto>(items, page, pageSize, total);
    }

    [HttpGet("{id:guid}"), RequirePage(PageKeys.Customers)]
    public async Task<ActionResult<CustomerDto>> Get(Guid id, CancellationToken ct)
    {
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        return new CustomerDto(c.Id, c.Name, c.Email, c.City, c.Country);
    }

    [HttpPost, RequirePage(PageKeys.Customers, PageAction.Write)]
    public async Task<ActionResult<CustomerDto>> Create(UpsertCustomerRequest req, CancellationToken ct)
    {
        var c = new Customer { Id = Guid.NewGuid(), Name = req.Name, Email = req.Email, City = req.City, Country = req.Country };
        db.Customers.Add(c);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = c.Id }, new CustomerDto(c.Id, c.Name, c.Email, c.City, c.Country));
    }

    [HttpDelete("{id:guid}"), RequirePage(PageKeys.Customers, PageAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await db.Customers.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        if (deleted == 0) return NotFound();
        return NoContent();
    }
}
