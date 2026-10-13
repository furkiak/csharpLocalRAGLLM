using System.CommandLine;
using System.Text;
using LocalRag;
using LocalRag.Ai;
using LocalRag.Cli;
using LocalRag.Documents;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

var dbOption = new Option<string?>("--db") { Description = "Index file (default: ~/.localrag/index.db, or LOCALRAG_DB)", Recursive = true };
var providerOption = new Option<string?>("--provider") { Description = "ollama (default) or openai for any OpenAI-compatible server (LM Studio, llama.cpp, vLLM…)", Recursive = true };
var urlOption = new Option<string?>("--url") { Description = "Server address (default: http://localhost:11434, or http://localhost:1234/v1 for openai)", Recursive = true };
var modelOption = new Option<string?>("--model", "-m") { Description = $"Chat model (default: {AiOptions.DefaultChatModel}, or LOCALRAG_MODEL)", Recursive = true };
var embedOption = new Option<string?>("--embed-model") { Description = $"Embedding model (default: the index's own, else {AiOptions.DefaultEmbeddingModel})", Recursive = true };

var root = new RootCommand("localrag — chat with your own documents, fully offline. Index PDF, Word, Markdown and text files, then ask questions answered with sources.")
{
    dbOption, providerOption, urlOption, modelOption, embedOption
};

Settings SettingsFrom(ParseResult p, int? sources = null) => Settings.Resolve(
    p.GetValue(dbOption), p.GetValue(providerOption), p.GetValue(urlOption), p.GetValue(modelOption), p.GetValue(embedOption), sources);

// ------------------------------------------------------------------ index
var pathsArgument = new Argument<string[]>("paths") { Description = "Files or folders to index (folders are scanned recursively)", Arity = ArgumentArity.OneOrMore };
var index = new Command("index", "Add or update documents. Unchanged files are skipped; files deleted from an indexed folder are removed.") { pathsArgument };
index.SetAction((p, ct) => Run(async () =>
{
    var (engine, _) = SettingsFrom(p).CreateEngine();
    using var _ = engine.Store;
    foreach (string path in p.GetValue(pathsArgument)!)
        await Commands.IndexAsync(engine, path, ct);
}));
root.Subcommands.Add(index);

// ------------------------------------------------------------------ reindex
var reindex = new Command("reindex", "Re-read and re-embed every indexed document (after changing the embedding model).");
reindex.SetAction((p, ct) => Run(() => Commands.ReindexAsync(SettingsFrom(p), ct)));
root.Subcommands.Add(reindex);

// ------------------------------------------------------------------ ask
var questionArgument = new Argument<string>("question") { Description = "The question, in any language" };
var sourcesOption = new Option<int?>("--sources", "-k") { Description = "Number of passages given to the model (default 6)" };
var quietOption = new Option<bool>("--no-sources") { Description = "Print only the answer" };
var ask = new Command("ask", "Answer one question from the indexed documents, with sources.") { questionArgument, sourcesOption, quietOption };
ask.SetAction((p, ct) => Run(async () =>
{
    var (engine, _) = SettingsFrom(p, p.GetValue(sourcesOption)).CreateEngine();
    using var _ = engine.Store;
    await Commands.AskAsync(engine, p.GetValue(questionArgument)!, null, !p.GetValue(quietOption), ct);
}));
root.Subcommands.Add(ask);

// ------------------------------------------------------------------ chat
var chat = new Command("chat", "Interactive chat with follow-up questions. Type /exit to leave, /clear to start over.") { sourcesOption };
chat.SetAction((p, ct) => Run(async () =>
{
    var (engine, _) = SettingsFrom(p, p.GetValue(sourcesOption)).CreateEngine();
    using var _ = engine.Store;
    await Commands.ChatAsync(engine, ct);
}));
root.Subcommands.Add(chat);

// ------------------------------------------------------------------ search
var queryArgument = new Argument<string>("query") { Description = "Words or a question" };
var limitOption = new Option<int>("--limit", "-n") { Description = "Number of results", DefaultValueFactory = _ => 8 };
var search = new Command("search", "Show the passages that best match a query, without asking the model.") { queryArgument, limitOption };
search.SetAction((p, ct) => Run(async () =>
{
    var (engine, _) = SettingsFrom(p).CreateEngine();
    using var _ = engine.Store;
    Commands.PrintHits(await engine.SearchAsync(p.GetValue(queryArgument)!, Math.Clamp(p.GetValue(limitOption), 1, 50), ct));
}));
root.Subcommands.Add(search);

// ------------------------------------------------------------------ list / remove
var list = new Command("list", "List the indexed documents.");
list.SetAction(p => Run(() => { Commands.List(SettingsFrom(p)); return Task.CompletedTask; }));
root.Subcommands.Add(list);

var removePath = new Argument<string>("path") { Description = "A file, or a folder whose documents should all be removed" };
var remove = new Command("remove", "Remove documents from the index (the files themselves are not touched).") { removePath };
remove.SetAction(p => Run(() => { Commands.Remove(SettingsFrom(p), p.GetValue(removePath)!); return Task.CompletedTask; }));
root.Subcommands.Add(remove);

// ------------------------------------------------------------------ doctor
var doctor = new Command("doctor", "Check the AI server, the models and the index, and say how to fix problems.");
doctor.SetAction((p, ct) => Run(() => Commands.DoctorAsync(SettingsFrom(p), ct)));
root.Subcommands.Add(doctor);

// ------------------------------------------------------------------ serve
var portOption = new Option<int>("--port") { Description = "Port on 127.0.0.1", DefaultValueFactory = _ => 5199 };
var openOption = new Option<bool>("--open") { Description = "Open the browser" };
var serve = new Command("serve", "Start the local web app (chat, sources and document library) on http://127.0.0.1.") { portOption, openOption };
serve.SetAction((p, ct) => Run(() => WebServer.RunAsync(SettingsFrom(p), p.GetValue(portOption), p.GetValue(openOption), ct)));
root.Subcommands.Add(serve);

return await root.Parse(args).InvokeAsync();

// Errors the user can act on are printed as one friendly line; anything else keeps its stack trace.
static async Task<int> Run(Func<Task> action)
{
    try
    {
        await action();
        return 0;
    }
    catch (OperationCanceledException)
    {
        AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
        return 130;
    }
    catch (Exception ex) when (ex is AiException or DocumentReadException or InvalidOperationException or FileNotFoundException or ArgumentException)
    {
        AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ex.Message)}");
        return 1;
    }
}
