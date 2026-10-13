using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using LocalRag.Ai;
using LocalRag.Chunking;
using LocalRag.Documents;
using LocalRag.Store;

namespace LocalRag;

/// <summary>Tuning for retrieval and answering.</summary>
public sealed record RagOptions
{
    /// <summary>Chunks given to the model as sources.</summary>
    public int Sources { get; init; } = 6;

    public int ChunkChars { get; init; } = 1200;
    public int ChunkOverlap { get; init; } = 200;

    /// <summary>Previous chat turns sent along with a follow-up question.</summary>
    public int HistoryTurns { get; init; } = 6;

    /// <summary>Folders that are never indexed.</summary>
    public IReadOnlySet<string> IgnoredFolders { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", ".vscode", "node_modules", "bin", "obj", "dist", "build", "__pycache__", ".venv", "venv", ".localrag"
    };
}

public enum IndexOutcome { Added, Updated, Unchanged, Removed, Failed, Empty }

/// <summary>Progress of an indexing run, reported once per file.</summary>
public sealed record IndexProgress(int Done, int Total, string Path, IndexOutcome Outcome, int Chunks, string? Error);

public sealed record IndexReport(int Added, int Updated, int Unchanged, int Removed, int Failed, int Empty, int Chunks, TimeSpan Elapsed, IReadOnlyList<(string Path, string Error)> Errors)
{
    public int Files => Added + Updated + Unchanged + Failed + Empty;
}

/// <summary>A source handed to the model, numbered as the answer cites it.</summary>
public sealed record Source(int Number, string Path, string Title, int Page, string Text, double Score)
{
    public string Label => Page > 0 ? $"{Title}, p. {Page}" : Title;
}

/// <summary>The sources are known before the answer starts streaming, so a UI can show them right away.</summary>
public sealed record RagAnswer(string Question, IReadOnlyList<Source> Sources, IAsyncEnumerable<string> Tokens);

/// <summary>Indexes documents and answers questions about them using only what was found.</summary>
public sealed class RagEngine(VectorStore store, IEmbedder embedder, IChatModel chat, RagOptions? options = null)
{
    private readonly RagOptions _options = options ?? new RagOptions();
    private readonly SemaphoreSlim _indexLock = new(1, 1);

    public VectorStore Store => store;
    public IEmbedder Embedder => embedder;
    public IChatModel Chat => chat;
    public RagOptions Options => _options;

    // ------------------------------------------------------------------ indexing

    /// <summary>
    /// Indexes a file or a folder (recursively). Unchanged files are skipped by content hash, changed files are
    /// re-embedded, and documents that were deleted from an indexed folder are removed from the index.
    /// </summary>
    public async Task<IndexReport> IndexAsync(string path, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        await _indexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var started = DateTime.UtcNow;
            string full = Path.GetFullPath(path);
            bool isFolder = Directory.Exists(full);
            if (!isFolder && !File.Exists(full)) throw new FileNotFoundException($"“{path}” does not exist.");

            var files = isFolder ? EnumerateFiles(full).ToList() : [full];
            if (!isFolder && !DocumentReader.IsSupported(full))
                throw new DocumentReadException($"“{Path.GetFileName(full)}” has an unsupported file type. Supported: {string.Join(" ", DocumentReader.SupportedExtensions)}");

            var stale = isFolder
                ? store.GetDocuments().Where(d => IsUnder(d.Path, full) && !File.Exists(d.Path)).ToList()
                : [];

            int total = files.Count + stale.Count, done = 0;
            int added = 0, updated = 0, unchanged = 0, removed = 0, failed = 0, empty = 0, chunks = 0;
            var errors = new List<(string, string)>();

            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                (IndexOutcome outcome, int count, string? error) = await IndexFileAsync(file, ct).ConfigureAwait(false);
                switch (outcome)
                {
                    case IndexOutcome.Added: added++; break;
                    case IndexOutcome.Updated: updated++; break;
                    case IndexOutcome.Unchanged: unchanged++; break;
                    case IndexOutcome.Empty: empty++; break;
                    default: failed++; errors.Add((file, error ?? "unknown error")); break;
                }
                chunks += count;
                progress?.Report(new IndexProgress(++done, total, file, outcome, count, error));
            }

            foreach (var doc in stale)
            {
                store.Remove(doc.Path);
                removed++;
                progress?.Report(new IndexProgress(++done, total, doc.Path, IndexOutcome.Removed, 0, null));
            }

            return new IndexReport(added, updated, unchanged, removed, failed, empty, chunks, DateTime.UtcNow - started, errors);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task<(IndexOutcome, int, string?)> IndexFileAsync(string file, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(file);
            string hash = await HashAsync(file, ct).ConfigureAwait(false);
            var existing = store.FindDocument(info.FullName);
            if (existing is not null && existing.Hash == hash) return (IndexOutcome.Unchanged, 0, null);

            var document = DocumentReader.Read(file);
            var chunks = new TextChunker(_options.ChunkChars, _options.ChunkOverlap).Split(document);
            if (chunks.Count == 0)
            {
                store.Remove(info.FullName);
                return (IndexOutcome.Empty, 0, null);
            }

            // The title and page go into the embedded text too: "page 3 of the travel policy" helps retrieval.
            var embeddings = await embedder.EmbedAsync(chunks.Select(c => EmbeddingText(document.Title, c)).ToList(), ct).ConfigureAwait(false);
            store.EnsureEmbeddingModel(embedder.Model, embeddings[0].Length);
            store.Upsert(info.FullName, document.Title, hash, info.Length, info.LastWriteTimeUtc, document.Pages.Count, chunks, embeddings);
            return (existing is null ? IndexOutcome.Added : IndexOutcome.Updated, chunks.Count, null);
        }
        catch (Exception ex) when (ex is DocumentReadException or IOException or UnauthorizedAccessException)
        {
            return (IndexOutcome.Failed, 0, ex.Message);
        }
    }

    internal static string EmbeddingText(string title, TextChunk chunk) =>
        chunk.Page > 0 ? $"{title} (page {chunk.Page})\n{chunk.Text}" : $"{title}\n{chunk.Text}";

    private IEnumerable<string> EnumerateFiles(string folder)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
                if (DocumentReader.IsSupported(file) && !Path.GetFileName(file).StartsWith('~')) yield return file;
            foreach (string dir in dirs.OrderDescending(StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(dir);
                if (!_options.IgnoredFolders.Contains(name) && !name.StartsWith('.')) pending.Push(dir);
            }
        }
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<string> HashAsync(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    // ------------------------------------------------------------------ retrieval and answers

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int? limit = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (store.GetStats().Chunks == 0) throw new InvalidOperationException("Nothing is indexed yet. Add documents first, for example `localrag index ./docs`.");

        var vector = (await embedder.EmbedAsync([query], ct).ConfigureAwait(false))[0];
        store.EnsureEmbeddingModel(embedder.Model, vector.Length);
        return store.Search(vector, query, limit ?? _options.Sources);
    }

    /// <summary>Finds the sources for a question and starts streaming an answer grounded in them.</summary>
    public async Task<RagAnswer> AskAsync(string question, IReadOnlyList<ChatMessage>? history = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Ask a question.", nameof(question));

        // A follow-up such as "and for managers?" retrieves better together with the previous question.
        string retrievalQuery = history?.LastOrDefault(m => m.Role == "user") is { } previous && question.Length < 80
            ? previous.Content + "\n" + question
            : question;

        var hits = await SearchAsync(retrievalQuery, _options.Sources, ct).ConfigureAwait(false);
        var sources = hits.Select((h, i) => new Source(i + 1, h.Path, h.Title, h.Page, h.Text, h.Score)).ToList();
        var messages = BuildMessages(question, sources, history);
        return new RagAnswer(question, sources, Stream(messages, ct));
    }

    private async IAsyncEnumerable<string> Stream(IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (string token in chat.StreamAsync(messages, ct).ConfigureAwait(false))
            yield return token;
    }

    internal IReadOnlyList<ChatMessage> BuildMessages(string question, IReadOnlyList<Source> sources, IReadOnlyList<ChatMessage>? history)
    {
        var messages = new List<ChatMessage> { ChatMessage.System(SystemPrompt) };
        if (history is { Count: > 0 })
            messages.AddRange(history.Where(m => m.Role is "user" or "assistant").TakeLast(_options.HistoryTurns));

        var sb = new StringBuilder();
        if (sources.Count == 0)
        {
            sb.AppendLine("No passage in the documents matched this question.");
        }
        else
        {
            sb.AppendLine("Sources:");
            foreach (var s in sources)
            {
                sb.Append('[').Append(s.Number).Append("] ").AppendLine(s.Label);
                sb.AppendLine(s.Text.Trim());
                sb.AppendLine();
            }
        }
        sb.Append("Question: ").Append(question.Trim());
        messages.Add(ChatMessage.User(sb.ToString()));
        return messages;
    }

    internal const string SystemPrompt = """
        You are a careful assistant that answers questions about the user's own documents.
        Use only the numbered sources given with the question. Do not use outside knowledge.
        Cite every statement with the number of its source in square brackets, like [1] or [2][3].
        If the sources do not contain the answer, say that the documents do not cover it, and mention what they do cover if that helps.
        Answer in the same language as the question. Be concise; use a short list when it makes the answer clearer.
        """;
}
