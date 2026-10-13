using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalRag.Ai;

/// <summary>JSON over HTTP with error messages that tell the user what to do.</summary>
internal static class HttpAi
{
    /// <summary>A request may wait this long for the first byte (models load lazily); streaming has no overall limit.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(5);

    public static HttpRequestMessage Post(string url, object body, string? apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    public static HttpRequestMessage Get(string url, string? apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    /// <summary>Sends the request and returns a successful response (headers read, body still streaming).</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string server, string model, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResponseTimeout);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"{server} did not answer within {ResponseTimeout.TotalMinutes:0} minutes.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException || ex.HttpRequestError == HttpRequestError.ConnectionError)
        {
            throw new AiException(server.StartsWith("Ollama", StringComparison.Ordinal)
                ? $"Cannot reach {server}. Is Ollama running? Start it with `ollama serve`, or install it from https://ollama.com."
                : $"Cannot reach {server}. Start the server, or check the address with --url.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiException($"Cannot reach {server}: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode) return response;

        string body;
        try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception) { body = ""; }
        var status = response.StatusCode;
        response.Dispose();

        string? detail = ErrorText(body);
        throw new AiException(status switch
        {
            HttpStatusCode.NotFound when server.StartsWith("Ollama", StringComparison.Ordinal) =>
                $"The model “{model}” is not installed in Ollama. Run `ollama pull {model}`.",
            HttpStatusCode.NotFound => $"Not found at {server}: check the address and the model name “{model}”. {detail}".Trim(),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{server} refused the request: check the API key. {detail}".Trim(),
            _ => $"{server} returned {(int)status} {status}. {detail}".Trim()
        });
    }

    /// <summary>Reads <c>{"error": "..."}</c> (Ollama) or <c>{"error": {"message": "..."}}</c> (OpenAI style).</summary>
    public static string? ErrorText(string body)
    {
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            return error switch
            {
                JsonValue value => value.ToString(),
                JsonObject obj => obj["message"]?.ToString(),
                _ => null
            };
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body.Trim() is { Length: > 0 } t ? t : null;
        }
    }

    public static async IAsyncEnumerable<string> ReadLinesAsync(HttpResponseMessage response, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            if (line.Length > 0) yield return line;
    }

    public static float[] ToVector(JsonNode? node)
    {
        if (node is not JsonArray array) throw new AiException("The server returned an embedding in an unexpected format.");
        var vector = new float[array.Count];
        for (int i = 0; i < vector.Length; i++) vector[i] = array[i]!.GetValue<float>();
        return vector;
    }
}
