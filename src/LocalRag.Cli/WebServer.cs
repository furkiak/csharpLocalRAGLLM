using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LocalRag.Ai;
using LocalRag.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace LocalRag.Cli;

/// <summary>
/// The local web app. It listens on 127.0.0.1 only, refuses other Host headers (DNS rebinding) and requires a custom
/// header on every API call, which a cross-site page cannot send without a CORS preflight this server never grants.
/// </summary>
internal static class WebServer
{
    public const string ApiHeader = "X-LocalRag";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(Settings settings, int port, bool openBrowser, CancellationToken ct)
    {
        var (engine, catalog) = settings.CreateEngine();
        using var store = engine.Store;
        string uploads = Path.Combine(Path.GetDirectoryName(settings.DatabasePath)!, "uploads");
        var app = Build(engine, catalog, settings.Ai, uploads, port);

        string url = $"http://127.0.0.1:{port}";
        await app.StartAsync(ct);
        AnsiConsole.MarkupLine($"[green]✓[/] localrag is running at [link]{url}[/]  [grey](Ctrl+C to stop)[/]");
        AnsiConsole.MarkupLine($"[grey]{store.GetStats().Documents} documents · chat {Markup.Escape(settings.Ai.ChatModel)} · embeddings {Markup.Escape(settings.Ai.EmbeddingModel)} · {settings.Ai.Provider} at {Markup.Escape(settings.Ai.EffectiveBaseUrl)}[/]");
        if (openBrowser)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { /* no browser available: the URL is printed above */ }
        }

        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
        await app.StopAsync(CancellationToken.None);
    }

    public static WebApplication Build(RagEngine engine, IModelCatalog catalog, AiOptions ai, string uploadsFolder, int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 512L * 1024 * 1024);
        builder.Services.Configure<FormOptions>(f => f.MultipartBodyLengthLimit = 512L * 1024 * 1024);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            string host = ctx.Request.Host.Host;
            if (host is not ("localhost" or "127.0.0.1" or "[::1]" or "::1"))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("localrag only answers on localhost.");
                return;
            }
            if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers[ApiHeader] != "1")
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = $"API calls need the {ApiHeader} header." });
                return;
            }
            var headers = ctx.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; connect-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'; form-action 'none'; base-uri 'none'";
            await next();
        });

        app.MapGet("/", () => Asset("index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", () => Asset("app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/app.css", () => Asset("app.css", "text/css; charset=utf-8"));
        app.MapGet("/icon.svg", () => Asset("icon.svg", "image/svg+xml"));

        // ------------------------------------------------------------------ status and library
        app.MapGet("/api/status", async (CancellationToken ct) =>
        {
            var stats = engine.Store.GetStats();
            object server;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var models = await catalog.ListModelsAsync(timeout.Token);
                bool chatReady = ai.Provider != AiProvider.Ollama || OllamaClient.HasModel(models, ai.ChatModel);
                bool embedReady = ai.Provider != AiProvider.Ollama || OllamaClient.HasModel(models, ai.EmbeddingModel);
                server = new { ok = chatReady && embedReady, chatReady, embedReady, error = (string?)null };
            }
            catch (Exception ex) when (ex is AiException or OperationCanceledException)
            {
                server = new { ok = false, chatReady = false, embedReady = false, error = ex is AiException ? ex.Message : "The AI server did not answer." };
            }
            return Results.Json(new
            {
                documents = stats.Documents,
                chunks = stats.Chunks,
                databaseBytes = stats.DatabaseBytes,
                provider = ai.Provider.ToString(),
                url = ai.EffectiveBaseUrl,
                chatModel = ai.ChatModel,
                embeddingModel = ai.EmbeddingModel,
                indexModel = stats.EmbeddingModel,
                server
            }, Json);
        });

        app.MapGet("/api/documents", () => Results.Json(engine.Store.GetDocuments().Select(d => new
        {
            d.Path, d.Title, d.Pages, d.Chunks, d.Size, indexed = d.IndexedUtc, type = Path.GetExtension(d.Path).TrimStart('.').ToLowerInvariant()
        }), Json));

        app.MapDelete("/api/documents", (string path) =>
            engine.Store.Remove(path) ? Results.NoContent() : Results.NotFound(new { error = "That document is not in the index." }));

        app.MapPost("/api/index", async (HttpContext ctx, IndexRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Path)) return Results.BadRequest(new { error = "Enter the path of a file or folder." });
            string path = Environment.ExpandEnvironmentVariables(body.Path.Trim().Trim('"'));
            if (!File.Exists(path) && !Directory.Exists(path)) return Results.BadRequest(new { error = $"“{path}” does not exist on this computer." });

            await StartEvents(ctx);
            // Reported synchronously from the indexing loop, so events stay in order and none arrives after "done".
            var progress = new InlineProgress<IndexProgress>(p =>
                Send(ctx, "progress", new { p.Done, p.Total, file = Path.GetFileName(p.Path), outcome = p.Outcome.ToString(), p.Error }).GetAwaiter().GetResult());
            await RunStreaming(ctx, async () =>
            {
                var report = await engine.IndexAsync(path, progress, ctx.RequestAborted);
                await Send(ctx, "done", ReportJson(report));
            });
            return Results.Empty;
        });

        app.MapPost("/api/upload", async (HttpContext ctx) =>
        {
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Send the files as multipart/form-data." });
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            Directory.CreateDirectory(uploadsFolder);

            var saved = new List<string>();
            var rejected = new List<string>();
            foreach (var file in form.Files)
            {
                string name = SafeFileName(file.FileName);
                if (name.Length == 0 || !DocumentReader.IsSupported(name)) { rejected.Add(file.FileName); continue; }
                string target = Path.Combine(uploadsFolder, name);
                await using (var stream = File.Create(target)) await file.CopyToAsync(stream, ctx.RequestAborted);
                saved.Add(target);
            }

            int added = 0, updated = 0, unchanged = 0, failed = 0, chunks = 0;
            var errors = new List<object>();
            foreach (string file in saved)
            {
                var r = await engine.IndexAsync(file, null, ctx.RequestAborted);
                added += r.Added; updated += r.Updated; unchanged += r.Unchanged; failed += r.Failed; chunks += r.Chunks;
                errors.AddRange(r.Errors.Select(e => new { file = Path.GetFileName(e.Path), e.Error }));
            }
            return Results.Json(new { added, updated, unchanged, failed, chunks, errors, rejected }, Json);
        });

        // ------------------------------------------------------------------ search and answers
        app.MapPost("/api/search", async (SearchRequest body, CancellationToken ct) =>
        {
            var hits = await engine.SearchAsync(body.Query ?? "", Math.Clamp(body.Limit ?? 8, 1, 50), ct);
            return Results.Json(hits.Select(h => new { h.Title, h.Path, h.Page, h.Text, h.Score, h.VectorRank, h.KeywordRank }), Json);
        });

        app.MapPost("/api/ask", async (HttpContext ctx, AskRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Question)) return Results.BadRequest(new { error = "Ask a question." });
            var history = (body.History ?? [])
                .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrEmpty(m.Content))
                .Select(m => new ChatMessage(m.Role!, m.Content!))
                .ToList();

            await StartEvents(ctx);
            await RunStreaming(ctx, async () =>
            {
                var answer = await engine.AskAsync(body.Question, history, ctx.RequestAborted);
                await Send(ctx, "sources", answer.Sources.Select(s => new { s.Number, s.Title, s.Label, s.Page, s.Path, s.Text }));
                await foreach (string token in answer.Tokens.WithCancellation(ctx.RequestAborted))
                    await Send(ctx, "token", token);
                await Send(ctx, "done", new { });
            });
            return Results.Empty;
        });

        return app;
    }

    // ------------------------------------------------------------------ helpers

    public sealed record IndexRequest(string? Path);
    public sealed record SearchRequest(string? Query, int? Limit);
    public sealed record AskRequest(string? Question, List<HistoryItem>? History);
    public sealed record HistoryItem(string? Role, string? Content);

    private static object ReportJson(IndexReport r) => new
    {
        r.Added, r.Updated, r.Unchanged, r.Removed, r.Failed, r.Empty, r.Chunks, seconds = Math.Round(r.Elapsed.TotalSeconds, 1),
        errors = r.Errors.Select(e => new { file = Path.GetFileName(e.Path), e.Error })
    };

    private static async Task StartEvents(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    private static async Task RunStreaming(HttpContext ctx, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // the browser stopped the request
        }
        catch (Exception ex) when (ex is AiException or InvalidOperationException or DocumentReadException or IOException or ArgumentException)
        {
            await Send(ctx, "error", new { message = ex.Message });
        }
    }

    /// <summary>Writes one server-sent event. Each request writes its events one after another.</summary>
    private static async Task Send(HttpContext ctx, string name, object data)
    {
        if (ctx.RequestAborted.IsCancellationRequested) return;
        string payload = $"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n";
        try
        {
            await ctx.Response.WriteAsync(payload, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
        catch (OperationCanceledException) { }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    internal static string SafeFileName(string name)
    {
        string file = Path.GetFileName(name.Replace('\\', '/').Split('/').Last());
        foreach (char c in Path.GetInvalidFileNameChars()) file = file.Replace(c, '_');
        return file.Trim().TrimStart('.');
    }

    private static IResult Asset(string name, string contentType)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/" + name);
        if (stream is null) return Results.NotFound();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Results.Bytes(memory.ToArray(), contentType);
    }
}
