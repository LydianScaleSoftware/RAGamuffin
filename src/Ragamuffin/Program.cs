using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ragamuffin;
using Ragamuffin.Api;
using Ragamuffin.Collections;
using Ragamuffin.Data;
using Ragamuffin.Embeddings;
using Ragamuffin.Ingestion;
using Ragamuffin.Layout;
using Ragamuffin.Mcp;
using Ragamuffin.Ocr;
using Ragamuffin.Search;
using Ragamuffin.Stats;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.Section));
var rag = builder.Configuration.GetSection(RagOptions.Section).Get<RagOptions>() ?? new RagOptions();

builder.Services.AddDbContext<RagDbContext>(o => o
    .UseNpgsql(builder.Configuration.GetConnectionString("Rag"), npgsql => npgsql.UseVector())
    .UseSnakeCaseNamingConvention());

builder.Services.AddHttpClient<OllamaEmbedder>((sp, http) =>
{
    var opt = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    http.BaseAddress = new Uri(opt.OllamaUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddHttpClient<OcrEngine>((sp, http) =>
{
    var opt = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    http.BaseAddress = new Uri(opt.OllamaUrl.TrimEnd('/') + "/");
    http.Timeout = Timeout.InfiniteTimeSpan; // each page has its own timeout (Rag:Ocr:PageTimeoutSeconds)
});
builder.Services.AddSingleton<OcrCancellation>();
builder.Services.AddHttpClient<LayoutClient>((sp, http) =>
{
    var opt = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    http.BaseAddress = new Uri(opt.Layout.Url.TrimEnd('/') + "/");
    http.Timeout = Timeout.InfiniteTimeSpan; // each page has its own timeout (Rag:Layout:PageTimeoutSeconds)
});
builder.Services.AddTransient<PageAnalyzer>();
builder.Services.AddHttpClient<CollectionSuggester>((sp, http) =>
{
    var opt = sp.GetRequiredService<IOptions<RagOptions>>().Value;
    http.BaseAddress = new Uri(opt.OllamaUrl.TrimEnd('/') + "/");
    http.Timeout = Timeout.InfiniteTimeSpan; // the suggester applies its own timeout
});

builder.Services.AddSingleton<FileStore>();
builder.Services.AddSingleton<IngestQueue>();
builder.Services.AddScoped<IngestService>();
builder.Services.AddScoped<CollectionService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddScoped<LibraryGuide>();
builder.Services.AddHostedService<IngestWorker>();

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = rag.MaxUploadBytes * 10);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = rag.MaxUploadBytes * 10);

builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "ragamuffin", Title = "RAGamuffin", Version = "1.0.0" })
    .WithHttpTransport(o =>
    {
        o.Stateless = true;
        // Describe the library (collections, descriptions, keywords) to each client as it connects.
        o.ConfigureSessionOptions = async (http, server, ct) =>
            server.ServerInstructions = await http.RequestServices.GetRequiredService<LibraryGuide>().InstructionsAsync(ct);
    })
    .WithTools<RagTools>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
    await db.Database.MigrateAsync();
}

app.UseDefaultFiles();
// Revalidate the UI on every load (cheap via ETag) so a redeploy is picked up without a manual refresh.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapRagApi();
app.MapOcrApi();
app.MapMcp("/mcp");

app.Run();
