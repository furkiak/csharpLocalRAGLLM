using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed class SyntheticHttpClientTests
{
    private static async Task<string> Line(Stream stream)
    {
        var text = new StringBuilder();
        byte[] one = new byte[1];
        while (await stream.ReadAsync(one) > 0)
        {
            if (one[0] == '\n') return text.ToString().TrimEnd('\r');
            text.Append((char)one[0]);
            if (text.Length > 16384) throw new InvalidDataException();
        }
        throw new EndOfStreamException();
    }

    private static async Task<byte[]> Bytes(Stream stream, int count)
    {
        byte[] result = new byte[count];
        int read = 0;
        while (read < count)
        {
            int part = await stream.ReadAsync(result.AsMemory(read));
            if (part == 0) throw new EndOfStreamException();
            read += part;
        }
        return result;
    }

    private static async Task<(string Path, Dictionary<string, string> Headers, byte[] Body)> Request(Stream stream)
    {
        string path = await Line(stream);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string line;
        while ((line = await Line(stream)) != "")
        {
            int colon = line.IndexOf(':');
            headers.Add(line[..colon], line[(colon + 1)..].Trim());
        }
        using var body = new MemoryStream();
        if (headers.TryGetValue("Transfer-Encoding", out var transfer) && transfer == "chunked")
        {
            int count;
            while ((count = Convert.ToInt32(await Line(stream), 16)) != 0)
            {
                body.Write(await Bytes(stream, count));
                Assert.Equal("", await Line(stream));
            }
            Assert.Equal("", await Line(stream));
        }
        else body.Write(await Bytes(stream, int.Parse(headers["Content-Length"])));
        return (path, headers, body.ToArray());
    }

    private static async Task Write(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        await stream.FlushAsync();
    }

    [Fact]
    public async Task SyntheticRealHttpPreservesExplicitModeQuotedUnicodeAndHasNoCredentials()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            var request = await Request(stream);
            string body = "{\"status\":\"no_answer\",\"mode\":\"generative\",\"generation\":{\"server_kind\":\"synthetic_fixture\"}}";
            await Write(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n" + body);
            return request;
        });
        using var client = new RagApiClient($"http://127.0.0.1:{port}");
        using var result = await client.ExecuteAsync(CliOptions.Parse(["ask", "--mode", "generative", "Türkçe \"alıntı\" sorusu?"]), CancellationToken.None);
        var captured = await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("POST /query HTTP/1.1", captured.Path);
        Assert.False(captured.Headers.ContainsKey("Authorization"));
        using var payload = JsonDocument.Parse(captured.Body);
        Assert.Equal("Türkçe \"alıntı\" sorusu?", payload.RootElement.GetProperty("query").GetString());
        Assert.Equal("generative", payload.RootElement.GetProperty("mode").GetString());
        Assert.Equal("synthetic_fixture", result.RootElement.GetProperty("generation").GetProperty("server_kind").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyntheticRealHttpBodyCancellationAndDeadlineStopClientWait(bool deadline)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            await Request(stream);
            await Write(stream, "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n1\r\n{\r\n");
            started.TrySetResult(true);
            await release.Task;
        });
        using var client = new RagApiClient($"http://127.0.0.1:{port}");
        using var cancel = new CancellationTokenSource();
        try
        {
            var operation = client.ExecuteAsync(CliOptions.Parse(["ask", "--mode", "generative", "A disposable question?"]), cancel.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
            if (deadline) cancel.CancelAfter(TimeSpan.FromMilliseconds(100));
            else cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(cancel.IsCancellationRequested);
        }
        finally { release.TrySetResult(true); await server.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    public async Task SyntheticRealHttpRedirectAndErrorAreNotFollowedOrEchoed(int status)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            await Request(stream);
            string body = "synthetic-secret-do-not-echo";
            await Write(stream, $"HTTP/1.1 {status} Fixture\r\nLocation: http://remote.invalid/\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n" + body);
        });
        using var client = new RagApiClient($"http://127.0.0.1:{port}");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ExecuteAsync(CliOptions.Parse(["ask", "Question?"]), CancellationToken.None));
        Assert.Contains($"HTTP {status}", error.Message);
        Assert.DoesNotContain("synthetic-secret", error.Message);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("http://remote.invalid")]
    [InlineData("http://localhost:8000")]
    [InlineData("http://user:secret@127.0.0.1")]
    [InlineData("http://127.0.0.1?key=secret")]
    [InlineData("http://127.0.0.1#fragment")]
    [InlineData("http://127.0.0.1/private")]
    public void ClientRejectsNonliteralRemoteOrCredentialTargetsBeforeSending(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new RagApiClient(endpoint));
    }

    [Fact]
    public async Task SyntheticRealHttpOversizedBodyIsRejectedBeforeParsingOrPartialOutput()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            await Request(stream);
            await Write(stream, "HTTP/1.1 200 OK\r\nContent-Length: 131073\r\nConnection: close\r\n\r\n");
        });
        using var client = new RagApiClient($"http://127.0.0.1:{port}");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ExecuteAsync(CliOptions.Parse(["ask", "Question?"]), CancellationToken.None));
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ExtractiveDefaultAndGenerativeTimeoutAreExplicitWithoutTreatingOptionsAsQuestion()
    {
        var normal = CliOptions.Parse(["ask", "What is the return period?"]);
        Assert.Equal("extractive", normal.Mode);
        Assert.Equal(45, normal.TimeoutSeconds);
        var generated = CliOptions.Parse(["ask", "--mode", "generative", "--timeout", "90", "--", "--quoted", "question?"]);
        Assert.Equal("generative", generated.Mode);
        Assert.Equal(90, generated.TimeoutSeconds);
        Assert.Equal("--quoted question?", generated.Values[0]);
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["ask", "--mode", "cloud", "question?"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["ask", "--timeout", "181", "question?"]));
    }
}
