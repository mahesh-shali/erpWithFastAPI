namespace Erp.Api.Domain;

/// <summary>
/// Pages whose documents can be printed with an organization's own formats (Settings › Print templates). A print format
/// belongs to a page of the menu (module › group › page), e.g. Sales &amp; Labour Work › Orders › Customer P.O.
/// Add a page here only together with its print model (PrintTemplateRenderer), starter (PrintTemplateStarter), sample
/// data and print endpoint.
/// </summary>
public static class PrintablePages
{
    public static readonly string[] All = [PageKeys.CustomerPos, PageKeys.SalesInvoices, PageKeys.WorkOrders];
}

/// <summary>
/// An organization's print format for one page (Settings › Print templates): HTML + CSS with Scriban placeholders,
/// rendered and sanitized on the server. A page can have several formats; one may be its default (none = the
/// built-in layout), and the print screen lets the user pick any other. Rows are immutable versions: editing adds
/// version n+1 (same FamilyId) and archives the previous row, so a document that froze a version (an issued invoice)
/// always reprints identically.
/// </summary>
public class PrintTemplate : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    /// <summary>Shared by every version of one template.</summary>
    public Guid FamilyId { get; set; }
    /// <summary>The page (PageKeys) whose documents this format prints; one of <see cref="PrintablePages.All"/>.</summary>
    public string PageKey { get; set; } = PageKeys.SalesInvoices;
    public string Name { get; set; } = default!;
    /// <summary>Template source (HTML/CSS with Scriban {{ }} placeholders), as the admin wrote it.</summary>
    public string Body { get; set; } = default!;
    public int Version { get; set; } = 1;
    public bool IsDefault { get; set; }
    /// <summary>Replaced by a newer version, or retired. Archived rows are kept for documents that froze them.</summary>
    public bool IsArchived { get; set; }
    /// <summary>Who saved it. A platform admin may save formats for another organization, so this is not always a member.</summary>
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
