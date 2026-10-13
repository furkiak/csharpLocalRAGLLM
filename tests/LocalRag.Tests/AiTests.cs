using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using LocalRag.Ai;

namespace LocalRag.Tests;

public class ThinkFilterTests
{
    [Theory]
    [InlineData(new[] { "Plain ", "answer" }, "Plain answer")]
    [InlineData(new[] { "<think>hidden</think>\n\nVisible" }, "Visible")]
    [InlineData(new[] { "<thi", "nk>reason", "ing</th", "ink>Done", "." }, "Done.")]
    [InlineData(new[] { "<thinking>never closed" }, "")]
    [InlineData(new[] { "a < b and ", "x<y" }, "a < b and x<y")]
    [InlineData(new[] { "Answer <thi" }, "Answer <thi")]
    public void Reasoning_is_removed_even_when_tags_are_split(string[] tokens, string expected)
    {
        var filter = new ThinkFilter();
        string output = string.Concat(tokens.Select(filter.Push)) + filter.Flush();
        Assert.Equal(expected, output);
    }
}

public class OllamaClientTests
{
    private static (OllamaClient Client, FakeHandler Handler) Create(AiOptions? options = null)
    {
        var handler = new FakeHandler();
        return (new OllamaClient(new HttpClient(handler), options ?? new AiOptions()), handler);
    }

    [Fact]
    public async Task Embeddings_are_requested_in_batches()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"embeddings":[[0.1,0.2]]}""");

        var vectors = await client.EmbedAsync(["hello"]);

        Assert.Equal(new[] { 0.1f, 0.2f }, vectors[0]);
        Assert.Equal("http://localhost:11434/api/embed", handler.Requests[0].Url);
        Assert.Contains("\"model\":\"bge-m3\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Chat_streams_ndjson_and_drops_reasoning()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """
            {"message":{"role":"assistant","content":"<think>let me"},"done":false}
            {"message":{"role":"assistant","content":" think</think>Bonjour"},"done":false}
            {"message":{"role":"assistant","content":" !"},"done":false}
            {"message":{"role":"assistant","content":""},"done":true}
            """, "application/x-ndjson");

        var tokens = new List<string>();
        await foreach (string t in client.StreamAsync([ChatMessage.User("Salut")])) tokens.Add(t);

        Assert.Equal("Bonjour !", string.Concat(tokens));
        Assert.Contains("\"stream\":true", handler.Requests[0].Body);
        Assert.Contains("\"think\":false", handler.Requests[0].Body);
    }

    [Fact]
    public async Task A_missing_model_tells_how_to_pull_it()
    {
        var (client, handler) = Create(new AiOptions { ChatModel = "llama9" });
        handler.Respond(HttpStatusCode.NotFound, """{"error":"model \"llama9\" not found, try pulling it first"}""");

        var ex = await Assert.ThrowsAsync<AiException>(async () => { await foreach (var _ in client.StreamAsync([ChatMessage.User("hi")])) { } });
        Assert.Contains("ollama pull llama9", ex.Message);
    }

    [Fact]
    public async Task An_unreachable_server_tells_how_to_start_ollama()
    {
        var (client, handler) = Create();
        handler.Throw(new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)));

        var ex = await Assert.ThrowsAsync<AiException>(() => client.EmbedAsync(["x"]));
        Assert.Contains("ollama serve", ex.Message);
    }

    [Fact]
    public async Task Installed_models_are_listed_and_latest_tags_match()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"models":[{"name":"qwen3:4b"},{"name":"bge-m3:latest"}]}""");

        var models = await client.ListModelsAsync();

        Assert.Equal(new[] { "bge-m3:latest", "qwen3:4b" }, models);
        Assert.True(OllamaClient.HasModel(models, "bge-m3"));
        Assert.True(OllamaClient.HasModel(models, "qwen3:4b"));
        Assert.False(OllamaClient.HasModel(models, "qwen3:8b"));
    }
}

public class OpenAiCompatibleClientTests
{
    private static (OpenAiCompatibleClient Client, FakeHandler Handler) Create() =>
        Create(new AiOptions { Provider = AiProvider.OpenAICompatible, ApiKey = "sk-local", ChatModel = "local-model", EmbeddingModel = "nomic" });

    private static (OpenAiCompatibleClient, FakeHandler) Create(AiOptions options)
    {
        var handler = new FakeHandler();
        return (new OpenAiCompatibleClient(new HttpClient(handler), options), handler);
    }

    [Fact]
    public async Task Embeddings_are_ordered_by_index()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """{"data":[{"index":1,"embedding":[2]},{"index":0,"embedding":[1]}]}""");

        var vectors = await client.EmbedAsync(["a", "b"]);

        Assert.Equal(1f, vectors[0][0]);
        Assert.Equal(2f, vectors[1][0]);
        Assert.Equal("http://localhost:1234/v1/embeddings", handler.Requests[0].Url);
    }

    [Fact]
    public async Task Chat_streams_server_sent_events()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.OK, """
            data: {"choices":[{"delta":{"role":"assistant"}}]}

            data: {"choices":[{"delta":{"content":"Hel"}}]}

            data: {"choices":[{"delta":{"content":"lo"}}]}

            data: [DONE]

            """, "text/event-stream");

        var tokens = new List<string>();
        await foreach (string t in client.StreamAsync([ChatMessage.User("hi")])) tokens.Add(t);

        Assert.Equal("Hello", string.Concat(tokens));
    }

    [Fact]
    public async Task Errors_carry_the_server_message()
    {
        var (client, handler) = Create();
        handler.Respond(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API key"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => client.EmbedAsync(["x"]));
        Assert.Contains("API key", ex.Message);
        Assert.Contains("Invalid API key", ex.Message);
    }

    [Fact]
    public void The_default_address_depends_on_the_provider()
    {
        Assert.Equal("http://localhost:11434", new AiOptions().EffectiveBaseUrl);
        Assert.Equal("http://localhost:1234/v1", new AiOptions { Provider = AiProvider.OpenAICompatible }.EffectiveBaseUrl);
        Assert.Equal("http://gpu-box:8080/v1", new AiOptions { Provider = AiProvider.OpenAICompatible, BaseUrl = " http://gpu-box:8080/v1/ " }.EffectiveBaseUrl);
    }
}
