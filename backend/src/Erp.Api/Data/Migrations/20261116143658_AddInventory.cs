using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Erp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "uom_id",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "document_series",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prefix = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_series", x => new { x.organization_id, x.prefix });
                    table.ForeignKey(
                        name: "fk_document_series_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stores", x => x.id);
                    table.ForeignKey(
                        name: "fk_stores_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "uoms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_uoms", x => x.id);
                    table.ForeignKey(
                        name: "fk_uoms_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stock_balances",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    valuation_rate = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    stock_value = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_balances", x => new { x.organization_id, x.product_id, x.store_id });
                    table.CheckConstraint("ck_stock_balances_quantity", "quantity >= 0");
                    table.CheckConstraint("ck_stock_balances_value", "stock_value >= 0");
                    table.ForeignKey(
                        name: "fk_stock_balances_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_stock_balances_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_stock_balances_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Draft"),
                    posting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    source_store_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_store_id = table.Column<Guid>(type: "uuid", nullable: true),
                    remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    total_value = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_entries", x => x.id);
                    table.CheckConstraint("ck_stock_entries_status", "status IN ('Draft','Submitted','Cancelled')");
                    table.CheckConstraint("ck_stock_entries_type", "type IN ('Receipt','Issue','Transfer')");
                    table.ForeignKey(
                        name: "fk_stock_entries_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_stock_entries_stores_source_store_id",
                        column: x => x.source_store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_entries_stores_target_store_id",
                        column: x => x.target_store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_ledger",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    store_id = table.Column<Guid>(type: "uuid", nullable: false),
                    posting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    posted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    qty_change = table.Column<int>(type: "integer", nullable: false),
                    qty_after = table.Column<int>(type: "integer", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    valuation_rate = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    value_change = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    value_after = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false),
                    voucher_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    voucher_id = table.Column<Guid>(type: "uuid", nullable: true),
                    voucher_no = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_reversal = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_ledger", x => x.id);
                    table.CheckConstraint("ck_stock_ledger_qty_after", "qty_after >= 0");
                    table.ForeignKey(
                        name: "fk_stock_ledger_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_stock_ledger_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_stock_ledger_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_entry_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stock_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(16,2)", precision: 16, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_entry_lines", x => x.id);
                    table.CheckConstraint("ck_stock_entry_lines_quantity", "quantity > 0");
                    table.CheckConstraint("ck_stock_entry_lines_rate", "rate >= 0");
                    table.ForeignKey(
                        name: "fk_stock_entry_lines_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_stock_entry_lines_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_entry_lines_stock_entries_stock_entry_id",
                        column: x => x.stock_entry_id,
                        principalTable: "stock_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 202,
                column: "name",
                value: "Stock Movement");

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 11,
                column: "is_implemented",
                value: true);

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 12,
                columns: new[] { "is_implemented", "name" },
                values: new object[] { true, "Stock Entries" });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 25,
                columns: new[] { "is_implemented", "name" },
                values: new object[] { true, "Stock Ledger" });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 26,
                column: "is_implemented",
                value: true);

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 27,
                column: "is_implemented",
                value: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_uom_id",
                table: "products",
                column: "uom_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_org_store",
                table: "stock_balances",
                columns: new[] { "organization_id", "store_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_product_id",
                table: "stock_balances",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_balances_store_id",
                table: "stock_balances",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_entries_org_created",
                table: "stock_entries",
                columns: new[] { "organization_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_stock_entries_source_store_id",
                table: "stock_entries",
                column: "source_store_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_entries_target_store_id",
                table: "stock_entries",
                column: "target_store_id");

            migrationBuilder.CreateIndex(
                name: "ux_stock_entries_org_number",
                table: "stock_entries",
                columns: new[] { "organization_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stock_entry_lines_entry",
                table: "stock_entry_lines",
                column: "stock_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_entry_lines_org_prod",
                table: "stock_entry_lines",
                columns: new[] { "organization_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_entry_lines_product_id",
                table: "stock_entry_lines",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_org_date",
                table: "stock_ledger",
                columns: new[] { "organization_id", "posting_date" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_org_prod_store",
                table: "stock_ledger",
                columns: new[] { "organization_id", "product_id", "store_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_org_voucher",
                table: "stock_ledger",
                columns: new[] { "organization_id", "voucher_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_product_id",
                table: "stock_ledger",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_stock_ledger_store_id",
                table: "stock_ledger",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "ux_stores_org_code",
                table: "stores",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_stores_org_default",
                table: "stores",
                column: "organization_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ux_uoms_org_code",
                table: "uoms",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_products_uoms_uom_id",
                table: "products",
                column: "uom_id",
                principalTable: "uoms",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_products_uoms_uom_id",
                table: "products");

            migrationBuilder.DropTable(
                name: "document_series");

            migrationBuilder.DropTable(
                name: "stock_balances");

            migrationBuilder.DropTable(
                name: "stock_entry_lines");

            migrationBuilder.DropTable(
                name: "stock_ledger");

            migrationBuilder.DropTable(
                name: "uoms");

            migrationBuilder.DropTable(
                name: "stock_entries");

            migrationBuilder.DropTable(
                name: "stores");

            migrationBuilder.DropIndex(
                name: "ix_products_uom_id",
                table: "products");

            migrationBuilder.DropColumn(
                name: "uom_id",
                table: "products");

            migrationBuilder.UpdateData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 202,
                column: "name",
                value: "Stock Addition");

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 11,
                column: "is_implemented",
                value: false);

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 12,
                columns: new[] { "is_implemented", "name" },
                values: new object[] { false, "Stock Addition" });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 25,
                columns: new[] { "is_implemented", "name" },
                values: new object[] { false, "Stock Report" });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 26,
                column: "is_implemented",
                value: false);

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 27,
                column: "is_implemented",
                value: false);
        }
    }
}
