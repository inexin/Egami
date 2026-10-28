using System.Numerics;

using BenchmarkDotNet.Attributes;

using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Drawing.Benchmarks;

/// <summary>
/// Drawing a paragraph of small text from a warm glyph cache, grayscale and LCD, with the default text blend and with text gamma
/// and contrast (2.7.5): what the blend costs per glyph, since the masks are cached.
/// </summary>
[MemoryDiagnoser]
public class TextBenchmarks
{
    private const string Line = "The quick brown fox jumps over the lazy dog. Sphinx of black quartz, judge my vow! 0123456789";

    private Raster<Rgb8> _raster = null!;
    private RasterCanvas<Rgb8> _canvas = null!;
    private Font _gray;
    private Font _lcd;

    /// <summary>Gets or sets the text blend: Default, Gamma (gamma 1.4, contrast 0.25) or GdiLook (Gdi filter and sampling, gamma 1.25).</summary>
    [Params("Default", "Gamma", "GdiLook")]
    public string Blend { get; set; } = "Default";

    [GlobalSetup]
    public void Setup()
    {
        Typeface face = Typeface.FromFile(System.IO.Path.Combine(Root(), "tests", "fonts", "NotoSans-Regular.ttf"));
        _gray = new Font(face, 13) { Subpixel = false };
        _lcd = _gray with { Edging = FontEdging.Lcd, Hinting = FontHinting.Light };
        var options = new CanvasOptions { PixelGeometry = PixelGeometry.RgbHorizontal };
        if (Blend == "Gamma")
        {
            options = options with { TextGamma = 1.4f, TextContrast = 0.25f };
        }
        else if (Blend == "GdiLook")
        {
            options = options with { LcdFilter = LcdFilter.Gdi, LcdSampling = LcdSampling.Gdi, TextGamma = 1.25f };
        }

        _raster = Raster.Create(720, 400, new Rgb8(0, 0x78, 0xd4));
        _canvas = _raster.CreateCanvas(options);
        Draw(_gray);
        Draw(_lcd);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _canvas.Dispose();
        _raster.Dispose();
    }

    [Benchmark]
    public void GrayscaleParagraph() => Draw(_gray);

    [Benchmark]
    public void LcdParagraph() => Draw(_lcd);

    /// <summary>The LCD paragraph with an empty glyph cache: what making the masks costs.</summary>
    [Benchmark]
    public void LcdParagraphCold()
    {
        GlyphCache.Clear();
        Draw(_lcd);
    }

    private void Draw(Font font)
    {
        for (int i = 0; i < 20; i++)
        {
            _canvas.DrawText(Line, new Vector2(4, 18 + (i * 19)), font, Rgba8.White);
        }
    }

    private static string Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
