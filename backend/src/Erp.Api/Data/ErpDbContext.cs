using System.Linq.Expressions;
using Erp.Api.Domain;
using Erp.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Data;

public class ErpDbContext(DbContextOptions<ErpDbContext> options, ITenantContext tenant) : DbContext(options)
{
    // Read through a property so EF re-evaluates the filter per context instance (works with pooling).
    public Guid CurrentOrgId => tenant.OrganizationId;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<TrustedDevice> TrustedDevices => Set<TrustedDevice>();
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<MenuModule> MenuModules => Set<MenuModule>();
    public DbSet<MenuGroup> MenuGroups => Set<MenuGroup>();

    /// <summary>Active pages with their group and module, in menu order (top-level pages first).</summary>
    public IQueryable<Page> MenuPages() =>
        Pages.AsNoTracking().Include(p => p.Group!).ThenInclude(g => g.Module)
            .Where(p => p.IsActive)
            .OrderBy(p => p.GroupId == null ? 0 : 1)
            .ThenBy(p => p.Group!.Module!.SortOrder).ThenBy(p => p.Group!.SortOrder).ThenBy(p => p.SortOrder);
    public DbSet<RolePagePermission> RolePagePermissions => Set<RolePagePermission>();
    public DbSet<UserPagePermission> UserPagePermissions => Set<UserPagePermission>();

    /// <summary>Effective rights of one user per page (SQL function; SuperAdmin and isRestricted rules applied in SQL).</summary>
    public IQueryable<EffectivePagePermission> EffectivePermissions(Guid userId) =>
        Set<EffectivePagePermission>().FromSqlInterpolated(
            $"SELECT page_id, page_key, is_visible, is_restricted, can_read, can_write, can_update, can_delete, source FROM effective_page_permissions({userId})");
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<ChatAuditLog> ChatAuditLogs => Set<ChatAuditLog>();

    // End-to-end encrypted chat (ciphertext only, see Domain/Chat.cs)
    public DbSet<ChatUserKey> ChatUserKeys => Set<ChatUserKey>();
    public DbSet<ChatConversation> ChatConversations => Set<ChatConversation>();
    public DbSet<ChatParticipant> ChatParticipants => Set<ChatParticipant>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatMessageKey> ChatMessageKeys => Set<ChatMessageKey>();

    // Inventory
    public DbSet<Uom> Uoms => Set<Uom>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<StockEntry> StockEntries => Set<StockEntry>();
    public DbSet<StockEntryLine> StockEntryLines => Set<StockEntryLine>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockLedgerEntry> StockLedger => Set<StockLedgerEntry>();
    public DbSet<DocumentSeries> DocumentSeries => Set<DocumentSeries>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // All table/column/index/constraint mapping lives in Data/Configurations/*.
        b.ApplyConfigurationsFromAssembly(typeof(ErpDbContext).Assembly);

        // Multi-tenant global query filter on every ITenantOwned entity
        foreach (var et in b.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)))
        {
            var param = Expression.Parameter(et.ClrType, "e");
            var body = Expression.Equal(
                Expression.Property(param, nameof(ITenantOwned.OrganizationId)),
                Expression.Property(Expression.Constant(this), nameof(CurrentOrgId)));
            b.Entity(et.ClrType).HasQueryFilter(Expression.Lambda(body, param));
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // Stamp tenant on inserts so callers can't forget (or spoof) it
        foreach (var e in ChangeTracker.Entries<ITenantOwned>().Where(e => e.State == EntityState.Added))
            e.Entity.OrganizationId = tenant.OrganizationId;
        return base.SaveChangesAsync(ct);
    }
}
