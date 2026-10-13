using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalRag.Ai;

/// <summary>OpenAI-style /embeddings, /chat/completions (server-sent events) and /models: LM Studio, llama.cpp, vLLM, LocalAI, Jan…</summary>
public sealed class OpenAiCompatibleClient(HttpClient http, AiOptions options) : IEmbedder, IChatModel, IModelCatalog
{
    private const int EmbedBatchSize = 32;
    private string Server => $"the server at {options.EffectiveBaseUrl}";

    string IEmbedder.Model => options.EmbeddingModel;
    string IChatModel.Model => options.ChatModel;

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        for (int start = 0; start < texts.Count; start += EmbedBatchSize)
        {
            var batch = texts.Skip(start).Take(EmbedBatchSize).ToArray();
            using var request = HttpAi.Post(options.EffectiveBaseUrl + "/embeddings", new { model = options.EmbeddingModel, input = batch }, options.ApiKey);
            using var response = await HttpAi.SendAsync(http, request, Server, options.EmbeddingModel, ct).ConfigureAwait(false);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            if (json?["data"] is not JsonArray data || data.Count != batch.Length)
                throw new AiException($"“{options.EmbeddingModel}” did not return embeddings. Load an embedding model in the server and pass its name with --embed-model.");
            // Entries carry an index; do not rely on the order of the array.
            result.AddRange(data.OrderBy(d => d?["index"]?.GetValue<int>() ?? 0).Select(d => HttpAi.ToVector(d?["embedding"])));
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
            temperature = options.Temperature
        };
        using var request = HttpAi.Post(options.EffectiveBaseUrl + "/chat/completions", body, options.ApiKey);
        using var response = await HttpAi.SendAsync(http, request, Server, options.ChatModel, ct).ConfigureAwait(false);

        var filter = new ThinkFilter();
        await foreach (string line in HttpAi.ReadLinesAsync(response, ct).ConfigureAwait(false))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line[5..].Trim();
            if (data == "[DONE]") break;

            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); }
            catch (JsonException) { continue; }

            if (chunk?["error"] is { } error) throw new AiException(HttpAi.ErrorText(chunk.ToJsonString()) ?? error.ToString());
            string token = chunk?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>() ?? "";
            if (filter.Push(token) is { Length: > 0 } visible) yield return visible;
        }
        if (filter.Flush() is { Length: > 0 } rest) yield return rest;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var request = HttpAi.Get(options.EffectiveBaseUrl + "/models", options.ApiKey);
        using var response = await HttpAi.SendAsync(http, request, Server, "", ct).ConfigureAwait(false);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return (json?["data"] as JsonArray ?? [])
            .Select(m => m?["id"]?.GetValue<string>())
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
