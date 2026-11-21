using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Erp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGeneralChannelAndSeniorMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_chat_conversations_kind",
                table: "chat_conversations");

            migrationBuilder.InsertData(
                table: "pages",
                columns: new[] { "id", "group_id", "is_active", "is_implemented", "key", "name", "route", "sort_order" },
                values: new object[] { 50, null, true, true, "messages-senior", "Message Senior Roles", "/messages/senior-roles", 26 });

            migrationBuilder.InsertData(
                table: "role_page_permissions",
                columns: new[] { "id", "can_delete", "can_read", "can_update", "can_write", "is_restricted", "is_visible", "organization_id", "page_id", "role_id", "updated_at", "updated_by_user_id" },
                values: new object[,]
                {
                    { 150, false, false, false, true, false, false, null, 50, 1, null, null },
                    { 250, false, false, false, false, false, false, null, 50, 2, null, null },
                    { 450, false, false, false, true, false, false, null, 50, 4, null, null }
                });

            migrationBuilder.CreateIndex(
                name: "ux_chat_conversations_general",
                table: "chat_conversations",
                column: "organization_id",
                unique: true,
                filter: "kind = 'general'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_chat_conversations_kind",
                table: "chat_conversations",
                sql: "kind IN ('direct', 'group', 'general', 'announcement')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_chat_conversations_general",
                table: "chat_conversations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_chat_conversations_kind",
                table: "chat_conversations");

            // The old constraint doesn't allow 'general': remove those channels (and, by cascade, their messages).
            migrationBuilder.Sql("DELETE FROM chat_conversations WHERE kind = 'general';");

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 150);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 250);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 450);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 50);

            migrationBuilder.AddCheckConstraint(
                name: "ck_chat_conversations_kind",
                table: "chat_conversations",
                sql: "kind IN ('direct', 'group', 'announcement')");
        }
    }
}
