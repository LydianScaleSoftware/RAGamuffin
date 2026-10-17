using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ragamuffin.Data.Migrations
{
    /// <inheritdoc />
    public partial class Collections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "collection",
                table: "documents",
                type: "character varying(200)",
                nullable: false,
                defaultValue: "default",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "default");

            migrationBuilder.CreateTable(
                name: "collections",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collections", x => x.id);
                    table.UniqueConstraint("ak_collections_name", x => x.name);
                });

            // Every collection already used by a document, plus the built-in default, becomes a row.
            migrationBuilder.Sql("""
                INSERT INTO collections (name, created_at)
                SELECT 'default', now()
                UNION
                SELECT DISTINCT collection, now() FROM documents
                ON CONFLICT (name) DO NOTHING;
                """);

            // ON UPDATE CASCADE (which EF cannot express) lets a collection rename carry over to its documents.
            migrationBuilder.Sql("""
                ALTER TABLE documents
                    ADD CONSTRAINT fk_documents_collections_collection FOREIGN KEY (collection)
                    REFERENCES collections (name) ON UPDATE CASCADE ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_documents_collections_collection",
                table: "documents");

            migrationBuilder.DropTable(
                name: "collections");

            migrationBuilder.AlterColumn<string>(
                name: "collection",
                table: "documents",
                type: "text",
                nullable: false,
                defaultValue: "default",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldDefaultValue: "default");
        }
    }
}
