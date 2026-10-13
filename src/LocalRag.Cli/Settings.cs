using LocalRag.Ai;
using LocalRag.Store;

namespace LocalRag.Cli;

/// <summary>Command-line options, resolved in this order: flag, environment variable, the index's own record, default.</summary>
internal sealed record Settings(string DatabasePath, AiOptions Ai, RagOptions Rag)
{
    public const string DefaultFolderName = ".localrag";

    public static string DefaultDatabasePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultFolderName, "index.db");

    public static Settings Resolve(string? db, string? provider, string? url, string? model, string? embedModel, int? sources,
        Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;

        string databasePath = Path.GetFullPath(First(db, env("LOCALRAG_DB")) ?? DefaultDatabasePath);

        var aiProvider = ParseProvider(First(provider, env("LOCALRAG_PROVIDER")));
        string? baseUrl = First(url, env("LOCALRAG_URL"), aiProvider == AiProvider.Ollama ? env("OLLAMA_HOST") : null);
        if (baseUrl is not null && !baseUrl.Contains("://", StringComparison.Ordinal)) baseUrl = "http://" + baseUrl;

        // Without an explicit choice, keep using the embedding model the index was built with.
        string embedding = First(embedModel, env("LOCALRAG_EMBED_MODEL")) ?? StoredEmbeddingModel(databasePath) ?? AiOptions.DefaultEmbeddingModel;

        var ai = new AiOptions
        {
            Provider = aiProvider,
            BaseUrl = baseUrl,
            ChatModel = First(model, env("LOCALRAG_MODEL")) ?? AiOptions.DefaultChatModel,
            EmbeddingModel = embedding,
            ApiKey = env("LOCALRAG_API_KEY")
        };
        var rag = new RagOptions { Sources = Math.Clamp(sources ?? 6, 1, 20) };
        return new Settings(databasePath, ai, rag);
    }

    public static AiProvider ParseProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "ollama" => AiProvider.Ollama,
        "openai" or "openai-compatible" or "lmstudio" or "lm-studio" or "llamacpp" or "llama.cpp" or "vllm" => AiProvider.OpenAICompatible,
        _ => throw new ArgumentException($"Unknown provider “{value}”. Use ollama or openai (any OpenAI-compatible server).")
    };

    private static string? StoredEmbeddingModel(string databasePath)
    {
        if (!File.Exists(databasePath)) return null;
        try
        {
            using var store = new VectorStore(databasePath);
            return store.EmbeddingModel;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    public (RagEngine Engine, IModelCatalog Catalog) CreateEngine()
    {
        var (embedder, chat, catalog) = AiClients.Create(Ai);
        return (new RagEngine(new VectorStore(DatabasePath), embedder, chat, Rag), catalog);
    }
}
