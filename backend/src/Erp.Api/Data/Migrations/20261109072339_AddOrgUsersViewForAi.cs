using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Api.Data.Migrations
{
    /// <summary>
    /// Lets the AI chatbot answer questions about the organization's login users ("how many users
    /// do we have?", "who are the admins?") without access to the users table itself.
    ///
    /// org_users exposes only safe columns (no password_hash) and only the current tenant's rows:
    ///  - the WHERE on app.org_id is the same per-transaction tenant setting the RLS policies use;
    ///  - security_barrier stops the planner from evaluating caller-supplied predicates (e.g. a cast
    ///    that errors and leaks a value) before that tenant filter;
    ///  - the view runs with its owner's rights, so erp_ai_ro needs SELECT on the view only.
    /// </summary>
    public partial class AddOrgUsersViewForAi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE VIEW org_users WITH (security_barrier = true) AS
                SELECT u.id,
                       u.full_name,
                       u.email,
                       r.name AS role,
                       u.created_at,
                       u.last_login_at
                FROM users u
                JOIN roles r ON r.id = u.role_id
                WHERE u.organization_id = NULLIF(current_setting('app.org_id', true), '')::uuid;
                """);
            migrationBuilder.Sql("COMMENT ON VIEW org_users IS 'Login users of the current organization (AI-safe: no password hashes, tenant-filtered)';");
            migrationBuilder.Sql("GRANT SELECT ON org_users TO erp_ai_ro;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS org_users;");
        }
    }
}
