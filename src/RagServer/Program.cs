using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RagServer;
using RagServer.Api;
using RagServer.Collections;
using RagServer.Data;
using RagServer.Embeddings;
using RagServer.Ingestion;
using RagServer.Layout;
using RagServer.Mcp;
using RagServer.Ocr;
using RagServer.Search;

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

builder.Services.AddSingleton<FileStore>();
builder.Services.AddSingleton<IngestQueue>();
builder.Services.AddScoped<IngestService>();
builder.Services.AddScoped<CollectionService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddHostedService<IngestWorker>();

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = rag.MaxUploadBytes * 10);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = rag.MaxUploadBytes * 10);

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
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
