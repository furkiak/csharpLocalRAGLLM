using LocalRag.Ai;
using LocalRag.Store;
using Spectre.Console;

namespace LocalRag.Cli;

/// <summary>The console side of each command.</summary>
internal static class Commands
{
    public static async Task IndexAsync(RagEngine engine, string path, CancellationToken ct)
    {
        IndexReport? report = null;
        await AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(Markup.Escape(Shorten(Path.GetFullPath(path), 50)));
                var progress = new Progress<IndexProgress>(p =>
                {
                    task.MaxValue = Math.Max(1, p.Total);
                    task.Value = p.Done;
                    task.Description = Markup.Escape(Shorten(Path.GetFileName(p.Path), 50));
                });
                report = await engine.IndexAsync(path, progress, ct);
                task.Value = task.MaxValue;
            });

        var r = report!;
        AnsiConsole.MarkupLine(
            $"[green]✓[/] {Markup.Escape(path)}: [bold]{r.Added}[/] added, [bold]{r.Updated}[/] updated, {r.Unchanged} unchanged, {r.Removed} removed" +
            (r.Empty > 0 ? $", {r.Empty} without text" : "") +
            (r.Failed > 0 ? $", [red]{r.Failed} failed[/]" : "") +
            $" · {r.Chunks} chunks in {r.Elapsed.TotalSeconds:0.0}s");
        foreach (var (file, error) in r.Errors.Take(20))
            AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(Path.GetFileName(file))}: [grey]{Markup.Escape(error)}[/]");
        if (r.Errors.Count > 20) AnsiConsole.MarkupLine($"  [grey]…and {r.Errors.Count - 20} more[/]");
    }

    public static async Task ReindexAsync(Settings settings, CancellationToken ct)
    {
        var (engine, _) = settings.CreateEngine();
        using var store = engine.Store;
        var files = store.GetDocuments().Select(d => d.Path).ToList();
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]The index is empty; nothing to rebuild.[/]");
            return;
        }

        store.Clear();
        AnsiConsole.MarkupLine($"Rebuilding {files.Count} documents with [bold]{Markup.Escape(engine.Embedder.Model)}[/]…");
        int failed = 0;
        await AnsiConsole.Progress().AutoClear(true).StartAsync(async ctx =>
        {
            var task = ctx.AddTask("Re-embedding", maxValue: files.Count);
            foreach (string file in files)
            {
                if (File.Exists(file))
                {
                    var r = await engine.IndexAsync(file, null, ct);
                    failed += r.Failed;
                }
                task.Increment(1);
            }
        });
        AnsiConsole.MarkupLine($"[green]✓[/] Rebuilt {store.GetStats().Documents} documents" + (failed > 0 ? $", [red]{failed} failed[/]" : "") + ".");
    }

    public static async Task AskAsync(RagEngine engine, string question, List<ChatMessage>? history, bool showSources, CancellationToken ct)
    {
        RagAnswer answer = null!;
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Searching your documents…", async _ =>
            answer = await engine.AskAsync(question, history, ct));

        var text = new System.Text.StringBuilder();
        bool first = true;
        await foreach (string token in answer.Tokens.WithCancellation(ct))
        {
            if (first) { AnsiConsole.WriteLine(); first = false; }
            Console.Write(token);
            text.Append(token);
        }
        Console.WriteLine();

        if (showSources && answer.Sources.Count > 0)
        {
            AnsiConsole.WriteLine();
            foreach (var s in answer.Sources)
                AnsiConsole.MarkupLine($"  [blue][[{s.Number}]][/] [bold]{Markup.Escape(s.Label)}[/] [grey]{Markup.Escape(s.Path)}[/]");
        }

        history?.Add(ChatMessage.User(question));
        history?.Add(ChatMessage.Assistant(text.ToString()));
    }

    public static async Task ChatAsync(RagEngine engine, CancellationToken ct)
    {
        var stats = engine.Store.GetStats();
        AnsiConsole.MarkupLine($"[bold]localrag chat[/] · {stats.Documents} documents · model [bold]{Markup.Escape(engine.Chat.Model)}[/]");
        AnsiConsole.MarkupLine("[grey]Ask anything about your documents. /clear starts over, /exit leaves.[/]");
        var history = new List<ChatMessage>();

        while (!ct.IsCancellationRequested)
        {
            AnsiConsole.WriteLine();
            string? line = AnsiConsole.Prompt(new TextPrompt<string>("[blue]›[/]").AllowEmpty());
            line = line?.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line is "/exit" or "/quit" or "exit") break;
            if (line == "/clear")
            {
                history.Clear();
                AnsiConsole.MarkupLine("[grey]Started a new conversation.[/]");
                continue;
            }

            try
            {
                await AskAsync(engine, line, history, showSources: true, ct);
            }
            catch (AiException ex)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
            }
        }
    }

    public static void PrintHits(IReadOnlyList<SearchHit> hits)
    {
        if (hits.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No matching passages.[/]");
            return;
        }

        var table = new Table().RoundedBorder().AddColumns("#", "Source", "Found by", "Passage");
        for (int i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            string by = (h.VectorRank, h.KeywordRank) switch
            {
                ({ } v, { } k) => $"meaning #{v} + words #{k}",
                ({ } v, null) => $"meaning #{v}",
                (null, { } k) => $"words #{k}",
                _ => ""
            };
            string label = h.Page > 0 ? $"{h.Title}, p. {h.Page}" : h.Title;
            table.AddRow((i + 1).ToString(), Markup.Escape(label), by, Markup.Escape(Snippet(h.Text, 160)));
        }
        AnsiConsole.Write(table);
    }

    public static void List(Settings settings)
    {
        using var store = new VectorStore(settings.DatabasePath);
        var docs = store.GetDocuments();
        var stats = store.GetStats();
        if (docs.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]The index at {Markup.Escape(settings.DatabasePath)} is empty. Add documents with[/] localrag index <folder>");
            return;
        }

        var table = new Table().RoundedBorder().AddColumns("Document", "Pages", "Chunks", "Size", "Indexed", "Path");
        foreach (var d in docs)
            table.AddRow(Markup.Escape(d.Title), d.Pages > 1 ? d.Pages.ToString() : "–", d.Chunks.ToString(), Size(d.Size), d.IndexedUtc.ToLocalTime().ToString("g"), $"[grey]{Markup.Escape(Shorten(d.Path, 60))}[/]");
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{stats.Documents} documents · {stats.Chunks} chunks · {Size(stats.DatabaseBytes)} · embeddings: {Markup.Escape(stats.EmbeddingModel ?? "–")} ({stats.Dimensions} d) · {Markup.Escape(settings.DatabasePath)}[/]");
    }

    public static void Remove(Settings settings, string path)
    {
        using var store = new VectorStore(settings.DatabasePath);
        string full = Path.GetFullPath(path);
        var targets = store.GetDocuments()
            .Where(d => string.Equals(d.Path, full, StringComparison.OrdinalIgnoreCase)
                        || d.Path.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var d in targets) store.Remove(d.Path);
        AnsiConsole.MarkupLine(targets.Count == 0
            ? $"[grey]Nothing in the index matches {Markup.Escape(full)}.[/]"
            : $"[green]✓[/] Removed {targets.Count} document{(targets.Count == 1 ? "" : "s")} from the index.");
    }

    public static async Task DoctorAsync(Settings settings, CancellationToken ct)
    {
        var ai = settings.Ai;
        AnsiConsole.MarkupLine($"Provider  [bold]{ai.Provider}[/] at {Markup.Escape(ai.EffectiveBaseUrl)}");
        var (engine, catalog) = settings.CreateEngine();
        using var store = engine.Store;

        bool ok = true;
        try
        {
            var models = await catalog.ListModelsAsync(ct);
            AnsiConsole.MarkupLine($"[green]✓[/] Server reachable · {models.Count} models");
            if (ai.Provider == AiProvider.Ollama)
            {
                ok &= Check(OllamaClient.HasModel(models, ai.ChatModel), $"chat model {ai.ChatModel}", $"ollama pull {ai.ChatModel}");
                ok &= Check(OllamaClient.HasModel(models, ai.EmbeddingModel), $"embedding model {ai.EmbeddingModel}", $"ollama pull {ai.EmbeddingModel}");
            }
            else
            {
                ok &= Check(models.Contains(ai.ChatModel, StringComparer.OrdinalIgnoreCase), $"chat model {ai.ChatModel}", "load it in the server or pass its id with --model");
                ok &= Check(models.Contains(ai.EmbeddingModel, StringComparer.OrdinalIgnoreCase), $"embedding model {ai.EmbeddingModel}", "load an embedding model and pass its id with --embed-model");
            }
        }
        catch (AiException ex)
        {
            ok = false;
            AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
        }

        var stats = store.GetStats();
        string mark = stats.Documents > 0 ? "[green]✓[/]" : "[yellow]![/]";
        AnsiConsole.MarkupLine($"{mark} Index {Markup.Escape(settings.DatabasePath)}: {stats.Documents} documents, {stats.Chunks} chunks" +
                               (stats.EmbeddingModel is { } m ? $", built with {Markup.Escape(m)}" : "") +
                               (stats.Documents == 0 ? " → add documents with localrag index <folder>" : ""));
        if (stats.EmbeddingModel is { } built && !string.Equals(built, ai.EmbeddingModel, StringComparison.OrdinalIgnoreCase))
        {
            ok = false;
            AnsiConsole.MarkupLine($"[red]✗[/] The index was built with {Markup.Escape(built)} but {Markup.Escape(ai.EmbeddingModel)} is selected: run [bold]localrag reindex[/] or pass --embed-model {Markup.Escape(built)}");
        }
        AnsiConsole.MarkupLine(ok ? "[green]Everything looks good.[/]" : "[yellow]Fix the items above, then run localrag doctor again.[/]");
    }

    private static bool Check(bool passed, string what, string fix)
    {
        AnsiConsole.MarkupLine(passed
            ? $"[green]✓[/] {Markup.Escape(what)} installed"
            : $"[red]✗[/] {Markup.Escape(what)} missing → [bold]{Markup.Escape(fix)}[/]");
        return passed;
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB"
    };

    /// <summary>Keeps the end (for paths, where the file name matters).</summary>
    public static string Shorten(string text, int max) => text.Length <= max ? text : "…" + text[^(max - 1)..];

    /// <summary>Keeps the start, on one line (for passages).</summary>
    public static string Snippet(string text, int max)
    {
        string line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}
