using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ragamuffin.Data.Migrations
{
    /// <inheritdoc />
    public partial class PageHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "blank_pages",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "muted_pages",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "note_pages",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "notice_muted",
                table: "documents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "problem_pages",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verified_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verified_by",
                table: "documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verified_note",
                table: "documents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "verified_pages",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "blank",
                table: "document_pages",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "issue_kind",
                table: "document_pages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "issue_muted",
                table: "document_pages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verified_at",
                table: "document_pages",
                type: "timestamp with time zone",
                nullable: true);

            // Existing issues: a skipped rotated page keeps its text, so it's a note; everything else is a problem.
            // Page counts are recounted at startup (HealthBackfill), which also checks text-less pages for blankness.
            migrationBuilder.Sql(@"UPDATE document_pages SET issue_kind = CASE WHEN issue LIKE 'Layout analysis skipped because the page is rotated%' THEN 'note' ELSE 'problem' END WHERE issue IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "blank_pages",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "muted_pages",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "note_pages",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "notice_muted",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "problem_pages",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "verified_by",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "verified_note",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "verified_pages",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "blank",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "issue_kind",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "issue_muted",
                table: "document_pages");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "document_pages");
        }
    }
}
