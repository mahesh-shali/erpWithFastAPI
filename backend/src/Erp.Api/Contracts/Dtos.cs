using System.ComponentModel.DataAnnotations;

namespace Erp.Api.Contracts;

// ---------- Auth ----------
public record RegisterRequest(
    [Required, MaxLength(200)] string OrganizationName,
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password,
    [Required, MaxLength(200)] string FullName);

public record LoginRequest([Required, EmailAddress] string Email, [Required] string Password);

/// <summary>The refresh token is NOT in the body: it is set as an HttpOnly cookie (erp_refresh).</summary>
public record AuthResponse(string AccessToken, DateTime ExpiresAt, UserDto User, Guid SessionId, DateTime SessionExpiresAt);

public record UserDto(Guid Id, string Email, string FullName, string Role, Guid OrganizationId);

public record SessionDto(Guid Id, DateTime CreatedAt, DateTime LastSeenAt, DateTime ExpiresAt,
    string? IpAddress, string? UserAgent, bool Current);

// ---------- Users & roles (Master) ----------
public record RoleDto(int Id, string Name, string? Description, int Level);

/// <param name="CanManage">True if the caller is higher in the hierarchy and may edit/delete this user.</param>
public record OrgUserDto(Guid Id, string Email, string FullName, string Role, DateTime CreatedAt, DateTime? LastLoginAt,
    bool CanManage = false);

// ---------- Pages & rights (Master) ----------
/// <summary>Where a page sits in the 3-level menu. Module/Group are null for top-level pages (Dashboard, AI Assistant).</summary>
public record MenuPosition(string? ModuleKey, string? Module, string? ModuleIcon, int ModuleSort,
    string? GroupKey, string? Group, int GroupSort, int SortOrder);

public record PageDto(int Id, string Key, string Name, string Route, MenuPosition Menu, bool IsActive, bool IsImplemented);

public record PageRightsDto(bool IsVisible, bool IsRestricted, bool CanRead, bool CanWrite, bool CanUpdate, bool CanDelete);

/// <summary>The caller's effective rights on one page (drives the menu and buttons in the UI).</summary>
public record MyPagePermissionDto(string PageKey, string Name, string Route, MenuPosition Menu, bool IsImplemented,
    bool IsVisible, bool IsRestricted, bool CanRead, bool CanWrite, bool CanUpdate, bool CanDelete);

public record MyPermissionsDto(string Role, IReadOnlyList<MyPagePermissionDto> Pages);

/// <param name="Source">Where the effective rights come from: superadmin | user | organization | system | none.</param>
public record UserRightsRowDto(int PageId, string PageKey, string PageName, MenuPosition Menu,
    PageRightsDto Effective, string Source, PageRightsDto RoleDefault, bool IsOverridden);

public record UserRightsDto(Guid UserId, string FullName, string Email, string Role, bool CanEdit,
    IReadOnlyList<UserRightsRowDto> Pages);

/// <param name="UseDefault">True = remove the override and fall back to the default (role / system).</param>
public record SetRightsItem([Required] int PageId, bool UseDefault, PageRightsDto? Rights);

public record SetRightsRequest([Required] List<SetRightsItem> Items);

/// <param name="IsCustomized">True if this organization changed the system default for this role/page.</param>
public record RoleRightsRowDto(int PageId, string PageKey, string PageName, MenuPosition Menu,
    PageRightsDto Rights, bool IsCustomized, PageRightsDto SystemDefault);

public record RoleRightsDto(int RoleId, string Role, bool CanEdit, IReadOnlyList<RoleRightsRowDto> Pages);

public record CreateUserRequest(
    [Required, EmailAddress] string Email,
    [Required, MaxLength(200)] string FullName,
    [Required, MinLength(8)] string Password,
    [Required] string Role);

public record UpdateUserRoleRequest([Required] string Role);

// ---------- Paging ----------
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

// ---------- Customers ----------
public record CustomerDto(Guid Id, string Name, string? Email, string? City, string? Country);
public record UpsertCustomerRequest(
    [Required, MaxLength(200)] string Name, [EmailAddress] string? Email, string? City, string? Country);

// ---------- Products ----------
public record ProductDto(Guid Id, string Sku, string Name, string? Category, decimal UnitPrice, int StockQuantity,
    Guid? UomId = null, string? Uom = null);
/// <summary>StockQuantity: on create, opening stock in the default store; on update, a change is posted there as an adjustment.</summary>
public record UpsertProductRequest(
    [Required, MaxLength(50)] string Sku,
    [Required, MaxLength(200)] string Name,
    string? Category,
    [Range(0, 10_000_000)] decimal UnitPrice,
    [Range(0, int.MaxValue)] int StockQuantity,
    Guid? UomId = null);

// ---------- Orders ----------
public record OrderItemRequest([Required] Guid ProductId, [Range(1, 10_000)] int Quantity);
public record CreateOrderRequest([Required] Guid CustomerId, [Required, MinLength(1)] List<OrderItemRequest> Items);
public record OrderItemDto(Guid ProductId, int Quantity, decimal UnitPrice);
public record OrderDto(Guid Id, Guid CustomerId, string Status, decimal TotalAmount, DateTime OrderedAt,
    IReadOnlyList<OrderItemDto> Items);
public record UpdateOrderStatusRequest([Required] string Status);

// ---------- Dashboard ----------
public record DashboardStats(int Customers, int Products, int Orders, decimal Revenue, int LowStockProducts);

// ---------- Analytics ----------
/// <summary>Totals for one period. Revenue and average order value exclude cancelled orders.</summary>
public record AnalyticsTotals(decimal Revenue, int Orders, decimal AverageOrderValue, int NewCustomers);
/// <summary>One point of the revenue trend: a day (30/90-day ranges) or a month (12-month range).</summary>
public record AnalyticsPoint(DateOnly Date, decimal Revenue, int Orders);
public record AnalyticsStatus(string Status, int Orders, decimal Amount);
/// <summary>A ranked row. Count = units sold (products, categories) or orders (customers, cities).</summary>
public record AnalyticsRanked(string Name, decimal Revenue, int Count);
public record AnalyticsReport(
    int Days, string Bucket, DateTime From, DateTime To,
    AnalyticsTotals Current, AnalyticsTotals Previous,
    IReadOnlyList<AnalyticsPoint> Trend,
    IReadOnlyList<AnalyticsStatus> Statuses,
    IReadOnlyList<AnalyticsRanked> TopProducts,
    IReadOnlyList<AnalyticsRanked> TopCustomers,
    IReadOnlyList<AnalyticsRanked> Cities,
    IReadOnlyList<AnalyticsRanked> Categories);

// ---------- Inventory ----------
public record UomDto(Guid Id, string Code, string Name, bool IsActive);
public record ItemLookupDto(Guid Id, string Sku, string Name, string? Uom, int StockQuantity, decimal UnitPrice);
public record UpsertUomRequest([Required, MaxLength(10)] string Code, [Required, MaxLength(50)] string Name, bool IsActive = true);

public record StoreDto(Guid Id, string Code, string Name, string? Location, bool IsDefault, bool IsActive);
public record UpsertStoreRequest([Required, MaxLength(20)] string Code, [Required, MaxLength(100)] string Name,
    [MaxLength(200)] string? Location, bool IsActive = true, bool IsDefault = false);

public record StockEntryLineRequest([Required] Guid ProductId, [Range(1, 1_000_000)] int Quantity,
    [Range(0, 10_000_000)] decimal? Rate);
/// <summary>Receipt: TargetStoreId and a Rate per line. Issue: SourceStoreId. Transfer: both (different).</summary>
public record UpsertStockEntryRequest([Required] string Type, DateOnly? PostingDate, Guid? SourceStoreId, Guid? TargetStoreId,
    [MaxLength(500)] string? Remarks, [Required, MinLength(1)] List<StockEntryLineRequest> Lines);
public record StockEntryLineDto(Guid ProductId, string Sku, string ProductName, string? Uom, int Quantity, decimal Rate, decimal Amount);
public record StockEntryDto(Guid Id, string Number, string Type, string Status, DateOnly PostingDate,
    Guid? SourceStoreId, string? SourceStore, Guid? TargetStoreId, string? TargetStore, string? Remarks,
    decimal TotalValue, DateTime CreatedAt, DateTime? SubmittedAt, DateTime? CancelledAt, IReadOnlyList<StockEntryLineDto> Lines);
public record StockEntrySummaryDto(Guid Id, string Number, string Type, string Status, DateOnly PostingDate,
    string? SourceStore, string? TargetStore, int LineCount, decimal TotalValue, DateTime CreatedAt);

public record StockPositionRow(Guid ProductId, string Sku, string Name, string? Category, string? Uom,
    Guid StoreId, string Store, int Quantity, decimal ValuationRate, decimal StockValue);
public record StockPositionResult(IReadOnlyList<StockPositionRow> Items, int Page, int PageSize, long Total,
    long TotalQuantity, decimal TotalValue);

public record StockLedgerRow(long Id, DateOnly PostingDate, DateTime PostedAt, Guid ProductId, string Sku, string ProductName,
    Guid StoreId, string Store, string VoucherType, Guid? VoucherId, string VoucherNo, bool IsReversal,
    int QtyChange, int QtyAfter, decimal Rate, decimal ValuationRate, decimal ValueChange, decimal ValueAfter);
