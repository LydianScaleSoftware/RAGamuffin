using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PDFtoImage;
using RagServer.Ingestion;
using SkiaSharp;

namespace RagServer.Ocr;

public sealed record OcrAvailability(bool Enabled, string Model, bool Available, string? Reason);

public sealed class OcrException(string message) : Exception(message);

/// <summary>Renders PDF pages and transcribes them with a vision model on the Ollama server.</summary>
public sealed class OcrEngine(HttpClient http, IOptions<RagOptions> options, ILogger<OcrEngine> log)
{
    OcrOptions Opt => options.Value.Ocr;

    static OcrAvailability? _cached;
    static DateTimeOffset _cachedAt;

    sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
    sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
    sealed record TagsResponse([property: JsonPropertyName("models")] List<TagModel> Models);
    sealed record TagModel([property: JsonPropertyName("name")] string Name);

    public static int PageCount(byte[] pdf) => Conversion.GetPageCount(pdf);

    /// <summary>Whether OCR is enabled and the configured model is installed on the Ollama server (cached briefly).</summary>
    public async Task<OcrAvailability> GetAvailabilityAsync(CancellationToken ct)
    {
        if (!Opt.Enabled)
            return new(false, Opt.Model, false, "OCR is disabled in settings (Rag:Ocr:Enabled).");
        if (_cached is { } c && c.Model == Opt.Model && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(15))
            return c;

        OcrAvailability result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var tags = await http.GetFromJsonAsync<TagsResponse>("api/tags", cts.Token);
            var installed = tags?.Models.Any(m => SameModel(m.Name, Opt.Model)) == true;
            result = new(true, Opt.Model, installed,
                installed ? null : $"Model \"{Opt.Model}\" is not installed on the Ollama server (ollama pull {Opt.Model}).");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            result = new(true, Opt.Model, false, $"Ollama server unreachable: {ex.Message}");
        }
        _cached = result;
        _cachedAt = DateTimeOffset.UtcNow;
        return result;
    }

    public static bool SameModel(string installed, string configured) =>
        string.Equals(installed, configured, StringComparison.OrdinalIgnoreCase) ||
        (!configured.Contains(':') && string.Equals(installed, configured + ":latest", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Renders one page (0-based index) to PNG, trimmed to its content and scaled so its longer side fits
    /// <see cref="OcrOptions.MaxImageEdge"/>.
    /// </summary>
    public byte[] RenderPage(byte[] pdf, int pageIndex)
    {
        using var full = Conversion.ToImage(pdf, page: pageIndex, options: new RenderOptions(Dpi: Opt.Dpi, WithAnnotations: true));

        // glm-ocr skips text (e.g. a title) when it fills only a small part of a mostly blank page,
        // so crop away blank margins first.
        using var trimmed = new SKBitmap();
        var bitmap = full;
        if (ContentBounds(full) is { } bounds && full.ExtractSubset(trimmed, bounds))
            bitmap = trimmed;

        var longest = Math.Max(bitmap.Width, bitmap.Height);
        SKBitmap? scaled = null;
        if (longest > Opt.MaxImageEdge)
        {
            var factor = Opt.MaxImageEdge / (double)longest;
            scaled = bitmap.Resize(new SKImageInfo((int)(bitmap.Width * factor), (int)(bitmap.Height * factor)),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            bitmap = scaled;
        }
        try
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>
    /// Bounding box of non-blank pixels plus a margin, or null when the page is blank or already mostly content.
    /// Samples every other pixel, which is plenty at OCR resolutions.
    /// </summary>
    public static SKRectI? ContentBounds(SKBitmap bitmap, byte threshold = 200, int padding = 24)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < bitmap.Height; y += 2)
        for (var x = 0; x < bitmap.Width; x += 2)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Alpha < 16 || (c.Red + c.Green + c.Blue) / 3 >= threshold) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        if (maxX < 0) return null;

        var box = new SKRectI(
            Math.Max(0, minX - padding), Math.Max(0, minY - padding),
            Math.Min(bitmap.Width, maxX + padding + 1), Math.Min(bitmap.Height, maxY + padding + 1));
        var keptShare = box.Width * (double)box.Height / (bitmap.Width * (double)bitmap.Height);
        return keptShare > 0.9 ? null : box;
    }

    /// <summary>Sends one page image to the vision model and returns its transcription.</summary>
    public async Task<string> TranscribeAsync(byte[] png, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Opt.Model,
            ["stream"] = false,
            ["keep_alive"] = Opt.KeepAlive,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = Opt.Prompt,
                ["images"] = new JsonArray(Convert.ToBase64String(png)),
            }),
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = Opt.NumCtx },
        };
        if (Opt.Think is { } think) body["think"] = think;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Opt.PageTimeoutSeconds));
        try
        {
            using var resp = await http.PostAsJsonAsync("api/chat", body, timeout.Token);
            if (!resp.IsSuccessStatusCode)
                throw new OcrException($"Ollama returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(timeout.Token)}");
            var result = await resp.Content.ReadFromJsonAsync<ChatResponse>(timeout.Token);
            return CleanTranscription(result?.Message?.Content);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("OCR of a page timed out after {Seconds}s", Opt.PageTimeoutSeconds);
            throw new OcrException($"A page took longer than {Opt.PageTimeoutSeconds}s to OCR.");
        }
        catch (HttpRequestException ex)
        {
            throw new OcrException($"Ollama server unreachable: {ex.Message}");
        }
    }

    /// <summary>Strips a code fence wrapped around the whole answer and normalizes line endings.</summary>
    public static string CleanTranscription(string? text)
    {
        var t = TextSanitizer.Clean((text ?? "").Replace("\r\n", "\n").Replace('\r', '\n')).Trim();
        if (t.StartsWith("```"))
        {
            var firstNewline = t.IndexOf('\n');
            var closing = t.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && closing > firstNewline)
                t = t[(firstNewline + 1)..closing].Trim();
        }
        return string.Join('\n', t.Split('\n').Select(l => l.TrimEnd()));
    }
}

/// <summary>Lets the API cancel an OCR run that is queued or in progress.</summary>
public sealed class OcrCancellation
{
    readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    readonly ConcurrentDictionary<Guid, bool> _cancelledBeforeStart = new();

    public CancellationTokenSource Start(Guid documentId, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        if (_cancelledBeforeStart.TryRemove(documentId, out _)) cts.Cancel();
        _running[documentId] = cts;
        return cts;
    }

    public void Finish(Guid documentId)
    {
        if (_running.TryRemove(documentId, out var cts)) cts.Dispose();
    }

    public void Cancel(Guid documentId)
    {
        if (_running.TryGetValue(documentId, out var cts)) cts.Cancel();
        else _cancelledBeforeStart[documentId] = true;
    }
}
