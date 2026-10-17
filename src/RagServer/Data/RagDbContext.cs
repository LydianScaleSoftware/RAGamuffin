using Microsoft.EntityFrameworkCore;

namespace RagServer.Data;

public sealed class RagDbContext(DbContextOptions<RagDbContext> options) : DbContext(options)
{
    public const int EmbeddingDimensions = 768;

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Chunk> Chunks => Set<Chunk>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");

        model.Entity<Document>(e =>
        {
            e.ToTable("documents");
            e.Property(d => d.Collection).HasDefaultValue("default");
            e.HasIndex(d => new { d.Collection, d.Name }).IsUnique();
            e.HasMany(d => d.Chunks).WithOne(c => c.Document).HasForeignKey(c => c.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Chunk>(e =>
        {
            e.ToTable("chunks");
            e.Property(c => c.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
            e.HasGeneratedTsVectorColumn(c => c.Tsv, "english", c => new { c.HeadingPath, c.Content })
                .HasIndex(c => c.Tsv).HasMethod("gin");
            e.HasIndex(c => c.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
            e.HasIndex(c => new { c.DocumentId, c.Ordinal });
        });
    }
}
