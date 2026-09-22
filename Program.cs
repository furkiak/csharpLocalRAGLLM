using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var baseUrl = Environment.GetEnvironmentVariable("RAG_API_URL") ?? "http://127.0.0.1:8000";
if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
    endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback)
{
    Console.Error.WriteLine("RAG_API_URL must be a loopback HTTP address.");
    return 2;
}

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: add <path> <category> <id> | ask <question> | delete <id>");
    return 2;
}

using var client = new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
try
{
    HttpResponseMessage response;
    switch (args[0].ToLowerInvariant())
    {
        case "add" when args.Length == 4:
            response = await client.PostAsJsonAsync("/documents", new DocumentRequest(
                Path.GetFullPath(args[1]), args[2], args[3]));
            break;
        case "ask" when args.Length >= 2:
            response = await client.PostAsJsonAsync("/query", new QueryRequest(
                string.Join(' ', args.Skip(1))));
            break;
        case "delete" when args.Length == 2:
            response = await client.DeleteAsync("/documents/" + Uri.EscapeDataString(args[1]));
            break;
        default:
            Console.Error.WriteLine("Usage: add <path> <category> <id> | ask <question> | delete <id>");
            return 2;
    }

    using (response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"RAG API returned {(int)response.StatusCode}: {body}");
            return 1;
        }
        using var json = JsonDocument.Parse(body);
        Console.WriteLine(JsonSerializer.Serialize(json.RootElement,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
{
    Console.Error.WriteLine("RAG request failed: " + error.Message);
    return 1;
}

internal sealed record DocumentRequest(
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("unique_id")] string UniqueId);

internal sealed record QueryRequest([property: JsonPropertyName("query")] string Query);
