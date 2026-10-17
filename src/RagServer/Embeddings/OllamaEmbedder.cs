using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RagServer.Embeddings;

/// <summary>Calls Ollama's /api/embed endpoint in batches.</summary>
public sealed class OllamaEmbedder(HttpClient http, IOptions<RagOptions> options, ILogger<OllamaEmbedder> log)
{
    readonly RagOptions _opt = options.Value;

    sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("truncate")] bool Truncate = true);

    sealed record EmbedResponse([property: JsonPropertyName("embeddings")] float[][] Embeddings);

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default) =>
        (await EmbedBatchAsync([_opt.QueryPrefix + query], ct))[0];

    public async Task<List<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var results = new List<float[]>(texts.Count);
        foreach (var batch in texts.Chunk(Math.Max(1, _opt.EmbeddingBatchSize)))
            results.AddRange(await EmbedBatchAsync(batch.Select(t => _opt.DocumentPrefix + t).ToList(), ct));
        return results;
    }

    async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> input, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await http.PostAsJsonAsync("api/embed", new EmbedRequest(_opt.EmbeddingModel, input), ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"Ollama /api/embed returned {(int)resp.StatusCode}: {body}");
                }
                var result = await resp.Content.ReadFromJsonAsync<EmbedResponse>(ct)
                    ?? throw new InvalidOperationException("Empty response from Ollama");
                if (result.Embeddings.Length != input.Count)
                    throw new InvalidOperationException($"Ollama returned {result.Embeddings.Length} embeddings for {input.Count} inputs");
                return result.Embeddings;
            }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Embedding attempt {Attempt} failed; retrying", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }
}
