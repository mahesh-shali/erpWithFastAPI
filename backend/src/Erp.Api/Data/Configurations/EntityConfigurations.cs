using Erp.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Api.Data.Configurations;

// Code-first schema. Table/column names come from the snake_case naming convention
// (UseSnakeCaseNamingConvention), so `OrderItem.UnitPrice` -> `order_items.unit_price`.
// Change these classes, then run `dotnet ef migrations add <Name>` to produce a migration.

internal static class Sql
{
    public const string NewUuid = "gen_random_uuid()"; // built into PostgreSQL 13+
    public const string Now = "now()";
}

public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> e)
    {
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Name).IsRequired();
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> e)
    {
        e.Property(x => x.Id).ValueGeneratedNever(); // stable, well-known ids (see Roles)
        e.Property(x => x.Name).HasMaxLength(50);
        e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_roles_name");

        // Reference data lives in code: changing this produces a migration with the INSERT/UPDATE.
        e.HasData(
            new Role { Id = Roles.SuperAdminId, Name = Roles.SuperAdmin, Level = Roles.SuperAdminLevel,
                       Description = "Organization owner: every right on every page; manages admins, users and all rights" },
            new Role { Id = Roles.AdminId, Name = Roles.Admin, Level = Roles.AdminLevel,
                       Description = "Manages users and their rights (never beyond the admin's own rights)" },
            new Role { Id = Roles.UserId, Name = Roles.User, Level = Roles.UserLevel,
                       Description = "Standard access as granted by page rights" });
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> e)
    {
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.RoleId).HasDefaultValue(Roles.UserId);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);

        e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("ux_users_email");
        e.HasIndex(x => x.OrganizationId).HasDatabaseName("ix_users_org");
        e.HasIndex(x => x.RoleId).HasDatabaseName("ix_users_role");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> e)
    {
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.RefreshTokenHash).HasMaxLength(64);          // SHA-256 hex
        e.Property(x => x.PreviousRefreshTokenHash).HasMaxLength(64);
        e.Property(x => x.RevokedReason).HasMaxLength(100);
        e.Property(x => x.IpAddress).HasMaxLength(64);
        e.Property(x => x.UserAgent).HasMaxLength(512);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);
        e.Property(x => x.LastSeenAt).HasDefaultValueSql(Sql.Now);

        e.HasIndex(x => x.RefreshTokenHash).IsUnique().HasDatabaseName("ux_user_sessions_refresh_hash");
        e.HasIndex(x => x.PreviousRefreshTokenHash).HasDatabaseName("ix_user_sessions_prev_hash");
        e.HasIndex(x => x.UserId).HasDatabaseName("ix_user_sessions_user");
        e.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_user_sessions_expires"); // cleanup job

        e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> e)
    {
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);

        e.HasIndex(x => x.OrganizationId).HasDatabaseName("ix_customers_org");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> e)
    {
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_products_unit_price", "unit_price >= 0");
            t.HasCheckConstraint("ck_products_stock_quantity", "stock_quantity >= 0");
        });
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.UnitPrice).HasPrecision(12, 2);
        e.Property(x => x.StockQuantity).HasDefaultValue(0);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);

        e.HasIndex(x => new { x.OrganizationId, x.Sku }).IsUnique().HasDatabaseName("ux_products_org_sku");
        e.HasIndex(x => new { x.OrganizationId, x.Category }).HasDatabaseName("ix_products_org_cat");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> e)
    {
        e.ToTable(t => t.HasCheckConstraint("ck_orders_status",
            "status IN (" + string.Join(",", OrderStatus.All.Select(s => $"'{s}'")) + ")"));
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Status).HasDefaultValue(OrderStatus.Pending);
        e.Property(x => x.TotalAmount).HasPrecision(14, 2).HasDefaultValue(0m);
        e.Property(x => x.OrderedAt).HasDefaultValueSql(Sql.Now);

        // (organization_id, ordered_at DESC) serves "latest orders for tenant" + keyset pagination
        e.HasIndex(x => new { x.OrganizationId, x.OrderedAt }).IsDescending(false, true)
            .HasDatabaseName("ix_orders_org_date");
        e.HasIndex(x => x.CustomerId).HasDatabaseName("ix_orders_customer");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> e)
    {
        e.ToTable(t => t.HasCheckConstraint("ck_order_items_quantity", "quantity > 0"));
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.UnitPrice).HasPrecision(12, 2);

        e.HasIndex(x => x.OrderId).HasDatabaseName("ix_order_items_order");
        e.HasIndex(x => new { x.OrganizationId, x.ProductId }).HasDatabaseName("ix_order_items_org_prod");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ChatAuditLogConfiguration : IEntityTypeConfiguration<ChatAuditLog>
{
    public void Configure(EntityTypeBuilder<ChatAuditLog> e)
    {
        e.ToTable("chat_audit_log");
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);
        e.HasIndex(x => new { x.OrganizationId, x.CreatedAt }).IsDescending(false, true)
            .HasDatabaseName("ix_chat_audit_org_date");
    }
}

public sealed class MenuModuleConfiguration : IEntityTypeConfiguration<MenuModule>
{
    public void Configure(EntityTypeBuilder<MenuModule> e)
    {
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Key).HasMaxLength(64);
        e.Property(x => x.Name).HasMaxLength(100);
        e.Property(x => x.Icon).HasMaxLength(32);
        e.HasIndex(x => x.Key).IsUnique().HasDatabaseName("ux_menu_modules_key");
        e.HasData(DefaultPermissions.Modules);
    }
}

public sealed class MenuGroupConfiguration : IEntityTypeConfiguration<MenuGroup>
{
    public void Configure(EntityTypeBuilder<MenuGroup> e)
    {
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Key).HasMaxLength(64);
        e.Property(x => x.Name).HasMaxLength(100);
        e.HasIndex(x => x.Key).IsUnique().HasDatabaseName("ux_menu_groups_key");
        e.HasOne(x => x.Module).WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
        e.HasData(DefaultPermissions.Groups);
    }
}

public sealed class PageConfiguration : IEntityTypeConfiguration<Page>
{
    public void Configure(EntityTypeBuilder<Page> e)
    {
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Key).HasMaxLength(64);
        e.Property(x => x.Name).HasMaxLength(100);
        e.Property(x => x.Route).HasMaxLength(200);
        e.HasIndex(x => x.Key).IsUnique().HasDatabaseName("ux_pages_key");
        e.HasIndex(x => x.Route).IsUnique().HasDatabaseName("ux_pages_route");
        e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Restrict);
        e.HasData(DefaultPermissions.Pages);
    }
}

public sealed class RolePagePermissionConfiguration : IEntityTypeConfiguration<RolePagePermission>
{
    public void Configure(EntityTypeBuilder<RolePagePermission> e)
    {
        // Seeded rows use explicit ids < 10000; identity values for new rows start above them.
        e.Property(x => x.Id).UseIdentityByDefaultColumn().HasIdentityOptions(startValue: 10000);
        // One row per (organization, role, page); NULL organization = system default (NULLs not distinct).
        e.HasIndex(x => new { x.OrganizationId, x.RoleId, x.PageId }).IsUnique().AreNullsDistinct(false)
            .HasDatabaseName("ux_role_page_permissions");
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Page>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
        e.HasData(DefaultPermissions.SystemRoleDefaults);
    }
}

public sealed class UserPagePermissionConfiguration : IEntityTypeConfiguration<UserPagePermission>
{
    public void Configure(EntityTypeBuilder<UserPagePermission> e)
    {
        e.HasKey(x => new { x.UserId, x.PageId });
        e.Property(x => x.UpdatedAt).HasDefaultValueSql(Sql.Now);
        e.HasIndex(x => x.OrganizationId).HasDatabaseName("ix_user_page_permissions_org");
        e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Page>().WithMany().HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Result type of the SQL function effective_page_permissions(); not a table.</summary>
public sealed class EffectivePagePermissionConfiguration : IEntityTypeConfiguration<EffectivePagePermission>
{
    public void Configure(EntityTypeBuilder<EffectivePagePermission> e)
    {
        e.HasNoKey().ToTable((string?)null); // no table: only materialised via FromSql
    }
}
