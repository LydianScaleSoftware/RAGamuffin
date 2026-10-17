using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ragamuffin.Data.Migrations
{
    /// <inheritdoc />
    public partial class LegacyNoTextToNeedsOcr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Before OCR existed, scanned PDFs were marked failed. They are exactly what "needs OCR" means now.
            migrationBuilder.Sql("""
                UPDATE documents
                SET status = 'needs_ocr',
                    error = 'No embedded text. Run OCR to read the scanned pages.'
                WHERE status = 'failed' AND content_type = 'pdf'
                  AND error LIKE 'PDF contains no extractable text%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only migration; nothing to undo.
        }
    }
}
