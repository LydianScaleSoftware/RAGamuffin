using Microsoft.EntityFrameworkCore;
using Ragamuffin.Data;
using Ragamuffin.Ingestion;

namespace Ragamuffin.Api;

public sealed record MarkdownExportRequest(string? Name);

/// <summary>
/// Markdown exports of PDFs: a preview listing the pages whose text is missing or probably wrong, so the user can fix
/// them first, then the export itself, saved as a Markdown document that stands in for the PDF in search.
/// </summary>
public static class ExportEndpoints
{
    public static void MapExportApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // What an export would contain and what's wrong with it; name= checks what that name would replace.
        api.MapGet("/documents/{id:guid}/markdown-export", async (Guid id, string? name, MarkdownExporter exporter, RagDbContext db, CancellationToken ct) =>
        {
            if (await exporter.BuildAsync(id, ct) is not var (pdf, export)) return Results.NotFound();
            var existing = await db.Documents.AsNoTracking().Where(d => d.SourceDocumentId == id)
                .OrderBy(d => d.Name).Select(d => new { d.Id, d.Name, d.Collection, d.ChunkSource, d.UpdatedAt }).ToListAsync(ct);
            var suggested = existing.FirstOrDefault(d => d.ChunkSource == ChunkSources.Export)?.Name ?? MarkdownExports.DefaultName(pdf.Name);
            var target = string.IsNullOrWhiteSpace(name) ? suggested : name.Trim();
            var replaces = await db.Documents.AsNoTracking().Where(d => d.Collection == pdf.Collection && d.Name == target)
                .Select(d => new { d.Id, d.Name, d.ContentType, d.ChunkSource, IsConversion = d.SourceDocumentId == id }).FirstOrDefaultAsync(ct);
            return Results.Ok(new
            {
                Pdf = pdf.Name, pdf.Collection, pdf.Status, Name = target, Suggested = suggested,
                export.Pages, export.PagesWithText, export.Verified, export.Unverified, export.Blank,
                Problems = export.Problems.Select(p => new { p.Page, p.Problem, p.Muted }),
                Chars = export.Text.Length, Chunks = export.Chunks.Count,
                Replaces = replaces,
                // Other conversions of this PDF (e.g. Claude's own Markdown for some chapters), which overlap an export.
                Conversions = existing.Where(d => d.Name != target || d.Collection != pdf.Collection),
            });
        });

        api.MapPost("/documents/{id:guid}/markdown-export", async (Guid id, MarkdownExportRequest req, MarkdownExporter exporter,
            RagDbContext db, IHostApplicationLifetime lifetime) =>
        {
            var pdf = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id);
            if (pdf is null) return Results.NotFound();
            List<string> errors = [];
            if (pdf.ContentType != "pdf") errors.Add("Only PDFs can be exported to Markdown.");
            else if (pdf.Status != DocumentStatus.Ready) errors.Add($"{pdf.Name} is {pdf.Status.Replace('_', ' ')}; export it once it's ready.");
            var name = string.IsNullOrWhiteSpace(req.Name) ? MarkdownExports.DefaultName(pdf.Name) : req.Name.Trim();
            if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) errors.Add("The name must end in .md.");
            if (await db.Documents.AnyAsync(d => d.Collection == pdf.Collection && d.Name == name &&
                    (d.ContentType == "pdf" || d.Status == DocumentStatus.Ocr || d.Status == DocumentStatus.Pending)))
                errors.Add($"\"{name}\" is a PDF or is being processed; choose another name.");
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            try
            {
                // Not the request's token: closing the page shouldn't abandon an export half embedded.
                var r = await exporter.SaveAsync(id, name, pdf.Collection, lifetime.ApplicationStopping);
                return Results.Ok(new { r.Document.Id, r.Document.Name, r.Document.Collection, r.Outcome, r.Chunks });
            }
            catch (ChunkPlanException ex)
            {
                return Results.BadRequest(new { errors = ex.Errors });
            }
        });
    }
}
