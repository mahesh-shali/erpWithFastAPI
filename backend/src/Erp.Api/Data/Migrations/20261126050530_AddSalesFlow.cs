using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Erp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "products",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "drawing_no",
                table: "products",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hsn_code",
                table: "products",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "products",
                type: "boolean",
                nullable: false,
                defaultValue: true); // hand-edited: existing items stay active (the model sets it explicitly on every save)

            migrationBuilder.AddColumn<string>(
                name: "item_type",
                table: "products",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Finished Good");

            migrationBuilder.AddColumn<int>(
                name: "lead_time_days",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "reorder_level",
                table: "products",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "standard_cost",
                table: "products",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "tax_rate",
                table: "products",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "address",
                table: "organizations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "email",
                table: "organizations",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "organizations",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "organizations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_id",
                table: "organizations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "address",
                table: "customers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_person",
                table: "customers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "payment_terms_days",
                table: "customers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "customers",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "postal_code",
                table: "customers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "customers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_id",
                table: "customers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "customer_pos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_po_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    po_date = table.Column<DateOnly>(type: "date", nullable: false),
                    delivery_date = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Draft"),
                    remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sub_total = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    confirmed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_pos", x => x.id);
                    table.CheckConstraint("ck_customer_pos_status", "status IN ('Draft','Confirmed','Completed','Cancelled')");
                    table.ForeignKey(
                        name: "fk_customer_pos_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_pos_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "customer_po_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_po_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    produced_qty = table.Column<int>(type: "integer", nullable: false),
                    invoiced_qty = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_po_lines", x => x.id);
                    table.CheckConstraint("ck_customer_po_lines_invoiced", "invoiced_qty >= 0 AND invoiced_qty <= quantity");
                    table.CheckConstraint("ck_customer_po_lines_produced", "produced_qty >= 0");
                    table.CheckConstraint("ck_customer_po_lines_quantity", "quantity > 0");
                    table.CheckConstraint("ck_customer_po_lines_rate", "rate >= 0 AND tax_rate >= 0 AND tax_rate <= 100");
                    table.ForeignKey(
                        name: "fk_customer_po_lines_customer_pos_customer_po_id",
                        column: x => x.customer_po_id,
                        principalTable: "customer_pos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_customer_po_lines_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_customer_po_lines_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_po_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invoice_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Draft"),
                    billing_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    billing_address = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true),
                    billing_tax_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    billing_state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    shipping_address = table.Column<string>(type: "character varying(800)", maxLength: 800, nullable: true),
                    remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sub_total = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    round_off = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    cost_of_goods = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_invoices", x => x.id);
                    table.CheckConstraint("ck_sales_invoices_due_date", "due_date >= invoice_date");
                    table.CheckConstraint("ck_sales_invoices_status", "status IN ('Draft','Submitted','Cancelled')");
                    table.ForeignKey(
                        name: "fk_sales_invoices_customer_pos_customer_po_id",
                        column: x => x.customer_po_id,
                        principalTable: "customer_pos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoices_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoices_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sales_invoices_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "work_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    customer_po_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_po_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    material_store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    posting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    other_cost = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    material_cost = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    unit_cost = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    total_cost = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Draft"),
                    remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_orders", x => x.id);
                    table.CheckConstraint("ck_work_orders_cost", "other_cost >= 0");
                    table.CheckConstraint("ck_work_orders_quantity", "quantity > 0");
                    table.CheckConstraint("ck_work_orders_status", "status IN ('Draft','Submitted','Cancelled')");
                    table.ForeignKey(
                        name: "fk_work_orders_customer_po_lines_customer_po_line_id",
                        column: x => x.customer_po_line_id,
                        principalTable: "customer_po_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_work_orders_customer_pos_customer_po_id",
                        column: x => x.customer_po_id,
                        principalTable: "customer_pos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_work_orders_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_work_orders_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_work_orders_stores_material_store_id",
                        column: x => x.material_store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_work_orders_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sales_invoice_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sales_invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_po_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    hsn_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    discount_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    cost_rate = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_invoice_lines", x => x.id);
                    table.CheckConstraint("ck_sales_invoice_lines_quantity", "quantity > 0");
                    table.CheckConstraint("ck_sales_invoice_lines_rates", "rate >= 0 AND discount_percent >= 0 AND discount_percent <= 100 AND tax_rate >= 0 AND tax_rate <= 100");
                    table.ForeignKey(
                        name: "fk_sales_invoice_lines_customer_po_lines_customer_po_line_id",
                        column: x => x.customer_po_line_id,
                        principalTable: "customer_po_lines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_lines_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sales_invoice_lines_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sales_invoice_lines_sales_invoices_sales_invoice_id",
                        column: x => x.sales_invoice_id,
                        principalTable: "sales_invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_order_materials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_order_materials", x => x.id);
                    table.CheckConstraint("ck_work_order_materials_quantity", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_work_order_materials_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_work_order_materials_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_work_order_materials_work_orders_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "work_orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 106,
                column: "name",
                value: "Inventory");

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "group_id", "name", "route", "sort_order" },
                values: new object[] { 106, "Items", "/master/items", 5 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 13,
                column: "is_implemented",
                value: true);

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 17,
                column: "is_implemented",
                value: true);

            migrationBuilder.InsertData(
                table: "pages",
                columns: new[] { "id", "group_id", "is_active", "is_implemented", "key", "name", "route", "sort_order" },
                values: new object[,]
                {
                    { 52, 201, true, true, "stock-details", "Stock Details", "/inventory/stock-details", 20 },
                    { 53, 301, true, true, "customer-pos", "Customer P.O.", "/sales/customer-po", 5 }
                });

            migrationBuilder.InsertData(
                table: "role_page_permissions",
                columns: new[] { "id", "can_delete", "can_read", "can_update", "can_write", "is_restricted", "is_visible", "organization_id", "page_id", "role_id", "updated_at", "updated_by_user_id" },
                values: new object[,]
                {
                    { 152, false, true, false, false, false, true, null, 52, 1, null, null },
                    { 153, true, true, true, true, false, true, null, 53, 1, null, null },
                    { 252, false, true, false, false, false, true, null, 52, 2, null, null },
                    { 253, false, true, true, true, false, true, null, 53, 2, null, null },
                    { 452, false, true, false, false, false, true, null, 52, 4, null, null },
                    { 453, false, true, true, true, false, true, null, 53, 4, null, null }
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_item_type",
                table: "products",
                sql: "item_type IN ('Finished Good','Semi-Finished','Raw Material','Consumable')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_reorder_level",
                table: "products",
                sql: "reorder_level >= 0 AND lead_time_days >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_standard_cost",
                table: "products",
                sql: "standard_cost >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_products_tax_rate",
                table: "products",
                sql: "tax_rate >= 0 AND tax_rate <= 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customers_payment_terms",
                table: "customers",
                sql: "payment_terms_days >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_customer_po_lines_org_prod",
                table: "customer_po_lines",
                columns: new[] { "organization_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_po_lines_po",
                table: "customer_po_lines",
                column: "customer_po_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_po_lines_product_id",
                table: "customer_po_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_pos_customer_id",
                table: "customer_pos",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_pos_org_created",
                table: "customer_pos",
                columns: new[] { "organization_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_customer_pos_org_customer",
                table: "customer_pos",
                columns: new[] { "organization_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ux_customer_pos_org_number",
                table: "customer_pos",
                columns: new[] { "organization_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_invoice",
                table: "sales_invoice_lines",
                column: "sales_invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_org_prod",
                table: "sales_invoice_lines",
                columns: new[] { "organization_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_po_line",
                table: "sales_invoice_lines",
                column: "customer_po_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoice_lines_product_id",
                table: "sales_invoice_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_customer_id",
                table: "sales_invoices",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_org_created",
                table: "sales_invoices",
                columns: new[] { "organization_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_org_customer",
                table: "sales_invoices",
                columns: new[] { "organization_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_po",
                table: "sales_invoices",
                column: "customer_po_id");

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_store_id",
                table: "sales_invoices",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "ux_sales_invoices_org_number",
                table: "sales_invoices",
                columns: new[] { "organization_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_work_order_materials_org_prod",
                table: "work_order_materials",
                columns: new[] { "organization_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_work_order_materials_product_id",
                table: "work_order_materials",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_order_materials_wo",
                table: "work_order_materials",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_material_store_id",
                table: "work_orders",
                column: "material_store_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_org_created",
                table: "work_orders",
                columns: new[] { "organization_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_po",
                table: "work_orders",
                column: "customer_po_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_po_line",
                table: "work_orders",
                column: "customer_po_line_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_product_id",
                table: "work_orders",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_orders_store_id",
                table: "work_orders",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "ux_work_orders_org_number",
                table: "work_orders",
                columns: new[] { "organization_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sales_invoice_lines");

            migrationBuilder.DropTable(
                name: "work_order_materials");

            migrationBuilder.DropTable(
                name: "sales_invoices");

            migrationBuilder.DropTable(
                name: "work_orders");

            migrationBuilder.DropTable(
                name: "customer_po_lines");

            migrationBuilder.DropTable(
                name: "customer_pos");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_item_type",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_reorder_level",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_standard_cost",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_products_tax_rate",
                table: "products");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customers_payment_terms",
                table: "customers");

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 152);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 153);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 252);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 253);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 452);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 453);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 52);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 53);

            migrationBuilder.DropColumn(
                name: "description",
                table: "products");

            migrationBuilder.DropColumn(
                name: "drawing_no",
                table: "products");

            migrationBuilder.DropColumn(
                name: "hsn_code",
                table: "products");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "products");

            migrationBuilder.DropColumn(
                name: "item_type",
                table: "products");

            migrationBuilder.DropColumn(
                name: "lead_time_days",
                table: "products");

            migrationBuilder.DropColumn(
                name: "reorder_level",
                table: "products");

            migrationBuilder.DropColumn(
                name: "standard_cost",
                table: "products");

            migrationBuilder.DropColumn(
                name: "tax_rate",
                table: "products");

            migrationBuilder.DropColumn(
                name: "address",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "email",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "state",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "tax_id",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "address",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "contact_person",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "payment_terms_days",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "postal_code",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "state",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "tax_id",
                table: "customers");

            migrationBuilder.UpdateData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 106,
                column: "name",
                value: "Inventory Setup");

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "group_id", "name", "route", "sort_order" },
                values: new object[] { 104, "Products", "/master/products", 10 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 13,
                column: "is_implemented",
                value: false);

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 17,
                column: "is_implemented",
                value: false);
        }
    }
}
