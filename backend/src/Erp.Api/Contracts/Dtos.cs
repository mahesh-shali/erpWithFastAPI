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
public record ProductDto(Guid Id, string Sku, string Name, string? Category, decimal UnitPrice, int StockQuantity);
public record UpsertProductRequest(
    [Required, MaxLength(50)] string Sku,
    [Required, MaxLength(200)] string Name,
    string? Category,
    [Range(0, 10_000_000)] decimal UnitPrice,
    [Range(0, int.MaxValue)] int StockQuantity);

// ---------- Orders ----------
public record OrderItemRequest([Required] Guid ProductId, [Range(1, 10_000)] int Quantity);
public record CreateOrderRequest([Required] Guid CustomerId, [Required, MinLength(1)] List<OrderItemRequest> Items);
public record OrderItemDto(Guid ProductId, int Quantity, decimal UnitPrice);
public record OrderDto(Guid Id, Guid CustomerId, string Status, decimal TotalAmount, DateTime OrderedAt,
    IReadOnlyList<OrderItemDto> Items);
public record UpdateOrderStatusRequest([Required] string Status);

// ---------- Dashboard ----------
public record DashboardStats(int Customers, int Products, int Orders, decimal Revenue, int LowStockProducts);
