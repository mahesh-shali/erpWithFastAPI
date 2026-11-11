using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Erp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVSmartModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "menu_groups",
                columns: new[] { "id", "key", "module_id", "name", "sort_order" },
                values: new object[,]
                {
                    { 106, "master-inventory-setup", 1, "Inventory Setup", 45 },
                    { 303, "sales-labour-work", 3, "Labour Work", 30 },
                    { 304, "sales-dispatch", 3, "Dispatch", 40 },
                    { 403, "purchase-subcontract", 4, "Subcontract", 30 },
                    { 404, "purchase-goods-receipt", 4, "Goods Receipt", 40 },
                    { 503, "production-shop-floor", 5, "Shop Floor", 30 },
                    { 504, "production-engineering", 5, "Engineering", 40 },
                    { 603, "accounts-vouchers", 6, "Vouchers", 30 },
                    { 604, "accounts-ledgers", 6, "Ledgers", 40 },
                    { 703, "inspection-in-process", 7, "In-Process", 15 },
                    { 704, "inspection-final", 7, "Final", 30 },
                    { 803, "hr-payroll", 8, "Payroll", 30 },
                    { 804, "hr-leave", 8, "Leave", 40 },
                    { 903, "reports-production", 9, "Production", 30 },
                    { 904, "reports-purchase", 9, "Purchase", 40 }
                });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 2,
                column: "sort_order",
                value: 60);

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "name", "sort_order" },
                values: new object[] { "Sales & Labour Work", 20 });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "name", "sort_order" },
                values: new object[] { "Purchase & Subcontract", 30 });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 6,
                columns: new[] { "name", "sort_order" },
                values: new object[] { "Accounts", 70 });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 7,
                columns: new[] { "name", "sort_order" },
                values: new object[] { "Inspection (QC)", 80 });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 8,
                columns: new[] { "name", "sort_order" },
                values: new object[] { "HRMS", 100 });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 9,
                column: "sort_order",
                value: 110);

            migrationBuilder.InsertData(
                table: "menu_modules",
                columns: new[] { "id", "icon", "key", "name", "sort_order" },
                values: new object[,]
                {
                    { 10, "calendar", "planning", "Planning", 40 },
                    { 11, "wrench", "maintenance", "Maintenance", 90 }
                });

            migrationBuilder.UpdateData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 501,
                columns: new[] { "module_id", "name" },
                values: new object[] { 10, "Production Planning" });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 16,
                column: "route",
                value: "/planning/production-plan");

            migrationBuilder.InsertData(
                table: "menu_groups",
                columns: new[] { "id", "key", "module_id", "name", "sort_order" },
                values: new object[,]
                {
                    { 1001, "planning-material", 10, "Material Planning", 20 },
                    { 1101, "maintenance-machines", 11, "Machines", 10 },
                    { 1102, "maintenance-preventive", 11, "Preventive", 20 },
                    { 1103, "maintenance-breakdown", 11, "Breakdown", 30 }
                });

            migrationBuilder.InsertData(
                table: "pages",
                columns: new[] { "id", "group_id", "is_active", "is_implemented", "key", "name", "route", "sort_order" },
                values: new object[,]
                {
                    { 26, 106, true, false, "uom", "Units of Measure", "/master/uom", 10 },
                    { 27, 106, true, false, "stores", "Stores", "/master/stores", 20 },
                    { 28, 303, true, false, "labour-work-orders", "Labour Work Orders", "/sales/labour-work-orders", 10 },
                    { 29, 303, true, false, "labour-work-invoices", "Labour Work Invoices", "/sales/labour-work-invoices", 20 },
                    { 30, 304, true, false, "delivery-challans", "Delivery Challans", "/sales/delivery-challans", 10 },
                    { 31, 403, true, false, "subcontract-orders", "Subcontract Orders", "/purchase/subcontract-orders", 10 },
                    { 32, 403, true, false, "subcontract-challans", "Subcontract Challans", "/purchase/subcontract-challans", 20 },
                    { 33, 404, true, false, "goods-receipts", "Goods Receipt Notes", "/purchase/grn", 10 },
                    { 35, 503, true, false, "job-cards", "Job Cards", "/production/job-cards", 10 },
                    { 36, 504, true, false, "bill-of-materials", "Bill of Materials", "/production/bom", 10 },
                    { 37, 603, true, false, "journal-vouchers", "Journal Vouchers", "/accounts/journal-vouchers", 10 },
                    { 38, 604, true, false, "ledger", "General Ledger", "/accounts/ledger", 10 },
                    { 39, 703, true, false, "in-process-inspection", "In-Process Inspection", "/inspection/in-process", 10 },
                    { 40, 704, true, false, "final-inspection", "Final Inspection", "/inspection/final", 10 },
                    { 44, 803, true, false, "payroll", "Payroll", "/hr/payroll", 10 },
                    { 45, 804, true, false, "leave-requests", "Leave Requests", "/hr/leave", 10 },
                    { 46, 903, true, false, "production-report", "Production Report", "/reports/production", 10 },
                    { 47, 904, true, false, "purchase-report", "Purchase Report", "/reports/purchase", 10 },
                    { 34, 1001, true, false, "mrp", "Material Requirements", "/planning/mrp", 10 },
                    { 41, 1101, true, false, "machines", "Machine Register", "/maintenance/machines", 10 },
                    { 42, 1102, true, false, "preventive-maintenance", "PM Schedule", "/maintenance/preventive", 10 },
                    { 43, 1103, true, false, "breakdowns", "Breakdown Log", "/maintenance/breakdowns", 10 }
                });

            migrationBuilder.InsertData(
                table: "role_page_permissions",
                columns: new[] { "id", "can_delete", "can_read", "can_update", "can_write", "is_restricted", "is_visible", "organization_id", "page_id", "role_id", "updated_at", "updated_by_user_id" },
                values: new object[,]
                {
                    { 126, true, true, true, true, false, true, null, 26, 1, null, null },
                    { 127, true, true, true, true, false, true, null, 27, 1, null, null },
                    { 128, true, true, true, true, false, true, null, 28, 1, null, null },
                    { 129, true, true, true, true, false, true, null, 29, 1, null, null },
                    { 130, true, true, true, true, false, true, null, 30, 1, null, null },
                    { 131, true, true, true, true, false, true, null, 31, 1, null, null },
                    { 132, true, true, true, true, false, true, null, 32, 1, null, null },
                    { 133, true, true, true, true, false, true, null, 33, 1, null, null },
                    { 135, true, true, true, true, false, true, null, 35, 1, null, null },
                    { 136, true, true, true, true, false, true, null, 36, 1, null, null },
                    { 137, true, true, true, true, false, true, null, 37, 1, null, null },
                    { 138, false, true, false, false, false, true, null, 38, 1, null, null },
                    { 139, true, true, true, true, false, true, null, 39, 1, null, null },
                    { 140, true, true, true, true, false, true, null, 40, 1, null, null },
                    { 144, true, true, true, true, false, true, null, 44, 1, null, null },
                    { 145, true, true, true, true, false, true, null, 45, 1, null, null },
                    { 146, false, true, false, false, false, true, null, 46, 1, null, null },
                    { 147, false, true, false, false, false, true, null, 47, 1, null, null },
                    { 226, false, true, false, false, false, true, null, 26, 2, null, null },
                    { 227, false, true, false, false, false, true, null, 27, 2, null, null },
                    { 228, false, false, false, false, false, false, null, 28, 2, null, null },
                    { 229, false, false, false, false, false, false, null, 29, 2, null, null },
                    { 230, false, true, true, true, false, true, null, 30, 2, null, null },
                    { 231, false, false, false, false, false, false, null, 31, 2, null, null },
                    { 232, false, false, false, false, false, false, null, 32, 2, null, null },
                    { 233, false, false, false, false, false, false, null, 33, 2, null, null },
                    { 235, false, true, true, true, false, true, null, 35, 2, null, null },
                    { 236, false, false, false, false, false, false, null, 36, 2, null, null },
                    { 237, false, false, false, false, false, false, null, 37, 2, null, null },
                    { 238, false, false, false, false, false, false, null, 38, 2, null, null },
                    { 239, false, false, false, false, false, false, null, 39, 2, null, null },
                    { 240, false, false, false, false, false, false, null, 40, 2, null, null },
                    { 244, false, false, false, false, false, false, null, 44, 2, null, null },
                    { 245, false, true, false, true, false, true, null, 45, 2, null, null },
                    { 246, false, true, false, false, false, true, null, 46, 2, null, null },
                    { 247, false, false, false, false, false, false, null, 47, 2, null, null },
                    { 134, true, true, true, true, false, true, null, 34, 1, null, null },
                    { 141, true, true, true, true, false, true, null, 41, 1, null, null },
                    { 142, true, true, true, true, false, true, null, 42, 1, null, null },
                    { 143, true, true, true, true, false, true, null, 43, 1, null, null },
                    { 234, false, false, false, false, false, false, null, 34, 2, null, null },
                    { 241, false, false, false, false, false, false, null, 41, 2, null, null },
                    { 242, false, false, false, false, false, false, null, 42, 2, null, null },
                    { 243, false, true, false, true, false, true, null, 43, 2, null, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 126);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 127);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 128);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 129);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 130);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 131);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 132);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 133);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 134);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 135);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 136);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 137);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 138);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 139);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 140);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 141);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 142);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 143);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 144);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 145);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 146);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 147);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 226);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 227);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 228);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 229);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 230);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 231);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 232);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 233);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 234);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 235);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 236);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 237);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 238);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 239);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 240);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 241);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 242);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 243);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 244);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 245);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 246);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 247);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 26);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 27);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 28);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 29);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 30);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 31);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 32);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 33);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 34);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 35);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 36);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 37);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 38);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 39);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 40);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 41);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 42);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 43);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 44);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 45);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 46);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 47);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 106);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 303);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 304);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 403);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 404);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 503);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 504);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 603);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 604);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 703);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 704);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 803);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 804);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 903);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 904);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 1001);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 1101);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 1102);

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 1103);

            migrationBuilder.UpdateData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 501,
                columns: new[] { "module_id", "name" },
                values: new object[] { 5, "Planning" });

            migrationBuilder.DeleteData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 11);

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 2,
                column: "sort_order",
                value: 20);

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "name", "sort_order" },
                values: new object[] { "Sales", 30 });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "name", "sort_order" },
                values: new object[] { "Purchase", 40 });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 6,
                columns: new[] { "name", "sort_order" },
                values: new object[] { "Cash Flow", 60 });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 7,
                columns: new[] { "name", "sort_order" },
                values: new object[] { "Inspection", 70 });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 8,
                columns: new[] { "name", "sort_order" },
                values: new object[] { "Human Resource", 80 });

            migrationBuilder.UpdateData(
                table: "menu_modules",
                keyColumn: "id",
                keyValue: 9,
                column: "sort_order",
                value: 90);

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 16,
                column: "route",
                value: "/production/plan");
        }
    }
}
