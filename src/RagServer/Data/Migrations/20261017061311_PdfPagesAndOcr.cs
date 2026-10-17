using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RagServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class PdfPagesAndOcr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "empty_pages",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ocr_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ocr_model",
                table: "documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ocr_pages",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "page_count",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "progress_done",
                table: "documents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "progress_total",
                table: "documents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "suspect_pages",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ocr",
                table: "chunks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "document_pages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page = table.Column<int>(type: "integer", nullable: false),
                    embedded_text = table.Column<string>(type: "text", nullable: true),
                    ocr_text = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    ocr_model = table.Column<string>(type: "text", nullable: true),
                    ocr_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    suspect_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_pages", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_pages_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_pages_document_id_page",
                table: "document_pages",
                columns: new[] { "document_id", "page" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_pages");

            migrationBuilder.DropColumn(
                name: "empty_pages",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ocr_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ocr_model",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ocr_pages",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "page_count",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "progress_done",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "progress_total",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "suspect_pages",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "ocr",
                table: "chunks");
        }
    }
}
