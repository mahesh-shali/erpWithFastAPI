namespace Erp.Api.Domain;

// Sales flow (make-to-order):
//
//   Customer P.O. (CPO-00001)  Draft -> Confirmed -> Completed (all lines invoiced) | Cancelled
//     ├─ Work Order (WO-00001)  Draft -> Submitted -> Cancelled
//     │     submit: raw materials out of the material store, the finished item into the output store,
//     │             valued at (materials consumed + other cost) / quantity; P.O. line produced_qty goes up
//     └─ Sales Invoice (SI-00001)  Draft -> Submitted -> Cancelled
//           submit: the items go out of the store at the moving-average rate; P.O. line invoiced_qty goes up
//
// Every stock movement goes through IStockPosting.PostAsync and is reversed on cancel (like Stock Entries).

public static class CustomerPoStatus
{
    public const string Draft = "Draft";
    /// <summary>Accepted: work orders and invoices can be made against it.</summary>
    public const string Confirmed = "Confirmed";
    /// <summary>Every line fully invoiced (set automatically; an invoice cancel re-opens it).</summary>
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Draft, Confirmed, Completed, Cancelled];
}

/// <summary>A purchase order received from a customer (our sales order). Numbered CPO-00001 per organization.</summary>
public class CustomerPo : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Number { get; set; } = default!;
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    /// <summary>The customer's own P.O. number (their reference, printed on the invoice).</summary>
    public string CustomerPoNo { get; set; } = default!;
    public DateOnly PoDate { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public string Status { get; set; } = CustomerPoStatus.Draft;
    public string? Remarks { get; set; }
    public decimal SubTotal { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal GrandTotal { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public List<CustomerPoLine> Lines { get; set; } = [];
}

public class CustomerPoLine : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CustomerPoId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public int Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal TaxRate { get; set; }
    /// <summary>Quantity × rate (before tax).</summary>
    public decimal Amount { get; set; }
    public decimal TaxAmount { get; set; }
    /// <summary>Sum of submitted work orders for this line (maintained by WorkOrderService).</summary>
    public int ProducedQty { get; set; }
    /// <summary>Sum of submitted invoice lines for this line (maintained by SalesInvoiceService).</summary>
    public int InvoicedQty { get; set; }
}

/// <summary>Production of one item, usually for a Customer P.O. line. Numbered WO-00001.</summary>
public class WorkOrder : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Number { get; set; } = default!;
    public Guid? CustomerPoId { get; set; }
    public CustomerPo? CustomerPo { get; set; }
    public Guid? CustomerPoLineId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public int Quantity { get; set; }
    /// <summary>Finished goods go into this store.</summary>
    public Guid StoreId { get; set; }
    public Store? Store { get; set; }
    /// <summary>Raw materials come out of this store.</summary>
    public Guid MaterialStoreId { get; set; }
    public Store? MaterialStore { get; set; }
    public DateOnly PostingDate { get; set; }
    /// <summary>Labour, machine and overhead cost added to the materials (total, not per unit).</summary>
    public decimal OtherCost { get; set; }
    public decimal MaterialCost { get; set; }
    /// <summary>Per-unit cost the finished item went into stock at (set on submit).</summary>
    public decimal UnitCost { get; set; }
    public decimal TotalCost { get; set; }
    public string Status { get; set; } = DocStatus.Draft;
    public string? Remarks { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public List<WorkOrderMaterial> Materials { get; set; } = [];
}

/// <summary>A raw material consumed by a work order. Rate/Amount are the valuation it left the store at.</summary>
public class WorkOrderMaterial : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public int Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>Tax invoice to a customer. Numbered SI-00001. Customer details are copied on save, so the print never changes.</summary>
public class SalesInvoice : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Number { get; set; } = default!;
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public Guid? CustomerPoId { get; set; }
    public CustomerPo? CustomerPo { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    /// <summary>The items are issued from this store.</summary>
    public Guid StoreId { get; set; }
    public Store? Store { get; set; }
    public string Status { get; set; } = DocStatus.Draft;
    public string BillingName { get; set; } = default!;
    public string? BillingAddress { get; set; }
    public string? BillingTaxId { get; set; }
    /// <summary>Customer's state: same as the company's = CGST + SGST, different = IGST.</summary>
    public string? BillingState { get; set; }
    public string? ShippingAddress { get; set; }
    public string? Remarks { get; set; }
    public decimal SubTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal TaxTotal { get; set; }
    /// <summary>Grand total rounded to the rupee; RoundOff is the difference.</summary>
    public decimal RoundOff { get; set; }
    public decimal GrandTotal { get; set; }
    /// <summary>Cost of the goods sold (valuation they left the store at), set on submit.</summary>
    public decimal CostOfGoods { get; set; }
    /// <summary>The print template version frozen when the invoice was issued (null = the built-in layout).</summary>
    public Guid? PrintTemplateId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public List<SalesInvoiceLine> Lines { get; set; } = [];
}

public class SalesInvoiceLine : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid SalesInvoiceId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid? CustomerPoLineId { get; set; }
    public string? HsnCode { get; set; }
    public int Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal DiscountPercent { get; set; }
    /// <summary>Taxable value: quantity × rate less the discount.</summary>
    public decimal Amount { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    /// <summary>Valuation rate the stock left at (set on submit).</summary>
    public decimal CostRate { get; set; }
}
