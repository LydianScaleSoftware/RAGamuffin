using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ragamuffin.Data.Migrations
{
    /// <inheritdoc />
    public partial class PageLayout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "layout_mode",
                table: "documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "layout_pages",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "layout_angle",
                table: "document_pages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "layout_at",
                table: "document_pages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "layout_json",
                table: "document_pages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "layout_text",
                table: "document_pages",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "layout_mode",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "layout_pages",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "layout_angle",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "layout_at",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "layout_json",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "layout_text",
                table: "document_pages");
        }
    }
}
