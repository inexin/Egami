using System.IO.Compression;
using System.Text;

using Egami.Formats;
using Egami.Pixels;

namespace Egami.Svg.Tests;

/// <summary>Task 14: SvgFormat reads SVG and SVGZ through Raster.Load and Raster.Probe.</summary>
public class SvgFormatTests
{
    private static readonly EgamiContext s_context = EgamiContext.Default.WithFormat(SvgFormat.Info);

    private const string Small = """<svg xmlns="http://www.w3.org/2000/svg" width="4" height="3"><rect width="4" height="3" fill="#f00"/></svg>""";

    public static TheoryData<string> Corpus() => TestSvg.CorpusData();

    [Theory]
    [MemberData(nameof(Corpus))]
    public void TheCorpusLoadsAndProbes(string name)
    {
        string path = TestSvg.CorpusFile(name);
        SvgDocument document = SvgDocument.Load(path);
        int width = (int)Math.Max(1, Math.Ceiling(document.Size.X));
        int height = (int)Math.Max(1, Math.Ceiling(document.Size.Y));

        RasterInfo info = Raster.Probe(path, s_context);
        Assert.Same(SvgFormat.Info, info.Format);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);

        using Raster<Rgba8> raster = Raster.Load<Rgba8>(path, s_context);
        Assert.Equal(width, raster.Width);
        Assert.Equal(height, raster.Height);
        Assert.Equal("SVG", raster.Metadata.FormatName);

        // The same pixels as the document drawn at its size, with nothing outside the file opened.
        using Raster<Rgba8> direct = document.Rasterize<Rgba8>(new SvgRenderOptions { Width = width, Height = height });
        for (int y = 0; y < height; y++)
        {
            Assert.True(direct.Row(y).SequenceEqual(raster.Row(y)), $"Row {y} differs.");
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void TheCorpusLoadsAsSvgz(string name)
    {
        byte[] svgz = Gzip(File.ReadAllBytes(TestSvg.CorpusFile(name)));
        RasterInfo info = Raster.Probe(svgz, s_context);
        using Raster raster = Raster.Load(svgz, s_context);
        Assert.Equal(info.Width, raster.Width);
        Assert.Equal(info.Height, raster.Height);
        Assert.IsType<Raster<Rgba8>>(raster);
    }

    [Fact]
    public void ScaleMultipliesTheSize()
    {
        SvgDecodeOptions options = new() { Scale = 2.5f };
        byte[] data = Encoding.UTF8.GetBytes(Small);
        Assert.Equal((10, 8), (Raster.Probe(data, s_context, options).Width, Raster.Probe(data, s_context, options).Height));
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(data, s_context, options);
        Assert.Equal((10, 8), (raster.Width, raster.Height));
        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.Row(3)[5]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgDecodeOptions { Scale = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgDecodeOptions { Scale = float.NaN });
    }

    [Theory]
    [InlineData(Small)]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("\uFEFF<?xml version=\"1.0\"?>\n<!-- a > comment -->\n<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"3\"/>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\" [<!ENTITY a \"<b>\">]><svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<?xml-stylesheet href=\"a.css\"?><svg:svg xmlns:svg=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"3\"/>")]
    public void SvgIsFoundByItsRoot(string text)
    {
        Assert.Same(SvgFormat.Info, Raster.Probe(Encoding.UTF8.GetBytes(text), s_context).Format);
        Assert.Same(SvgFormat.Info, Raster.Probe(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text.TrimStart('\uFEFF'))).ToArray(), s_context).Format);
        Assert.Same(SvgFormat.Info, Raster.Probe(Gzip(Encoding.UTF8.GetBytes(text)), s_context).Format);
    }

    [Fact]
    public void ALongPrologIsFoundByItsDoctypeOrNamespace()
    {
        string comment = "<!--" + new string('x', 5000) + "-->";
        Assert.Same(SvgFormat.Info, Raster.Probe(Encoding.UTF8.GetBytes("<!DOCTYPE svg>" + comment + "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"), s_context).Format);
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(Encoding.UTF8.GetBytes(comment + "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"), s_context));
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"/>")]
    [InlineData("<svgx/>")]
    [InlineData("svg")]
    [InlineData("")]
    public void OtherDataIsNotSvg(string text)
    {
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(Encoding.UTF8.GetBytes(text), s_context));
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(Gzip(Encoding.UTF8.GetBytes(text)), s_context));
    }

    [Fact]
    public void OtherFormatsAreStillFound()
    {
        using Raster<Rgba8> raster = Raster.Create(3, 2, new Rgba8(1, 2, 3, 255));
        using MemoryStream png = new();
        raster.Save(png, Egami.Formats.Png.PngFormat.Info);
        Assert.Equal("PNG", Raster.Probe(png.ToArray(), s_context).Format.Name);

        // Data shorter than SVG's signature still reaches every format.
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe("P"u8.ToArray(), s_context));
    }

    [Fact]
    public void TheFormatOnlyReads()
    {
        Assert.Null(SvgFormat.Info.Writer);
        Assert.Equal(["svg", "svgz"], SvgFormat.Info.Extensions);
        Assert.Equal("image/svg+xml", SvgFormat.Info.MimeTypes[0]);
    }

    [Fact]
    public void NothingOutsideTheFileIsOpened()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-svgformat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            using (Raster<Rgba8> red = Raster.Create(4, 3, new Rgba8(255, 0, 0, 255)))
            {
                red.Save(System.IO.Path.Combine(folder, "red.png"));
            }

            string svg = System.IO.Path.Combine(folder, "linked.svg");
            File.WriteAllText(svg, """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="4" height="3"><image xlink:href="red.png" width="4" height="3"/></svg>""");
            using Raster<Rgba8> raster = Raster.Load<Rgba8>(svg, s_context);
            Assert.Equal(0, raster.Row(1)[1].A);
            Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Svg.ImageNotLoaded");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WarningsGoToTheRasterAndStrictThrows()
    {
        byte[] data = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" width="4" height="3"><rect width="x" height="3"/></svg>""");
        List<EgamiDiagnostic> sent = [];
        using (Raster<Rgba8> raster = Raster.Load<Rgba8>(data, s_context with { Diagnostics = sent.Add }))
        {
            Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Svg.BadAttribute");
        }

        Assert.Single(sent, w => w.Id == "Svg.BadAttribute");
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => Raster.Load<Rgba8>(data, s_context, new SvgDecodeOptions { Recovery = RecoveryMode.Strict }));
        Assert.IsType<SvgException>(e.InnerException);
        Assert.Throws<CorruptDataException>(() => Raster.Load(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><g>"), s_context));
    }

    [Fact]
    public void SafetyLimitsApply()
    {
        EgamiContext small = s_context with { Limits = new SafetyLimits { MaxPixels = 100 } };
        byte[] data = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"/>""");
        Assert.Equal(20, Raster.Probe(data, small).Width);
        Assert.Throws<LimitExceededException>(() => Raster.Load<Rgba8>(data, small));
        Assert.Throws<LimitExceededException>(() => Raster.Load<Rgba8>(Encoding.UTF8.GetBytes(Small), small, new SvgDecodeOptions { Scale = 10 }));
        Assert.Throws<LimitExceededException>(() => Raster.Load<Rgba8>(Encoding.UTF8.GetBytes(Small + new string(' ', 200)), s_context with { Limits = new SafetyLimits { MaxTotalBytes = 100 } }));
    }

    private static byte[] Gzip(byte[] data)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return output.ToArray();
    }
}
