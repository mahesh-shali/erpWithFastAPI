using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveAnalyticsUnderReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "menu_groups",
                columns: new[] { "id", "key", "module_id", "name", "sort_order" },
                values: new object[] { 905, "reports-analytics", 9, "Analytics", 5 });

            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 48,
                columns: new[] { "group_id", "sort_order" },
                values: new object[] { 905, 10 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Move the page out of the group first: deleting the group while the page still points at it would fail
            // (or cascade to the page and its rights).
            migrationBuilder.UpdateData(
                table: "pages",
                keyColumn: "id",
                keyValue: 48,
                columns: new[] { "group_id", "sort_order" },
                values: new object[] { null, 15 });

            migrationBuilder.DeleteData(
                table: "menu_groups",
                keyColumn: "id",
                keyValue: 905);
        }
    }
}
