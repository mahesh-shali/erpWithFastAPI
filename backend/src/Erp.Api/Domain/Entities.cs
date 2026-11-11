namespace Erp.Api.Domain;

/// <summary>Marker for tenant-owned entities (global query filter applies).</summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; set; }
}

public class Organization
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class User
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Email { get; set; } = default!;
    public string PasswordHash { get; set; } = default!;
    public string FullName { get; set; } = default!;
    public int RoleId { get; set; } = Roles.UserId;
    public Role? Role { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// A role, stored in the <c>roles</c> table. The rows are seeded by migration (HasData), and
/// the names are what goes into the JWT "role" claim and [Authorize(Roles = ...)].
/// </summary>
public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    /// <summary>Hierarchy rank: you can only manage users/roles with a lower level than your own.</summary>
    public int Level { get; set; }
}

/// <summary>Well-known seeded roles (hierarchy SuperAdmin > Admin > User). Ids are referenced by seed data.</summary>
public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string User = "User";
    public const int SuperAdminId = 3;
    public const int AdminId = 1;
    public const int UserId = 2;

    public const int SuperAdminLevel = 100;
    public const int AdminLevel = 50;
    public const int UserLevel = 10;

    public static int LevelOf(string role) => role switch
    {
        SuperAdmin => SuperAdminLevel,
        Admin => AdminLevel,
        User => UserLevel,
        _ => 0,
    };
}

/// <summary>A screen of the application (Pages master). Seeded from code; rights are granted per page.</summary>
/// <summary>Level 1 of the side menu, e.g. Master, Inventory, Sales.</summary>
public class MenuModule
{
    public int Id { get; set; }
    public string Key { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>Icon name the frontend maps to an SVG (e.g. "settings", "box").</summary>
    public string Icon { get; set; } = default!;
    public int SortOrder { get; set; }
}

/// <summary>Level 2 of the side menu, e.g. Master > Users, Inventory > Stock Position.</summary>
public class MenuGroup
{
    public int Id { get; set; }
    public int ModuleId { get; set; }
    public MenuModule? Module { get; set; }
    public string Key { get; set; } = default!;
    public string Name { get; set; } = default!;
    public int SortOrder { get; set; }
}

/// <summary>
/// Level 3 of the side menu: a screen (Pages master). Rights are granted per page.
/// GroupId null = top-level link outside the modules (Dashboard, AI Assistant).
/// </summary>
public class Page
{
    public int Id { get; set; }
    /// <summary>Stable key used in code, permission checks and the SQL permission functions.</summary>
    public string Key { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string Route { get; set; } = default!;
    public int? GroupId { get; set; }
    public MenuGroup? Group { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>False = in the menu and rights matrix already, but the screen is "coming soon".</summary>
    public bool IsImplemented { get; set; } = true;
}

/// <summary>Keys of the seeded pages. Must match PageConfiguration seed data.</summary>
public static class PageKeys
{
    public const string Dashboard = "dashboard";
    public const string Customers = "customers";
    public const string Products = "products";
    public const string Orders = "orders";
    public const string AiChat = "ai-chat";
    public const string MasterUsers = "master-users";
    public const string MasterUserRights = "master-user-rights";
    public const string MasterRoleRights = "master-role-rights";
    public const string MasterPages = "master-pages";
    public const string Vendors = "vendors";
    public const string StockPosition = "stock-position";
    public const string StockAddition = "stock-addition";
    public const string SalesInvoices = "sales-invoices";
    public const string PurchaseOrders = "purchase-orders";
    public const string PurchaseBills = "purchase-bills";
    public const string ProductionPlan = "production-plan";
    public const string WorkOrders = "work-orders";
    public const string Receipts = "receipts";
    public const string Payments = "payments";
    public const string IncomingInspection = "incoming-inspection";
    public const string QualityChecks = "quality-checks";
    public const string Employees = "employees";
    public const string Attendance = "attendance";
    public const string SalesReport = "sales-report";
    public const string StockReport = "stock-report";
    // Master
    public const string Uom = "uom";
    public const string Stores = "stores";
    // Sales & Labour Work
    public const string LabourWorkOrders = "labour-work-orders";
    public const string LabourWorkInvoices = "labour-work-invoices";
    public const string DeliveryChallans = "delivery-challans";
    // Purchase & Subcontract
    public const string SubcontractOrders = "subcontract-orders";
    public const string SubcontractChallans = "subcontract-challans";
    public const string GoodsReceipts = "goods-receipts";
    // Planning
    public const string Mrp = "mrp";
    // Production
    public const string JobCards = "job-cards";
    public const string BillOfMaterials = "bill-of-materials";
    // Accounts
    public const string JournalVouchers = "journal-vouchers";
    public const string Ledger = "ledger";
    // Inspection (QC)
    public const string InProcessInspection = "in-process-inspection";
    public const string FinalInspection = "final-inspection";
    // Maintenance
    public const string Machines = "machines";
    public const string PreventiveMaintenance = "preventive-maintenance";
    public const string Breakdowns = "breakdowns";
    // HRMS
    public const string Payroll = "payroll";
    public const string LeaveRequests = "leave-requests";
    // Reports
    public const string ProductionReport = "production-report";
    public const string PurchaseReport = "purchase-report";
}

public enum PageAction { Read, Write, Update, Delete }

/// <summary>The six rights on a page. IsRestricted is a hard block that overrides all the others.</summary>
public interface IPageRights
{
    bool IsVisible { get; set; }
    bool IsRestricted { get; set; }
    bool CanRead { get; set; }
    bool CanWrite { get; set; }
    bool CanUpdate { get; set; }
    bool CanDelete { get; set; }
}

/// <summary>
/// Default rights of a role on a page. OrganizationId NULL = system default (seeded from code);
/// a row with an OrganizationId is that organization's customised default for the role.
/// </summary>
public class RolePagePermission : IPageRights
{
    public int Id { get; set; }
    public Guid? OrganizationId { get; set; }
    public int RoleId { get; set; }
    public int PageId { get; set; }
    public bool IsVisible { get; set; }
    public bool IsRestricted { get; set; }
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDelete { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>Per-user override of a page's rights. When present it replaces the role default for that page.</summary>
public class UserPagePermission : IPageRights
{
    public Guid UserId { get; set; }
    public int PageId { get; set; }
    public Guid OrganizationId { get; set; }
    public bool IsVisible { get; set; }
    public bool IsRestricted { get; set; }
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDelete { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
}

/// <summary>Effective rights of a user on a page, as computed by the SQL function effective_page_permissions().</summary>
public class EffectivePagePermission
{
    public int PageId { get; set; }
    public string PageKey { get; set; } = default!;
    public bool IsVisible { get; set; }
    public bool IsRestricted { get; set; }
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDelete { get; set; }
    /// <summary>superadmin | user | organization | system | none</summary>
    public string Source { get; set; } = default!;

    public bool Allows(PageAction action) => !IsRestricted && action switch
    {
        PageAction.Read => CanRead,
        PageAction.Write => CanWrite,
        PageAction.Update => CanUpdate,
        PageAction.Delete => CanDelete,
        _ => false,
    };
}

/// <summary>
/// A login session (one per sign-in, per device). The browser holds a random refresh token in an
/// HttpOnly cookie; only its SHA-256 hash is stored here. Short-lived access tokens carry the session
/// id ("sid" claim) and are rejected as soon as the session is revoked or expires.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }
    public string RefreshTokenHash { get; set; } = default!;
    /// <summary>Hash of the token before the last rotation; seeing it again means the token was stolen.</summary>
    public string? PreviousRefreshTokenHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Last login or refresh (updated at most once per access-token lifetime, not per request).</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public User? User { get; set; }

    public bool IsActive(DateTime now) => RevokedAt is null && ExpiresAt > now;
}

public class Customer : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = default!;
    public string? Email { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Product : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Sku { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? Category { get; set; }
    public decimal UnitPrice { get; set; }
    public int StockQuantity { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class OrderStatus
{
    public const string Pending = "Pending";
    public const string Paid = "Paid";
    public const string Shipped = "Shipped";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Pending, Paid, Shipped, Cancelled];
}

public class Order : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CustomerId { get; set; }
    public string Status { get; set; } = OrderStatus.Pending;
    public decimal TotalAmount { get; set; }
    public DateTime OrderedAt { get; set; } = DateTime.UtcNow;
    public Customer? Customer { get; set; }
    public List<OrderItem> Items { get; set; } = [];
}

public class OrderItem : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

/// <summary>
/// One row per AI chatbot question. Written only by the AI service through the
/// SECURITY DEFINER function <c>log_chat()</c> (see the AddRowLevelSecurityAndAiRole migration).
/// Not tenant-filtered by EF: it is an operator/audit table, not business data.
/// </summary>
public class ChatAuditLog
{
    public long Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string Question { get; set; } = default!;
    public string? GeneratedSql { get; set; }
    public int? RowCount { get; set; }
    public int? LatencyMs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
