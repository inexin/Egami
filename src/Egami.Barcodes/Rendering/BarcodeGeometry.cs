using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;

namespace Egami.Barcodes.Rendering;

/// <summary>
/// What a linear symbol draws besides plain bars: the bars that reach down into the text line (EAN and UPC guards), where an
/// add-on starts (its bars leave room for its text above them), bearer bars around the symbol (ITF-14), and where each piece of
/// text goes. A symbol without one draws its text centered under the bars.
/// </summary>
/// <param name="TallRuns">For each run, whether it is a bar that extends into the text line; or <see langword="null"/>.</param>
/// <param name="AddOnStart">The module where an add-on symbol starts, or -1.</param>
/// <param name="Bearer">The thickness of the bearer bars around the symbol and its quiet zone, in modules, or 0.</param>
/// <param name="Segments">The pieces of text and where they go, or <see langword="null"/> for one centered line.</param>
internal sealed record class LinearDecoration(bool[]? TallRuns, int AddOnStart, int Bearer, TextSegment[]? Segments);

/// <summary>A piece of human-readable text centered between two module positions (from the symbol's first module), under the bars or above an add-on.</summary>
internal readonly record struct TextSegment(string Text, float Left, float Right, bool Above = false);

/// <summary>A symbol's size with its quiet zone and bearers, and its text, for one set of draw options.</summary>
internal sealed class BarcodeLayout
{
    private BarcodeLayout(Barcode barcode, BarcodeDrawOptions options)
    {
        Barcode = barcode;
        Options = options;
        QuietZone = options.QuietZone ? barcode.QuietZone : 0;
        Bearer = barcode.Decoration?.Bearer ?? 0;
        BarcodeModules modules = barcode.Modules;
        Columns = modules.Width + (2 * (QuietZone + Bearer));
        Rows = modules.IsLinear ? 0 : modules.Height + (2 * QuietZone);
        if (options.Font is Font font && !string.IsNullOrEmpty(barcode.HumanReadableText))
        {
            Font = font;
            TextMeasure measure = font.MeasureText(barcode.HumanReadableText);
            Ascent = measure.Ascent;
            TextLine = measure.Ascent - measure.Descent;
            HasText = true;
        }
    }

    public Barcode Barcode { get; }

    public BarcodeDrawOptions Options { get; }

    public int QuietZone { get; }

    public int Bearer { get; }

    /// <summary>Gets the width in modules with quiet zones and bearers.</summary>
    public int Columns { get; }

    /// <summary>Gets a 2D symbol's height in modules with its quiet zone.</summary>
    public int Rows { get; }

    public bool HasText { get; }

    public Font Font { get; }

    public float Ascent { get; }

    /// <summary>Gets the height of the text line, ascent to descent, in output units.</summary>
    public float TextLine { get; }

    public static BarcodeLayout Create(Barcode barcode, BarcodeDrawOptions options) => new(barcode, options);

    /// <summary>Returns the height the text takes under the symbol: one module of gap and the text line.</summary>
    public float TextBlock(float module) => HasText ? module + TextLine : 0;

    /// <summary>Returns the total height for a module size and, for a linear symbol, a bar height.</summary>
    public float Height(float moduleWidth, float moduleHeight, float barHeight) => Barcode.Modules.IsLinear
        ? (2 * Bearer * moduleWidth) + barHeight + TextBlock(moduleWidth)
        : (Rows * moduleHeight) + TextBlock(moduleHeight);
}

/// <summary>
/// Where a symbol goes: the top-left corner of its quiet zone (or bearers), the module size, the height of a linear symbol's bars,
/// and, when drawing snaps to pixels, how many device pixels a unit is, so derived lengths land on whole pixels too.
/// </summary>
internal readonly record struct BarcodeGeometry(Vector2 Origin, float ModuleWidth, float ModuleHeight, float BarHeight, float PixelsX = 0, float PixelsY = 0)
{
    /// <summary>
    /// Returns every dark rectangle: one per run of dark modules in a row (each bar for a linear symbol), then the bearer bars.
    /// All are wound the same way, so in one path neighbouring rectangles share edges and antialiasing leaves no seams.
    /// </summary>
    public List<RectF> Rectangles(BarcodeLayout layout)
    {
        Barcode barcode = layout.Barcode;
        BarcodeModules modules = barcode.Modules;
        var rects = new List<RectF>();
        float reduction = layout.Options.BarWidthReduction;
        (float shrinkLeft, float shrinkRight) = Split(reduction, PixelsX);
        float x0 = Origin.X + ((layout.QuietZone + layout.Bearer) * ModuleWidth);
        if (!modules.IsLinear)
        {
            (float shrinkTop, float shrinkBottom) = Split(reduction, PixelsY);
            float y0 = Origin.Y + (layout.QuietZone * ModuleHeight);
            for (int y = 0; y < modules.Height; y++)
            {
                ReadOnlySpan<bool> row = modules.Row(y);
                float top = y0 + (y * ModuleHeight) + shrinkTop, height = ModuleHeight - shrinkTop - shrinkBottom;
                for (int x = 0; x < row.Length;)
                {
                    if (!row[x])
                    {
                        x++;
                        continue;
                    }

                    int start = x;
                    while (x < row.Length && row[x])
                    {
                        x++;
                    }

                    Add(rects, x0 + (start * ModuleWidth) + shrinkLeft, top, ((x - start) * ModuleWidth) - shrinkLeft - shrinkRight, height);
                }
            }

            return rects;
        }

        LinearDecoration? decoration = barcode.Decoration;
        float barTop = Origin.Y + (layout.Bearer * ModuleWidth);
        float extension = 0, addOnTop = 0;
        if (layout.HasText && decoration is not null)
        {
            extension = SnapY(MathF.Min(5 * ModuleWidth, layout.TextBlock(ModuleWidth)));
            addOnTop = SnapY(layout.TextBlock(ModuleWidth));
        }

        ReadOnlySpan<int> runs = modules.Runs;
        int module = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            if (i % 2 == 0)
            {
                bool addOn = decoration is { AddOnStart: >= 0 } && module >= decoration.AddOnStart;
                bool tall = addOn || (decoration?.TallRuns is bool[] tallRuns && tallRuns[i]);
                float top = barTop + (addOn ? addOnTop : 0), bottom = barTop + BarHeight + (tall ? extension : 0);
                Add(rects, x0 + (module * ModuleWidth) + shrinkLeft, top, (runs[i] * ModuleWidth) - shrinkLeft - shrinkRight, bottom - top);
            }

            module += runs[i];
        }

        if (layout.Bearer > 0)
        {
            float thickness = layout.Bearer * ModuleWidth, width = layout.Columns * ModuleWidth;
            Add(rects, Origin.X, Origin.Y, width, thickness);
            Add(rects, Origin.X, Origin.Y + thickness + BarHeight, width, thickness);
            Add(rects, Origin.X, Origin.Y + thickness, thickness, BarHeight);
            Add(rects, Origin.X + width - thickness, Origin.Y + thickness, thickness, BarHeight);
        }

        return rects;
    }

    /// <summary>Returns the symbol as one path.</summary>
    public Path ToPath(BarcodeLayout layout)
    {
        var builder = new PathBuilder();
        foreach (RectF rect in Rectangles(layout))
        {
            builder.AddRect(rect);
        }

        return builder.ToPath();
    }

    /// <summary>Returns each piece of text with the left end of its baseline.</summary>
    public List<(string Text, Vector2 Origin)> Texts(BarcodeLayout layout)
    {
        var texts = new List<(string, Vector2)>();
        if (!layout.HasText)
        {
            return texts;
        }

        Barcode barcode = layout.Barcode;
        BarcodeModules modules = barcode.Modules;
        float x0 = Origin.X + ((layout.QuietZone + layout.Bearer) * ModuleWidth);
        float below = modules.IsLinear
            ? Origin.Y + (2 * layout.Bearer * ModuleWidth) + BarHeight + ModuleWidth + layout.Ascent
            : Origin.Y + (layout.Rows * ModuleHeight) + ModuleHeight + layout.Ascent;
        float above = Origin.Y + (layout.Bearer * ModuleWidth) + layout.Ascent;
        TextSegment[] segments = barcode.Decoration?.Segments ?? [new TextSegment(barcode.HumanReadableText!, 0, modules.Width)];
        foreach (TextSegment segment in segments)
        {
            float advance = layout.Font.MeasureText(segment.Text).Advance;
            float center = x0 + ((segment.Left + segment.Right) / 2 * ModuleWidth);
            texts.Add((segment.Text, new Vector2(center - (advance / 2), segment.Above ? above : below)));
        }

        return texts;
    }

    private static (float First, float Second) Split(float reduction, float pixels)
    {
        if (pixels <= 0)
        {
            return (reduction / 2, reduction / 2);
        }

        float whole = MathF.Round(reduction * pixels), first = MathF.Floor(whole / 2);
        return (first / pixels, (whole - first) / pixels);
    }

    private float SnapY(float length) => PixelsY > 0 ? MathF.Ceiling((length * PixelsY) - 1e-3f) / PixelsY : length;

    private static void Add(List<RectF> rects, float x, float y, float width, float height)
    {
        if (width > 0 && height > 0)
        {
            rects.Add(new RectF(x, y, width, height));
        }
    }
}
