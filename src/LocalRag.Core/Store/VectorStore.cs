using System.Globalization;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LocalRag.Chunking;
using Microsoft.Data.Sqlite;

namespace LocalRag.Store;

/// <summary>A document in the index.</summary>
public sealed record IndexedDocument(long Id, string Path, string Title, string Hash, long Size, DateTime ModifiedUtc, int Pages, int Chunks, DateTime IndexedUtc);

/// <summary>A chunk found by a search, with its document and how it was found.</summary>
public sealed record SearchHit(long ChunkId, string Path, string Title, int Page, string Text, double Score, int? VectorRank, int? KeywordRank);

/// <summary>Index statistics.</summary>
public sealed record StoreStats(int Documents, int Chunks, long DatabaseBytes, string? EmbeddingModel, int Dimensions);

/// <summary>
/// The index: documents and chunks in one SQLite file, with an FTS5 full-text index for keyword search and the
/// normalised embeddings kept in memory for vector search. <see cref="Search"/> fuses both rankings (reciprocal rank
/// fusion), so exact terms (names, codes, error numbers) are found as reliably as paraphrases.
/// </summary>
public sealed partial class VectorStore : IDisposable
{
    private const int RrfK = 60;
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private VectorCache? _cache;

    public VectorStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ConnectionString);
        _db.Open();
        Execute("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA synchronous = NORMAL;");
        Execute(Schema);
    }

    public string Path { get; }

    // ------------------------------------------------------------------ embedding model guard

    /// <summary>The embedding model the stored vectors came from (vectors from different models are not comparable).</summary>
    public string? EmbeddingModel => GetMeta("embedding_model");

    public int Dimensions => int.TryParse(GetMeta("dimensions"), out int d) ? d : 0;

    /// <summary>Throws when the index was built with another embedding model; records the model on first use.</summary>
    public void EnsureEmbeddingModel(string model, int dimensions)
    {
        lock (_gate)
        {
            string? stored = EmbeddingModel;
            if (stored is null || CountChunks() == 0)
            {
                SetMeta("embedding_model", model);
                SetMeta("dimensions", dimensions.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (!string.Equals(stored, model, StringComparison.OrdinalIgnoreCase) || Dimensions != dimensions)
                throw new InvalidOperationException(
                    $"This index was built with the embedding model “{stored}” ({Dimensions} dimensions), not “{model}” ({dimensions}). " +
                    $"Use --embed-model {stored}, or rebuild the index with `localrag reindex`.");
        }
    }

    // ------------------------------------------------------------------ documents

    public IReadOnlyList<IndexedDocument> GetDocuments()
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT id, path, title, hash, size, modified, pages, chunks, indexed_at FROM documents ORDER BY path COLLATE NOCASE");
            using var r = cmd.ExecuteReader();
            var list = new List<IndexedDocument>();
            while (r.Read()) list.Add(ReadDocument(r));
            return list;
        }
    }

    public IndexedDocument? FindDocument(string path)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT id, path, title, hash, size, modified, pages, chunks, indexed_at FROM documents WHERE path = $p", ("$p", path));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadDocument(r) : null;
        }
    }

    /// <summary>Replaces a document and all its chunks in one transaction.</summary>
    public void Upsert(string path, string title, string hash, long size, DateTime modifiedUtc, int pages, IReadOnlyList<TextChunk> chunks, IReadOnlyList<float[]> embeddings)
    {
        if (chunks.Count != embeddings.Count) throw new ArgumentException("Every chunk needs exactly one embedding.");
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using (var del = Command("DELETE FROM documents WHERE path = $p", ("$p", path))) { del.Transaction = tx; del.ExecuteNonQuery(); }

            long documentId;
            using (var ins = Command("""
                INSERT INTO documents(path, title, hash, size, modified, pages, chunks, indexed_at)
                VALUES ($path, $title, $hash, $size, $modified, $pages, $chunks, $indexed) RETURNING id
                """,
                ("$path", path), ("$title", title), ("$hash", hash), ("$size", size), ("$modified", modifiedUtc.ToString("O")),
                ("$pages", pages), ("$chunks", chunks.Count), ("$indexed", DateTime.UtcNow.ToString("O"))))
            {
                ins.Transaction = tx;
                documentId = (long)ins.ExecuteScalar()!;
            }

            using var chunk = Command("INSERT INTO chunks(document_id, ordinal, page, text, embedding) VALUES ($d, $o, $p, $t, $e)");
            chunk.Transaction = tx;
            var pd = chunk.Parameters.Add("$d", SqliteType.Integer);
            var po = chunk.Parameters.Add("$o", SqliteType.Integer);
            var pp = chunk.Parameters.Add("$p", SqliteType.Integer);
            var pt = chunk.Parameters.Add("$t", SqliteType.Text);
            var pe = chunk.Parameters.Add("$e", SqliteType.Blob);
            for (int i = 0; i < chunks.Count; i++)
            {
                pd.Value = documentId;
                po.Value = chunks[i].Ordinal;
                pp.Value = chunks[i].Page;
                pt.Value = chunks[i].Text;
                pe.Value = ToBlob(embeddings[i]);
                chunk.ExecuteNonQuery();
            }
            tx.Commit();
            _cache = null;
        }
    }

    public bool Remove(string path)
    {
        lock (_gate)
        {
            using var cmd = Command("DELETE FROM documents WHERE path = $p", ("$p", path));
            bool removed = cmd.ExecuteNonQuery() > 0;
            if (removed) _cache = null;
            return removed;
        }
    }

    /// <summary>Removes every document and the embedding model record (used before a full re-index).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Execute("DELETE FROM documents; DELETE FROM meta WHERE key IN ('embedding_model', 'dimensions');");
            Execute("INSERT INTO chunks_fts(chunks_fts) VALUES ('rebuild'); VACUUM;");
            _cache = null;
        }
    }

    public StoreStats GetStats()
    {
        lock (_gate)
        {
            using var docs = Command("SELECT COUNT(*) FROM documents");
            int documents = Convert.ToInt32(docs.ExecuteScalar(), CultureInfo.InvariantCulture);
            long bytes = new FileInfo(Path).Length + (File.Exists(Path + "-wal") ? new FileInfo(Path + "-wal").Length : 0);
            return new StoreStats(documents, CountChunks(), bytes, EmbeddingModel, Dimensions);
        }
    }

    // ------------------------------------------------------------------ search

    /// <summary>Hybrid search: vector similarity and BM25 keyword ranking fused with reciprocal rank fusion.</summary>
    public IReadOnlyList<SearchHit> Search(float[] queryEmbedding, string queryText, int limit, int candidates = 40)
    {
        lock (_gate)
        {
            var vector = VectorSearch(queryEmbedding, candidates);
            var keyword = KeywordSearch(queryText, candidates);

            var scores = new Dictionary<long, (double Score, int? V, int? K)>();
            for (int i = 0; i < vector.Count; i++)
                scores[vector[i]] = (1.0 / (RrfK + i + 1), i + 1, null);
            for (int i = 0; i < keyword.Count; i++)
            {
                scores.TryGetValue(keyword[i], out var s);
                scores[keyword[i]] = (s.Score + 1.0 / (RrfK + i + 1), s.V, i + 1);
            }

            var top = scores.OrderByDescending(kv => kv.Value.Score).Take(limit).ToList();
            return top.Select(kv => LoadHit(kv.Key, kv.Value.Score, kv.Value.V, kv.Value.K)).OfType<SearchHit>().ToList();
        }
    }

    /// <summary>Chunk ids by cosine similarity, best first.</summary>
    internal List<long> VectorSearch(float[] queryEmbedding, int limit)
    {
        var cache = _cache ??= LoadVectors();
        if (cache.Count == 0) return [];
        if (queryEmbedding.Length != cache.Dimensions)
            throw new InvalidOperationException($"The query embedding has {queryEmbedding.Length} dimensions, the index {cache.Dimensions}.");

        var query = Normalize(queryEmbedding);
        var best = new PriorityQueue<long, float>(limit + 1);
        for (int i = 0; i < cache.Count; i++)
        {
            float similarity = TensorPrimitives.Dot(cache.Row(i), query);
            best.Enqueue(cache.Ids[i], similarity);
            if (best.Count > limit) best.Dequeue();
        }

        var result = new List<long>(best.Count);
        while (best.Count > 0) result.Add(best.Dequeue());
        result.Reverse();
        return result;
    }

    /// <summary>Chunk ids by BM25 over the full-text index, best first. Each word of the query is matched on its own.</summary>
    internal List<long> KeywordSearch(string queryText, int limit)
    {
        var terms = Words().Matches(queryText).Select(m => m.Value).Where(w => w.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToList();
        if (terms.Count == 0) return [];

        string match = string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
        using var cmd = Command("SELECT rowid FROM chunks_fts WHERE chunks_fts MATCH $q ORDER BY bm25(chunks_fts) LIMIT $k", ("$q", match), ("$k", limit));
        using var r = cmd.ExecuteReader();
        var ids = new List<long>();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    private SearchHit? LoadHit(long chunkId, double score, int? vectorRank, int? keywordRank)
    {
        using var cmd = Command("""
            SELECT d.path, d.title, c.page, c.text FROM chunks c JOIN documents d ON d.id = c.document_id WHERE c.id = $id
            """, ("$id", chunkId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new SearchHit(chunkId, r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), score, vectorRank, keywordRank) : null;
    }

    private VectorCache LoadVectors()
    {
        using var cmd = Command("SELECT id, embedding FROM chunks ORDER BY id");
        using var r = cmd.ExecuteReader();
        var ids = new List<long>();
        var rows = new List<float[]>();
        while (r.Read())
        {
            ids.Add(r.GetInt64(0));
            rows.Add(Normalize(FromBlob((byte[])r[1])));
        }
        int dims = rows.Count > 0 ? rows[0].Length : 0;
        var flat = new float[rows.Count * dims];
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Length != dims) throw new InvalidOperationException("The index contains embeddings of different sizes. Rebuild it with `localrag reindex`.");
            rows[i].CopyTo(flat, i * dims);
        }
        return new VectorCache(ids.ToArray(), flat, dims);
    }

    private sealed record VectorCache(long[] Ids, float[] Flat, int Dimensions)
    {
        public int Count => Ids.Length;
        public ReadOnlySpan<float> Row(int i) => Flat.AsSpan(i * Dimensions, Dimensions);
    }

    internal static float[] Normalize(float[] vector)
    {
        float norm = TensorPrimitives.Norm(vector);
        if (norm == 0) return vector;
        var result = new float[vector.Length];
        TensorPrimitives.Divide(vector, norm, result);
        return result;
    }

    private static byte[] ToBlob(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    private static float[] FromBlob(byte[] blob) => MemoryMarshal.Cast<byte, float>(blob).ToArray();

    // ------------------------------------------------------------------ plumbing

    private int CountChunks()
    {
        using var cmd = Command("SELECT COUNT(*) FROM chunks");
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT value FROM meta WHERE key = $k", ("$k", key));
            return cmd.ExecuteScalar() as string;
        }
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = Command("INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));
        cmd.ExecuteNonQuery();
    }

    private static IndexedDocument ReadDocument(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
        DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        r.GetInt32(6), r.GetInt32(7),
        DateTime.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private SqliteCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex Words();

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS documents (
          id         INTEGER PRIMARY KEY,
          path       TEXT NOT NULL UNIQUE,
          title      TEXT NOT NULL,
          hash       TEXT NOT NULL,
          size       INTEGER NOT NULL,
          modified   TEXT NOT NULL,
          pages      INTEGER NOT NULL,
          chunks     INTEGER NOT NULL,
          indexed_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS chunks (
          id          INTEGER PRIMARY KEY,
          document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
          ordinal     INTEGER NOT NULL,
          page        INTEGER NOT NULL,
          text        TEXT NOT NULL,
          embedding   BLOB NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_chunks_document ON chunks(document_id);

        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
          text, content = 'chunks', content_rowid = 'id', tokenize = 'unicode61 remove_diacritics 2');

        CREATE TRIGGER IF NOT EXISTS chunks_ai AFTER INSERT ON chunks BEGIN
          INSERT INTO chunks_fts(rowid, text) VALUES (new.id, new.text);
        END;
        CREATE TRIGGER IF NOT EXISTS chunks_ad AFTER DELETE ON chunks BEGIN
          INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
        END;
        """;
}
