using System.ComponentModel.DataAnnotations;

namespace Erp.Api.Contracts;

// ---------- Company (Settings > Company; printed on invoices) ----------
public record CompanyDto(string Name, string? Address, string? State, string? Phone, string? Email, string? TaxId);
public record UpdateCompanyRequest(
    [Required, MaxLength(200)] string Name,
    [MaxLength(500)] string? Address,
    [MaxLength(100)] string? State,
    [MaxLength(30)] string? Phone,
    [EmailAddress, MaxLength(320)] string? Email,
    [MaxLength(20)] string? TaxId);

// ---------- Customer P.O. ----------
/// <param name="TaxRate">Null = the item's GST rate.</param>
public record CustomerPoLineRequest([Required] Guid ProductId, [Range(1, 1_000_000)] int Quantity,
    [Range(0, 10_000_000)] decimal Rate, [Range(0, 100)] decimal? TaxRate = null);
public record UpsertCustomerPoRequest(
    [Required] Guid CustomerId,
    [Required, MaxLength(50)] string CustomerPoNo,
    DateOnly? PoDate,
    DateOnly? DeliveryDate,
    [MaxLength(500)] string? Remarks,
    [Required, MinLength(1)] List<CustomerPoLineRequest> Lines);

/// <param name="ToProduce">Ordered − produced (never below 0).</param>
/// <param name="ToInvoice">Ordered − invoiced.</param>
public record CustomerPoLineDto(Guid Id, int LineNo, Guid ProductId, string Sku, string ProductName, string? Uom, string? HsnCode,
    int Quantity, decimal Rate, decimal TaxRate, decimal Amount, decimal TaxAmount,
    int ProducedQty, int InvoicedQty, int ToProduce, int ToInvoice, int InStock);
public record CustomerPoDto(Guid Id, string Number, Guid CustomerId, string CustomerName, string CustomerPoNo,
    DateOnly PoDate, DateOnly? DeliveryDate, string Status, string? Remarks,
    decimal SubTotal, decimal TaxTotal, decimal GrandTotal,
    DateTime CreatedAt, DateTime? ConfirmedAt, DateTime? CompletedAt, DateTime? CancelledAt,
    IReadOnlyList<CustomerPoLineDto> Lines,
    IReadOnlyList<DocumentLinkDto> WorkOrders, IReadOnlyList<DocumentLinkDto> Invoices);
/// <param name="Progress">Invoiced quantity / ordered quantity, 0..1.</param>
public record CustomerPoSummaryDto(Guid Id, string Number, string CustomerName, string CustomerPoNo, DateOnly PoDate,
    DateOnly? DeliveryDate, string Status, int LineCount, decimal GrandTotal, int OrderedQty, int ProducedQty, int InvoicedQty,
    DateTime CreatedAt);
/// <summary>A related document shown on another (e.g. a P.O.'s work orders and invoices).</summary>
public record DocumentLinkDto(Guid Id, string Number, string Status, DateOnly Date, decimal Amount, string? Detail = null);

// ---------- Work orders (production) ----------
public record WorkOrderMaterialRequest([Required] Guid ProductId, [Range(1, 1_000_000)] int Quantity);
/// <param name="CustomerPoLineId">The P.O. line being produced (its item is used), or null to produce for stock.</param>
/// <param name="MaterialStoreId">Null = same as <paramref name="StoreId"/>.</param>
public record UpsertWorkOrderRequest(
    Guid? CustomerPoLineId,
    Guid? ProductId,
    [Range(1, 1_000_000)] int Quantity,
    [Required] Guid StoreId,
    Guid? MaterialStoreId,
    DateOnly? PostingDate,
    [Range(0, 100_000_000)] decimal OtherCost,
    [MaxLength(500)] string? Remarks,
    List<WorkOrderMaterialRequest>? Materials);
public record WorkOrderMaterialDto(Guid ProductId, string Sku, string ProductName, string? Uom, int Quantity, decimal Rate, decimal Amount,
    int InStock);
public record WorkOrderDto(Guid Id, string Number, string Status, DateOnly PostingDate,
    Guid? CustomerPoId, string? CustomerPoNumber, string? CustomerName, Guid? CustomerPoLineId,
    Guid ProductId, string Sku, string ProductName, string? Uom, int Quantity,
    Guid StoreId, string Store, Guid MaterialStoreId, string MaterialStore,
    decimal OtherCost, decimal MaterialCost, decimal UnitCost, decimal TotalCost, string? Remarks,
    DateTime CreatedAt, DateTime? SubmittedAt, DateTime? CancelledAt, IReadOnlyList<WorkOrderMaterialDto> Materials);
public record WorkOrderSummaryDto(Guid Id, string Number, string Status, DateOnly PostingDate, string? CustomerPoNumber,
    string? CustomerName, string Sku, string ProductName, int Quantity, string Store, decimal TotalCost, DateTime CreatedAt);

// ---------- Sales invoices ----------
/// <param name="CustomerPoLineId">The P.O. line being invoiced (its item is used), or null for an item not on a P.O.</param>
/// <param name="TaxRate">Null = the P.O. line's rate, else the item's GST rate.</param>
public record SalesInvoiceLineRequest(Guid? CustomerPoLineId, Guid? ProductId, [Range(1, 1_000_000)] int Quantity,
    [Range(0, 10_000_000)] decimal Rate, [Range(0, 100)] decimal DiscountPercent = 0, [Range(0, 100)] decimal? TaxRate = null);
/// <param name="CustomerPoId">Invoice against this P.O. (its customer is used); lines must then be its lines.</param>
/// <param name="DueDate">Null = invoice date + the customer's payment terms.</param>
public record UpsertSalesInvoiceRequest(
    Guid? CustomerId,
    Guid? CustomerPoId,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    [Required] Guid StoreId,
    [MaxLength(800)] string? ShippingAddress,
    [MaxLength(500)] string? Remarks,
    [Required, MinLength(1)] List<SalesInvoiceLineRequest> Lines);
public record SalesInvoiceLineDto(int LineNo, Guid ProductId, string Sku, string ProductName, string? Uom, string? HsnCode,
    Guid? CustomerPoLineId, int Quantity, decimal Rate, decimal DiscountPercent, decimal Amount, decimal TaxRate,
    decimal TaxAmount, decimal Total, decimal CostRate, int InStock);
/// <summary>A tax-rate row of the invoice's tax summary (CGST + SGST = half each within the state, IGST otherwise).</summary>
public record TaxSummaryRow(decimal TaxRate, decimal Taxable, decimal Tax);
public record SalesInvoiceDto(Guid Id, string Number, string Status, DateOnly InvoiceDate, DateOnly DueDate,
    Guid CustomerId, string CustomerName, Guid? CustomerPoId, string? CustomerPoNumber, string? CustomerPoRef,
    Guid StoreId, string Store, string BillingName, string? BillingAddress, string? BillingTaxId, string? BillingState,
    string? ShippingAddress, string? Remarks,
    decimal SubTotal, decimal DiscountTotal, decimal TaxTotal, decimal RoundOff, decimal GrandTotal, decimal CostOfGoods,
    bool InterState, string AmountInWords, IReadOnlyList<TaxSummaryRow> TaxSummary,
    DateTime CreatedAt, DateTime? SubmittedAt, DateTime? CancelledAt, IReadOnlyList<SalesInvoiceLineDto> Lines);
public record SalesInvoiceSummaryDto(Guid Id, string Number, string Status, DateOnly InvoiceDate, DateOnly DueDate,
    string CustomerName, string? CustomerPoNumber, int LineCount, decimal GrandTotal, DateTime CreatedAt);
/// <summary>
/// Everything the print layout needs in one call. Template = the organization's custom layout, already rendered and
/// sanitized (null = use the built-in layout). TemplateWarning explains a fall-back to the built-in layout.
/// </summary>
public record InvoicePrintDto(CompanyDto Company, SalesInvoiceDto Invoice, RenderedTemplateDto? Template = null,
    string? TemplateWarning = null);

// ---------- Print templates (Settings > Print templates) ----------
/// <param name="Html">Sanitized HTML for a shadow root; <paramref name="Css"/> goes into a constructed stylesheet.</param>
public record RenderedTemplateDto(Guid? TemplateId, string Name, int Version, string Html, string Css);
public record PrintTemplateSummaryDto(Guid Id, Guid FamilyId, string DocumentType, string Name, int Version, bool IsDefault,
    bool IsArchived, DateTime CreatedAt);
public record PrintTemplateDto(Guid Id, Guid FamilyId, string DocumentType, string Name, int Version, bool IsDefault,
    bool IsArchived, DateTime CreatedAt, string Body);
public record CreatePrintTemplateRequest(
    [Required, MaxLength(30)] string DocumentType,
    [Required, MaxLength(100)] string Name,
    [Required, MaxLength(200_000)] string Body,
    bool MakeDefault = false);
public record UpdatePrintTemplateRequest([Required, MaxLength(100)] string Name, [Required, MaxLength(200_000)] string Body);
/// <param name="InvoiceId">Preview with this invoice's data; null = sample data.</param>
public record PreviewPrintTemplateRequest([Required, MaxLength(30)] string DocumentType, [Required, MaxLength(200_000)] string Body,
    Guid? InvoiceId = null);
public record PrintTemplateStarterDto(string DocumentType, string Body);

// ---------- Stock details (Inventory > Stock Position > Stock Details) ----------
/// <param name="Committed">Still to be invoiced on confirmed customer P.O.s.</param>
/// <param name="InProduction">On draft work orders (planned, not yet produced).</param>
/// <param name="Available">On hand − committed (can be negative: a shortage).</param>
/// <param name="Status">OK | Reorder (available below the reorder level) | Short (available below zero).</param>
public record StockDetailsRow(Guid ProductId, string Sku, string Name, string ItemType, string? Category, string? Uom,
    int OnHand, decimal StockValue, int Committed, int InProduction, int Available, int ReorderLevel, string Status);
public record StockDetailsResult(IReadOnlyList<StockDetailsRow> Items, int Page, int PageSize, long Total);
public record StoreBalanceDto(Guid StoreId, string Store, int Quantity, decimal ValuationRate, decimal StockValue);
public record ItemCommitmentDto(Guid CustomerPoId, string Number, string CustomerName, DateOnly? DeliveryDate,
    int Ordered, int Produced, int Invoiced, int Pending);
public record StockItemDetailsDto(ProductDto Item, StockDetailsRow Summary, IReadOnlyList<StoreBalanceDto> Stores,
    IReadOnlyList<ItemCommitmentDto> Commitments, IReadOnlyList<DocumentLinkDto> OpenWorkOrders,
    IReadOnlyList<StockLedgerRow> RecentMovements);
