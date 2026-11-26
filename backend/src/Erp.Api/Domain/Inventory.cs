namespace Erp.Api.Domain;

// Inventory module. Design follows the usual ERP model (e.g. ERPNext: Warehouse, Stock Entry, Stock Ledger Entry,
// Bin; moving-average valuation), written for this code base:
//
//   Stock Entry (document, Draft -> Submitted -> Cancelled)
//     └─ on submit: one immutable Stock Ledger Entry per movement, and the Stock Balance (product × store) is updated
//   products.stock_quantity always equals the sum of that product's balances over all stores.

/// <summary>Unit of measure, e.g. NOS (numbers), KG, BOX.</summary>
public class Uom : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A store / warehouse holding stock. Each organization has exactly one default store (Main Store).</summary>
public class Store : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Location { get; set; }
    /// <summary>Sales orders issue from, and product opening stock goes into, the default store.</summary>
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class StockEntryType
{
    /// <summary>Stock comes in (target store), at a given rate.</summary>
    public const string Receipt = "Receipt";
    /// <summary>Stock goes out (source store), valued at the store's moving-average rate.</summary>
    public const string Issue = "Issue";
    /// <summary>Stock moves between two stores at the source store's rate.</summary>
    public const string Transfer = "Transfer";
    public static readonly string[] All = [Receipt, Issue, Transfer];
}

/// <summary>Lifecycle shared by business documents: only Draft can be edited; Submitted posts; Cancelled reverses.</summary>
public static class DocStatus
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Draft, Submitted, Cancelled];
}

/// <summary>What caused a ledger movement.</summary>
public static class VoucherType
{
    public const string Opening = "Opening";
    public const string StockEntry = "Stock Entry";
    public const string SalesOrder = "Sales Order";
    public const string ProductAdjustment = "Product Adjustment";
    public const string WorkOrder = "Work Order";
    public const string SalesInvoice = "Sales Invoice";
}

public class StockEntry : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    /// <summary>Per-organization number, e.g. SE-00001 (document_series).</summary>
    public string Number { get; set; } = default!;
    public string Type { get; set; } = StockEntryType.Receipt;
    public string Status { get; set; } = DocStatus.Draft;
    public DateOnly PostingDate { get; set; }
    public Guid? SourceStoreId { get; set; }
    public Store? SourceStore { get; set; }
    public Guid? TargetStoreId { get; set; }
    public Store? TargetStore { get; set; }
    public string? Remarks { get; set; }
    public decimal TotalValue { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public List<StockEntryLine> Lines { get; set; } = [];
}

public class StockEntryLine : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid StockEntryId { get; set; }
    public int LineNo { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public int Quantity { get; set; }
    /// <summary>Receipt: the incoming rate entered. Issue/Transfer: the valuation rate used when submitted.</summary>
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>Current quantity and moving-average value of one product in one store (ERPNext calls this a Bin).</summary>
public class StockBalance : ITenantOwned
{
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid StoreId { get; set; }
    public Store? Store { get; set; }
    public int Quantity { get; set; }
    public decimal ValuationRate { get; set; }
    public decimal StockValue { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One stock movement. Never updated or deleted; a cancellation adds reversing rows.</summary>
public class StockLedgerEntry : ITenantOwned
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid StoreId { get; set; }
    public Store? Store { get; set; }
    public DateOnly PostingDate { get; set; }
    public DateTime PostedAt { get; set; } = DateTime.UtcNow;
    public int QtyChange { get; set; }
    public int QtyAfter { get; set; }
    /// <summary>Rate of this movement: incoming rate, or the valuation rate it went out at.</summary>
    public decimal Rate { get; set; }
    /// <summary>Moving-average rate of the balance after this movement.</summary>
    public decimal ValuationRate { get; set; }
    public decimal ValueChange { get; set; }
    public decimal ValueAfter { get; set; }
    public string VoucherType { get; set; } = default!;
    public Guid? VoucherId { get; set; }
    public string VoucherNo { get; set; } = default!;
    /// <summary>True on the reversing rows written when a document is cancelled.</summary>
    public bool IsReversal { get; set; }
}

/// <summary>Next document number per organization and prefix (SE, later PO, GRN, SI…). Updated with SQL only.</summary>
public class DocumentSeries
{
    public Guid OrganizationId { get; set; }
    public string Prefix { get; set; } = default!;
    public int NextNumber { get; set; }
}
