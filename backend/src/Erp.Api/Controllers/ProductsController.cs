using Erp.Api.Contracts;
using Erp.Api.Domain;
using Erp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Erp.Api.Controllers;

[ApiController]
[Route("api/products")]
public class ProductsController(IProductService products) : ControllerBase
{
    [HttpGet, RequirePage(PageKeys.Products)]
    public Task<PagedResult<ProductDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? category = null, CancellationToken ct = default)
        => products.ListAsync(page, pageSize, category, ct);

    [HttpPost, RequirePage(PageKeys.Products, PageAction.Write)]
    public async Task<ActionResult<ProductDto>> Create(UpsertProductRequest req, CancellationToken ct)
    {
        var p = await products.CreateAsync(req, ct);
        return CreatedAtAction(nameof(List), new { id = p.Id }, p);
    }

    [HttpPut("{id:guid}"), RequirePage(PageKeys.Products, PageAction.Update)]
    public Task<ProductDto> Update(Guid id, UpsertProductRequest req, CancellationToken ct)
        => products.UpdateAsync(id, req, ct);

    [HttpDelete("{id:guid}"), RequirePage(PageKeys.Products, PageAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await products.DeleteAsync(id, ct);
        return NoContent();
    }
}
