using System.Numerics;

using BenchmarkDotNet.Attributes;

using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Pixels;

namespace Egami.Drawing.Benchmarks;

/// <summary>
/// Drawing a laid-out paragraph from a warm glyph cache: in one color, and with every fourth word in red, where lines hold a
/// run per color; and laying the colored paragraph out and drawing it.
/// </summary>
[MemoryDiagnoser]
public class TextLayoutBenchmarks
{
    private const float Width = 400;

    private Raster<Rgb8> _raster = null!;
    private RasterCanvas<Rgb8> _canvas = null!;
    private Paragraph _colored = null!;
    private TextLayout _plainLayout = null!;
    private TextLayout _coloredLayout = null!;

    [GlobalSetup]
    public void Setup()
    {
        string root = Root();
        string text = string.Join(' ', File.ReadAllLines(System.IO.Path.Combine(root, "tests", "reference", "shaping", "texts", "english.txt")).Skip(3).Take(4));
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(root, "tests", "fonts", "NotoSans-Regular.ttf"));
        ParagraphStyle style = new() { DefaultStyle = new TextStyle { FontFamilies = [fonts.Families[0]], Size = 13 } };
        _plainLayout = Paragraph.Create(text, style).Layout(Width);
        _colored = Paragraph.Create(Colored(text), style);
        _coloredLayout = _colored.Layout(Width);
        _raster = Raster.Create(420, 400, new Rgb8(255, 255, 255));
        _canvas = _raster.CreateCanvas();
        _canvas.DrawTextLayout(_plainLayout, Vector2.Zero);
        _canvas.DrawTextLayout(_coloredLayout, Vector2.Zero);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _canvas.Dispose();
        _raster.Dispose();
    }

    [Benchmark]
    public void DrawPlainLayout() => _canvas.DrawTextLayout(_plainLayout, Vector2.Zero);

    [Benchmark]
    public void DrawColoredLayout() => _canvas.DrawTextLayout(_coloredLayout, Vector2.Zero);

    [Benchmark]
    public void LayOutAndDrawColored() => _canvas.DrawTextLayout(_colored.Layout(Width), Vector2.Zero);

    /// <summary>The text with every fourth word in red, as syntax highlighting or a link colors words.</summary>
    private static RichText Colored(string text)
    {
        TextStyle red = new() { Color = new Rgba8(200, 0, 0, 255) };
        List<StyleSpan> spans = [];
        int word = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (i == 0 || text[i - 1] == ' ')
            {
                int end = text.IndexOf(' ', i);
                end = end < 0 ? text.Length : end;
                if (word++ % 4 == 3)
                {
                    spans.Add(new StyleSpan(i, end - i, red));
                }
            }
        }

        return new RichText(text, spans);
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
