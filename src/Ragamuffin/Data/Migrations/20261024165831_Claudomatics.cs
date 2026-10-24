using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ragamuffin.Data.Migrations
{
    /// <inheritdoc />
    public partial class Claudomatics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "encoding",
                table: "documents",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                table: "chunks",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            // Existing Claudomatics date from their document's last indexing. (Only those: updating every chunk rewrites its
            // vector index entry too, which takes minutes on a large library; other chunks keep the migration's date.)
            migrationBuilder.Sql("UPDATE chunks c SET created_at = d.updated_at FROM documents d WHERE d.id = c.document_id AND d.chunk_source = 'claude';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "encoding",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "chunks");
        }
    }
}
