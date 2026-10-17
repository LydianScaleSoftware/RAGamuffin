using Microsoft.EntityFrameworkCore;

namespace RagServer.Data;

public sealed class RagDbContext(DbContextOptions<RagDbContext> options) : DbContext(options)
{
    public const int EmbeddingDimensions = 768;

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<Collection> Collections => Set<Collection>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");

        model.Entity<Collection>(e =>
        {
            e.ToTable("collections");
            e.Property(c => c.Name).HasMaxLength(200);
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
