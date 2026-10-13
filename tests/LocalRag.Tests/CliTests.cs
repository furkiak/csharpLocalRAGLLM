using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LocalRag.Ai;
using LocalRag.Cli;
using LocalRag.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace LocalRag.Tests;

public class SettingsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Flags_win_over_environment_variables_and_defaults()
    {
        using var tmp = new TempFolder();
        var env = Env(("LOCALRAG_MODEL", "env-model"), ("LOCALRAG_DB", Path.Combine(tmp.Path, "env.db")), ("LOCALRAG_API_KEY", "secret"));

        var fromEnv = Settings.Resolve(null, null, null, null, null, null, env);
        var fromFlags = Settings.Resolve(Path.Combine(tmp.Path, "flag.db"), null, null, "flag-model", null, 3, env);

        Assert.Equal("env-model", fromEnv.Ai.ChatModel);
        Assert.EndsWith("env.db", fromEnv.DatabasePath);
        Assert.Equal("secret", fromEnv.Ai.ApiKey);
        Assert.Equal("flag-model", fromFlags.Ai.ChatModel);
        Assert.EndsWith("flag.db", fromFlags.DatabasePath);
        Assert.Equal(3, fromFlags.Rag.Sources);
        Assert.Equal(AiOptions.DefaultEmbeddingModel, fromFlags.Ai.EmbeddingModel);
    }

    [Fact]
    public void Ollama_host_without_a_scheme_is_accepted()
    {
        var s = Settings.Resolve("x.db", null, null, null, null, null, Env(("OLLAMA_HOST", "10.0.0.5:11434")));
        Assert.Equal("http://10.0.0.5:11434", s.Ai.EffectiveBaseUrl);
    }

    [Theory]
    [InlineData("ollama", AiProvider.Ollama)]
    [InlineData("OpenAI", AiProvider.OpenAICompatible)]
    [InlineData("lmstudio", AiProvider.OpenAICompatible)]
    [InlineData(null, AiProvider.Ollama)]
    public void Providers_have_friendly_names(string? name, AiProvider expected) =>
        Assert.Equal(expected, Settings.ParseProvider(name));

    [Fact]
    public void An_unknown_provider_is_refused() =>
        Assert.Throws<ArgumentException>(() => Settings.ParseProvider("watson"));

    [Fact]
    public void The_index_keeps_its_embedding_model_unless_another_is_chosen()
    {
        using var tmp = new TempFolder();
        string db = Path.Combine(tmp.Path, "index.db");
        using (var store = new VectorStore(db)) store.EnsureEmbeddingModel("nomic-embed-text", 768);

        Assert.Equal("nomic-embed-text", Settings.Resolve(db, null, null, null, null, null, Env()).Ai.EmbeddingModel);
        Assert.Equal("bge-m3", Settings.Resolve(db, null, null, null, "bge-m3", null, Env()).Ai.EmbeddingModel);
    }
}

public sealed class WebServerTests : IAsyncLifetime
{
    private readonly TempFolder _tmp = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private VectorStore _store = null!;
    private readonly FakeChat _chat = new("Badges are issued at reception ", "[1].");

    private sealed class Catalog : IModelCatalog
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([AiOptions.DefaultChatModel, AiOptions.DefaultEmbeddingModel + ":latest"]);
    }

    public async Task InitializeAsync()
    {
        _store = new VectorStore(Path.Combine(_tmp.Path, "index.db"));
        var engine = new RagEngine(_store, new FakeEmbedder(), _chat);
        _app = WebServer.Build(engine, new Catalog(), new AiOptions(), Path.Combine(_tmp.Path, "uploads"), port: 0);
        await _app.StartAsync();
        string url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(url) };
        _http.DefaultRequestHeaders.Add(WebServer.ApiHeader, "1");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _store.Dispose();
        _tmp.Dispose();
    }

    [Fact]
    public async Task Api_calls_without_the_custom_header_are_refused()
    {
        using var bare = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.Equal(HttpStatusCode.Forbidden, (await bare.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Foreign_host_names_are_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task The_page_and_its_assets_are_served_with_a_strict_policy()
    {
        var page = await _http.GetAsync("/");
        string html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<title>localrag</title>", html);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/app.css")).StatusCode);
    }

    [Fact]
    public async Task Uploads_are_indexed_and_answers_stream_with_sources()
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Visitor badges are issued at the reception desk on the ground floor."));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(file, "files", "../../visitors.md");
        form.Add(new ByteArrayContent([1, 2, 3]), "files", "photo.png");

        var upload = await _http.PostAsync("/api/upload", form);
        string uploadJson = await upload.Content.ReadAsStringAsync();
        Assert.Contains("\"added\":1", uploadJson);
        Assert.Contains("photo.png", uploadJson);
        Assert.True(File.Exists(Path.Combine(_tmp.Path, "uploads", "visitors.md")), "the upload must stay inside the uploads folder");

        var ask = await _http.PostAsync("/api/ask", new StringContent("""{"question":"Where do visitors get a badge?"}""", Encoding.UTF8, "application/json"));
        string events = await ask.Content.ReadAsStringAsync();

        Assert.Equal("text/event-stream", ask.Content.Headers.ContentType!.MediaType);
        Assert.Contains("event: sources", events);
        Assert.Contains("visitors", events);
        Assert.Contains("event: token\ndata: \"Badges are issued at reception \"", events);
        Assert.EndsWith("event: done\ndata: {}\n\n", events);
    }

    [Fact]
    public async Task A_folder_is_indexed_with_progress_events_and_can_be_removed()
    {
        string docs = Path.Combine(_tmp.Path, "docs");
        _tmp.File("docs/a.md", "Alpha document about printers.");
        _tmp.File("docs/b.txt", "Beta document about parking.");

        var response = await _http.PostAsync("/api/index", new StringContent($$"""{"path":{{System.Text.Json.JsonSerializer.Serialize(docs)}}}""", Encoding.UTF8, "application/json"));
        string events = await response.Content.ReadAsStringAsync();
        Assert.Equal(2, events.Split("event: progress").Length - 1);
        Assert.Contains("event: done", events);
        Assert.Contains("\"added\":2", events);

        string list = await _http.GetStringAsync("/api/documents");
        Assert.Contains("printers", await (await _http.PostAsync("/api/search", new StringContent("""{"query":"printers"}""", Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync());
        Assert.Contains("a.md", list);

        var delete = await _http.DeleteAsync("/api/documents?path=" + Uri.EscapeDataString(Path.Combine(docs, "a.md")));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.DoesNotContain("a.md", await _http.GetStringAsync("/api/documents"));
    }

    [Fact]
    public async Task Bad_requests_get_clear_messages()
    {
        var missing = await _http.PostAsync("/api/index", new StringContent("""{"path":"Z:\\does\\not\\exist"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("does not exist", await missing.Content.ReadAsStringAsync());

        var empty = await _http.PostAsync("/api/ask", new StringContent("""{"question":"   "}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Status_reports_the_models_and_the_index()
    {
        string json = await _http.GetStringAsync("/api/status");
        Assert.Contains("\"ok\":true", json);
        Assert.Contains("\"chatModel\":\"qwen3:4b\"", json);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\boot.ini", "boot.ini")]
    [InlineData(".hidden.md", "hidden.md")]
    [InlineData("report:2026?.pdf", "report_2026_.pdf")]
    public void Uploaded_file_names_cannot_escape_the_folder(string name, string expected)
    {
        string safe = WebServer.SafeFileName(name);
        if (OperatingSystem.IsWindows()) Assert.Equal(expected, safe);
        else Assert.DoesNotContain("/", safe);
    }
}
