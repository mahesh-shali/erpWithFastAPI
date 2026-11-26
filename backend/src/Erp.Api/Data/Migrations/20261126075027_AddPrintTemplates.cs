using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPrintTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "print_template_id",
                table: "sales_invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "print_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    body = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_print_templates", x => x.id);
                    table.CheckConstraint("ck_print_templates_default_current", "NOT (is_default AND is_archived)");
                    table.CheckConstraint("ck_print_templates_document_type", "document_type IN ('sales-invoice')");
                    table.CheckConstraint("ck_print_templates_version", "version >= 1");
                    table.ForeignKey(
                        name: "fk_print_templates_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sales_invoices_print_template_id",
                table: "sales_invoices",
                column: "print_template_id");

            migrationBuilder.CreateIndex(
                name: "ux_print_templates_family_version",
                table: "print_templates",
                columns: new[] { "organization_id", "family_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_print_templates_org_default",
                table: "print_templates",
                columns: new[] { "organization_id", "document_type" },
                unique: true,
                filter: "is_default");

            migrationBuilder.AddForeignKey(
                name: "fk_sales_invoices_print_templates_print_template_id",
                table: "sales_invoices",
                column: "print_template_id",
                principalTable: "print_templates",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_sales_invoices_print_templates_print_template_id",
                table: "sales_invoices");

            migrationBuilder.DropTable(
                name: "print_templates");

            migrationBuilder.DropIndex(
                name: "ix_sales_invoices_print_template_id",
                table: "sales_invoices");

            migrationBuilder.DropColumn(
                name: "print_template_id",
                table: "sales_invoices");
        }
    }
}
