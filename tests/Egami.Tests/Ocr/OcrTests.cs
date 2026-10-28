using System.Numerics;
using System.Text;

using Egami.Ocr;
using Egami.Pixels;

namespace Egami.Tests.Ocr;

/// <summary>Egami 2.12 (Egap 5.7 stage A): the OCR engine types, hOCR and ALTO, and the command engine.</summary>
public class OcrTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "ocr");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FakeEngineGetsTheRequestAndItsWordsComeBack()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(200, 100);
        FakeEngine engine = new();
        OcrResult result = await engine.RecognizeAsync(new OcrRequest(image) { Dpi = 150, Languages = ["eng", "nor"], PageIndex = 3 }, Ct);

        Assert.Same(image, engine.Last!.Image);
        Assert.Equal(150, engine.Last.Dpi);
        Assert.Equal(["eng", "nor"], engine.Last.Languages);
        Assert.Equal(3, engine.Last.PageIndex);
        Assert.Equal("Hello world\nsecond", result.Text);
        Assert.Equal(3, result.Words.Count());
        Assert.Equal(new Rect(10, 10, 90, 20), result.Lines[0].Bounds);
        Assert.Equal(90, result.Rotation);
    }

    [Fact]
    public void RequestAndOptionsRejectBadValues()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcrRequest(image) { Dpi = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcrRequest(image) { Dpi = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcrReadOptions { MaxBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OcrReadOptions { MaxWords = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandOcrOptions { Timeout = TimeSpan.Zero });
        Assert.True(new OcrEngineInfo("x").IsThreadSafe);
    }

    [Theory]
    [InlineData(0, 10, 20, 60, 20)]
    [InlineData(90, 10, 40, 10, 20)]
    [InlineData(180, 60, 40, 10, 40)]
    [InlineData(270, 60, 20, 60, 40)]
    public void QuarterTurnQuadsReadInTheirDirection(int angle, float topLeftX, float topLeftY, float topRightX, float topRightY)
    {
        OcrQuad quad = OcrQuad.FromBounds(10, 20, 50, 20, angle);

        Assert.Equal(new Vector2(topLeftX, topLeftY), quad.TopLeft);
        Assert.Equal(new Vector2(topRightX, topRightY), quad.TopRight);
        Assert.Equal(angle, quad.Angle, 3);
        Assert.Equal(new Rect(10, 20, 50, 20), quad.Bounds);
    }

    [Fact]
    public void QuadBoundsRoundOutwardAndTransformMapsEveryCorner()
    {
        OcrQuad quad = new(new Vector2(1.5f, 2.5f), new Vector2(10.2f, 1.1f), new Vector2(11, 8.9f), new Vector2(2, 9.5f));
        Assert.Equal(new Rect(1, 1, 10, 9), quad.Bounds);
        Assert.InRange(quad.Angle, 9, 10);

        OcrQuad scaled = OcrQuad.FromBounds(1, 2, 3, 4).Transform(Matrix3x2.CreateScale(2) * Matrix3x2.CreateTranslation(5, 0));
        Assert.Equal(OcrQuad.FromBounds(7, 4, 6, 8), scaled);
    }

    [Fact]
    public void ReadsTesseractStyleHocr()
    {
        using FileStream file = File.OpenRead(Path.Combine(Images, "tesseract-style.hocr"));
        OcrResult page = Assert.Single(Hocr.Read(file));

        Assert.Equal(new Size(1240, 1754), page.ImageSize);
        Assert.Equal(["Quarterly report: Fish & Chips", "Blåbær <2026>", "Margin note"], page.Lines.Select(l => l.Text));
        OcrWord first = page.Lines[0].Words[0];
        Assert.Equal(OcrQuad.FromBounds(120, 140, 210, 50), first.Box);
        Assert.Equal(0.96, first.Confidence!.Value, 6);
        Assert.Equal("eng", first.Language);
        Assert.Equal("nor", page.Lines[1].Words[0].Language);

        // textangle 90: the words run up the page, so each starts at the bottom left of its box.
        OcrWord margin = page.Lines[2].Words[0];
        Assert.Equal(new Vector2(40, 900), margin.Box.TopLeft);
        Assert.Equal(90, margin.Box.Angle, 3);
        Assert.Equal(new Rect(40, 650, 50, 250), margin.Box.Bounds);
    }

    [Fact]
    public void ReadsLooseHtmlHocr()
    {
        OcrResult page = Assert.Single(Hocr.Parse(File.ReadAllText(Path.Combine(Images, "loose.hocr"))));

        Assert.Equal(["alone", "two words"], page.Lines.Select(l => l.Text));
        Assert.Null(page.Lines[0].Words[0].Confidence);
        Assert.Equal(0.5, page.Lines[1].Words[0].Confidence);
        Assert.Equal(default, page.ImageSize);
    }

    [Fact]
    public void HocrWithNoPageAndNoWordsIsCorrupt()
    {
        Assert.Throws<CorruptDataException>(() => Hocr.Parse("<html><body><p>just text</p></body></html>"));
        Assert.Throws<CorruptDataException>(() => Hocr.Parse(new string('<', 3000)));
        Assert.Throws<CorruptDataException>(() => Hocr.Parse(string.Concat(Enumerable.Repeat("<div>", 2000))));
    }

    [Fact]
    public void HocrLimitsHold()
    {
        string hocr = File.ReadAllText(Path.Combine(Images, "tesseract-style.hocr"));
        Assert.Equal("MaxWords", Assert.Throws<LimitExceededException>(() => Hocr.Parse(hocr, new OcrReadOptions { MaxWords = 3 })).LimitName);
        Assert.Equal("MaxBytes", Assert.Throws<LimitExceededException>(() => Hocr.Parse(hocr, new OcrReadOptions { MaxBytes = 100 })).LimitName);
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(hocr));
        Assert.Equal("MaxBytes", Assert.Throws<LimitExceededException>(() => Hocr.Read(stream, new OcrReadOptions { MaxBytes = 100 })).LimitName);
    }

    [Fact]
    public void ReadsAbbyyStyleAltoInTenthsOfAMillimetre()
    {
        IReadOnlyList<OcrResult> pages = Alto.Parse(File.ReadAllText(Path.Combine(Images, "abbyy-style.xml")), new OcrReadOptions { Dpi = 25.4 });

        Assert.Equal(2, pages.Count);
        OcrResult page = pages[0];
        Assert.Equal(new Size(210, 297), page.ImageSize);
        Assert.Equal(["Straßen bahn-", "haltestelle"], page.Lines.Select(l => l.Text));
        OcrWord first = page.Lines[0].Words[0];
        Assert.Equal(new Rect(25, 25, 31, 6), first.Box.Bounds);
        Assert.Equal(0.95, first.Confidence);
        Assert.Equal("de", first.Language);
        Assert.Equal("en", page.Lines[0].Words[1].Language);
        Assert.Null(page.Lines[1].Words[0].Confidence);
        Assert.Empty(pages[1].Lines);
    }

    [Fact]
    public void AltoThatIsNotAltoIsCorrupt()
    {
        Assert.Throws<CorruptDataException>(() => Alto.Parse("<html/>"));
        Assert.Throws<CorruptDataException>(() => Alto.Parse("<alto><Layout>"));
        Assert.Throws<CorruptDataException>(() => Alto.Parse("not xml"));
    }

    [Fact]
    public void AltoNeverFetchesOrExpandsAnEntityBomb()
    {
        const string Bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE alto [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c SYSTEM "file:///etc/passwd">]>
            <alto><Layout><Page WIDTH="10" HEIGHT="10"><TextLine><String HPOS="0" VPOS="0" WIDTH="1" HEIGHT="1" CONTENT="x"/></TextLine></Page></Layout></alto>
            """;
        OcrResult page = Assert.Single(Alto.Parse(Bomb));
        Assert.Equal("x", page.Text);
    }

    [Theory]
    [InlineData("hocr")]
    [InlineData("alto")]
    public void WritingAndReadingAgainKeepsEveryWord(string format)
    {
        OcrResult source = Assert.Single(Hocr.Parse(File.ReadAllText(Path.Combine(Images, "tesseract-style.hocr"))));
        source = source with { Lines = [.. source.Lines, new OcrLine([new OcrWord("bad\u0001char", OcrQuad.FromBounds(1, 1, 2, 2, 180))])] };
        using MemoryStream stream = new();
        if (format == "hocr")
        {
            Hocr.Write(stream, [source, OcrResult.Empty]);
        }
        else
        {
            Alto.Write(stream, [source, OcrResult.Empty]);
        }

        stream.Position = 0;
        IReadOnlyList<OcrResult> again = format == "hocr" ? Hocr.Read(stream) : Alto.Read(stream);

        Assert.Equal(2, again.Count);
        Assert.Equal(source.ImageSize, again[0].ImageSize);
        Assert.Equal(source.Text.Replace('\u0001', '\uFFFD'), again[0].Text);
        foreach ((OcrWord expected, OcrWord actual) in source.Words.Zip(again[0].Words))
        {
            Assert.Equal(expected.Box, actual.Box);
            Assert.Equal(expected.Confidence, actual.Confidence);
            Assert.Equal(expected.Language, actual.Language);
        }

        Assert.Empty(again[1].Lines);
    }

    [Fact]
    public void CommandWordsSplitAtSpacesAndKeepQuotes()
    {
        Assert.Equal(["tesseract", "{image}", "-", "-l", "{langs}", "hocr"], CommandOcrEngine.Split("tesseract {image} - -l {langs} hocr"));
        Assert.Equal(["C:\\Program Files\\Tesseract-OCR\\tesseract.exe", "--dpi={dpi}", "it's"], CommandOcrEngine.Split("\"C:\\Program Files\\Tesseract-OCR\\tesseract.exe\"  --dpi={dpi} \"it's\""));
        Assert.Equal([""], CommandOcrEngine.Split("''"));
        Assert.Equal("a{b}c", CommandOcrEngine.Substitute("a{{b}}c", _ => "x"));
        Assert.Equal("page-7.txt", CommandOcrEngine.Substitute("page-{page}.txt", _ => "7"));
    }

    [Fact]
    public void CommandsWithMistakesAreRejectedUpFront()
    {
        Assert.Throws<ArgumentException>(() => new CommandOcrEngine("   "));
        Assert.Throws<ArgumentException>(() => new CommandOcrEngine("tesseract \"{image}"));
        Assert.Throws<ArgumentException>(() => new CommandOcrEngine("tesseract {imgae} - hocr"));
        Assert.Throws<ArgumentException>(() => new CommandOcrEngine("tesseract {image"));
        CommandOcrEngine engine = new("/usr/bin/tesseract {image} - hocr", new CommandOcrOptions { Languages = ["eng"] });
        Assert.Equal("tesseract", engine.Info.Name);
        Assert.Equal(["eng"], engine.Info.Languages);
    }

    [Fact]
    public async Task CommandEngineReadsStandardOutput()
    {
        SkipWithoutShell();
        string fixture = Path.Combine(Images, "tesseract-style.hocr");
        CommandOcrEngine engine = new(Shell($"test -s \"$0\" && cat \"{fixture}\""), new CommandOcrOptions { Name = "fake" });
        using Raster<Rgba8> image = Raster.Create<Rgba8>(64, 32);

        OcrResult result = await engine.RecognizeAsync(new OcrRequest(image), Ct);

        Assert.Equal("fake", engine.Info.Name);
        Assert.Equal(7, result.Words.Count());
        Assert.Equal(new Size(1240, 1754), result.ImageSize);
    }

    [Fact]
    public async Task CommandEngineReadsTheOutputFileWithTheExtensionTheProgramAdds()
    {
        SkipWithoutShell();
        string fixture = Path.Combine(Images, "abbyy-style.xml");
        CommandOcrEngine engine = new(Shell($"cp \"{fixture}\" \"$1.xml\"", "{image} {output}"));
        using Raster<Rgba8> image = Raster.Create<Rgba8>(64, 32);

        OcrResult result = await engine.RecognizeAsync(new OcrRequest(image), Ct);

        Assert.Equal("Straßen bahn-\nhaltestelle", result.Text);
    }

    [Fact]
    public async Task CommandEnginePassesLanguagesDpiAndPage()
    {
        SkipWithoutShell();
        CommandOcrEngine engine = new(Shell("printf '<div class=ocr_page><span class=ocrx_word title=\"bbox 0 0 1 1\">%s|%s|%s</span></div>' \"$2\" \"$3\" \"$4\"", "{image} {page} {langs} {dpi} {page}"), new CommandOcrOptions { Languages = ["eng"] });
        using Raster<Rgba8> image = Raster.Create<Rgba8>(8, 8);

        OcrResult given = await engine.RecognizeAsync(new OcrRequest(image) { Languages = ["deu", "fra"], Dpi = 199.6, PageIndex = 4 }, Ct);
        OcrResult defaults = await engine.RecognizeAsync(new OcrRequest(image), Ct);

        Assert.Equal("deu+fra|200|4", given.Text);
        Assert.Equal("eng|300|0", defaults.Text);
        Assert.Equal(new Size(8, 8), defaults.ImageSize);
    }

    [Fact]
    public async Task CommandEngineFailuresAreOcrEngineExceptions()
    {
        SkipWithoutShell();
        using Raster<Rgba8> image = Raster.Create<Rgba8>(8, 8);
        OcrRequest request = new(image);

        OcrEngineException failed = await Assert.ThrowsAsync<OcrEngineException>(() => new CommandOcrEngine(Shell("echo broken >&2; exit 3")).RecognizeAsync(request, Ct));
        Assert.Equal(3, failed.ExitCode);
        Assert.Equal("broken", failed.ErrorOutput);

        await Assert.ThrowsAsync<OcrEngineException>(() => new CommandOcrEngine("egami-no-such-ocr-program {image}").RecognizeAsync(request, Ct));
        await Assert.ThrowsAsync<OcrEngineException>(() => new CommandOcrEngine(Shell("echo no hocr here")).RecognizeAsync(request, Ct));
        await Assert.ThrowsAsync<OcrEngineException>(() => new CommandOcrEngine(Shell("true")).RecognizeAsync(request, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => new CommandOcrEngine(Shell("echo \"$2\"", "{image} {page} {langs} {dpi} {page}")).RecognizeAsync(request, Ct));

        OcrEngineException slow = await Assert.ThrowsAsync<OcrEngineException>(() => new CommandOcrEngine(Shell("sleep 30"), new CommandOcrOptions { Timeout = TimeSpan.FromMilliseconds(300) }).RecognizeAsync(request, Ct));
        Assert.Null(slow.ExitCode);
    }

    [Fact]
    public async Task CommandEngineStopsOnCancel()
    {
        SkipWithoutShell();
        using Raster<Rgba8> image = Raster.Create<Rgba8>(8, 8);
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CommandOcrEngine(Shell("sleep 30")).RecognizeAsync(new OcrRequest(image), cancel.Token));
    }

    [Theory]
    [InlineData("gray", 1)]
    [InlineData("color", 3)]
    [InlineData("alpha", 4)]
    public async Task TheProgramGetsThePngWithTheChannelsTheImageNeeds(string kind, int channels)
    {
        // A gray scan goes out as a gray PNG, which Tesseract reads about a quarter faster than RGBA.
        using Raster<Rgba8> image = Raster.Create<Rgba8>(16, 8, new Rgba8(200, 200, 200, 255));
        image.Row(3)[5] = kind switch
        {
            "color" => new Rgba8(200, 20, 20, 255),
            "alpha" => new Rgba8(200, 200, 200, 128),
            _ => new Rgba8(30, 30, 30, 255),
        };
        string folder = Directory.CreateTempSubdirectory("egami-ocr-test-").FullName;
        try
        {
            string kept = Path.Combine(folder, "kept.png"), hocr = Path.Combine(folder, "page.hocr");
            File.WriteAllText(hocr, "<html><body><div class='ocr_page' title='bbox 0 0 16 8'></div></body></html>");
            string command = OperatingSystem.IsWindows()
                ? $"cmd /c copy /y {{image}} \"{kept}\" >NUL & type \"{hocr}\""
                : Shell($"cp \"$0\" '{kept}' && cat '{hocr}'");
            await new CommandOcrEngine(command, new CommandOcrOptions { Output = OcrOutputFormat.Hocr }).RecognizeAsync(new OcrRequest(image), Ct);

            Assert.Equal(channels, Raster.Probe(kept).Layout.ChannelCount);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A command that runs a script through sh with the placeholders as its arguments, from $0; without {output} the engine reads standard output.</summary>
    private static string Shell(string script, string arguments = "{image}")
    {
        return $"sh -c '{script.Replace("'", "'\"'\"'", StringComparison.Ordinal)}' {arguments}";
    }

    /// <summary>The command engine tests run scripts through sh, which Windows lacks; the engine itself is the same there.</summary>
    private static void SkipWithoutShell()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "These tests run OCR stand-ins through sh.");
    }

    private sealed class FakeEngine : IOcrEngine
    {
        public OcrRequest? Last { get; private set; }

        public OcrEngineInfo Info { get; } = new("fake") { Languages = ["eng"], FindsOrientation = true };

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            OcrLine first = new([new OcrWord("Hello", OcrQuad.FromBounds(10, 10, 40, 20), 0.9), new OcrWord("world", OcrQuad.FromBounds(60, 12, 40, 18))]);
            OcrLine second = new([new OcrWord("second", OcrQuad.FromBounds(10, 40, 60, 20), Language: "eng")]);
            return Task.FromResult(new OcrResult([first, second]) { Rotation = 90 });
        }
    }
}
