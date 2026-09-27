using System.Text.Json;

using var userCancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; userCancellation.Cancel(); };
try
{
    var options = CliOptions.Parse(args);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(userCancellation.Token);
    deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
    using var client = new RagApiClient(Environment.GetEnvironmentVariable("RAG_API_URL") ?? "http://127.0.0.1:8000");
    if (options.Command == "ask")
        Console.Error.WriteLine(options.Mode == "generative"
            ? "Mode: generative, opt-in local HTTP. Model output is not fact-checked; inspect citations. Ctrl+C stops this client's wait."
            : "Mode: extractive, source excerpt; no model invocation. Ctrl+C cancels this client's request.");
    using var json = await client.ExecuteAsync(options, deadline.Token);
    if (json.RootElement.TryGetProperty("generation", out var generation)
        && generation.TryGetProperty("server_kind", out var kind))
    {
        string label = kind.GetString() switch
        {
            "synthetic_fixture" => "SYNTHETIC HTTP FIXTURE: no model inference.",
            "local_model" => "Operator-declared local model; server/model identity is not independently verified by this client.",
            _ => "UNVERIFIED local HTTP server; no real-model validation is claimed."
        };
        Console.Error.WriteLine(label);
    }
    Console.WriteLine(JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    return json.RootElement.TryGetProperty("status", out var status) && status.GetString() == "generation_error" ? 1 : 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(userCancellation.IsCancellationRequested
        ? "Client request cancelled. The API/model server may continue until its own bounded deadline."
        : "Client request timed out. The API/model server may continue until its own bounded deadline.");
    return userCancellation.IsCancellationRequested ? 130 : 1;
}
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
{
    Console.Error.WriteLine("RAG request failed; check the local API and response format. No partial answer is returned.");
    return 1;
}
