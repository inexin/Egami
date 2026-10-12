using System.IO.Compression;
using System.Text;

namespace Egami.Svg.Tests;

/// <summary>Task 2: the XML reader, .svgz, limits, strictness and diagnostics.</summary>
public class XmlReadingTests
{
    // Tests of resvg's suite that are not well-formed XML on purpose, or not SVG at all.
    private static readonly HashSet<string> s_notXml = new(StringComparer.Ordinal)
    {
        "structure/svg/not-UTF-8-encoding",
        "structure/svg/not-an-svg",
        "structure/svg/xmlns-validation",
        "structure/svg/no-children",
    };

    [Theory]
    [MemberData(nameof(TestSvg.CorpusData), MemberType = typeof(TestSvg))]
    public void CorpusFilesLoad(string name)
    {
        SvgDocument document = SvgDocument.Load(TestSvg.CorpusFile(name));
        Assert.Equal("svg", document.Root.Name);
        Assert.True(document.Root.Descendants().Any());
    }

    [Fact]
    public void SuiteFilesLoad()
    {
        List<string> failed = [];
        foreach (string test in TestSvg.Suite())
        {
            try
            {
                SvgDocument.Load(TestSvg.SuiteFile(test));
            }
            catch (SvgException e) when (!s_notXml.Contains(test))
            {
                failed.Add($"{test}: {e.Message}");
            }
            catch (SvgException)
            {
            }
        }

        Assert.Empty(failed);
    }

    [Fact]
    public void TreeKeepsEverything()
    {
        const string text = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- before -->
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape" inkscape:version="1.4">
              <title>Hi</title>
              <style><![CDATA[rect { fill: red }]]></style>
              <inkscape:grid id="g1"/>
              <use xlink:href="#r"/>
              <text>a &amp; <tspan>b</tspan>!</text>
              <?pi data?>
            </svg>
            """;
        SvgDocument document = SvgDocument.Parse(text);
        SvgElement root = document.Root;
        Assert.Equal("Hi", document.Title);
        Assert.Equal("1.4", root.Attributes.Get("version", "http://www.inkscape.org/namespaces/inkscape"));
        SvgElement style = root.Elements("style").Single();
        Assert.True(Assert.IsType<SvgTextContent>(Assert.Single(style.Children)).IsCData);
        SvgElement grid = root.Elements().Single(e => e.Name == "grid");
        Assert.Equal("http://www.inkscape.org/namespaces/inkscape", grid.Namespace);
        Assert.Equal("inkscape", grid.Prefix);
        Assert.Same(grid, document.FindById("g1"));
        Assert.Equal("#r", root.Elements("use").Single().Attributes.Get("href", SvgNamespaces.XLink));
        SvgElement t = root.Elements("text").Single();
        Assert.Equal("a & b!", t.Text);
        Assert.Contains(root.Children, c => c is SvgProcessingInstruction { Target: "pi" });
        Assert.True(t.Line > 1);
    }

    [Fact]
    public void IllustratorEntitiesExpand()
    {
        const string text = """
            <?xml version="1.0"?>
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd" [
              <!ENTITY ns_svg "http://www.w3.org/2000/svg">
              <!ENTITY st0 "fill:#F00;">
            ]>
            <svg xmlns="&ns_svg;"><rect style="&st0;" width="1" height="1"/></svg>
            """;
        SvgDocument document = SvgDocument.Parse(text);
        Assert.True(document.Root.IsSvg);
        Assert.Equal("fill:#F00;", document.Root.Elements("rect").Single().Attributes["style"]);
    }

    [Fact]
    public void BillionLaughsStops()
    {
        StringBuilder dtd = new("<!DOCTYPE svg [<!ENTITY a0 \"lollollollollollollollollollol\">");
        for (int i = 1; i < 12; i++)
        {
            dtd.Append(System.Globalization.CultureInfo.InvariantCulture, $"<!ENTITY a{i} \"&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};&a{i - 1};\">");
        }

        dtd.Append("]>");
        string text = dtd + "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>&a11;</text></svg>";
        Assert.Throws<SvgException>(() => SvgDocument.Parse(text));
    }

    [Fact]
    public void ExternalEntitiesAreNeverOpened()
    {
        string secret = Path.Combine(Path.GetTempPath(), $"egami-svg-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "SECRET");
        try
        {
            string text = $"<!DOCTYPE svg [<!ENTITY x SYSTEM \"{new Uri(secret)}\">]><svg xmlns=\"http://www.w3.org/2000/svg\"><text>&x;</text></svg>";
            SvgDocument? document = null;
            try
            {
                document = SvgDocument.Parse(text);
            }
            catch (SvgException)
            {
            }

            Assert.DoesNotContain("SECRET", document?.Root.Text ?? "", StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void DeepNestingThrows()
    {
        string text = "<svg xmlns=\"http://www.w3.org/2000/svg\">" + string.Concat(Enumerable.Repeat("<g>", 300)) + string.Concat(Enumerable.Repeat("</g>", 300)) + "</svg>";
        SvgException e = Assert.Throws<SvgException>(() => SvgDocument.Parse(text));
        Assert.Contains("256", e.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ElementCountIsBounded()
    {
        string text = "<svg xmlns=\"http://www.w3.org/2000/svg\">" + string.Concat(Enumerable.Repeat("<rect/>", 10_001)) + "</svg>";
        EgamiContext context = new() { Limits = new SafetyLimits { MaxTotalBytes = 1 << 20 } };
        Assert.Throws<SvgException>(() => SvgDocument.Parse(text, new SvgLoadOptions { Context = context }));
        Assert.Equal(10_001, SvgDocument.Parse(text).Root.Elements().Count());
    }

    [Fact]
    public void FileSizeIsBounded()
    {
        string text = "<svg xmlns=\"http://www.w3.org/2000/svg\">" + new string(' ', 5000) + "</svg>";
        EgamiContext context = new() { Limits = new SafetyLimits { MaxTotalBytes = 1000 } };
        LimitExceededException e = Assert.Throws<LimitExceededException>(() => SvgDocument.Parse(text, new SvgLoadOptions { Context = context }));
        Assert.Equal(nameof(SafetyLimits.MaxTotalBytes), e.LimitName);
    }

    [Fact]
    public void SvgzLoadsAndGzipBombsStop()
    {
        byte[] svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\"><rect/></svg>");
        Assert.Equal("10", SvgDocument.Load(new MemoryStream(Gzip(svg))).Root.Attributes["width"]);

        byte[] bomb = Gzip(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><!--" + new string('x', 200_000) + "--></svg>"));
        EgamiContext context = new() { Limits = new SafetyLimits { MaxTotalBytes = 50_000 } };
        Assert.True(bomb.Length < 50_000);
        Assert.Throws<LimitExceededException>(() => SvgDocument.Load(new MemoryStream(bomb), new SvgLoadOptions { Context = context }));

        Assert.Throws<SvgException>(() => SvgDocument.Load(new MemoryStream([0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3])));
    }

    [Fact]
    public void StreamsThatCannotSeekLoad()
    {
        byte[] svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><circle r=\"5\"/></svg>");
        Assert.Single(SvgDocument.Load(new ForwardOnlyStream(svg)).Root.Elements("circle"));
        Assert.Single(SvgDocument.Load(new ForwardOnlyStream(Gzip(svg))).Root.Elements("circle"));
    }

    [Fact]
    public void BadXmlReportsLineAndColumn()
    {
        SvgException e = Assert.Throws<SvgException>(() => SvgDocument.Parse("<svg xmlns=\"http://www.w3.org/2000/svg\">\n  <rect></g>\n</svg>"));
        Assert.Equal(2, e.Line);
        Assert.True(e.Column > 0);
    }

    [Fact]
    public void RootMustBeSvg()
    {
        Assert.Throws<SvgException>(() => SvgDocument.Parse("<html xmlns=\"http://www.w3.org/1999/xhtml\"/>"));
        Assert.Throws<SvgException>(() => SvgDocument.Parse("<svg xmlns=\"urn:other\"/>"));
    }

    [Fact]
    public void MissingNamespaceIsLenientOrStrict()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add };
        SvgDocument document = SvgDocument.Parse("<svg><rect/></svg>", new SvgLoadOptions { Context = context });
        Assert.Equal("Svg.NoNamespace", Assert.Single(document.Warnings).Id);
        Assert.Equal("Svg.NoNamespace", Assert.Single(seen).Id);
        Assert.Equal("Svg", seen[0].Source);

        SvgException e = Assert.Throws<SvgException>(() => SvgDocument.Parse("<svg>\n<rect/></svg>", new SvgLoadOptions { Strict = true }));
        Assert.Equal(1, e.Line);
    }

    [Fact]
    public void EditsChangeTheVersion()
    {
        SvgDocument document = SvgDocument.Parse("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect id=\"a\"/></svg>");
        int version = document.Version;
        SvgElement rect = document.FindById("a")!;
        rect.Attributes["fill"] = "red";
        Assert.NotEqual(version, document.Version);
        version = document.Version;
        SvgElement circle = new("circle") { Id = "c" };
        document.Root.Children.Add(circle);
        Assert.NotEqual(version, document.Version);
        Assert.Same(circle, document.FindById("c"));
        Assert.Same(document, circle.Document);
        circle.Remove();
        Assert.Null(document.FindById("c"));
        Assert.Null(circle.Document);
        Assert.Throws<InvalidOperationException>(() => rect.Children.Add(rect));
        Assert.Throws<InvalidOperationException>(() => document.Root.Children.Add(rect));
        SvgElement copy = (SvgElement)rect.Clone();
        Assert.Equal("red", copy.Attributes["fill"]);
        Assert.Null(copy.Parent);
    }

    private static byte[] Gzip(byte[] data)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Position { get => base.Position; set => throw new NotSupportedException(); }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
