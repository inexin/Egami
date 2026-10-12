using System.Numerics;

namespace Egami.Fonts;

/// <summary>Styling a face lacks, which a matched <see cref="Font"/> asks the renderer to synthesize (drawn from 2.7.2 on).</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="match"/>
/// </example>
[Flags]
public enum FontSynthesis
{
    /// <summary>Nothing to synthesize.</summary>
    None = 0,

    /// <summary>The face is lighter than asked: embolden it.</summary>
    Bold = 1,

    /// <summary>The face is upright but italic or oblique was asked: slant it.</summary>
    Italic = 2,
}

/// <summary>
/// A typeface at a size: the em in canvas units (pixels on a raster, points on a PDF page). Scales the face's font-unit metrics and
/// turns text into a positioned <see cref="GlyphRun"/>.
/// </summary>
/// <remarks>
/// There is no DPI: a 12-unit font on a raster is 12 pixels per em, and the canvas transform scales it like any other drawing. Use
/// <see cref="FromPoints(Typeface, float, float)"/> to size from points at a resolution.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="caption"/>
/// </example>
public readonly record struct Font
{
    private readonly bool _noKerning;
    private readonly bool _noOpticalSizing;

    /// <summary>Initializes a font.</summary>
    /// <param name="typeface">The face.</param>
    /// <param name="size">The em size in canvas units, finite and at least 0.</param>
    /// <exception cref="ArgumentNullException"><paramref name="typeface"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is negative, infinite or NaN.</exception>
    public Font(Typeface typeface, float size)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        if (!float.IsFinite(size) || size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The size must be finite and at least 0.");
        }

        Typeface = typeface;
        Size = size;
    }

    /// <summary>Gets the face.</summary>
    public Typeface Typeface { get; }

    /// <summary>Gets the em size in canvas units.</summary>
    public float Size { get; }

    /// <summary>Gets whether pair kerning is applied when shaping. Defaults to true.</summary>
    public bool Kerning { get => !_noKerning; init => _noKerning = !value; }

    /// <summary>Gets the styling the face lacks and matching asked for; recorded in 2.7.1, drawn from 2.7.2 on.</summary>
    public FontSynthesis Synthesis { get; init; }

    /// <summary>
    /// Gets whether a variable face's optical size axis follows <see cref="Size"/>: <see cref="Fonts.OpticalSizing.Auto"/> (the
    /// default) sets opsz to the size in canvas units, clamped to the axis, unless the typeface sets opsz itself.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public OpticalSizing OpticalSizing
    {
        get => _noOpticalSizing ? OpticalSizing.None : OpticalSizing.Auto;
        init => _noOpticalSizing = Enum.IsDefined(value) ? value == OpticalSizing.None : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets the face glyphs are drawn and measured from: <see cref="Typeface"/>, or with <see cref="OpticalSizing"/> on a face with
    /// an opsz axis it does not set, the face at opsz = <see cref="Size"/>. A PDF writer embeds this face.
    /// </summary>
    public Typeface EffectiveTypeface
    {
        get
        {
            Typeface face = Typeface ?? throw new InvalidOperationException("The font has no typeface; use a constructor, not default.");
            return _noOpticalSizing ? face : face.AtOpticalSize(Size);
        }
    }

    /// <summary>Gets the factor from font units to canvas units: <see cref="Size"/> over units per em.</summary>
    public float Scale => Typeface is null ? 0 : Size / Typeface.UnitsPerEm;

    /// <summary>Gets the face's extents at this size, y up.</summary>
    public FontExtents Extents => Face.Extents.Scale(Scale);

    /// <summary>Gets the distance from one baseline to the next at this size.</summary>
    public float LineHeight => Face.Extents.LineHeight * Scale;

    private Typeface Face => EffectiveTypeface;

    /// <summary>Creates a font sized in points at a resolution: <c>points * dpi / 72</c> canvas units per em.</summary>
    /// <param name="typeface">The face.</param>
    /// <param name="points">The size in points.</param>
    /// <param name="dpi">The canvas units per inch, such as 96 for a screen or 72 for a PDF page.</param>
    /// <returns>The font.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size is negative, infinite or NaN, or <paramref name="dpi"/> is not positive.</exception>
    public static Font FromPoints(Typeface typeface, float points, float dpi)
    {
        if (!float.IsFinite(dpi) || dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "The resolution must be positive.");
        }

        return new Font(typeface, points * dpi / 72f);
    }

    /// <summary>Gets a glyph's advance at this size.</summary>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>The advance in canvas units.</returns>
    public float GetAdvance(ushort glyph) => Face.GetAdvance(glyph) * Scale;

    /// <summary>Gets the box around a glyph's ink at this size, y up from the baseline.</summary>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>The box in canvas units.</returns>
    public GlyphExtents GetGlyphExtents(ushort glyph) => Face.GetGlyphExtents(glyph).Scale(Scale);

    /// <summary>
    /// Turns text into a glyph run, the 2.7.1 way: one glyph per code point through the character map (variation selectors and
    /// surrogate pairs handled), pair kerning between neighbours, left to right. <c>\n</c>, <c>\r\n</c> and <c>\r</c> start a new
    /// line one <see cref="LineHeight"/> lower at x = 0; other control characters give no glyph; missing characters give .notdef.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The run, with the text and a cluster per glyph; positions are relative to the first baseline's left end, y down.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public GlyphRun Shape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Typeface face = Face;
        float scale = Scale, lineHeight = LineHeight;
        bool kern = Kerning && face.HasKerning;
        var glyphs = new ushort[text.Length];
        var positions = new Vector2[text.Length];
        var clusters = new int[text.Length];
        int count = 0, previous = -1, previousCodePoint = -1;
        float x = 0, y = 0, lastAdvance = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int start = i;
            int cp = Typeface.ReadCodePoint(text, ref i);
            if (cp is '\n' or '\r')
            {
                if (cp == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                x = 0;
                y += lineHeight;
                previous = -1;
                previousCodePoint = -1;
                continue;
            }

            if (cp < 0x20 || cp is >= 0x7F and <= 0x9F)
            {
                previous = -1;
                previousCodePoint = -1;
                continue;
            }

            if (Typeface.IsVariationSelector(cp) && previousCodePoint >= 0)
            {
                ushort varied = face.GetGlyph(previousCodePoint, cp);
                x -= lastAdvance;
                lastAdvance = face.GetAdvance(varied) * scale;
                x += lastAdvance;
                glyphs[count - 1] = varied;
                previous = varied;
                previousCodePoint = -1;
                continue;
            }

            ushort glyph = face.GetGlyph(cp);
            if (kern && previous >= 0)
            {
                x += face.GetKerning((ushort)previous, glyph) * scale;
            }

            glyphs[count] = glyph;
            positions[count] = new Vector2(x, y);
            clusters[count] = start;
            count++;
            lastAdvance = face.GetAdvance(glyph) * scale;
            x += lastAdvance;
            previous = glyph;
            previousCodePoint = cp;
        }

        return new GlyphRun(this, glyphs.AsSpan(0, count).ToArray(), positions.AsSpan(0, count).ToArray(), text, clusters.AsSpan(0, count).ToArray());
    }

    /// <summary>
    /// Measures text as <see cref="Shape(string)"/> lays it out, so a measured width always equals the drawn advance.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The widest line's advance, the exact ink bounds, the line count and the vertical metrics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public TextMeasure MeasureText(string text)
    {
        GlyphRun run = Shape(text);
        FontExtents extents = Extents;
        int lines = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        // \r alone ends a line too; \r\n counted once by its \n.
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n'))
            {
                lines++;
            }
        }

        return new TextMeasure(run.Advance, run.Bounds, lines, extents.Ascent, extents.Descent, LineHeight);
    }
}
