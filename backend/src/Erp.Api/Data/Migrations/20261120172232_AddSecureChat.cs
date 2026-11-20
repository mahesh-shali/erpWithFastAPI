using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Erp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecureChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    direct_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_message_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_conversations", x => x.id);
                    table.CheckConstraint("ck_chat_conversations_kind", "kind IN ('direct', 'group', 'announcement')");
                    table.ForeignKey(
                        name: "fk_chat_conversations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chat_conversations_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "chat_user_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    public_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    encrypted_private_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    kdf_salt = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kdf_iterations = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_user_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_chat_user_keys_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chat_user_keys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "chat_participants",
                columns: table => new
                {
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_read_message_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_participants", x => new { x.conversation_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_chat_participants_chat_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "chat_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chat_participants_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sender_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ciphertext = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: false),
                    iv = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_chat_messages_chat_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "chat_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chat_messages_chat_user_keys_sender_key_id",
                        column: x => x.sender_key_id,
                        principalTable: "chat_user_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chat_messages_users_sender_id",
                        column: x => x.sender_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "chat_message_keys",
                columns: table => new
                {
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    recipient_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wrapped_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chat_message_keys", x => new { x.message_id, x.recipient_key_id });
                    table.ForeignKey(
                        name: "fk_chat_message_keys_chat_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "chat_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chat_message_keys_chat_user_keys_recipient_key_id",
                        column: x => x.recipient_key_id,
                        principalTable: "chat_user_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "pages",
                columns: new[] { "id", "group_id", "is_active", "is_implemented", "key", "name", "route", "sort_order" },
                values: new object[] { 49, null, true, true, "messages", "Messages", "/messages", 25 });

            migrationBuilder.InsertData(
                table: "role_page_permissions",
                columns: new[] { "id", "can_delete", "can_read", "can_update", "can_write", "is_restricted", "is_visible", "organization_id", "page_id", "role_id", "updated_at", "updated_by_user_id" },
                values: new object[,]
                {
                    { 149, false, true, false, true, false, true, null, 49, 1, null, null },
                    { 249, false, true, false, true, false, true, null, 49, 2, null, null }
                });

            migrationBuilder.CreateIndex(
                name: "ix_chat_conversations_created_by_user_id",
                table: "chat_conversations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_chat_conversations_announcement",
                table: "chat_conversations",
                column: "organization_id",
                unique: true,
                filter: "kind = 'announcement'");

            migrationBuilder.CreateIndex(
                name: "ux_chat_conversations_direct",
                table: "chat_conversations",
                columns: new[] { "organization_id", "direct_key" },
                unique: true,
                filter: "direct_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_chat_message_keys_recipient",
                table: "chat_message_keys",
                column: "recipient_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_conversation",
                table: "chat_messages",
                columns: new[] { "conversation_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_sender_id",
                table: "chat_messages",
                column: "sender_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_messages_sender_key_id",
                table: "chat_messages",
                column: "sender_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_participants_user",
                table: "chat_participants",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_chat_user_keys_org",
                table: "chat_user_keys",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_chat_user_keys_active",
                table: "chat_user_keys",
                column: "user_id",
                unique: true,
                filter: "revoked_at IS NULL");

            // Defense in depth: the API's DB user owns these tables and bypasses RLS. Enabling RLS with no
            // policies means any other role (e.g. the AI service's erp_ai_ro) sees no rows even if a broad
            // GRANT is added later. Content is ciphertext anyway; this also hides who talks to whom.
            foreach (var t in new[] { "chat_user_keys", "chat_conversations", "chat_participants", "chat_messages", "chat_message_keys" })
            {
                migrationBuilder.Sql($"ALTER TABLE {t} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'erp_ai_ro') THEN
                        REVOKE ALL ON {t} FROM erp_ai_ro;
                    END IF;
                END $$;");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_message_keys");

            migrationBuilder.DropTable(
                name: "chat_participants");

            migrationBuilder.DropTable(
                name: "chat_messages");

            migrationBuilder.DropTable(
                name: "chat_conversations");

            migrationBuilder.DropTable(
                name: "chat_user_keys");

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 149);

            migrationBuilder.DeleteData(
                table: "role_page_permissions",
                keyColumn: "id",
                keyValue: 249);

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "id",
                keyValue: 49);
        }
    }
}
