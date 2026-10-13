namespace LocalRag.Ai;

/// <summary>Turns texts into embedding vectors.</summary>
public interface IEmbedder
{
    string Model { get; }

    /// <summary>One vector per input text, in the same order.</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>A chat model that streams its answer.</summary>
public interface IChatModel
{
    string Model { get; }

    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
}

/// <summary>Reports whether the AI server is reachable and which models it has.</summary>
public interface IModelCatalog
{
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);
    public static ChatMessage User(string content) => new("user", content);
    public static ChatMessage Assistant(string content) => new("assistant", content);
}

public enum AiProvider
{
    /// <summary>Ollama's native API (http://localhost:11434).</summary>
    Ollama,

    /// <summary>Any server with OpenAI-style /v1/embeddings and /v1/chat/completions: LM Studio, llama.cpp, vLLM, LocalAI, Jan…</summary>
    OpenAICompatible
}

/// <summary>Where the models run and which ones to use.</summary>
public sealed record AiOptions
{
    public const string DefaultOllamaUrl = "http://localhost:11434";
    public const string DefaultOpenAiUrl = "http://localhost:1234/v1";
    public const string DefaultChatModel = "qwen3:4b";
    public const string DefaultEmbeddingModel = "bge-m3";

    public AiProvider Provider { get; init; } = AiProvider.Ollama;
    public string? BaseUrl { get; init; }
    public string ChatModel { get; init; } = DefaultChatModel;
    public string EmbeddingModel { get; init; } = DefaultEmbeddingModel;
    public string? ApiKey { get; init; }

    /// <summary>Context window requested from Ollama (tokens).</summary>
    public int ContextTokens { get; init; } = 8192;

    public double Temperature { get; init; } = 0.2;

    public string EffectiveBaseUrl =>
        (string.IsNullOrWhiteSpace(BaseUrl) ? Provider == AiProvider.Ollama ? DefaultOllamaUrl : DefaultOpenAiUrl : BaseUrl.Trim()).TrimEnd('/');
}

/// <summary>A failure talking to the AI server, with a message that says what to do about it.</summary>
public sealed class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Builds the clients for the configured provider.</summary>
public static class AiClients
{
    public static (IEmbedder Embedder, IChatModel Chat, IModelCatalog Catalog) Create(AiOptions options, HttpClient? http = null)
    {
        http ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return options.Provider switch
        {
            AiProvider.OpenAICompatible => Triple(new OpenAiCompatibleClient(http, options)),
            _ => Triple(new OllamaClient(http, options))
        };

        static (IEmbedder, IChatModel, IModelCatalog) Triple<T>(T client) where T : IEmbedder, IChatModel, IModelCatalog => (client, client, client);
    }
}
