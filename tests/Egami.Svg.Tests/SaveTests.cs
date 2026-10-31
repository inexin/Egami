using System.Text;

using Egami.Drawing;

namespace Egami.Svg.Tests;

/// <summary>Task 5: Save writes the element tree back so it reads again the same.</summary>
public class SaveTests
{
    [Theory]
    [MemberData(nameof(TestSvg.CorpusData), MemberType = typeof(TestSvg))]
    public void CorpusFilesRoundTrip(string name) => AssertRoundTrip(SvgDocument.Load(TestSvg.CorpusFile(name)));

    [Trait("Category", "Slow")]
    [Fact]
    public void SuiteFilesRoundTrip()
    {
        int count = 0;
        foreach (string test in TestSvg.Suite())
        {
            SvgDocument document;
            try
            {
                document = SvgDocument.Load(TestSvg.SuiteFile(test));
            }
            catch (SvgException)
            {
                continue;
            }

            AssertRoundTrip(document, test);
            count++;
        }

        Assert.True(count > 1700);
    }

    [Fact]
    public void DeclarationDoctypeAndPrologComeBack()
    {
        const string text = """
            <?xml version="1.0" encoding="UTF-8" standalone="no"?>
            <!DOCTYPE svg [ <!ENTITY c "#F00"> ]>
            <!-- made by hand -->
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:x="http://www.w3.org/1999/xlink"><rect fill="&c;" x:title="t"/></svg>
            <!-- after -->
            """;
        string saved = Save(SvgDocument.Parse(text));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"", saved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<!DOCTYPE svg [", saved, StringComparison.Ordinal);
        Assert.Contains("<!-- made by hand -->", saved, StringComparison.Ordinal);
        Assert.Contains("<!-- after -->", saved, StringComparison.Ordinal);
        Assert.Contains("fill=\"#F00\"", saved, StringComparison.Ordinal);
        Assert.Contains("x:title=\"t\"", saved, StringComparison.Ordinal);
        Assert.Contains("xmlns:x=\"http://www.w3.org/1999/xlink\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentsMadeInCodeSave()
    {
        SvgElement root = new("svg");
        root.Attributes.Set("width", 10.123456);
        SvgElement path = new("path");
        path.Attributes.SetPath("d", Egami.Drawing.Path.Rect(new RectF(0.11111f, 0, 1, 1)));
        path.Attributes.Set("href", "#a", SvgNamespaces.XLink);
        root.Children.Add(path);
        root.Children.Add(new SvgTextContent("a < b & c"));
        root.Children.Add(new SvgTextContent("x ]]> y", isCData: true));
        root.Children.Add(new SvgComment("a -- b-"));
        SvgDocument document = new(root);

        string saved = Save(document, new SvgSaveOptions { Precision = 2 });
        Assert.Contains("width=\"10.12\"", saved, StringComparison.Ordinal);
        Assert.Contains("M0.11 0", saved, StringComparison.Ordinal);
        Assert.Contains("xlink:href=\"#a\"", saved, StringComparison.Ordinal);
        Assert.Contains("a &lt; b &amp; c", saved, StringComparison.Ordinal);
        Assert.Equal("10.123456", root.Attributes["width"]);
        SvgDocument again = SvgDocument.Parse(saved);
        Assert.Equal("a < b & cx ]]> y", again.Root.Text);
        Assert.Equal("#a", again.Root.Elements("path").Single().Attributes.Get("href", SvgNamespaces.XLink));
    }

    [Fact]
    public void GzipSavesSvgz()
    {
        SvgDocument document = SvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg' width='3' height='4'/>");
        using MemoryStream stream = new();
        document.Save(stream, new SvgSaveOptions { Gzip = true });
        Assert.Equal(0x1F, stream.ToArray()[0]);
        stream.Position = 0;
        Assert.Equal("3", SvgDocument.Load(stream).Root.Attributes["width"]);
    }

    internal static string Save(SvgDocument document, SvgSaveOptions? options = null)
    {
        using MemoryStream stream = new();
        document.Save(stream, options);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void AssertRoundTrip(SvgDocument document, string? name = null)
    {
        string saved = Save(document);
        SvgDocument again;
        try
        {
            again = SvgDocument.Parse(saved);
        }
        catch (SvgException e)
        {
            Assert.Fail($"{name}: the saved file does not read: {e.Message}");
            return;
        }

        AssertSame(document.Root, again.Root, name);
    }

    private static void AssertSame(SvgElement expected, SvgElement actual, string? name)
    {
        Assert.True(expected.Name == actual.Name && expected.Namespace == actual.Namespace, $"{name}: {expected} became {actual}");
        var a = expected.Attributes.Where(x => x.Namespace != SvgNamespaces.Xmlns).Select(x => (x.Namespace, x.Name, x.Value)).Order().ToList();
        var b = actual.Attributes.Where(x => x.Namespace != SvgNamespaces.Xmlns).Select(x => (x.Namespace, x.Name, x.Value)).Order().ToList();
        Assert.True(a.SequenceEqual(b), $"{name}: attributes of {expected} changed");
        // Indentation may add whitespace between the children of an element that holds no text; it means nothing there.
        bool textElement = expected.Name is "text" or "tspan" or "textPath" or "title" or "desc" or "style" or "script";
        List<SvgContent> x = [.. expected.Children.Where(c => c is not SvgTextContent t || (textElement ? t.Value.Length > 0 : !string.IsNullOrWhiteSpace(t.Value)))];
        List<SvgContent> y = [.. actual.Children.Where(c => c is not SvgTextContent t || (textElement ? t.Value.Length > 0 : !string.IsNullOrWhiteSpace(t.Value)))];
        Assert.True(Text(x) == Text(y), $"{name}: the text in {expected} changed");
        List<SvgElement> xe = [.. x.OfType<SvgElement>()];
        List<SvgElement> ye = [.. y.OfType<SvgElement>()];
        Assert.True(xe.Count == ye.Count, $"{name}: {expected} has {xe.Count} children, then {ye.Count}");
        for (int i = 0; i < xe.Count; i++)
        {
            AssertSame(xe[i], ye[i], name);
        }
    }

    private static string Text(List<SvgContent> items) => string.Concat(items.OfType<SvgTextContent>().Select(t => t.Value));
}
