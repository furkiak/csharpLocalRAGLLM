using LocalRag.Ai;
using LocalRag.Chunking;
using LocalRag.Store;

namespace LocalRag.Tests;

public class VectorStoreTests
{
    private static readonly FakeEmbedder Embedder = new();

    private static void Add(VectorStore store, string path, params string[] texts)
    {
        var chunks = texts.Select((t, i) => new TextChunk(i, 0, t)).ToList();
        store.EnsureEmbeddingModel(Embedder.Model, 128);
        store.Upsert(path, Path.GetFileNameWithoutExtension(path), "hash-" + path, 10, DateTime.UtcNow, 1, chunks, texts.Select(Embedder.Embed).ToList());
    }

    [Fact]
    public void Hybrid_search_finds_paraphrases_and_exact_codes()
    {
        using var tmp = new TempFolder();
        using var store = new VectorStore(Path.Combine(tmp.Path, "index.db"));
        Add(store, "/docs/travel.md", "Travel expenses are refunded within thirty days after the trip.", "Hotels up to four stars are allowed.");
        Add(store, "/docs/errors.md", "Error code E-4012 means the printer queue is full.", "Restart the spooler service to clear it.");

        var semantic = store.Search(Embedder.Embed("when are travel expenses refunded"), "when are travel expenses refunded", 3);
        var exact = store.Search(Embedder.Embed("E-4012"), "E-4012", 3);

        Assert.Equal("/docs/travel.md", semantic[0].Path);
        Assert.Contains("thirty days", semantic[0].Text);
        Assert.NotNull(semantic[0].VectorRank);
        Assert.Contains("E-4012", exact[0].Text);
        Assert.Equal(1, exact[0].KeywordRank);
    }

    [Fact]
    public void Keyword_search_ignores_case_and_diacritics_and_survives_odd_input()
    {
        using var tmp = new TempFolder();
        using var store = new VectorStore(Path.Combine(tmp.Path, "index.db"));
        Add(store, "/docs/tr.md", "Yıllık izin süresi çalışanlar için on dört gündür.");

        Assert.Single(store.KeywordSearch("CALISANLAR icin", 5));
        Assert.Empty(store.KeywordSearch("\"\" OR (* NEAR", 5));
        Assert.Empty(store.KeywordSearch("   ", 5));
    }

    [Fact]
    public void Replacing_or_removing_a_document_updates_both_indexes()
    {
        using var tmp = new TempFolder();
        using var store = new VectorStore(Path.Combine(tmp.Path, "index.db"));
        Add(store, "/docs/a.md", "The old parking rule allows bicycles only.");
        Add(store, "/docs/a.md", "The new parking rule allows scooters too.");

        Assert.Empty(store.KeywordSearch("bicycles", 5));
        Assert.Single(store.KeywordSearch("scooters", 5));
        Assert.Equal(1, store.GetStats().Documents);

        Assert.True(store.Remove("/docs/a.md"));
        Assert.Empty(store.KeywordSearch("scooters", 5));
        Assert.Empty(store.VectorSearch(Embedder.Embed("parking scooters"), 5));
        Assert.Equal(0, store.GetStats().Chunks);
    }

    [Fact]
    public void Mixing_embedding_models_is_refused_until_the_index_is_cleared()
    {
        using var tmp = new TempFolder();
        using var store = new VectorStore(Path.Combine(tmp.Path, "index.db"));
        Add(store, "/docs/a.md", "Some text about holidays.");

        var ex = Assert.Throws<InvalidOperationException>(() => store.EnsureEmbeddingModel("other-model", 768));
        Assert.Contains("fake-embed", ex.Message);
        Assert.Contains("reindex", ex.Message);

        store.Clear();
        store.EnsureEmbeddingModel("other-model", 768);
        Assert.Equal("other-model", store.EmbeddingModel);
        Assert.Equal(0, store.GetStats().Documents);
    }

    [Fact]
    public void The_index_survives_a_restart()
    {
        using var tmp = new TempFolder();
        string db = Path.Combine(tmp.Path, "index.db");
        using (var store = new VectorStore(db)) Add(store, "/docs/a.md", "Coffee machine on the third floor.");

        using var reopened = new VectorStore(db);
        Assert.Equal("fake-embed", reopened.EmbeddingModel);
        Assert.Equal("/docs/a.md", reopened.Search(Embedder.Embed("coffee machine"), "coffee machine", 1).Single().Path);
    }
}

public class RagEngineTests
{
    private static RagEngine Engine(TempFolder tmp, FakeChat? chat = null, FakeEmbedder? embedder = null) =>
        new(new VectorStore(Path.Combine(tmp.Path, ".localrag", "index.db")), embedder ?? new FakeEmbedder(), chat ?? new FakeChat());

    [Fact]
    public async Task Indexing_is_incremental_and_follows_the_folder()
    {
        using var tmp = new TempFolder();
        string docs = Path.Combine(tmp.Path, "docs");
        tmp.File("docs/leave.md", "Annual leave is twenty days per year.");
        string remote = tmp.File("docs/sub/remote.txt", "Remote work is allowed on Mondays and Fridays.");
        tmp.File("docs/photo.jpg", "not a document");
        tmp.File("docs/node_modules/lib.md", "ignored dependency docs");
        tmp.File("docs/empty.md", "   ");
        var engine = Engine(tmp);

        var first = await engine.IndexAsync(docs);
        Assert.Equal((2, 0, 0, 1), (first.Added, first.Unchanged, first.Failed, first.Empty));

        var second = await engine.IndexAsync(docs);
        Assert.Equal((0, 2), (second.Added, second.Unchanged));

        File.WriteAllText(remote, "Remote work is allowed on Fridays only.");
        File.Delete(Path.Combine(docs, "leave.md"));
        var progress = new List<IndexProgress>();
        var third = await engine.IndexAsync(docs, new SyncProgress<IndexProgress>(progress.Add));

        Assert.Equal((1, 1), (third.Updated, third.Removed));
        Assert.Single(engine.Store.GetDocuments());
        Assert.Single(engine.Store.KeywordSearch("only", 5));
        Assert.Empty(engine.Store.KeywordSearch("Mondays", 5));
        Assert.Contains(progress, p => p.Outcome == IndexOutcome.Removed && p.Path.EndsWith("leave.md"));
    }

    [Fact]
    public async Task A_single_unsupported_file_is_refused_clearly()
    {
        using var tmp = new TempFolder();
        var engine = Engine(tmp);
        string jpg = tmp.File("x.jpg", "binary");

        var ex = await Assert.ThrowsAsync<LocalRag.Documents.DocumentReadException>(() => engine.IndexAsync(jpg));
        Assert.Contains("unsupported", ex.Message);
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.IndexAsync(Path.Combine(tmp.Path, "missing")));
    }

    [Fact]
    public async Task Answers_are_grounded_in_numbered_sources()
    {
        using var tmp = new TempFolder();
        tmp.File("docs/expenses.md", "Expense reports must be filed within 30 days of the purchase.");
        tmp.File("docs/security.md", "Laptops must use full-disk encryption and a screen lock after 5 minutes.");
        var chat = new FakeChat("Within ", "30 days [1].");
        var engine = Engine(tmp, chat);
        await engine.IndexAsync(Path.Combine(tmp.Path, "docs"));

        var answer = await engine.AskAsync("How long do I have to file an expense report?");
        string text = string.Concat(await answer.Tokens.ToListAsync());

        Assert.Equal("Within 30 days [1].", text);
        Assert.Equal("expenses", answer.Sources[0].Title);
        Assert.Equal(1, answer.Sources[0].Number);

        var messages = chat.Requests.Single();
        Assert.Equal("system", messages[0].Role);
        Assert.Contains("[1] expenses", messages[^1].Content);
        Assert.EndsWith("Question: How long do I have to file an expense report?", messages[^1].Content);
    }

    [Fact]
    public async Task Follow_up_questions_carry_the_conversation()
    {
        using var tmp = new TempFolder();
        tmp.File("docs/leave.md", "Managers get twenty-five days of annual leave; other staff get twenty days.");
        var chat = new FakeChat("25 days [1].");
        var engine = Engine(tmp, chat);
        await engine.IndexAsync(Path.Combine(tmp.Path, "docs"));

        var history = new List<ChatMessage> { ChatMessage.User("How many days of annual leave do I get?"), ChatMessage.Assistant("Twenty days [1].") };
        var answer = await engine.AskAsync("And managers?", history);
        await answer.Tokens.ToListAsync();

        var sent = chat.Requests.Single();
        Assert.Contains(sent, m => m.Role == "assistant" && m.Content == "Twenty days [1].");
        Assert.NotEmpty(answer.Sources);
    }

    [Fact]
    public async Task Asking_an_empty_index_explains_what_to_do()
    {
        using var tmp = new TempFolder();
        var engine = Engine(tmp);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.AskAsync("anything?"));
        Assert.Contains("localrag index", ex.Message);
    }

    [Fact]
    public void The_embedded_text_names_the_document_and_page()
    {
        Assert.Equal("Policy (page 3)\nBody", RagEngine.EmbeddingText("Policy", new TextChunk(0, 3, "Body")));
        Assert.Equal("Notes\nBody", RagEngine.EmbeddingText("Notes", new TextChunk(0, 0, "Body")));
    }
}
