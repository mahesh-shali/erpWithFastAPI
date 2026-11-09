using Erp.Api.Domain;
using Erp.Api.Contracts;
using Erp.Api.Data;
using Erp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Controllers;

[ApiController]
[Route("api/orders")]
public class OrdersController(ErpDbContext db, IOrderService orders) : ControllerBase
{
    /// <summary>Keyset pagination (cursor = last OrderedAt) - stays O(log n) at millions of rows.</summary>
    [HttpGet, RequirePage(PageKeys.Orders)]
    public async Task<IReadOnlyList<OrderDto>> List([FromQuery] DateTime? before = null,
        [FromQuery] int limit = 20, [FromQuery] string? status = null, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var q = db.Orders.AsNoTracking().Include(o => o.Items).AsSplitQuery();
        if (before is not null) q = q.Where(o => o.OrderedAt < before);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(o => o.Status == status);
        var list = await q.OrderByDescending(o => o.OrderedAt).Take(limit).ToListAsync(ct);
        return list.Select(OrderService.ToDto).ToList();
    }

    [HttpGet("{id:guid}"), RequirePage(PageKeys.Orders)]
    public async Task<ActionResult<OrderDto>> Get(Guid id, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound();
        return OrderService.ToDto(o);
    }

    [HttpPost, RequirePage(PageKeys.Orders, PageAction.Write)]
    public async Task<ActionResult<OrderDto>> Create(CreateOrderRequest req, CancellationToken ct)
    {
        var o = await orders.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = o.Id }, o);
    }

    [HttpPatch("{id:guid}/status"), RequirePage(PageKeys.Orders, PageAction.Update)]
    public Task<OrderDto> UpdateStatus(Guid id, UpdateOrderStatusRequest req, CancellationToken ct)
        => orders.UpdateStatusAsync(id, req.Status, ct);
}
