using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Erp.Api.Data.Migrations
{
    /// <summary>
    /// Master module: pages master, SuperAdmin role and role levels (SuperAdmin > Admin > User),
    /// role default rights (system rows seeded from code + per-organization overrides), per-user overrides,
    /// and the SQL functions effective_page_permissions() / ai_user_access() that resolve rights.
    /// Existing organizations: the earliest Admin becomes the SuperAdmin (hand-written data step).
    /// </summary>
    public partial class AddMasterPagesAndRights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "level",
                table: "roles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "pages",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    route = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    module = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "role_page_permissions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'10000', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    page_id = table.Column<int>(type: "integer", nullable: false),
                    is_visible = table.Column<bool>(type: "boolean", nullable: false),
                    is_restricted = table.Column<bool>(type: "boolean", nullable: false),
                    can_read = table.Column<bool>(type: "boolean", nullable: false),
                    can_write = table.Column<bool>(type: "boolean", nullable: false),
                    can_update = table.Column<bool>(type: "boolean", nullable: false),
                    can_delete = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_page_permissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_role_page_permissions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_page_permissions_pages_page_id",
                        column: x => x.page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_page_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_page_permissions",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_id = table.Column<int>(type: "integer", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_visible = table.Column<bool>(type: "boolean", nullable: false),
                    is_restricted = table.Column<bool>(type: "boolean", nullable: false),
                    can_read = table.Column<bool>(type: "boolean", nullable: false),
                    can_write = table.Column<bool>(type: "boolean", nullable: false),
                    can_update = table.Column<bool>(type: "boolean", nullable: false),
                    can_delete = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_page_permissions", x => new { x.user_id, x.page_id });
                    table.ForeignKey(
                        name: "fk_user_page_permissions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_page_permissions_pages_page_id",
                        column: x => x.page_id,
                        principalTable: "pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_page_permissions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "pages",
                columns: new[] { "id", "is_active", "key", "module", "name", "route", "sort_order" },
                values: new object[,]
                {
                    { 1, true, "dashboard", "Main", "Dashboard", "/", 10 },
                    { 2, true, "customers", "Main", "Customers", "/customers", 20 },
                    { 3, true, "products", "Main", "Products", "/products", 30 },
                    { 4, true, "orders", "Main", "Orders", "/orders", 40 },
                    { 5, true, "ai-chat", "Main", "AI Assistant", "/assistant", 50 },
                    { 6, true, "master-users", "Master", "Users", "/master/users", 100 },
                    { 7, true, "master-user-rights", "Master", "User Rights", "/master/user-rights", 110 },
                    { 8, true, "master-role-rights", "Master", "Role Rights", "/master/role-rights", 120 },
                    { 9, true, "master-pages", "Master", "Pages", "/master/pages", 130 }
                });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "description", "level" },
                values: new object[] { "Manages users and their rights (never beyond the admin's own rights)", 50 });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "description", "level" },
                values: new object[] { "Standard access as granted by page rights", 10 });

            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "id", "description", "level", "name" },
                values: new object[] { 3, "Organization owner: every right on every page; manages admins, users and all rights", 100, "SuperAdmin" });

            migrationBuilder.InsertData(
                table: "role_page_permissions",
                columns: new[] { "id", "can_delete", "can_read", "can_update", "can_write", "is_restricted", "is_visible", "organization_id", "page_id", "role_id", "updated_at", "updated_by_user_id" },
                values: new object[,]
                {
                    { 101, false, true, false, false, false, true, null, 1, 1, null, null },
                    { 102, true, true, true, true, false, true, null, 2, 1, null, null },
                    { 103, true, true, true, true, false, true, null, 3, 1, null, null },
                    { 104, true, true, true, true, false, true, null, 4, 1, null, null },
                    { 105, false, true, false, false, false, true, null, 5, 1, null, null },
                    { 106, true, true, true, true, false, true, null, 6, 1, null, null },
                    { 107, false, true, true, false, false, true, null, 7, 1, null, null },
                    { 108, false, false, false, false, false, false, null, 8, 1, null, null },
                    { 109, false, true, false, false, false, true, null, 9, 1, null, null },
                    { 201, false, true, false, false, false, true, null, 1, 2, null, null },
                    { 202, false, true, true, true, false, true, null, 2, 2, null, null },
                    { 203, false, true, false, false, false, true, null, 3, 2, null, null },
                    { 204, false, true, true, true, false, true, null, 4, 2, null, null },
                    { 205, false, true, false, false, false, true, null, 5, 2, null, null },
                    { 206, false, false, false, false, false, false, null, 6, 2, null, null },
                    { 207, false, false, false, false, false, false, null, 7, 2, null, null },
                    { 208, false, false, false, false, false, false, null, 8, 2, null, null },
                    { 209, false, false, false, false, false, false, null, 9, 2, null, null }
                });

            migrationBuilder.CreateIndex(
                name: "ux_pages_key",
                table: "pages",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_page_permissions_page_id",
                table: "role_page_permissions",
                column: "page_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_page_permissions_role_id",
                table: "role_page_permissions",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ux_role_page_permissions",
                table: "role_page_permissions",
                columns: new[] { "organization_id", "role_id", "page_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_user_page_permissions_org",
                table: "user_page_permissions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_page_permissions_page_id",
                table: "user_page_permissions",
                column: "page_id");

            // ================= Hand-written =================

            // ---- Every existing organization gets a SuperAdmin: its earliest Admin (else its earliest user) ----
            migrationBuilder.Sql("""
                UPDATE users u SET role_id = 3
                FROM (SELECT DISTINCT ON (organization_id) id, organization_id
                      FROM users
                      ORDER BY organization_id, (role_id = 1) DESC, created_at, id) first_user
                WHERE u.id = first_user.id
                  AND NOT EXISTS (SELECT 1 FROM users s
                                  WHERE s.organization_id = first_user.organization_id AND s.role_id = 3);
                """);

            // ---- Effective rights: single source of truth for the API and the AI service ----
            // Precedence per page: user override > organization role default > system role default > nothing.
            // SuperAdmin (role 3) always has every right. is_restricted is a hard block: it clears all rights.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION effective_page_permissions(p_user UUID)
                RETURNS TABLE (page_id INT, page_key TEXT, is_visible BOOLEAN, is_restricted BOOLEAN,
                               can_read BOOLEAN, can_write BOOLEAN, can_update BOOLEAN, can_delete BOOLEAN,
                               source TEXT)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
                  WITH me AS (
                    SELECT u.id, u.organization_id, u.role_id, (u.role_id = 3) AS su
                    FROM users u WHERE u.id = p_user
                  )
                  SELECT p.id,
                         p.key::text,
                         me.su OR (COALESCE(e.is_visible, false) AND NOT COALESCE(e.is_restricted, false)),
                         NOT me.su AND COALESCE(e.is_restricted, false),
                         me.su OR (COALESCE(e.can_read,   false) AND NOT COALESCE(e.is_restricted, false)),
                         me.su OR (COALESCE(e.can_write,  false) AND NOT COALESCE(e.is_restricted, false)),
                         me.su OR (COALESCE(e.can_update, false) AND NOT COALESCE(e.is_restricted, false)),
                         me.su OR (COALESCE(e.can_delete, false) AND NOT COALESCE(e.is_restricted, false)),
                         CASE WHEN me.su THEN 'superadmin' ELSE COALESCE(e.src, 'none') END
                  FROM me
                  CROSS JOIN pages p
                  LEFT JOIN LATERAL (
                    SELECT x.* FROM (
                      SELECT up.is_visible, up.is_restricted, up.can_read, up.can_write, up.can_update, up.can_delete,
                             'user' AS src, 1 AS prio
                      FROM user_page_permissions up
                      WHERE up.user_id = me.id AND up.page_id = p.id
                      UNION ALL
                      SELECT rp.is_visible, rp.is_restricted, rp.can_read, rp.can_write, rp.can_update, rp.can_delete,
                             'organization', 2
                      FROM role_page_permissions rp
                      WHERE rp.organization_id = me.organization_id AND rp.role_id = me.role_id AND rp.page_id = p.id
                      UNION ALL
                      SELECT rp.is_visible, rp.is_restricted, rp.can_read, rp.can_write, rp.can_update, rp.can_delete,
                             'system', 3
                      FROM role_page_permissions rp
                      WHERE rp.organization_id IS NULL AND rp.role_id = me.role_id AND rp.page_id = p.id
                    ) x
                    ORDER BY x.prio
                    LIMIT 1
                  ) e ON true
                  WHERE p.is_active
                  ORDER BY p.sort_order;
                $$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION effective_page_permissions(UUID) FROM PUBLIC;");

            // ---- AI service: one round trip for "session still active?" + "which pages may this user read?" ----
            // The AI uses readable_pages to decide which tables a question may touch (customers -> customers page, ...).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION ai_user_access(p_session UUID, p_user UUID)
                RETURNS TABLE (session_active BOOLEAN, readable_pages TEXT[])
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public AS $$
                  SELECT session_is_active(p_session, p_user),
                         COALESCE((SELECT array_agg(e.page_key ORDER BY e.page_key)
                                   FROM effective_page_permissions(p_user) e
                                   WHERE e.can_read), '{}'::text[]);
                $$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION ai_user_access(UUID, UUID) FROM PUBLIC;");
            migrationBuilder.Sql("GRANT EXECUTE ON FUNCTION ai_user_access(UUID, UUID) TO erp_ai_ro;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS ai_user_access(UUID, UUID);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS effective_page_permissions(UUID);");
            // SuperAdmins become Admins again before the SuperAdmin role row is deleted.
            migrationBuilder.Sql("UPDATE users SET role_id = 1 WHERE role_id = 3;");

            migrationBuilder.DropTable(
                name: "role_page_permissions");

            migrationBuilder.DropTable(
                name: "user_page_permissions");

            migrationBuilder.DropTable(
                name: "pages");

            migrationBuilder.DeleteData(
                table: "roles",
                keyColumn: "id",
                keyValue: 3);

            migrationBuilder.DropColumn(
                name: "level",
                table: "roles");

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 1,
                column: "description",
                value: "Full access, including product and user management");

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 2,
                column: "description",
                value: "Standard access: read data, create orders, use the AI assistant");
        }
    }
}
