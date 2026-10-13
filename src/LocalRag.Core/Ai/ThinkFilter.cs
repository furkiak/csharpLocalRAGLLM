using System.Text;

namespace LocalRag.Ai;

/// <summary>
/// Removes the reasoning that thinking models (Qwen 3, DeepSeek-R1, gpt-oss…) wrap in &lt;think&gt;…&lt;/think&gt;
/// from a token stream. Tags may be split across tokens, so a possible partial tag is held back until it resolves.
/// </summary>
public sealed class ThinkFilter
{
    private static readonly string[] OpenTags = ["<think>", "<thinking>"];
    private static readonly string[] CloseTags = ["</think>", "</thinking>"];

    private readonly StringBuilder _pending = new();
    private bool _inside;
    private bool _started;

    /// <summary>Feeds one token and returns the visible text that is now certain (possibly empty).</summary>
    public string Push(string token)
    {
        _pending.Append(token);
        var output = new StringBuilder();

        while (_pending.Length > 0)
        {
            string text = _pending.ToString();
            string[] tags = _inside ? CloseTags : OpenTags;
            (int index, string? tag) = FindFirst(text, tags);

            if (tag is not null)
            {
                if (!_inside) output.Append(text[..index]);
                _pending.Remove(0, index + tag.Length);
                _inside = !_inside;
                continue;
            }

            // Keep a suffix that could still become a tag; everything before it is decided.
            int keep = PartialTagSuffix(text, tags);
            if (!_inside) output.Append(text[..^keep]);
            _pending.Remove(0, text.Length - keep);
            break;
        }

        return Visible(output.ToString());
    }

    /// <summary>Returns whatever is still held back once the stream has ended (an unclosed think block is dropped).</summary>
    public string Flush()
    {
        string rest = _inside ? "" : _pending.ToString();
        _pending.Clear();
        return Visible(rest);
    }

    /// <summary>Models often put blank lines after the closing tag; the answer should not start with them.</summary>
    private string Visible(string text)
    {
        if (_started) return text;
        string trimmed = text.TrimStart();
        if (trimmed.Length > 0) _started = true;
        return trimmed;
    }

    private static (int Index, string? Tag) FindFirst(string text, string[] tags)
    {
        int best = -1;
        string? found = null;
        foreach (string tag in tags)
        {
            int i = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (best < 0 || i < best)) { best = i; found = tag; }
        }
        return (best, found);
    }

    private static int PartialTagSuffix(string text, string[] tags)
    {
        int longest = 0;
        foreach (string tag in tags)
            for (int length = Math.Min(tag.Length - 1, text.Length); length > longest; length--)
                if (text.EndsWith(tag[..length], StringComparison.OrdinalIgnoreCase)) { longest = length; break; }
        return longest;
    }

    /// <summary>Convenience for a complete text.</summary>
    public static string Strip(string text)
    {
        var filter = new ThinkFilter();
        return filter.Push(text) + filter.Flush();
    }
}
