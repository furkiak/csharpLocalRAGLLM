using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LocalRag.Documents;

/// <summary>One page of a document. <see cref="Number"/> is 1-based, or 0 for formats without pages.</summary>
public sealed record DocumentPage(int Number, string Text);

/// <summary>The text of a document, page by page.</summary>
public sealed record LoadedDocument(string Path, string Title, IReadOnlyList<DocumentPage> Pages)
{
    public int CharacterCount => Pages.Sum(p => p.Text.Length);
}

/// <summary>Raised when a file cannot be turned into text (damaged, encrypted, too large, unsupported).</summary>
public sealed class DocumentReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads PDF, Word (.docx), HTML, Markdown, plain text and source code files.</summary>
public static partial class DocumentReader
{
    /// <summary>Files larger than this are skipped: they are rarely documents and would dominate the index.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".log", ".csv", ".tsv", ".json", ".xml", ".yml", ".yaml", ".ini", ".toml",
        ".cs", ".fs", ".vb", ".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".kt", ".go", ".rs", ".rb", ".php",
        ".c", ".h", ".cpp", ".hpp", ".swift", ".sql", ".sh", ".ps1", ".css", ".scss"
    };

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".xhtml" };

    /// <summary>Every extension this reader understands.</summary>
    public static IReadOnlyCollection<string> SupportedExtensions { get; } =
        TextExtensions.Concat(HtmlExtensions).Append(".pdf").Append(".docx").Order().ToArray();

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static LoadedDocument Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new DocumentReadException($"The file “{path}” does not exist.");
        if (info.Length > MaxFileBytes) throw new DocumentReadException($"“{info.Name}” is larger than {MaxFileBytes / (1024 * 1024)} MB and was skipped.");

        string extension = info.Extension;
        string title = Path.GetFileNameWithoutExtension(info.Name);
        try
        {
            IReadOnlyList<DocumentPage> pages = extension.ToLowerInvariant() switch
            {
                ".pdf" => ReadPdf(path),
                ".docx" => [new DocumentPage(0, ReadDocx(path))],
                _ when HtmlExtensions.Contains(extension) => [new DocumentPage(0, HtmlToText(ReadText(path)))],
                _ when TextExtensions.Contains(extension) => [new DocumentPage(0, ReadText(path))],
                _ => throw new DocumentReadException($"“{info.Name}” has an unsupported file type ({extension}).")
            };
            return new LoadedDocument(info.FullName, title, pages);
        }
        catch (DocumentReadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DocumentReadException($"“{info.Name}” could not be read: {ex.Message}", ex);
        }
    }

    // ------------------------------------------------------------------ formats

    private static List<DocumentPage> ReadPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var pages = new List<DocumentPage>(pdf.NumberOfPages);
        foreach (var page in pdf.GetPages())
        {
            string text = ContentOrderTextExtractor.GetText(page);
            if (!string.IsNullOrWhiteSpace(text)) pages.Add(new DocumentPage(page.Number, text));
        }
        return pages;
    }

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string ReadDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new DocumentReadException("The file is not a Word document.");
        using var stream = entry.Open();
        // No DTDs in Office XML: refusing them blocks entity-expansion attacks from crafted files.
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(xml);

        var sb = new StringBuilder();
        foreach (var paragraph in doc.Descendants(W + "p"))
        {
            foreach (var node in paragraph.Descendants())
            {
                if (node.Name == W + "t") sb.Append(node.Value);
                else if (node.Name == W + "tab") sb.Append('\t');
                else if (node.Name == W + "br" || node.Name == W + "cr") sb.Append('\n');
            }
            sb.Append("\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Reads text with BOM detection; files that are not valid UTF-8 fall back to Windows-1252.</summary>
    internal static string ReadText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
            return new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();

        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>A forgiving HTML to text conversion: drops scripts and styles, keeps block structure as line breaks.</summary>
    internal static string HtmlToText(string html)
    {
        string text = ScriptsAndStyles().Replace(html, " ");
        text = BlockTags().Replace(text, "\n");
        text = Tags().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return HorizontalSpace().Replace(text, " ");
    }

    [GeneratedRegex(@"<(script|style|noscript|template)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptsAndStyles();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/tr|/h[1-6]|/section|/article|/blockquote|/pre)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex HorizontalSpace();
}
