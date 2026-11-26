namespace Erp.Api.Domain;

/// <summary>Documents that can be printed with an organization's own template.</summary>
public static class PrintDocumentType
{
    public const string SalesInvoice = "sales-invoice";
    public static readonly string[] All = [SalesInvoice];
}

/// <summary>
/// An organization's custom print layout (Settings › Print templates): HTML + CSS with Scriban placeholders, rendered
/// and sanitized on the server. Rows are immutable versions: editing adds version n+1 (same FamilyId) and archives the
/// previous row, so a document that froze a version (e.g. an issued invoice) always reprints identically.
/// At most one current row per organization and document type is the default; none = the built-in layout.
/// </summary>
public class PrintTemplate : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    /// <summary>Shared by every version of one template.</summary>
    public Guid FamilyId { get; set; }
    public string DocumentType { get; set; } = PrintDocumentType.SalesInvoice;
    public string Name { get; set; } = default!;
    /// <summary>Template source (HTML/CSS with Scriban {{ }} placeholders), as the admin wrote it.</summary>
    public string Body { get; set; } = default!;
    public int Version { get; set; } = 1;
    public bool IsDefault { get; set; }
    /// <summary>Replaced by a newer version, or retired. Archived rows are kept for documents that froze them.</summary>
    public bool IsArchived { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
