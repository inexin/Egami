using System.Numerics;

using Egami.Ocr;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Ocr
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #region engine
    // An engine is one method: an image in, lines of words out. This one finds a single word; a real one wraps an OCR program,
    // an OS service or a cloud API (see samples/Ocr).
    public sealed class OneWordEngine : IOcrEngine
    {
        public OcrEngineInfo Info { get; } = new("one-word") { Languages = ["eng"], IsThreadSafe = true };

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            OcrQuad box = OcrQuad.FromBounds(10, 10, request.Image.Width / 2, 20);
            OcrLine line = new([new OcrWord("Duck", box, Confidence: 0.9, Language: "eng")]);
            return Task.FromResult(new OcrResult([line]) { ImageSize = new Size(request.Image.Width, request.Image.Height) });
        }
    }
    #endregion

    [Fact]
    public async Task Recognize()
    {
        using SampleFiles files = new();

        #region recognize
        // Any IOcrEngine reads a Raster<Rgba8>; say the resolution and languages when you know them.
        OneWordEngine engine = new();
        using Raster<Rgba8> page = Raster.Load<Rgba8>("duck.png");
        OcrResult result = await engine.RecognizeAsync(new OcrRequest(page) { Dpi = 300, Languages = ["eng"] }, Ct);

        Console.WriteLine(result.Text);
        foreach (OcrWord word in result.Words)
        {
            Console.WriteLine($"{word.Text} at {word.Box.Bounds}, {word.Confidence:P0} sure");
        }
        #endregion

        Assert.Equal("Duck", result.Text);
    }

    [Fact]
    public void Quads()
    {
        #region quads
        // Boxes are quads, corners named as the word reads, so turned and skewed words keep their shape.
        OcrQuad upright = OcrQuad.FromBounds(100, 50, 200, 40);
        OcrQuad upThePage = OcrQuad.FromBounds(20, 300, 40, 200, angle: 90); // TopLeft is the bottom left of the box
        Console.WriteLine($"{upThePage.Angle}° from {upThePage.TopLeft} to {upThePage.TopRight}");

        // Map boxes back from an image the engine saw at half size, or into another space.
        OcrQuad full = upright.Transform(Matrix3x2.CreateScale(2));
        #endregion

        Assert.Equal(90, upThePage.Angle, 3);
        Assert.Equal(new Rect(200, 100, 400, 80), full.Bounds);
    }

    [Fact]
    public void ReadAndWriteHocr()
    {
        using SampleFiles files = new();

        #region hocr
        // Read hOCR from Tesseract or any other engine, under limits for files you did not make.
        using FileStream file = File.OpenRead("page.hocr");
        IReadOnlyList<OcrResult> pages = Hocr.Read(file, new OcrReadOptions { MaxWords = 100_000 });
        Console.WriteLine($"{pages.Count} page, {pages[0].Words.Count()} words: {pages[0].Lines[0].Text}");

        // Write results back as hOCR (or Alto.Write for ALTO).
        using FileStream copy = File.Create("copy.hocr");
        Hocr.Write(copy, pages);
        #endregion

        Assert.Equal("Quarterly report: Fish & Chips", pages[0].Lines[0].Text);
    }

    [Fact]
    public void ReadAndWriteAlto()
    {
        using SampleFiles files = new();

        #region alto
        // ALTO from ABBYY, Tesseract or a library's digitization: positions in 1/10 mm or 1/1200 inch become pixels at Dpi.
        IReadOnlyList<OcrResult> pages = Alto.Parse(File.ReadAllText("page.xml"), new OcrReadOptions { Dpi = 300 });
        Console.WriteLine($"{pages[0].ImageSize}: {pages[0].Text}");

        // Turn it into hOCR, or write ALTO 4 in pixels.
        using FileStream hocr = File.Create("page.hocr");
        Hocr.Write(hocr, pages);
        using FileStream alto = File.Create("page-v4.xml");
        Alto.Write(alto, pages);
        #endregion

        Assert.Equal(new Size(2480, 3508), pages[0].ImageSize);
    }

    [Fact]
    public async Task CommandEngine()
    {
        Assert.SkipUnless(OnPath("tesseract"), "Tesseract is not installed.");
        using SampleFiles files = new();
        using Raster<Rgba8> page = Raster.Load<Rgba8>("duck.png");

        #region command
        // Any OCR program that writes hOCR or ALTO is an engine. No shell runs the command, and each run has its own temp folder.
        CommandOcrEngine tesseract = new("tesseract {image} {output} -l {langs} --dpi {dpi} hocr", new CommandOcrOptions
        {
            Languages = ["eng"],
            Timeout = TimeSpan.FromMinutes(2),
        });

        try
        {
            OcrResult result = await tesseract.RecognizeAsync(new OcrRequest(page), Ct);
            Console.WriteLine(result.Text);
        }
        catch (OcrEngineException e)
        {
            Console.WriteLine($"{e.Message} {e.ErrorOutput}");
        }
        #endregion

        Assert.Equal("tesseract", tesseract.Info.Name);
    }

    private static bool OnPath(string program)
    {
        string[] names = OperatingSystem.IsWindows() ? [program + ".exe"] : [program];
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Any(folder => names.Any(name => File.Exists(Path.Combine(folder, name))));
    }
}
