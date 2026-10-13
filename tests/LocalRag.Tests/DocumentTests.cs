using System.IO.Compression;
using System.Text;
using LocalRag.Chunking;
using LocalRag.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace LocalRag.Tests;

public class DocumentReaderTests
{
    [Fact]
    public void Pdf_pages_keep_their_numbers()
    {
        using var tmp = new TempFolder();
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Travel expenses are refunded within 30 days.", 12, new PdfPoint(40, 750), font);
        builder.AddPage(PageSize.A4).AddText("Remote work is allowed two days a week.", 12, new PdfPoint(40, 750), font);
        string path = Path.Combine(tmp.Path, "policy.pdf");
        File.WriteAllBytes(path, builder.Build());

        var doc = DocumentReader.Read(path);

        Assert.Equal("policy", doc.Title);
        Assert.Equal(new[] { 1, 2 }, doc.Pages.Select(p => p.Number));
        Assert.Contains("refunded within 30 days", doc.Pages[0].Text);
        Assert.Contains("two days a week", doc.Pages[1].Text);
    }

    [Fact]
    public void Docx_paragraphs_tabs_and_breaks_are_read()
    {
        using var tmp = new TempFolder();
        string path = Path.Combine(tmp.Path, "memo.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("""
                <?xml version="1.0" encoding="UTF-8"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
                  <w:p><w:r><w:t>Quarterly</w:t></w:r><w:r><w:t xml:space="preserve"> report</w:t></w:r></w:p>
                  <w:p><w:r><w:t>Revenue</w:t><w:tab/><w:t>12%</w:t><w:br/><w:t>Costs down</w:t></w:r></w:p>
                </w:body></w:document>
                """);
        }

        var doc = DocumentReader.Read(path);

        Assert.Single(doc.Pages);
        Assert.Equal(0, doc.Pages[0].Number);
        Assert.Contains("Quarterly report", doc.Pages[0].Text);
        Assert.Contains("Revenue\t12%\nCosts down", doc.Pages[0].Text);
    }

    [Fact]
    public void Docx_with_a_dtd_is_rejected_instead_of_expanding_entities()
    {
        using var tmp = new TempFolder();
        string path = Path.Combine(tmp.Path, "evil.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write("""<?xml version="1.0"?><!DOCTYPE d [<!ENTITY a "aaaaaaaaaa">]><d>&a;</d>""");
        }

        var ex = Assert.Throws<DocumentReadException>(() => DocumentReader.Read(path));
        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public void Html_keeps_text_and_structure_but_drops_scripts()
    {
        string text = DocumentReader.HtmlToText("<html><head><style>p{}</style><script>alert(1)</script></head><body><h1>Title</h1><p>One &amp; two</p><p>Three</p></body></html>");
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("p{}", text);
        Assert.Contains("One & two", text);
        Assert.Contains("Title", text);
        Assert.Contains('\n', text);
    }

    [Fact]
    public void Text_files_are_decoded_with_bom_detection_and_a_legacy_fallback()
    {
        using var tmp = new TempFolder();
        string utf8 = Path.Combine(tmp.Path, "a.txt");
        File.WriteAllText(utf8, "Çalışma saatleri", new UTF8Encoding(true));
        string utf16 = Path.Combine(tmp.Path, "b.txt");
        File.WriteAllText(utf16, "Öğle arası", Encoding.Unicode);
        string latin1 = Path.Combine(tmp.Path, "c.txt");
        File.WriteAllBytes(latin1, Encoding.Latin1.GetBytes("café crème"));

        Assert.Equal("Çalışma saatleri", DocumentReader.ReadText(utf8));
        Assert.Equal("Öğle arası", DocumentReader.ReadText(utf16));
        Assert.Equal("café crème", DocumentReader.ReadText(latin1));
    }

    [Fact]
    public void Unsupported_and_missing_files_give_clear_errors()
    {
        using var tmp = new TempFolder();
        string exe = tmp.File("tool.exe", "MZ");
        Assert.Contains("unsupported", Assert.Throws<DocumentReadException>(() => DocumentReader.Read(exe)).Message);
        Assert.Contains("does not exist", Assert.Throws<DocumentReadException>(() => DocumentReader.Read(Path.Combine(tmp.Path, "nope.md"))).Message);
        Assert.True(DocumentReader.IsSupported("notes.MD"));
        Assert.False(DocumentReader.IsSupported("photo.jpg"));
    }
}

public class TextChunkerTests
{
    private static string Sentences(int count, string word = "policy") =>
        string.Join(" ", Enumerable.Range(1, count).Select(i => $"Sentence {i} talks about the {word} in some detail."));

    [Fact]
    public void Short_text_is_one_chunk()
    {
        var chunks = new TextChunker().SplitText("Hello world.\n\nSecond paragraph.").ToList();
        Assert.Single(chunks);
        Assert.Equal("Hello world.\n\nSecond paragraph.", chunks[0]);
    }

    [Fact]
    public void Long_text_is_split_within_the_limit_with_overlap()
    {
        var chunker = new TextChunker(maxChars: 400, overlapChars: 80);
        string text = string.Join("\n\n", Enumerable.Range(1, 12).Select(i => Sentences(3, "topic" + i)));

        var chunks = chunker.SplitText(text).ToList();

        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, 400));
        // consecutive chunks share text (the overlap)
        for (int i = 1; i < chunks.Count; i++)
        {
            string tail = chunks[i - 1][^30..];
            Assert.True(chunks[i].Contains(tail[..15]) || chunks[i - 1].Length < 80, $"chunk {i} does not overlap the previous one");
        }
    }

    [Fact]
    public void A_paragraph_longer_than_a_chunk_is_split_by_sentences_and_words()
    {
        var chunker = new TextChunker(maxChars: 300, overlapChars: 0);
        string longParagraph = Sentences(20);
        string noBreaks = string.Join(" ", Enumerable.Repeat("token", 200));

        var a = chunker.SplitText(longParagraph).ToList();
        var b = chunker.SplitText(noBreaks).ToList();

        Assert.All(a, c => Assert.True(c.Length <= 300));
        Assert.All(a, c => Assert.EndsWith(".", c));
        Assert.All(b, c => Assert.True(c.Length <= 300 && !c.StartsWith(' ')));
        Assert.Equal(noBreaks.Replace(" ", ""), string.Concat(b).Replace(" ", ""));
    }

    [Fact]
    public void Chunks_never_cross_pages_and_keep_the_page_number()
    {
        var doc = new LoadedDocument("x.pdf", "x", [new DocumentPage(1, Sentences(30)), new DocumentPage(2, "Short page two.")]);
        var chunks = new TextChunker(maxChars: 500, overlapChars: 50).Split(doc);

        Assert.Contains(chunks, c => c.Page == 1);
        Assert.Equal("Short page two.", chunks.Single(c => c.Page == 2).Text);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void Whitespace_is_normalised_and_empty_pages_give_nothing()
    {
        var chunker = new TextChunker();
        Assert.Empty(chunker.SplitText("   \n\n\t  "));
        Assert.Equal("a b\n\nc", chunker.SplitText("a    b  \r\n\r\n\r\n\r\nc").Single());
    }

    [Fact]
    public void Invalid_settings_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(maxChars: 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(maxChars: 400, overlapChars: 300));
    }
}
