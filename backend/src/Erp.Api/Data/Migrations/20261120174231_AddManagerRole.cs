using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Erp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManagerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "id", "description", "level", "name" },
                values: new object[] { 4, "Runs day-to-day operations: create and edit business documents, view users; no deletes or rights admin", 30, "Manager" });

            migrationBuilder.InsertData(
                table: "role_page_permissions",
                columns: new[] { "id", "can_delete", "can_read", "can_update", "can_write", "is_restricted", "is_visible", "organization_id", "page_id", "role_id", "updated_at", "updated_by_user_id" },
                values: new object[,]
                {
                    { 401, false, true, false, false, false, true, null, 1, 4, null, null },
                    { 402, false, true, true, true, false, true, null, 2, 4, null, null },
                    { 403, false, true, true, true, false, true, null, 3, 4, null, null },
                    { 404, false, true, true, true, false, true, null, 4, 4, null, null },
                    { 405, false, true, false, false, false, true, null, 5, 4, null, null },
                    { 406, false, true, false, false, false, true, null, 6, 4, null, null },
                    { 407, false, false, false, false, false, false, null, 7, 4, null, null },
                    { 408, false, false, false, false, false, false, null, 8, 4, null, null },
                    { 409, false, false, false, false, false, false, null, 9, 4, null, null },
                    { 410, false, true, true, true, false, true, null, 10, 4, null, null },
                    { 411, false, true, false, false, false, true, null, 11, 4, null, null },
                    { 412, false, true, true, true, false, true, null, 12, 4, null, null },
                    { 413, false, true, true, true, false, true, null, 13, 4, null, null },
                    { 414, false, true, true, true, false, true, null, 14, 4, null, null },
                    { 415, false, true, true, true, false, true, null, 15, 4, null, null },
                    { 416, false, true, true, true, false, true, null, 16, 4, null, null },
                    { 417, false, true, true, true, false, true, null, 17, 4, null, null },
                    { 418, false, true, true, true, false, true, null, 18, 4, null, null },
                    { 419, false, true, true, true, false, true, null, 19, 4, null, null },
                    { 420, false, true, true, true, false, true, null, 20, 4, null, null },
                    { 421, false, true, true, true, false, true, null, 21, 4, null, null },
                    { 422, false, true, true, true, false, true, null, 22, 4, null, null },
                    { 423, false, true, true, true, false, true, null, 23, 4, null, null },
                    { 424, false, true, false, false, false, true, null, 24, 4, null, null },
                    { 425, false, true, false, false, false, true, null, 25, 4, null, null },
                    { 426, false, true, true, true, false, true, null, 26, 4, null, null },
                    { 427, false, true, true, true, false, true, null, 27, 4, null, null },
                    { 428, false, true, true, true, false, true, null, 28, 4, null, null },
                    { 429, false, true, true, true, false, true, null, 29, 4, null, null },
                    { 430, false, true, true, true, false, true, null, 30, 4, null, null },
                    { 431, false, true, true, true, false, true, null, 31, 4, null, null },
                    { 432, false, true, true, true, false, true, null, 32, 4, null, null },
                    { 433, false, true, true, true, false, true, null, 33, 4, null, null },
                    { 434, false, true, true, true, false, true, null, 34, 4, null, null },
                    { 435, false, true, true, true, false, true, null, 35, 4, null, null },
                    { 436, false, true, true, true, false, true, null, 36, 4, null, null },
                    { 437, false, true, true, true, false, true, null, 37, 4, null, null },
                    { 438, false, true, false, false, false, true, null, 38, 4, null, null },
                    { 439, false, true, true, true, false, true, null, 39, 4, null, null },
                    { 440, false, true, true, true, false, true, null, 40, 4, null, null },
                    { 441, false, true, true, true, false, true, null, 41, 4, null, null },
                    { 442, false, true, true, true, false, true, null, 42, 4, null, null },
                    { 443, false, true, true, true, false, true, null, 43, 4, null, null },
                    { 444, false, true, true, true, false, true, null, 44, 4, null, null },
                    { 445, false, true, true, true, false, true, null, 45, 4, null, null },
                    { 446, false, true, false, false, false, true, null, 46, 4, null, null },
                    { 447, false, true, false, false, false, true, null, 47, 4, null, null },
                    { 448, false, true, false, false, false, true, null, 48, 4, null, null },
                    { 449, false, true, false, true, false, true, null, 49, 4, null, null }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 401);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 402);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 403);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 404);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 405);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 406);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 407);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 408);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 409);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 410);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 411);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 412);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 413);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 414);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 415);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 416);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 417);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 418);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 419);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 420);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 421);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 422);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 423);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 424);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 425);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 426);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 427);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 428);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 429);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 430);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 431);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 432);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 433);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 434);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 435);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 436);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 437);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 438);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 439);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 440);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 441);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 442);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 443);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 444);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 445);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 446);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 447);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 448);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 449);

            migrationBuilder.DeleteData(
                table: "roles",
                keyColumn: "id",
                keyValue: 4);
        }
    }
}
