using System.Text;
using System.Text.RegularExpressions;
using LocalRag.Documents;

namespace LocalRag.Chunking;

/// <summary>A piece of a document small enough to embed. <see cref="Page"/> is 0 for formats without pages.</summary>
public sealed record TextChunk(int Ordinal, int Page, string Text);

/// <summary>
/// Splits documents into overlapping chunks that follow the text's own structure: paragraphs first, then sentences,
/// then words. Chunks never cross a page boundary, so every chunk can be cited with its page number.
/// </summary>
public sealed partial class TextChunker
{
    public TextChunker(int maxChars = 1200, int overlapChars = 200)
    {
        if (maxChars < 200) throw new ArgumentOutOfRangeException(nameof(maxChars), "A chunk must allow at least 200 characters.");
        if (overlapChars < 0 || overlapChars >= maxChars / 2) throw new ArgumentOutOfRangeException(nameof(overlapChars), "The overlap must be smaller than half a chunk.");
        MaxChars = maxChars;
        OverlapChars = overlapChars;
    }

    public int MaxChars { get; }
    public int OverlapChars { get; }

    public IReadOnlyList<TextChunk> Split(LoadedDocument document)
    {
        var chunks = new List<TextChunk>();
        foreach (var page in document.Pages)
            foreach (string text in SplitText(page.Text))
                chunks.Add(new TextChunk(chunks.Count, page.Number, text));
        return chunks;
    }

    /// <summary>Splits one page of text into chunks of at most <see cref="MaxChars"/> characters.</summary>
    public IEnumerable<string> SplitText(string text)
    {
        var pieces = new List<string>();
        foreach (string paragraph in Paragraphs(Normalize(text)))
        {
            if (paragraph.Length <= MaxChars) pieces.Add(paragraph);
            else pieces.AddRange(SplitLong(paragraph));
        }

        var current = new StringBuilder();
        foreach (string piece in pieces)
        {
            int separator = current.Length == 0 ? 0 : 2;
            if (current.Length + separator + piece.Length > MaxChars && current.Length > 0)
            {
                string done = current.ToString().Trim();
                yield return done;
                current.Clear();
                string tail = Tail(done);
                if (tail.Length > 0 && tail.Length + 2 + piece.Length <= MaxChars) current.Append(tail).Append("\n\n");
            }
            if (current.Length > 0 && !current.ToString().EndsWith('\n')) current.Append("\n\n");
            current.Append(piece);
        }

        if (current.Length > 0 && current.ToString().Trim() is { Length: > 0 } last) yield return last;
    }

    /// <summary>The end of a finished chunk, cut at a sentence or word boundary, repeated at the start of the next one.</summary>
    private string Tail(string chunk)
    {
        if (OverlapChars == 0 || chunk.Length <= OverlapChars) return "";
        int start = chunk.Length - OverlapChars;
        var sentence = SentenceBoundary().Match(chunk, start);
        if (sentence.Success && sentence.Index + sentence.Length < chunk.Length) return chunk[(sentence.Index + sentence.Length)..].Trim();
        int space = chunk.IndexOf(' ', start);
        return space < 0 ? "" : chunk[(space + 1)..].Trim();
    }

    private IEnumerable<string> SplitLong(string paragraph)
    {
        var current = new StringBuilder();
        foreach (string sentence in Sentences(paragraph))
        {
            if (sentence.Length > MaxChars)
            {
                if (current.Length > 0) { yield return current.ToString().Trim(); current.Clear(); }
                foreach (string part in HardSplit(sentence)) yield return part;
                continue;
            }
            if (current.Length + sentence.Length + 1 > MaxChars && current.Length > 0)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(sentence);
        }
        if (current.Length > 0) yield return current.ToString().Trim();
    }

    /// <summary>Last resort for text without sentence breaks (tables, minified code): split at the last space that fits.</summary>
    private IEnumerable<string> HardSplit(string text)
    {
        int position = 0;
        while (position < text.Length)
        {
            int length = Math.Min(MaxChars, text.Length - position);
            if (position + length < text.Length)
            {
                int space = text.LastIndexOf(' ', position + length - 1, length);
                if (space > position + MaxChars / 2) length = space - position;
            }
            yield return text.Substring(position, length).Trim();
            position += length;
        }
    }

    internal static string Normalize(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ');
        text = TrailingSpace().Replace(text, "\n");
        text = HorizontalSpace().Replace(text, " ");
        return ManyBlankLines().Replace(text, "\n\n").Trim();
    }

    private static IEnumerable<string> Paragraphs(string text) =>
        text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> Sentences(string paragraph) =>
        SentenceSplit().Split(paragraph).Where(s => s.Length > 0);

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"[ \t\f\v]{2,}")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBlankLines();

    [GeneratedRegex(@"(?<=[.!?…。])\s+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"[.!?…。]\s+")]
    private static partial Regex SentenceBoundary();
}
