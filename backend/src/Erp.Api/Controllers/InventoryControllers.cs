using Erp.Api.Contracts;
using Erp.Api.Domain;
using Erp.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Erp.Api.Controllers;

// Rights: Read = list/view · Write = create · Update = edit (and submit, for stock entries) · Delete = delete (and cancel).

[ApiController]
[Route("api/uoms")]
public class UomsController(IInventoryService inv) : ControllerBase
{
    [HttpGet, RequirePage(PageKeys.Uom)]
    public Task<IReadOnlyList<UomDto>> List([FromQuery] bool all = false, CancellationToken ct = default) => inv.UomsAsync(all, ct);

    [HttpPost, RequirePage(PageKeys.Uom, PageAction.Write)]
    public async Task<ActionResult<UomDto>> Create(UpsertUomRequest req, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await inv.SaveUomAsync(null, req, ct));

    [HttpPut("{id:guid}"), RequirePage(PageKeys.Uom, PageAction.Update)]
    public Task<UomDto> Update(Guid id, UpsertUomRequest req, CancellationToken ct) => inv.SaveUomAsync(id, req, ct);

    [HttpDelete("{id:guid}"), RequirePage(PageKeys.Uom, PageAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await inv.DeleteUomAsync(id, ct); return NoContent(); }
}

[ApiController]
[Route("api/stores")]
public class StoresController(IInventoryService inv) : ControllerBase
{
    [HttpGet, RequirePage(PageKeys.Stores)]
    public Task<IReadOnlyList<StoreDto>> List([FromQuery] bool all = false, CancellationToken ct = default) => inv.StoresAsync(all, ct);

    [HttpPost, RequirePage(PageKeys.Stores, PageAction.Write)]
    public async Task<ActionResult<StoreDto>> Create(UpsertStoreRequest req, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await inv.SaveStoreAsync(null, req, ct));

    [HttpPut("{id:guid}"), RequirePage(PageKeys.Stores, PageAction.Update)]
    public Task<StoreDto> Update(Guid id, UpsertStoreRequest req, CancellationToken ct) => inv.SaveStoreAsync(id, req, ct);

    [HttpDelete("{id:guid}"), RequirePage(PageKeys.Stores, PageAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await inv.DeleteStoreAsync(id, ct); return NoContent(); }
}

[ApiController]
[Route("api/stock-entries")]
public class StockEntriesController(IInventoryService inv) : ControllerBase
{
    [HttpGet, RequirePage(PageKeys.StockAddition)]
    public Task<PagedResult<StockEntrySummaryDto>> List([FromQuery] string? status, [FromQuery] string? type,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        inv.EntriesAsync(status, type, page, pageSize, ct);

    [HttpGet("{id:guid}"), RequirePage(PageKeys.StockAddition)]
    public Task<StockEntryDto> Get(Guid id, CancellationToken ct) => inv.EntryAsync(id, ct);

    /// <summary>Saves a draft. Nothing moves until it is submitted.</summary>
    [HttpPost, RequirePage(PageKeys.StockAddition, PageAction.Write)]
    public async Task<ActionResult<StockEntryDto>> Create(UpsertStockEntryRequest req, CancellationToken ct)
    {
        var e = await inv.CreateEntryAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = e.Id }, e);
    }

    [HttpPut("{id:guid}"), RequirePage(PageKeys.StockAddition, PageAction.Update)]
    public Task<StockEntryDto> Update(Guid id, UpsertStockEntryRequest req, CancellationToken ct) => inv.UpdateEntryAsync(id, req, ct);

    [HttpDelete("{id:guid}"), RequirePage(PageKeys.StockAddition, PageAction.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await inv.DeleteEntryAsync(id, ct); return NoContent(); }

    /// <summary>Posts the stock movements (needs Update).</summary>
    [HttpPost("{id:guid}/submit"), RequirePage(PageKeys.StockAddition, PageAction.Update)]
    public Task<StockEntryDto> Submit(Guid id, CancellationToken ct) => inv.SubmitEntryAsync(id, ct);

    /// <summary>Reverses a submitted entry (needs Delete).</summary>
    [HttpPost("{id:guid}/cancel"), RequirePage(PageKeys.StockAddition, PageAction.Delete)]
    public Task<StockEntryDto> Cancel(Guid id, CancellationToken ct) => inv.CancelEntryAsync(id, ct);
}

[ApiController]
[Route("api/stock")]
public class StockController(IInventoryService inv) : ControllerBase
{
    [HttpGet("position"), RequirePage(PageKeys.StockPosition)]
    public Task<StockPositionResult> Position([FromQuery] Guid? storeId, [FromQuery] string? search,
        [FromQuery] bool lowOnly = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        inv.PositionAsync(storeId, search, lowOnly, page, pageSize, ct);

    [HttpGet("ledger"), RequirePage(PageKeys.StockReport)]
    public Task<PagedResult<StockLedgerRow>> Ledger([FromQuery] Guid? productId, [FromQuery] Guid? storeId,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken ct = default) => inv.LedgerAsync(productId, storeId, from, to, page, pageSize, ct);

    // Lookups for the inventory screens: whoever can open any of them may list active stores, units and items.
    [HttpGet("stores"), RequireAnyPage(PageKeys.Stores, PageKeys.StockAddition, PageKeys.StockPosition, PageKeys.StockReport, PageKeys.Products,
        PageKeys.StockDetails, PageKeys.WorkOrders, PageKeys.SalesInvoices)]
    public Task<IReadOnlyList<StoreDto>> Stores(CancellationToken ct) => inv.StoresAsync(false, ct);

    [HttpGet("uoms"), RequireAnyPage(PageKeys.Uom, PageKeys.Products)]
    public Task<IReadOnlyList<UomDto>> Uoms(CancellationToken ct) => inv.UomsAsync(false, ct);

    [HttpGet("items"), RequireAnyPage(PageKeys.StockAddition, PageKeys.StockPosition, PageKeys.StockReport, PageKeys.Products,
        PageKeys.StockDetails, PageKeys.CustomerPos, PageKeys.WorkOrders, PageKeys.SalesInvoices)]
    public Task<IReadOnlyList<ItemLookupDto>> Items([FromQuery] string? search, CancellationToken ct) => inv.ItemsAsync(search, ct);
}
