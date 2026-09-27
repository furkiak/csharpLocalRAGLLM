using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class RagApiClient : IDisposable
{
    private const int MaxResponseBytes = 128 * 1024;
    private readonly HttpClient client;

    public RagApiClient(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp
            || !IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address)
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.AbsolutePath != "/")
            throw new ArgumentException("RAG_API_URL must be an HTTP API root on a literal loopback IP, without credentials, query or fragment.");
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<JsonDocument> ExecuteAsync(CliOptions options, CancellationToken token)
    {
        using var request = options.Command switch
        {
            "add" => new HttpRequestMessage(HttpMethod.Post, "/documents")
            { Content = JsonContent.Create(new DocumentRequest(Path.GetFullPath(options.Values[0]), options.Values[1], options.Values[2])) },
            "ask" => new HttpRequestMessage(HttpMethod.Post, "/query")
            { Content = JsonContent.Create(new QueryRequest(options.Values[0], options.Mode)) },
            "delete" => new HttpRequestMessage(HttpMethod.Delete, "/documents/" + Uri.EscapeDataString(options.Values[0])),
            _ => throw new ArgumentException(CliOptions.Usage)
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"RAG API returned HTTP {(int)response.StatusCode}; no response body is echoed.");
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("RAG response exceeds 128 KiB.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (bytes.Length + read > MaxResponseBytes) throw new InvalidDataException("RAG response exceeds 128 KiB.");
            bytes.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        return JsonDocument.Parse(bytes.ToArray());
    }

    public void Dispose() => client.Dispose();
}

internal sealed record DocumentRequest(
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("unique_id")] string UniqueId);
internal sealed record QueryRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("mode")] string Mode);
