using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Ragamuffin.Layout;

public sealed record LayoutTable(
    [property: JsonPropertyName("html")] string Html,
    [property: JsonPropertyName("cells")] List<double[]> Cells);

/// <summary>One region of a page in reading order; <see cref="Bbox"/> is x1, y1, x2, y2 in upright-image pixels.</summary>
public sealed record LayoutRegion(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("bbox")] double[] Bbox,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("table")] LayoutTable? Table);

public sealed record LayoutResult(
    [property: JsonPropertyName("angle")] int Angle,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("upright_png")] string? UprightPng,
    [property: JsonPropertyName("regions")] List<LayoutRegion> Regions);

public sealed record LayoutAvailability(bool Enabled, bool Available, string? Reason);

public sealed class LayoutException(string message) : Exception(message);

/// <summary>Talks to the layout service (layout-service/app.py).</summary>
public sealed class LayoutClient(HttpClient http, IOptions<RagOptions> options, ILogger<LayoutClient> log)
{
    LayoutOptions Opt => options.Value.Layout;

    static LayoutAvailability? _cached;
    static DateTimeOffset _cachedAt;

    sealed record Health([property: JsonPropertyName("status")] string Status);

    public async Task<LayoutAvailability> GetAvailabilityAsync(CancellationToken ct)
    {
        if (!Opt.Enabled)
            return new(false, false, "Layout analysis is disabled in settings (Rag:Layout:Enabled).");
        if (_cached is { } c && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(15))
            return c;

        LayoutAvailability result;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var health = await http.GetFromJsonAsync<Health>("health", cts.Token);
            result = health?.Status == "ok"
                ? new(true, true, null)
                : new(true, false, "Layout service is still loading its models.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            result = new(true, false, $"Layout service unreachable at {Opt.Url} ({ex.Message}). Start it with: docker compose up -d layout");
        }
        _cached = result;
        _cachedAt = DateTimeOffset.UtcNow;
        return result;
    }

    public async Task<LayoutResult> AnalyzeAsync(byte[] png, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Opt.PageTimeoutSeconds));
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(png);
        image.Headers.ContentType = new("image/png");
        form.Add(image, "image", "page.png");
        try
        {
            using var resp = await http.PostAsync("analyze", form, timeout.Token);
            if (!resp.IsSuccessStatusCode)
                throw new LayoutException($"Layout service returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(timeout.Token)}");
            return await resp.Content.ReadFromJsonAsync<LayoutResult>(timeout.Token)
                ?? throw new LayoutException("Empty response from the layout service.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Layout analysis of a page timed out after {Seconds}s", Opt.PageTimeoutSeconds);
            throw new LayoutException($"Layout analysis took longer than {Opt.PageTimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new LayoutException($"Layout service unreachable: {ex.Message}");
        }
    }
}
