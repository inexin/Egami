using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Ocr;
using Egami.Pixels;

using Path = System.IO.Path;

namespace Egami.Samples.Ocr;

/// <summary>Draws a page of text, reads it back with an OCR engine and writes what the engine found as text, hOCR and ALTO.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="engineName">"tesseract" or "azure".</param>
    /// <param name="output">Where to write.</param>
    /// <returns>A task that completes when the files are written.</returns>
    public static async Task RunAsync(string engineName, string output)
    {
        IOcrEngine engine = engineName switch
        {
            "tesseract" => new TesseractEngine(),
            "azure" => AzureDocumentIntelligenceEngine.FromEnvironment(),
            _ => throw new ArgumentException($"Unknown engine '{engineName}': use tesseract or azure.", nameof(engineName)),
        };

        using Raster<Rgba8> page = DrawPage();
        page.Save(Path.Combine(output, "page.png"));

        OcrResult result = await engine.RecognizeAsync(new OcrRequest(page) { Dpi = 300, Languages = ["eng"] });
        Console.WriteLine($"{engine.Info.Name} found {result.Words.Count()} words:");
        Console.WriteLine(result.Text);

        File.WriteAllText(Path.Combine(output, "page.txt"), result.Text);
        using (FileStream hocr = File.Create(Path.Combine(output, "page.hocr")))
        {
            Hocr.Write(hocr, [result]);
        }

        using (FileStream alto = File.Create(Path.Combine(output, "page.xml")))
        {
            Alto.Write(alto, [result]);
        }
    }

    /// <summary>Draws three lines of text at about 12 points on a 300 dpi page strip, and one line turned to run up the margin.</summary>
    private static Raster<Rgba8> DrawPage()
    {
        Typeface face = Typeface.FromFile(Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.ttf"));
        Font body = new(face, 48);
        Raster<Rgba8> page = Raster.Create(1800, 700, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = page.CreateCanvas();
        canvas.DrawText("Egami reads text through any engine.", new Vector2(200, 160), body, Rgba8.Black);
        canvas.DrawText("The quick brown fox jumps over the lazy dog.", new Vector2(200, 280), body, Rgba8.Black);
        canvas.DrawText("Invoice 2026-10-23, total 1,234.50 NOK", new Vector2(200, 400), body, Rgba8.Black);
        return page;
    }
}
