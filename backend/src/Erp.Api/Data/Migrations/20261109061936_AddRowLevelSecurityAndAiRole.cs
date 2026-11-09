using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Api.Data.Migrations
{
    /// <summary>
    /// Database-level security that the EF model cannot express, kept in code as a migration:
    ///  1. Row-Level Security on business tables, keyed on the per-transaction setting app.org_id.
    ///  2. erp_ai_ro: SELECT-only role used by the AI service (subject to RLS, no access to users/audit).
    ///     Created NOLOGIN; DatabaseInitializer sets LOGIN + PASSWORD from AI_DB_PASSWORD at deploy time.
    ///  3. log_chat(): SECURITY DEFINER function, the AI role's only write path (into chat_audit_log).
    /// The API's own role owns the tables, so it bypasses RLS and relies on EF global query filters.
    /// </summary>
    public partial class AddRowLevelSecurityAndAiRole : Migration
    {
        private static readonly string[] TenantTables = ["customers", "products", "orders", "order_items"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---------- 1. Row-Level Security ----------
            foreach (var t in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {t} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation ON {t} " +
                    "USING (organization_id = NULLIF(current_setting('app.org_id', true), '')::uuid);");
            }

            // ---------- 2. Read-only AI role ----------
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'erp_ai_ro') THEN
                    CREATE ROLE erp_ai_ro NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
                  END IF;
                END $$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA public FROM erp_ai_ro;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA public TO erp_ai_ro;");
            migrationBuilder.Sql($"GRANT SELECT ON {string.Join(", ", TenantTables)} TO erp_ai_ro;");

            // Defence in depth: hard limits on the AI role
            migrationBuilder.Sql("ALTER ROLE erp_ai_ro SET statement_timeout = '5s';");
            migrationBuilder.Sql("ALTER ROLE erp_ai_ro SET default_transaction_read_only = on;");
            migrationBuilder.Sql("ALTER ROLE erp_ai_ro SET idle_in_transaction_session_timeout = '10s';");

            // ---------- 3. Audit write path ----------
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION log_chat(p_org UUID, p_user UUID, p_q TEXT, p_sql TEXT, p_rows INT, p_ms INT)
                RETURNS VOID LANGUAGE sql SECURITY DEFINER SET search_path = public AS $$
                  INSERT INTO chat_audit_log(organization_id, user_id, question, generated_sql, row_count, latency_ms)
                  VALUES (p_org, p_user, p_q, p_sql, p_rows, p_ms);
                $$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION log_chat(UUID, UUID, TEXT, TEXT, INT, INT) FROM PUBLIC;");
            migrationBuilder.Sql("GRANT EXECUTE ON FUNCTION log_chat(UUID, UUID, TEXT, TEXT, INT, INT) TO erp_ai_ro;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS log_chat(UUID, UUID, TEXT, TEXT, INT, INT);");

            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'erp_ai_ro') THEN
                    REVOKE ALL ON ALL TABLES IN SCHEMA public FROM erp_ai_ro;
                    REVOKE USAGE ON SCHEMA public FROM erp_ai_ro;
                    DROP OWNED BY erp_ai_ro;
                    DROP ROLE erp_ai_ro;
                  END IF;
                END $$;
                """);

            foreach (var t in TenantTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {t};");
                migrationBuilder.Sql($"ALTER TABLE {t} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
