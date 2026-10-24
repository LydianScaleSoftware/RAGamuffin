using Microsoft.EntityFrameworkCore;

namespace Ragamuffin.Data;

public sealed class RagDbContext(DbContextOptions<RagDbContext> options) : DbContext(options)
{
    public const int EmbeddingDimensions = 1024;

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<DocumentPage> Pages => Set<DocumentPage>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");

        model.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });

        model.Entity<Collection>(e =>
        {
            e.ToTable("collections");
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.Description).HasMaxLength(1000);
            e.Property(c => c.Keywords).HasDefaultValueSql("'{}'::text[]");
        });

        model.Entity<Document>(e =>
        {
            e.ToTable("documents");
            e.Property(d => d.Collection).HasDefaultValue(Collection.Default);
            // The migration adds ON UPDATE CASCADE to this key so collection renames carry over.
            e.HasOne<Collection>().WithMany().HasForeignKey(d => d.Collection).HasPrincipalKey(c => c.Name)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(d => new { d.Collection, d.Name }).IsUnique();
            e.HasMany(d => d.Chunks).WithOne(c => c.Document).HasForeignKey(c => c.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(d => d.Pages).WithOne().HasForeignKey(p => p.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(d => d.ChunkSource).HasMaxLength(20).HasDefaultValue(ChunkSources.Auto);
            // Deleting the PDF leaves its Markdown conversion in place, unlinked.
            e.HasOne<Document>().WithMany().HasForeignKey(d => d.SourceDocumentId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<DocumentPage>(e =>
        {
            e.ToTable("document_pages");
            e.HasIndex(p => new { p.DocumentId, p.Page }).IsUnique();
            e.Ignore(p => p.EffectiveText);
            e.Ignore(p => p.EmbeddedSource);
            e.Property(p => p.LayoutJson).HasColumnType("jsonb");
        });

        model.Entity<Chunk>(e =>
        {
            e.ToTable("chunks");
            e.Property(c => c.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
            e.Property(c => c.Kind).HasMaxLength(40);
            e.Property(c => c.Symbol).HasMaxLength(300);
            e.Property(c => c.Title).HasMaxLength(300);
            e.HasGeneratedTsVectorColumn(c => c.Tsv, "english", c => new { c.Title, c.Symbol, c.HeadingPath, c.Summary, c.Content })
                .HasIndex(c => c.Tsv).HasMethod("gin");
            e.HasIndex(c => c.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
            e.HasIndex(c => new { c.DocumentId, c.Ordinal });
            e.HasIndex(c => c.Symbol);
        });
    }
}
