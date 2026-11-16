using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Erp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "pages",
                columns: new[] { "id", "group_id", "is_active", "is_implemented", "key", "name", "route", "sort_order" },
                values: new object[] { 48, null, true, true, "analytics", "Analytics", "/analytics", 15 });

            migrationBuilder.InsertData(
                table: "role_page_permissions",
                columns: new[] { "id", "can_delete", "can_read", "can_update", "can_write", "is_restricted", "is_visible", "organization_id", "page_id", "role_id", "updated_at", "updated_by_user_id" },
                values: new object[,]
                {
                    { 148, false, true, false, false, false, true, null, 48, 1, null, null },
                    { 248, false, false, false, false, false, false, null, 48, 2, null, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 148);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 248);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 48);
        }
    }
}
