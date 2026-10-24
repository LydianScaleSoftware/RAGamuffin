using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Ragamuffin.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClaudeChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "chunk_source",
                table: "documents",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<Guid>(
                name: "source_document_id",
                table: "documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claude_at",
                table: "document_pages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "claude_note",
                table: "document_pages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "claude_text",
                table: "document_pages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_before_claude",
                table: "document_pages",
                type: "text",
                nullable: true);


            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "chunks",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "page_end",
                table: "chunks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "summary",
                table: "chunks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "symbol",
                table: "chunks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "chunks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            // A generated column's expression can't be altered in place: drop it (its GIN index goes with it) and add it
            // back over the new columns.
            migrationBuilder.Sql("""
                ALTER TABLE chunks DROP COLUMN tsv;
                ALTER TABLE chunks ADD COLUMN tsv tsvector GENERATED ALWAYS AS (to_tsvector('english',
                    COALESCE(title, '') || ' ' || COALESCE(symbol, '') || ' ' || COALESCE(heading_path, '') || ' ' ||
                    COALESCE(summary, '') || ' ' || content)) STORED;
                CREATE INDEX ix_chunks_tsv ON chunks USING gin (tsv);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_documents_source_document_id",
                table: "documents",
                column: "source_document_id");

            migrationBuilder.CreateIndex(
                name: "ix_chunks_symbol",
                table: "chunks",
                column: "symbol");

            migrationBuilder.AddForeignKey(
                name: "fk_documents_documents_source_document_id",
                table: "documents",
                column: "source_document_id",
                principalTable: "documents",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE chunks DROP COLUMN tsv;
                ALTER TABLE chunks ADD COLUMN tsv tsvector GENERATED ALWAYS AS (to_tsvector('english',
                    COALESCE(heading_path, '') || ' ' || content)) STORED;
                CREATE INDEX ix_chunks_tsv ON chunks USING gin (tsv);
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_documents_documents_source_document_id",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "ix_documents_source_document_id",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "ix_chunks_symbol",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "chunk_source",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "source_document_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "claude_at",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "claude_note",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "claude_text",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "source_before_claude",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "page_end",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "summary",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "symbol",
                table: "chunks");

            migrationBuilder.DropColumn(
                name: "title",
                table: "chunks");

        }
    }
}
