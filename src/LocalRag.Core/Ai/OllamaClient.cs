using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalRag.Ai;

/// <summary>Ollama's native API: /api/embed, /api/chat (streamed NDJSON) and /api/tags.</summary>
public sealed class OllamaClient(HttpClient http, AiOptions options) : IEmbedder, IChatModel, IModelCatalog
{
    private const int EmbedBatchSize = 16;
    private string Server => $"Ollama at {options.EffectiveBaseUrl}";

    string IEmbedder.Model => options.EmbeddingModel;
    string IChatModel.Model => options.ChatModel;

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        for (int start = 0; start < texts.Count; start += EmbedBatchSize)
        {
            var batch = texts.Skip(start).Take(EmbedBatchSize).ToArray();
            using var request = HttpAi.Post(options.EffectiveBaseUrl + "/api/embed", new { model = options.EmbeddingModel, input = batch, truncate = true }, options.ApiKey);
            using var response = await HttpAi.SendAsync(http, request, Server, options.EmbeddingModel, ct).ConfigureAwait(false);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (json?["embeddings"] is not JsonArray vectors || vectors.Count != batch.Length)
                throw new AiException($"“{options.EmbeddingModel}” did not return embeddings. Is it an embedding model? Try `ollama pull {AiOptions.DefaultEmbeddingModel}`.");
            result.AddRange(vectors.Select(HttpAi.ToVector));
        }
        return result.ToArray();
    }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new
        {
            model = options.ChatModel,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
            think = false,
            options = new { temperature = options.Temperature, num_ctx = options.ContextTokens }
        };
        using var request = HttpAi.Post(options.EffectiveBaseUrl + "/api/chat", body, options.ApiKey);
        using var response = await HttpAi.SendAsync(http, request, Server, options.ChatModel, ct).ConfigureAwait(false);

        var filter = new ThinkFilter();
        await foreach (string line in HttpAi.ReadLinesAsync(response, ct).ConfigureAwait(false))
        {
            JsonNode? chunk;
            try { chunk = JsonNode.Parse(line); }
            catch (JsonException) { continue; }

            if (chunk?["error"] is { } error) throw new AiException($"Ollama: {error}");
            string token = chunk?["message"]?["content"]?.GetValue<string>() ?? "";
            if (filter.Push(token) is { Length: > 0 } visible) yield return visible;
            if (chunk?["done"]?.GetValue<bool>() == true) break;
        }
        if (filter.Flush() is { Length: > 0 } rest) yield return rest;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var request = HttpAi.Get(options.EffectiveBaseUrl + "/api/tags", options.ApiKey);
        using var response = await HttpAi.SendAsync(http, request, Server, "", ct).ConfigureAwait(false);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return (json?["models"] as JsonArray ?? [])
            .Select(m => m?["name"]?.GetValue<string>())
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Ollama lists "bge-m3:latest" for a model pulled as "bge-m3".</summary>
    public static bool HasModel(IEnumerable<string> installed, string model) =>
        installed.Any(name => string.Equals(name, model, StringComparison.OrdinalIgnoreCase)
                              || !model.Contains(':') && string.Equals(name, model + ":latest", StringComparison.OrdinalIgnoreCase));
}
