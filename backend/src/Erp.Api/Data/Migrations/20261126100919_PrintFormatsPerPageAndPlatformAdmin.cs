using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Api.Data.Migrations
{
    /// <summary>
    /// Print formats belong to a menu page (customer-pos, sales-invoices, work-orders) instead of a document type, and
    /// users.is_platform_admin lets a platform owner manage the formats of every organization.
    /// Hand-edited: document_type is renamed to page_key and its values mapped ('sales-invoice' → 'sales-invoices'),
    /// so existing templates (and invoices that froze one) are kept.
    /// </summary>
    public partial class PrintFormatsPerPageAndPlatformAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_print_templates_document_type",
                table: "print_templates");

            migrationBuilder.RenameColumn(
                name: "document_type",
                table: "print_templates",
                newName: "page_key");

            migrationBuilder.AlterColumn<string>(
                name: "page_key",
                table: "print_templates",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30);

            migrationBuilder.Sql("UPDATE print_templates SET page_key = 'sales-invoices' WHERE page_key = 'sales-invoice';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_print_templates_page_key",
                table: "print_templates",
                sql: "page_key IN ('customer-pos','sales-invoices','work-orders')");

            migrationBuilder.AddColumn<bool>(
                name: "is_platform_admin",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_platform_admin",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_print_templates_page_key",
                table: "print_templates");

            // The old schema only knew invoice templates. Customer P.O. / Work Order formats are referenced by nothing
            // (only sales_invoices.print_template_id points at templates), so they can go.
            migrationBuilder.Sql("DELETE FROM print_templates WHERE page_key <> 'sales-invoices';");
            migrationBuilder.Sql("UPDATE print_templates SET page_key = 'sales-invoice';");

            migrationBuilder.AlterColumn<string>(
                name: "page_key",
                table: "print_templates",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.RenameColumn(
                name: "page_key",
                table: "print_templates",
                newName: "document_type");

            migrationBuilder.AddCheckConstraint(
                name: "ck_print_templates_document_type",
                table: "print_templates",
                sql: "document_type IN ('sales-invoice')");
        }
    }
}
