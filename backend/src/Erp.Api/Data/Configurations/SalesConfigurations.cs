using Erp.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Erp.Api.Data.Configurations.StockEntryConfiguration;

namespace Erp.Api.Data.Configurations;

public sealed class CustomerPoConfiguration : IEntityTypeConfiguration<CustomerPo>
{
    public void Configure(EntityTypeBuilder<CustomerPo> e)
    {
        e.ToTable("customer_pos", t => t.HasCheckConstraint("ck_customer_pos_status", In("status", CustomerPoStatus.All)));
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Number).HasMaxLength(20);
        e.Property(x => x.CustomerPoNo).HasMaxLength(50);
        e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(CustomerPoStatus.Draft);
        e.Property(x => x.Remarks).HasMaxLength(500);
        e.Property(x => x.SubTotal).HasPrecision(16, 2);
        e.Property(x => x.TaxTotal).HasPrecision(16, 2);
        e.Property(x => x.GrandTotal).HasPrecision(16, 2);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);

        e.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_customer_pos_org_number");
        e.HasIndex(x => new { x.OrganizationId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_customer_pos_org_created");
        e.HasIndex(x => new { x.OrganizationId, x.CustomerId }).HasDatabaseName("ix_customer_pos_org_customer");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.CustomerPoId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CustomerPoLineConfiguration : IEntityTypeConfiguration<CustomerPoLine>
{
    public void Configure(EntityTypeBuilder<CustomerPoLine> e)
    {
        e.ToTable("customer_po_lines", t =>
        {
            t.HasCheckConstraint("ck_customer_po_lines_quantity", "quantity > 0");
            t.HasCheckConstraint("ck_customer_po_lines_rate", "rate >= 0 AND tax_rate >= 0 AND tax_rate <= 100");
            t.HasCheckConstraint("ck_customer_po_lines_produced", "produced_qty >= 0");
            t.HasCheckConstraint("ck_customer_po_lines_invoiced", "invoiced_qty >= 0 AND invoiced_qty <= quantity");
        });
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Rate).HasPrecision(14, 2);
        e.Property(x => x.TaxRate).HasPrecision(5, 2);
        e.Property(x => x.Amount).HasPrecision(16, 2);
        e.Property(x => x.TaxAmount).HasPrecision(16, 2);
        e.HasIndex(x => x.CustomerPoId).HasDatabaseName("ix_customer_po_lines_po");
        e.HasIndex(x => new { x.OrganizationId, x.ProductId }).HasDatabaseName("ix_customer_po_lines_org_prod");
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public void Configure(EntityTypeBuilder<WorkOrder> e)
    {
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_work_orders_status", In("status", DocStatus.All));
            t.HasCheckConstraint("ck_work_orders_quantity", "quantity > 0");
            t.HasCheckConstraint("ck_work_orders_cost", "other_cost >= 0");
        });
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Number).HasMaxLength(20);
        e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(DocStatus.Draft);
        e.Property(x => x.Remarks).HasMaxLength(500);
        e.Property(x => x.OtherCost).HasPrecision(16, 2);
        e.Property(x => x.MaterialCost).HasPrecision(16, 2);
        e.Property(x => x.UnitCost).HasPrecision(14, 4);
        e.Property(x => x.TotalCost).HasPrecision(16, 2);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);

        e.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_work_orders_org_number");
        e.HasIndex(x => new { x.OrganizationId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_work_orders_org_created");
        e.HasIndex(x => x.CustomerPoLineId).HasDatabaseName("ix_work_orders_po_line");
        e.HasIndex(x => x.CustomerPoId).HasDatabaseName("ix_work_orders_po");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.CustomerPo).WithMany().HasForeignKey(x => x.CustomerPoId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<CustomerPoLine>().WithMany().HasForeignKey(x => x.CustomerPoLineId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.MaterialStore).WithMany().HasForeignKey(x => x.MaterialStoreId).OnDelete(DeleteBehavior.Restrict);
        e.HasMany(x => x.Materials).WithOne().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkOrderMaterialConfiguration : IEntityTypeConfiguration<WorkOrderMaterial>
{
    public void Configure(EntityTypeBuilder<WorkOrderMaterial> e)
    {
        e.ToTable(t => t.HasCheckConstraint("ck_work_order_materials_quantity", "quantity > 0"));
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Rate).HasPrecision(14, 4);
        e.Property(x => x.Amount).HasPrecision(16, 2);
        e.HasIndex(x => x.WorkOrderId).HasDatabaseName("ix_work_order_materials_wo");
        e.HasIndex(x => new { x.OrganizationId, x.ProductId }).HasDatabaseName("ix_work_order_materials_org_prod");
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class SalesInvoiceConfiguration : IEntityTypeConfiguration<SalesInvoice>
{
    public void Configure(EntityTypeBuilder<SalesInvoice> e)
    {
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_sales_invoices_status", In("status", DocStatus.All));
            t.HasCheckConstraint("ck_sales_invoices_due_date", "due_date >= invoice_date");
        });
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.Number).HasMaxLength(20);
        e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(DocStatus.Draft);
        e.Property(x => x.BillingName).HasMaxLength(200);
        e.Property(x => x.BillingAddress).HasMaxLength(800);
        e.Property(x => x.BillingTaxId).HasMaxLength(20);
        e.Property(x => x.BillingState).HasMaxLength(100);
        e.Property(x => x.ShippingAddress).HasMaxLength(800);
        e.Property(x => x.Remarks).HasMaxLength(500);
        foreach (var p in new[] { nameof(SalesInvoice.SubTotal), nameof(SalesInvoice.DiscountTotal), nameof(SalesInvoice.TaxTotal),
                     nameof(SalesInvoice.RoundOff), nameof(SalesInvoice.GrandTotal), nameof(SalesInvoice.CostOfGoods) })
            e.Property(p).HasPrecision(16, 2);
        e.Property(x => x.CreatedAt).HasDefaultValueSql(Sql.Now);

        e.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique().HasDatabaseName("ux_sales_invoices_org_number");
        e.HasIndex(x => new { x.OrganizationId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_sales_invoices_org_created");
        e.HasIndex(x => new { x.OrganizationId, x.CustomerId }).HasDatabaseName("ix_sales_invoices_org_customer");
        e.HasIndex(x => x.CustomerPoId).HasDatabaseName("ix_sales_invoices_po");

        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.CustomerPo).WithMany().HasForeignKey(x => x.CustomerPoId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SalesInvoiceId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SalesInvoiceLineConfiguration : IEntityTypeConfiguration<SalesInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SalesInvoiceLine> e)
    {
        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_sales_invoice_lines_quantity", "quantity > 0");
            t.HasCheckConstraint("ck_sales_invoice_lines_rates",
                "rate >= 0 AND discount_percent >= 0 AND discount_percent <= 100 AND tax_rate >= 0 AND tax_rate <= 100");
        });
        e.Property(x => x.Id).HasDefaultValueSql(Sql.NewUuid);
        e.Property(x => x.HsnCode).HasMaxLength(20);
        e.Property(x => x.Rate).HasPrecision(14, 2);
        e.Property(x => x.DiscountPercent).HasPrecision(5, 2);
        e.Property(x => x.Amount).HasPrecision(16, 2);
        e.Property(x => x.TaxRate).HasPrecision(5, 2);
        e.Property(x => x.TaxAmount).HasPrecision(16, 2);
        e.Property(x => x.Total).HasPrecision(16, 2);
        e.Property(x => x.CostRate).HasPrecision(14, 4);
        e.HasIndex(x => x.SalesInvoiceId).HasDatabaseName("ix_sales_invoice_lines_invoice");
        e.HasIndex(x => new { x.OrganizationId, x.ProductId }).HasDatabaseName("ix_sales_invoice_lines_org_prod");
        e.HasIndex(x => x.CustomerPoLineId).HasDatabaseName("ix_sales_invoice_lines_po_line");
        e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<CustomerPoLine>().WithMany().HasForeignKey(x => x.CustomerPoLineId).OnDelete(DeleteBehavior.Restrict);
    }
}
