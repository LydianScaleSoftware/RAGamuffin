using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Ragamuffin.Data.Migrations
{
    /// <inheritdoc />
    public partial class Qwen3Embeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Vectors from the old model can't be converted (or compared); clear them, change the size, and re-index
            // every document afterwards (POST /api/reindex-all) to embed them again.
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_chunks_embedding;
                UPDATE chunks SET embedding = NULL;
                ALTER TABLE chunks ALTER COLUMN embedding TYPE vector(1024);
                CREATE INDEX ix_chunks_embedding ON chunks USING hnsw (embedding vector_cosine_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Vectors from the old model can't be converted (or compared); clear them, change the size, and re-index
            // every document afterwards (POST /api/reindex-all) to embed them again.
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS ix_chunks_embedding;
                UPDATE chunks SET embedding = NULL;
                ALTER TABLE chunks ALTER COLUMN embedding TYPE vector(768);
                CREATE INDEX ix_chunks_embedding ON chunks USING hnsw (embedding vector_cosine_ops);
                """);
        }
    }
}
