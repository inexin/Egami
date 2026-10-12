using System.Numerics;

using Egami.Fonts;

namespace Egami.Drawing;

/// <summary>Where text on a path is anchored at its start point, as SVG's text-anchor.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="path"/>
/// </example>
public enum TextAnchor
{
    /// <summary>The text starts at the start point.</summary>
    Start,

    /// <summary>The text is centred on the start point.</summary>
    Middle,

    /// <summary>The text ends at the start point.</summary>
    End,
}

/// <summary>Which side of a path text stands on.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="path"/>
/// </example>
public enum TextPathSide
{
    /// <summary>The text runs along the path's direction, standing on its left (above a left-to-right line on a y-down canvas).</summary>
    Left,

    /// <summary>The text runs against the path's direction, standing on its right: the path walked backwards, as SVG's side="right".</summary>
    Right,
}

/// <summary>How <see cref="FontExtensions.ShapeOnPath(Font, string, Path, TextOnPathOptions?)"/> lays text along a path.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="path"/>
/// </example>
public sealed record class TextOnPathOptions
{
    private readonly float _startOffset;
    private readonly float _startFraction;
    private readonly float _baselineShift;
    private readonly TextAnchor _anchor;
    private readonly TextPathSide _side;

    /// <summary>Gets the default options: start at the path's start, anchored at the text's start, on the left side.</summary>
    public static TextOnPathOptions Default { get; } = new();

    /// <summary>Gets the start point's distance along the path in canvas units, added to <see cref="StartFraction"/>. Defaults to 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite.</exception>
    public float StartOffset { get => _startOffset; init => _startOffset = Finite(value); }

    /// <summary>Gets the start point as a fraction of the path's length, such as 0.5 for its middle, as SVG's percentage startOffset. Defaults to 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite.</exception>
    public float StartFraction { get => _startFraction; init => _startFraction = Finite(value); }

    /// <summary>Gets how the text sits at the start point. Defaults to <see cref="TextAnchor.Start"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public TextAnchor Anchor { get => _anchor; init => _anchor = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null); }

    /// <summary>Gets how far each glyph moves off the path along its normal, in canvas units: positive away from the side the text stands on (up for text above a line). Defaults to 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite.</exception>
    public float BaselineShift { get => _baselineShift; init => _baselineShift = Finite(value); }

    /// <summary>Gets the side of the path the text stands on. Defaults to <see cref="TextPathSide.Left"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public TextPathSide Side { get => _side; init => _side = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null); }

    /// <summary>Gets whether glyphs past the end of a path of one closed contour wrap round to its start, instead of being dropped. Defaults to false, as SVG.</summary>
    public bool WrapClosed { get; init; }

    private static float Finite(float value) => float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The value must be finite.");
}

public static partial class FontExtensions
{
    /// <summary>
    /// Lays text along a path by SVG's textPath rules: the text is shaped as one line, each glyph's advance midpoint is placed on the
    /// path at the start point plus its distance along the line, and the glyph is turned to the path's direction there and moved by
    /// the baseline shift along the normal. Glyphs whose midpoint falls before the path's start or past its end are left out, with
    /// their clusters. Contours are walked one after another as one path.
    /// </summary>
    /// <param name="font">The font.</param>
    /// <param name="text">The text; newlines draw nothing.</param>
    /// <param name="path">The path, in the same coordinates the run is drawn in.</param>
    /// <param name="options">Where the text starts and sits, or <see langword="null"/> for <see cref="TextOnPathOptions.Default"/>.</param>
    /// <returns>A run with a <see cref="GlyphTransform"/> per glyph, relative to (0, 0).</returns>
    /// <exception cref="ArgumentException"><paramref name="font"/> has no typeface.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="path"/>
    /// </example>
    public static GlyphRun ShapeOnPath(this Font font, string text, Path path, TextOnPathOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);
        if (font.Typeface is null)
        {
            throw new ArgumentException("The font has no typeface.", nameof(font));
        }

        options ??= TextOnPathOptions.Default;
        GlyphRun line = OneLine(font, text, out float width);
        var measure = new PathMeasure(path);
        float total = measure.TotalLength;
        var glyphs = new List<ushort>(line.Count);
        var transforms = new List<GlyphTransform>(line.Count);
        var clusters = new List<int>(line.Count);
        if (total <= 0 || line.Count == 0)
        {
            return GlyphRun.CreateTransformed(font, [], [], text);
        }

        float start = (options.StartFraction * total) + options.StartOffset;
        start -= options.Anchor switch
        {
            TextAnchor.Middle => width / 2,
            TextAnchor.End => width,
            _ => 0,
        };
        bool wrap = options.WrapClosed && measure.Contours.Count == 1 && measure.Contours[0].IsClosed;
        bool backwards = options.Side == TextPathSide.Right;
        for (int i = 0; i < line.Count; i++)
        {
            float advance = font.GetAdvance(line.Glyphs[i]);
            float mid = start + line.Positions[i].X + (advance / 2);
            if (wrap)
            {
                mid = ((mid % total) + total) % total;
            }
            else if (mid < 0 || mid > total)
            {
                continue;
            }

            (Vector2 point, Vector2 tangent) = PointAt(measure, backwards ? total - mid : mid);
            if (backwards)
            {
                tangent = -tangent;
            }

            // The glyph's midpoint on the path; the normal pointing to the text's top is the tangent turned a quarter anticlockwise on a y-down canvas.
            Vector2 normal = new(tangent.Y, -tangent.X);
            Vector2 origin = point - (tangent * (advance / 2)) + (normal * options.BaselineShift);
            glyphs.Add(line.Glyphs[i]);
            transforms.Add(new GlyphTransform(tangent.X, tangent.Y, origin.X, origin.Y));
            if (!line.Clusters.IsEmpty)
            {
                clusters.Add(line.Clusters[i]);
            }
        }

        return GlyphRun.CreateTransformed(font, [.. glyphs], [.. transforms], text, clusters.Count == glyphs.Count ? [.. clusters] : []);
    }

    // The text shaped as one line: each line of Shape's result follows the previous one along x.
    private static GlyphRun OneLine(Font font, string text, out float width)
    {
        GlyphRun run = font.Shape(text);
        if (run.Count == 0)
        {
            width = 0;
            return run;
        }

        float lineHeight = font.LineHeight;
        var positions = new Vector2[run.Count];
        float offset = 0, lineEnd = 0;
        int currentLine = 0;
        for (int i = 0; i < run.Count; i++)
        {
            int lineIndex = lineHeight > 0 ? (int)MathF.Round(run.Positions[i].Y / lineHeight) : 0;
            if (lineIndex != currentLine)
            {
                offset = lineEnd;
                currentLine = lineIndex;
            }

            positions[i] = new Vector2(offset + run.Positions[i].X, 0);
            lineEnd = MathF.Max(lineEnd, positions[i].X + font.GetAdvance(run.Glyphs[i]));
        }

        width = lineEnd;
        return GlyphRun.Create(font, run.Glyphs, positions, run.Text, run.Clusters);
    }

    // The point and direction at a distance along all contours walked in order.
    private static (Vector2 Point, Vector2 Tangent) PointAt(PathMeasure measure, float distance)
    {
        IReadOnlyList<ContourMeasure> contours = measure.Contours;
        for (int c = 0; c < contours.Count; c++)
        {
            ContourMeasure contour = contours[c];
            if (distance <= contour.Length || c == contours.Count - 1)
            {
                float d = Math.Clamp(distance, 0, contour.Length);
                return (contour.GetPosition(d), contour.GetTangent(d));
            }

            distance -= contour.Length;
        }

        return (Vector2.Zero, Vector2.UnitX);
    }
}
