using Erp.Api.Domain;

namespace Erp.Api.Data.Configurations;

/// <summary>
/// Code-first seed for the 3-level side menu (Module > Group > Page) and the system-default
/// rights matrix per role. Change it here, then `make migration name=...` produces the SQL.
///
/// Rules: ids are permanent (rights rows reference page ids) - add new ids, never renumber.
/// SuperAdmin has no rows: it always has every right (enforced in effective_page_permissions()).
/// </summary>
public static class DefaultPermissions
{
    // ---------------- Level 1: modules ----------------
    public static readonly MenuModule[] Modules =
    [
        new() { Id = 1, Key = "master",         Name = "Master",         Icon = "settings",   SortOrder = 10 },
        new() { Id = 2, Key = "inventory",      Name = "Inventory",      Icon = "box",        SortOrder = 20 },
        new() { Id = 3, Key = "sales",          Name = "Sales",          Icon = "cart",       SortOrder = 30 },
        new() { Id = 4, Key = "purchase",       Name = "Purchase",       Icon = "truck",      SortOrder = 40 },
        new() { Id = 5, Key = "production",     Name = "Production",     Icon = "factory",    SortOrder = 50 },
        new() { Id = 6, Key = "cash-flow",      Name = "Cash Flow",      Icon = "wallet",     SortOrder = 60 },
        new() { Id = 7, Key = "inspection",     Name = "Inspection",     Icon = "check",      SortOrder = 70 },
        new() { Id = 8, Key = "human-resource", Name = "Human Resource", Icon = "people",     SortOrder = 80 },
        new() { Id = 9, Key = "reports",        Name = "Reports",        Icon = "chart",      SortOrder = 90 },
    ];

    // ---------------- Level 2: groups (id = module * 100 + n) ----------------
    public static readonly MenuGroup[] Groups =
    [
        G(101, 1, "master-users", "Users", 10),
        G(102, 1, "master-customers", "Customers", 20),
        G(103, 1, "master-vendors", "Vendors", 30),
        G(104, 1, "master-products", "Products", 40),
        G(105, 1, "master-settings", "Settings", 50),
        G(201, 2, "inventory-stock-position", "Stock Position", 10),
        G(202, 2, "inventory-stock-addition", "Stock Addition", 20),
        G(301, 3, "sales-orders", "Orders", 10),
        G(302, 3, "sales-invoices", "Invoices", 20),
        G(401, 4, "purchase-orders", "Orders", 10),
        G(402, 4, "purchase-bills", "Bills", 20),
        G(501, 5, "production-planning", "Planning", 10),
        G(502, 5, "production-work-orders", "Work Orders", 20),
        G(601, 6, "cash-flow-receipts", "Receipts", 10),
        G(602, 6, "cash-flow-payments", "Payments", 20),
        G(701, 7, "inspection-incoming", "Incoming", 10),
        G(702, 7, "inspection-quality", "Quality", 20),
        G(801, 8, "hr-employees", "Employees", 10),
        G(802, 8, "hr-attendance", "Attendance", 20),
        G(901, 9, "reports-sales", "Sales", 10),
        G(902, 9, "reports-inventory", "Inventory", 20),
    ];

    // ---------------- Level 3: pages ----------------
    public static readonly Page[] Pages =
    [
        // top level (no module)
        P(1,  PageKeys.Dashboard,        "Dashboard",           "/",                          null, 10),
        P(5,  PageKeys.AiChat,           "AI Assistant",        "/assistant",                 null, 20),
        // Master
        P(6,  PageKeys.MasterUsers,      "Users",               "/master/users",              101, 10),
        P(7,  PageKeys.MasterUserRights, "User Rights",         "/master/user-rights",        101, 20),
        P(8,  PageKeys.MasterRoleRights, "Role Rights",         "/master/role-rights",        101, 30),
        P(2,  PageKeys.Customers,        "Customers",           "/master/customers",          102, 10),
        P(10, PageKeys.Vendors,          "Vendors",             "/master/vendors",            103, 10, implemented: false),
        P(3,  PageKeys.Products,         "Products",            "/master/products",           104, 10),
        P(9,  PageKeys.MasterPages,      "Pages",               "/master/pages",              105, 10),
        // Inventory
        P(11, PageKeys.StockPosition,    "Stock Position",      "/inventory/stock-position",  201, 10, implemented: false),
        P(12, PageKeys.StockAddition,    "Stock Addition",      "/inventory/stock-addition",  202, 10, implemented: false),
        // Sales
        P(4,  PageKeys.Orders,           "Sales Orders",        "/sales/orders",              301, 10),
        P(13, PageKeys.SalesInvoices,    "Sales Invoices",      "/sales/invoices",            302, 10, implemented: false),
        // Purchase
        P(14, PageKeys.PurchaseOrders,   "Purchase Orders",     "/purchase/orders",           401, 10, implemented: false),
        P(15, PageKeys.PurchaseBills,    "Purchase Bills",      "/purchase/bills",            402, 10, implemented: false),
        // Production
        P(16, PageKeys.ProductionPlan,   "Production Plan",     "/production/plan",           501, 10, implemented: false),
        P(17, PageKeys.WorkOrders,       "Work Orders",         "/production/work-orders",    502, 10, implemented: false),
        // Cash Flow
        P(18, PageKeys.Receipts,         "Receipts",            "/cash-flow/receipts",        601, 10, implemented: false),
        P(19, PageKeys.Payments,         "Payments",            "/cash-flow/payments",        602, 10, implemented: false),
        // Inspection
        P(20, PageKeys.IncomingInspection, "Incoming Inspection", "/inspection/incoming",     701, 10, implemented: false),
        P(21, PageKeys.QualityChecks,    "Quality Checks",      "/inspection/quality",        702, 10, implemented: false),
        // Human Resource
        P(22, PageKeys.Employees,        "Employees",           "/hr/employees",              801, 10, implemented: false),
        P(23, PageKeys.Attendance,       "Attendance",          "/hr/attendance",             802, 10, implemented: false),
        // Reports
        P(24, PageKeys.SalesReport,      "Sales Report",        "/reports/sales",             901, 10, implemented: false),
        P(25, PageKeys.StockReport,      "Stock Report",        "/reports/stock",             902, 10, implemented: false),
    ];

    // V = visible, R = read, W = write (create), U = update, D = delete. Missing page = no rights.
    private static readonly Dictionary<int, Dictionary<string, string>> Matrix = new()
    {
        [Roles.AdminId] = new()
        {
            [PageKeys.Dashboard] = "VR",
            [PageKeys.AiChat] = "VR",
            [PageKeys.MasterUsers] = "VRWUD",
            [PageKeys.MasterUserRights] = "VRU",
            [PageKeys.MasterPages] = "VR",
            [PageKeys.Customers] = "VRWUD",
            [PageKeys.Vendors] = "VRWUD",
            [PageKeys.Products] = "VRWUD",
            [PageKeys.StockPosition] = "VR",
            [PageKeys.StockAddition] = "VRWUD",
            [PageKeys.Orders] = "VRWUD",
            [PageKeys.SalesInvoices] = "VRWUD",
            [PageKeys.PurchaseOrders] = "VRWUD",
            [PageKeys.PurchaseBills] = "VRWUD",
            [PageKeys.ProductionPlan] = "VRWUD",
            [PageKeys.WorkOrders] = "VRWUD",
            [PageKeys.Receipts] = "VRWUD",
            [PageKeys.Payments] = "VRWUD",
            [PageKeys.IncomingInspection] = "VRWUD",
            [PageKeys.QualityChecks] = "VRWUD",
            [PageKeys.Employees] = "VRWUD",
            [PageKeys.Attendance] = "VRWUD",
            [PageKeys.SalesReport] = "VR",
            [PageKeys.StockReport] = "VR",
        },
        [Roles.UserId] = new()
        {
            [PageKeys.Dashboard] = "VR",
            [PageKeys.AiChat] = "VR",
            [PageKeys.Customers] = "VRWU",
            [PageKeys.Products] = "VR",
            [PageKeys.StockPosition] = "VR",
            [PageKeys.Orders] = "VRWU",
            [PageKeys.Attendance] = "VR",
            [PageKeys.SalesReport] = "VR",
            [PageKeys.StockReport] = "VR",
        },
    };

    /// <summary>System defaults (OrganizationId = null). Ids: roleId * 100 + pageId (stable for migrations).</summary>
    public static readonly RolePagePermission[] SystemRoleDefaults =
        Matrix.SelectMany(role => Pages.Select(page =>
        {
            var f = role.Value.GetValueOrDefault(page.Key, "");
            return new RolePagePermission
            {
                Id = role.Key * 100 + page.Id, OrganizationId = null, RoleId = role.Key, PageId = page.Id,
                IsVisible = f.Contains('V'), IsRestricted = false, CanRead = f.Contains('R'),
                CanWrite = f.Contains('W'), CanUpdate = f.Contains('U'), CanDelete = f.Contains('D'),
            };
        })).ToArray();

    private static MenuGroup G(int id, int module, string key, string name, int sort) =>
        new() { Id = id, ModuleId = module, Key = key, Name = name, SortOrder = sort };

    private static Page P(int id, string key, string name, string route, int? group, int sort, bool implemented = true) =>
        new() { Id = id, Key = key, Name = name, Route = route, GroupId = group, SortOrder = sort, IsImplemented = implemented };
}
