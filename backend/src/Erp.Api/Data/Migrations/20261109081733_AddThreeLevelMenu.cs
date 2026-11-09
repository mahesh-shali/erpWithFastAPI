using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Erp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddThreeLevelMenu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "module",
                table: "pages");

            migrationBuilder.AddColumn<int>(
                name: "group_id",
                table: "pages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_implemented",
                table: "pages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "menu_modules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    icon = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_menu_modules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "menu_groups",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    module_id = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_menu_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_menu_groups_menu_modules_module_id",
                        column: x => x.module_id,
                        principalTable: "menu_modules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "menu_modules",
                columns: new[] { "id", "icon", "key", "name", "sort_order" },
                values: new object[,]
                {
                    { 1, "settings", "master", "Master", 10 },
                    { 2, "box", "inventory", "Inventory", 20 },
                    { 3, "cart", "sales", "Sales", 30 },
                    { 4, "truck", "purchase", "Purchase", 40 },
                    { 5, "factory", "production", "Production", 50 },
                    { 6, "wallet", "cash-flow", "Cash Flow", 60 },
                    { 7, "check", "inspection", "Inspection", 70 },
                    { 8, "people", "human-resource", "Human Resource", 80 },
                    { 9, "chart", "reports", "Reports", 90 }
                });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "group_id", "is_implemented" },
                values: new object[] { null, true });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "group_id", "is_implemented", "route", "sort_order" },
                values: new object[] { 102, true, "/master/customers", 10 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "group_id", "is_implemented", "route", "sort_order" },
                values: new object[] { 104, true, "/master/products", 10 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "group_id", "is_implemented", "name", "route", "sort_order" },
                values: new object[] { 301, true, "Sales Orders", "/sales/orders", 10 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 5,
                columns: new[] { "group_id", "is_implemented", "sort_order" },
                values: new object[] { null, true, 20 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 6,
                columns: new[] { "group_id", "is_implemented", "sort_order" },
                values: new object[] { 101, true, 10 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 7,
                columns: new[] { "group_id", "is_implemented", "sort_order" },
                values: new object[] { 101, true, 20 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 8,
                columns: new[] { "group_id", "is_implemented", "sort_order" },
                values: new object[] { 101, true, 30 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 9,
                columns: new[] { "group_id", "is_implemented", "sort_order" },
                values: new object[] { 105, true, 10 });

            migrationBuilder.InsertData(
                table: "menu_groups",
                columns: new[] { "id", "key", "module_id", "name", "sort_order" },
                values: new object[,]
                {
                    { 101, "master-users", 1, "Users", 10 },
                    { 102, "master-customers", 1, "Customers", 20 },
                    { 103, "master-vendors", 1, "Vendors", 30 },
                    { 104, "master-products", 1, "Products", 40 },
                    { 105, "master-settings", 1, "Settings", 50 },
                    { 201, "inventory-stock-position", 2, "Stock Position", 10 },
                    { 202, "inventory-stock-addition", 2, "Stock Addition", 20 },
                    { 301, "sales-orders", 3, "Orders", 10 },
                    { 302, "sales-invoices", 3, "Invoices", 20 },
                    { 401, "purchase-orders", 4, "Orders", 10 },
                    { 402, "purchase-bills", 4, "Bills", 20 },
                    { 501, "production-planning", 5, "Planning", 10 },
                    { 502, "production-work-orders", 5, "Work Orders", 20 },
                    { 601, "cash-flow-receipts", 6, "Receipts", 10 },
                    { 602, "cash-flow-payments", 6, "Payments", 20 },
                    { 701, "inspection-incoming", 7, "Incoming", 10 },
                    { 702, "inspection-quality", 7, "Quality", 20 },
                    { 801, "hr-employees", 8, "Employees", 10 },
                    { 802, "hr-attendance", 8, "Attendance", 20 },
                    { 901, "reports-sales", 9, "Sales", 10 },
                    { 902, "reports-inventory", 9, "Inventory", 20 }
                });

            migrationBuilder.InsertData(
                table: "pages",
                columns: new[] { "id", "group_id", "is_active", "is_implemented", "key", "name", "route", "sort_order" },
                values: new object[,]
                {
                    { 10, 103, true, false, "vendors", "Vendors", "/master/vendors", 10 },
                    { 11, 201, true, false, "stock-position", "Stock Position", "/inventory/stock-position", 10 },
                    { 12, 202, true, false, "stock-addition", "Stock Addition", "/inventory/stock-addition", 10 },
                    { 13, 302, true, false, "sales-invoices", "Sales Invoices", "/sales/invoices", 10 },
                    { 14, 401, true, false, "purchase-orders", "Purchase Orders", "/purchase/orders", 10 },
                    { 15, 402, true, false, "purchase-bills", "Purchase Bills", "/purchase/bills", 10 },
                    { 16, 501, true, false, "production-plan", "Production Plan", "/production/plan", 10 },
                    { 17, 502, true, false, "work-orders", "Work Orders", "/production/work-orders", 10 },
                    { 18, 601, true, false, "receipts", "Receipts", "/cash-flow/receipts", 10 },
                    { 19, 602, true, false, "payments", "Payments", "/cash-flow/payments", 10 },
                    { 20, 701, true, false, "incoming-inspection", "Incoming Inspection", "/inspection/incoming", 10 },
                    { 21, 702, true, false, "quality-checks", "Quality Checks", "/inspection/quality", 10 },
                    { 22, 801, true, false, "employees", "Employees", "/hr/employees", 10 },
                    { 23, 802, true, false, "attendance", "Attendance", "/hr/attendance", 10 },
                    { 24, 901, true, false, "sales-report", "Sales Report", "/reports/sales", 10 },
                    { 25, 902, true, false, "stock-report", "Stock Report", "/reports/stock", 10 }
                });

            migrationBuilder.InsertData(
                table: "role_page_permissions",
                columns: new[] { "id", "can_delete", "can_read", "can_update", "can_write", "is_restricted", "is_visible", "organization_id", "page_id", "role_id", "updated_at", "updated_by_user_id" },
                values: new object[,]
                {
                    { 110, true, true, true, true, false, true, null, 10, 1, null, null },
                    { 111, false, true, false, false, false, true, null, 11, 1, null, null },
                    { 112, true, true, true, true, false, true, null, 12, 1, null, null },
                    { 113, true, true, true, true, false, true, null, 13, 1, null, null },
                    { 114, true, true, true, true, false, true, null, 14, 1, null, null },
                    { 115, true, true, true, true, false, true, null, 15, 1, null, null },
                    { 116, true, true, true, true, false, true, null, 16, 1, null, null },
                    { 117, true, true, true, true, false, true, null, 17, 1, null, null },
                    { 118, true, true, true, true, false, true, null, 18, 1, null, null },
                    { 119, true, true, true, true, false, true, null, 19, 1, null, null },
                    { 120, true, true, true, true, false, true, null, 20, 1, null, null },
                    { 121, true, true, true, true, false, true, null, 21, 1, null, null },
                    { 122, true, true, true, true, false, true, null, 22, 1, null, null },
                    { 123, true, true, true, true, false, true, null, 23, 1, null, null },
                    { 124, false, true, false, false, false, true, null, 24, 1, null, null },
                    { 125, false, true, false, false, false, true, null, 25, 1, null, null },
                    { 210, false, false, false, false, false, false, null, 10, 2, null, null },
                    { 211, false, true, false, false, false, true, null, 11, 2, null, null },
                    { 212, false, false, false, false, false, false, null, 12, 2, null, null },
                    { 213, false, false, false, false, false, false, null, 13, 2, null, null },
                    { 214, false, false, false, false, false, false, null, 14, 2, null, null },
                    { 215, false, false, false, false, false, false, null, 15, 2, null, null },
                    { 216, false, false, false, false, false, false, null, 16, 2, null, null },
                    { 217, false, false, false, false, false, false, null, 17, 2, null, null },
                    { 218, false, false, false, false, false, false, null, 18, 2, null, null },
                    { 219, false, false, false, false, false, false, null, 19, 2, null, null },
                    { 220, false, false, false, false, false, false, null, 20, 2, null, null },
                    { 221, false, false, false, false, false, false, null, 21, 2, null, null },
                    { 222, false, false, false, false, false, false, null, 22, 2, null, null },
                    { 223, false, true, false, false, false, true, null, 23, 2, null, null },
                    { 224, false, true, false, false, false, true, null, 24, 2, null, null },
                    { 225, false, true, false, false, false, true, null, 25, 2, null, null }
                });

            migrationBuilder.CreateIndex(
                name: "ix_pages_group_id",
                table: "pages",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ux_pages_route",
                table: "pages",
                column: "route",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_menu_groups_module_id",
                table: "menu_groups",
                column: "module_id");

            migrationBuilder.CreateIndex(
                name: "ux_menu_groups_key",
                table: "menu_groups",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_menu_modules_key",
                table: "menu_modules",
                column: "key",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_pages_menu_groups_group_id",
                table: "pages",
                column: "group_id",
                principalTable: "menu_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_pages_menu_groups_group_id",
                table: "pages");

            migrationBuilder.DropTable(
                name: "menu_groups");

            migrationBuilder.DropTable(
                name: "menu_modules");

            migrationBuilder.DropIndex(
                name: "ix_pages_group_id",
                table: "pages");

            migrationBuilder.DropIndex(
                name: "ux_pages_route",
                table: "pages");

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 110);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 111);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 112);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 113);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 114);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 115);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 116);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 117);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 118);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 119);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 120);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 121);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 122);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 123);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 124);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 125);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 210);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 211);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 212);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 213);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 214);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 215);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 216);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 217);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 218);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 219);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 220);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 221);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 222);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 223);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 224);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 225);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 11);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 13);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 14);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 15);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 16);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 17);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 18);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 19);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 20);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 21);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 22);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 23);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 24);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 25);

            migrationBuilder.DropColumn(
                name: "group_id",
                table: "pages");

            migrationBuilder.DropColumn(
                name: "is_implemented",
                table: "pages");

            migrationBuilder.AddColumn<string>(
                name: "module",
                table: "pages",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 1,
                column: "module",
                value: "Main");

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "module", "route", "sort_order" },
                values: new object[] { "Main", "/customers", 20 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "module", "route", "sort_order" },
                values: new object[] { "Main", "/products", 30 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "module", "name", "route", "sort_order" },
                values: new object[] { "Main", "Orders", "/orders", 40 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 5,
                columns: new[] { "module", "sort_order" },
                values: new object[] { "Main", 50 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 6,
                columns: new[] { "module", "sort_order" },
                values: new object[] { "Master", 100 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 7,
                columns: new[] { "module", "sort_order" },
                values: new object[] { "Master", 110 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 8,
                columns: new[] { "module", "sort_order" },
                values: new object[] { "Master", 120 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 9,
                columns: new[] { "module", "sort_order" },
                values: new object[] { "Master", 130 });
        }
    }
}
