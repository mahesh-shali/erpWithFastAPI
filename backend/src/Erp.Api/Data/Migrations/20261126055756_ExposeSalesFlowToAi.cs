using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Api.Data.Migrations
{
    /// <summary>
    /// Lets the AI assistant answer questions about the sales flow (customer P.O.s, work orders, invoices):
    /// row-level security per organization + SELECT for the read-only role erp_ai_ro, same as customers/orders.
    /// The API's own user owns these tables and is not affected (owners bypass RLS unless FORCEd).
    /// Which of these tables a user's questions may touch still depends on page rights (PAGE_TABLES in sql_guard.py).
    /// </summary>
    public partial class ExposeSalesFlowToAi : Migration
    {
        private static readonly string[] Tables =
        [
            "customer_pos", "customer_po_lines", "work_orders", "work_order_materials", "sales_invoices", "sales_invoice_lines",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var t in Tables)
            {
                migrationBuilder.Sql($"ALTER TABLE {t} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {t};");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation ON {t} " +
                    "USING (organization_id = NULLIF(current_setting('app.org_id', true), '')::uuid);");
            }
            migrationBuilder.Sql($"GRANT SELECT ON {string.Join(", ", Tables)} TO erp_ai_ro;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"REVOKE SELECT ON {string.Join(", ", Tables)} FROM erp_ai_ro;");
            foreach (var t in Tables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {t};");
                migrationBuilder.Sql($"ALTER TABLE {t} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
