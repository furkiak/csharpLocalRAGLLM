using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using LocalRag.Ai;

namespace LocalRag.Tests;

/// <summary>A deterministic bag-of-words embedder: texts that share words get similar vectors. No model needed.</summary>
public sealed partial class FakeEmbedder(string model = "fake-embed", int dimensions = 128) : IEmbedder
{
    public string Model { get; } = model;
    public int Calls { get; private set; }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(texts.Select(Embed).ToArray());
    }

    public float[] Embed(string text)
    {
        var vector = new float[dimensions];
        foreach (Match word in Words().Matches(text.ToLowerInvariant()))
            vector[(int)((uint)StableHash(word.Value) % (uint)dimensions)] += 1;
        return vector;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }

    [GeneratedRegex(@"\p{L}{3,}")]
    private static partial Regex Words();
}

/// <summary>Records what it was asked and streams a fixed answer token by token.</summary>
public sealed class FakeChat(params string[] tokens) : IChatModel
{
    public string Model => "fake-chat";
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Requests.Add(messages);
        foreach (string token in tokens.Length > 0 ? tokens : ["The answer", " is in the sources [1]."])
        {
            await Task.Yield();
            yield return token;
        }
    }
}

/// <summary>Answers HTTP requests from a queue and remembers what was sent.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = [];

    public FakeHandler Respond(HttpStatusCode status, string body, string mediaType = "application/json")
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
        return this;
    }

    public FakeHandler Throw(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.ToString(), body));
        if (_responses.Count == 0) throw new InvalidOperationException("No response queued for " + request.RequestUri);
        return _responses.Dequeue()(request);
    }
}

/// <summary>Reports on the calling thread (Progress&lt;T&gt; posts to the thread pool, which makes assertions racy).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>A temporary folder deleted after the test.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "localrag-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string relative, string content)
    {
        string full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
