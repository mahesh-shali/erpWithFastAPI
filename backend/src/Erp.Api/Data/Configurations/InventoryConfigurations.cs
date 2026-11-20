using Erp.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Api.Data.Configurations;

public sealed class UomConfiguration : IEntityTypeConfiguration<Uom>
{
    public void Configure(EntityTypeBuilder<Uom> e)
    {
        e.ToTable("uoms");
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Code).HasMaxLength(10);
        e.Property(x => x.Name).HasMaxLength(50);
        e.Property(x => x.IsActive).HasDefaultValue(true);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);
        e.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("ux_uoms_org_code");
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> e)
    {
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Code).HasMaxLength(20);
        e.Property(x => x.Name).HasMaxLength(100);
        e.Property(x => x.Location).HasMaxLength(200);
        e.Property(x => x.IsActive).HasDefaultValue(true);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);
        e.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("ux_stores_org_code");
        // Exactly one default store per organization (also stops two concurrent "create Main Store" calls).
        e.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("is_default").HasDatabaseName("ux_stores_org_default");
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class StockEntryConfiguration : IEntityTypeConfiguration<StockEntry>
{
    public void Configure(EntityTypeBuilder<StockEntry> e)
    {
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_stock_entries_type", In("type", StockEntryType.All));
            t.HasCheckConstraint("ck_stock_entries_status", In("status", DocStatus.All));
        });
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Number).HasMaxLength(20);
        e.Property(x => x.Type).HasMaxLength(20);
        e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(DocStatus.Draft);
        e.Property(x => x.Remarks).HasMaxLength(500);
        e.Property(x => x.TotalValue).HasPrecision(16, 2);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);

        e.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_stock_entries_org_number");
        e.HasIndex(x => new { x.OrganizationId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_stock_entries_org_created");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.SourceStore).WithMany().HasForeignKey(x => x.SourceStoreId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.TargetStore).WithMany().HasForeignKey(x => x.TargetStoreId).OnDelete(DeleteBehavior.Restrict);
        e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockEntryId).OnDelete(DeleteBehavior.Cascade);
    }

    internal static string In(string column, IEnumerable<string> values) =>
        $"{column} IN (" + string.Join(",", values.Select(v => $"'{v}'")) + ")";
}

public sealed class StockEntryLineConfiguration : IEntityTypeConfiguration<StockEntryLine>
{
    public void Configure(EntityTypeBuilder<StockEntryLine> e)
    {
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_stock_entry_lines_quantity", "quantity > 0");
            t.HasCheckConstraint("ck_stock_entry_lines_rate", "rate >= 0");
        });
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Rate).HasPrecision(14, 4);
        e.Property(x => x.Amount).HasPrecision(16, 2);
        e.HasIndex(x => x.StockEntryId).HasDatabaseName("ix_stock_entry_lines_entry");
        e.HasIndex(x => new { x.OrganizationId, x.ProductId }).HasDatabaseName("ix_stock_entry_lines_org_prod");
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> e)
    {
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_stock_balances_quantity", "quantity >= 0");   // no negative stock
            t.HasCheckConstraint("ck_stock_balances_value", "stock_value >= 0");
        });
        e.HasKey(x => new { x.OrganizationId, x.ProductId, x.StoreId });
        e.Property(x => x.ValuationRate).HasPrecision(14, 4);
        e.Property(x => x.StockValue).HasPrecision(16, 2);
        e.Property(x => x.UpdatedAt).HasDefaultValueSql(Sql.Now);
        e.HasIndex(x => new { x.OrganizationId, x.StoreId }).HasDatabaseName("ix_stock_balances_org_store");
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class StockLedgerEntryConfiguration : IEntityTypeConfiguration<StockLedgerEntry>
{
    public void Configure(EntityTypeBuilder<StockLedgerEntry> e)
    {
        e.ToTable(t => t.HasCheckConstraint("ck_stock_ledger_qty_after", "qty_after >= 0"));
        e.Property(x => x.Id).UseIdentityAlwaysColumn();
        e.Property(x => x.PostedAt).HasDefaultValueSql(Sql.Now);
        e.Property(x => x.Rate).HasPrecision(14, 4);
        e.Property(x => x.ValuationRate).HasPrecision(14, 4);
        e.Property(x => x.ValueChange).HasPrecision(16, 2);
        e.Property(x => x.ValueAfter).HasPrecision(16, 2);
        e.Property(x => x.VoucherType).HasMaxLength(30);
        e.Property(x => x.VoucherNo).HasMaxLength(40);

        // Stock ledger report: per product (and store), newest first
        e.HasIndex(x => new { x.OrganizationId, x.ProductId, x.StoreId, x.Id }).HasDatabaseName("ix_stock_ledger_org_prod_store");
        e.HasIndex(x => new { x.OrganizationId, x.PostingDate }).HasDatabaseName("ix_stock_ledger_org_date");
        e.HasIndex(x => new { x.OrganizationId, x.VoucherId }).HasDatabaseName("ix_stock_ledger_org_voucher");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DocumentSeriesConfiguration : IEntityTypeConfiguration<DocumentSeries>
{
    public void Configure(EntityTypeBuilder<DocumentSeries> e)
    {
        e.ToTable("document_series");
        e.HasKey(x => new { x.OrganizationId, x.Prefix });
        e.Property(x => x.Prefix).HasMaxLength(10);
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}
