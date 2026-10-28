using System.Numerics;

using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

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
/// <para>
/// There is no DPI: a 12-unit font on a raster is 12 pixels per em, and the canvas transform scales it like any other drawing. Use
/// <see cref="FromPoints(Typeface, float, float)"/> to size from points at a resolution.
/// </para>
/// <para>
/// <c>default(Font)</c> has no typeface: its <see cref="Typeface"/> is <see langword="null"/>, its <see cref="Scale"/> 0, and the
/// members that need the face (metrics, advances, shaping and measuring) throw <see cref="InvalidOperationException"/>. Create
/// fonts with the constructor or <see cref="FromPoints(Typeface, float, float)"/>.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="caption"/>
/// </example>
public readonly record struct Font
{
    private const string NoTypeface = "A Font needs a typeface: create it with the constructor or Font.FromPoints, not default(Font).";

    private readonly bool _noKerning;
    private readonly bool _noOpticalSizing;
    private readonly bool _noSubpixel;
    private readonly bool _noSynthesis;
    // Stored as bytes so Font stays 40 bytes with Embolden and StemDarkening (2.7.5).
    private readonly byte _hinting;
    private readonly byte _edging;
    private readonly bool _noColorGlyphs;
    private readonly FontPalette? _palette;
    private readonly float _embolden;
    private readonly bool _stemDarkening;

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

    /// <summary>Gets the face; <see langword="null"/> only for <c>default(Font)</c>.</summary>
    public Typeface Typeface { get; }

    /// <summary>Gets the em size in canvas units.</summary>
    public float Size { get; }

    /// <summary>Gets whether pair kerning is applied when shaping. Defaults to true.</summary>
    public bool Kerning { get => !_noKerning; init => _noKerning = !value; }

    /// <summary>
    /// Gets the styling the face lacks and matching asked for: bold outsets the outline (em/24 at 9 pixels per em down to em/32 at
    /// 36 and above, advances unchanged) and italic slants it 14 degrees. Drawn, measured as ink and returned by glyph paths unless
    /// <see cref="AllowSynthesis"/> is off.
    /// </summary>
    public FontSynthesis Synthesis { get; init; }

    /// <summary>
    /// Gets how far outlines grow on every side, in canvas units at this size (pixels on a raster): 0.15 makes each stem 0.3 pixels
    /// heavier. Defaults to 0. Applied as synthetic bold is, with round joins, on top of it; advances do not change. Drawn, measured
    /// as ink and returned by glyph paths.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="embolden"/>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, infinite or NaN.</exception>
    public float Embolden
    {
        get => _embolden;
        init => _embolden = float.IsFinite(value) && value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Embolden must be finite and at least 0.");
    }

    /// <summary>
    /// Gets whether small text is darkened: outlines grow by 0.15 device pixels on every side at 8 pixels per em and below, fading
    /// linearly to none at 32 and above, so thin stems keep their weight where antialiasing would wash them out. Defaults to false.
    /// It follows the device size, so it applies when drawing on a raster and not to glyph paths or ink bounds.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="embolden"/>
    /// </example>
    public bool StemDarkening { get => _stemDarkening; init => _stemDarkening = value; }

    /// <summary>Gets whether <see cref="Synthesis"/> is applied. Defaults to true.</summary>
    public bool AllowSynthesis { get => !_noSynthesis; init => _noSynthesis = !value; }

    /// <summary>Gets how outlines are fitted to the pixel grid on a raster. Defaults to <see cref="FontHinting.None"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public FontHinting Hinting
    {
        get => (FontHinting)_hinting;
        init => _hinting = Enum.IsDefined(value) ? (byte)value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets whether glyph origins keep quarter-pixel x positions on a raster (four cached phases; y snaps to whole pixels), or
    /// snap to whole pixels as in 2.7.1. Defaults to true. Measuring never depends on it.
    /// </summary>
    public bool Subpixel { get => !_noSubpixel; init => _noSubpixel = !value; }

    /// <summary>Gets how glyph edges become pixels on a raster. Defaults to <see cref="FontEdging.Antialias"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public FontEdging Edging
    {
        get => (FontEdging)_edging;
        init => _edging = Enum.IsDefined(value) ? (byte)value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets whether color glyphs (COLR, sbix, CBDT) draw in color; false draws every glyph's outline in the paint. Defaults to true.
    /// </summary>
    public bool ColorGlyphs { get => !_noColorGlyphs; init => _noColorGlyphs = !value; }

    /// <summary>Gets the palette color glyphs draw with. Defaults to <see cref="FontPalette.Default"/>.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public FontPalette Palette { get => _palette ?? FontPalette.Default; init => _palette = value ?? throw new ArgumentNullException(nameof(value)); }

    /// <summary>Gets the synthesis drawn: <see cref="Synthesis"/>, or none when <see cref="AllowSynthesis"/> is off.</summary>
    internal FontSynthesis AppliedSynthesis => _noSynthesis ? FontSynthesis.None : Synthesis;

    /// <summary>
    /// The outset outlines grow by at a device size of <paramref name="pixelsPerEm"/>, in device units: synthetic bold's curve, plus
    /// <see cref="Embolden"/> scaled from canvas to device units, plus stem darkening when <paramref name="device"/>.
    /// </summary>
    internal float Outset(float pixelsPerEm, bool device)
    {
        float outset = (AppliedSynthesis & FontSynthesis.Bold) != 0 ? Fonts.Synthesis.BoldOutset(pixelsPerEm) : 0;
        if (_embolden > 0 && Size > 0)
        {
            outset += _embolden * MathF.Abs(pixelsPerEm) / Size;
        }

        return device && _stemDarkening ? outset + Fonts.Synthesis.DarkeningOutset(pixelsPerEm) : outset;
    }

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
    /// <exception cref="InvalidOperationException">The font is <c>default(Font)</c>, which has no typeface.</exception>
    public Typeface EffectiveTypeface
    {
        get
        {
            Typeface face = Typeface ?? throw new InvalidOperationException(NoTypeface);
            return _noOpticalSizing ? face : face.AtOpticalSize(Size);
        }
    }

    /// <summary>Gets the factor from font units to canvas units: <see cref="Size"/> over units per em.</summary>
    public float Scale => Typeface is null ? 0 : Size / Typeface.UnitsPerEm;

    /// <summary>Gets the face's extents at this size, y up.</summary>
    /// <exception cref="InvalidOperationException">The font is <c>default(Font)</c>, which has no typeface.</exception>
    public FontExtents Extents => Face.Extents.Scale(Scale);

    /// <summary>Gets the distance from one baseline to the next at this size.</summary>
    /// <exception cref="InvalidOperationException">The font is <c>default(Font)</c>, which has no typeface.</exception>
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
    /// <exception cref="InvalidOperationException">The font is <c>default(Font)</c>, which has no typeface.</exception>
    public float GetAdvance(ushort glyph) => Face.GetAdvance(glyph) * Scale;

    /// <summary>Gets the box around a glyph's ink at this size, y up from the baseline, synthetic bold and italic included.</summary>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>The box in canvas units.</returns>
    /// <exception cref="InvalidOperationException">The font is <c>default(Font)</c>, which has no typeface.</exception>
    public GlyphExtents GetGlyphExtents(ushort glyph)
    {
        FontSynthesis synthesis = AppliedSynthesis;
        if (synthesis == FontSynthesis.None && _embolden == 0)
        {
            return Face.GetGlyphExtents(glyph).Scale(Scale);
        }

        GlyphExtents e = (synthesis & FontSynthesis.Italic) != 0 ? Face.GetSkewedExtents(glyph, Fonts.Synthesis.Skew) : Face.GetGlyphExtents(glyph);
        e = e.Scale(Scale);
        float d = Outset(Size, false);
        if (d > 0 && !e.IsEmpty)
        {
            e = new GlyphExtents(e.XMin - d, e.YMin - d, e.XMax + d, e.YMax + d);
        }

        return e;
    }

    /// <summary>
    /// Turns text into a glyph run with the full shaper (<see cref="TextShaper.Shape(string, Font, ShapingOptions?)"/>): ligatures,
    /// kerning, marks and the script rules of the font's GSUB and GPOS, bidi order, one glyph run for every line. <c>\n</c>,
    /// <c>\r\n</c>, <c>\r</c>, U+0085, U+2028 and U+2029 start a new line one <see cref="LineHeight"/> lower at x = 0 (a vertical
    /// line one <see cref="LineHeight"/> to the left); other control characters give no glyph; characters the face lacks give .notdef.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The direction, language, script, features and spans, or <see langword="null"/> for
    /// <see cref="ShapingOptions.Default"/>. One run has one font, so the options may not ask for fallback or span fonts; draw such
    /// text from <see cref="TextShaper.Shape(string, Font, ShapingOptions?)"/>.</param>
    /// <returns>The run, with the text and a cluster per glyph; positions are relative to the first baseline's left end, y down.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> asks for fallback or a span in another font.</exception>
    /// <exception cref="InvalidOperationException">The font is <c>default(Font)</c>, which has no typeface.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="caption"/>
    /// </example>
    public GlyphRun Shape(string text, ShapingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Shape(text, text, options);
    }

    // Shapes text into one run, keeping source and clusters when source is given.
    private GlyphRun Shape(ReadOnlySpan<char> text, string? source, ShapingOptions? options)
    {
        _ = Typeface ?? throw new InvalidOperationException(NoTypeface);
        ShapingOptions o = options ?? ShapingOptions.Default;
        if (o.Fallback.Families.Count > 0 || o.Fallback.Resolver is not null)
        {
            throw new ArgumentException("A glyph run has one font; shape text with fallback through TextShaper.Shape.", nameof(options));
        }

        Font self = this;
        if (o.Spans is { } spans && spans.Any(s => s.Font is Font f && f != self))
        {
            throw new ArgumentException("A glyph run has one font; shape spans in other fonts through TextShaper.Shape.", nameof(options));
        }

        List<ShapedLine> lines = TextItemizer.ShapeLines(text, this, o);
        bool vertical = o.Direction == TextDirection.TopToBottom;
        float lineHeight = LineHeight;
        List<ushort> glyphs = new(text.Length);
        List<Vector2> positions = new(text.Length);
        List<int>? clusters = source is null ? null : new(text.Length);
        List<float> advances = new(text.Length);
        Script? script = null;
        bool oneScript = true;
        for (int l = 0; l < lines.Count; l++)
        {
            float x = vertical ? -l * lineHeight : 0, y = vertical ? 0 : l * lineHeight;
            foreach (ShapedRun run in lines[l].Runs)
            {
                oneScript &= script is null || script == run.Script;
                script ??= run.Script;
                ReadOnlySpan<GlyphPosition> p = run.Glyphs.Positions;
                for (int i = 0; i < p.Length; i++)
                {
                    glyphs.Add(run.Glyphs.GlyphIds[i]);
                    positions.Add(new Vector2(x + p[i].XOffset, y - p[i].YOffset));
                    clusters?.Add(run.Glyphs.Clusters[i]);
                    advances.Add(p[i].XAdvance);
                    x += p[i].XAdvance;
                    y -= p[i].YAdvance;
                }
            }
        }

        return new GlyphRun(this, [.. glyphs], [.. positions], source, clusters is null ? null : [.. clusters], null, [.. advances])
        {
            BidiLevel = lines.Count == 0 ? 0 : lines[0].ParagraphLevel,
            Script = oneScript && script is Script s ? s : Script.Common,
            Language = o.Language,
        };
    }

    /// <summary>
    /// Measures text as <see cref="Shape(string, ShapingOptions?)"/> lays it out, so a measured width always equals the drawn advance.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The shaping options, as for <see cref="Shape(string, ShapingOptions?)"/>.</param>
    /// <returns>The widest line's advance, the exact ink bounds, the line count and the vertical metrics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> asks for fallback or a span in another font.</exception>
    /// <exception cref="InvalidOperationException">The font is <c>default(Font)</c>, which has no typeface.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="measure"/>
    /// </example>
    public TextMeasure MeasureText(string text, ShapingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MeasureText(text.AsSpan(), options);
    }

    /// <summary>
    /// Measures text as <see cref="Shape(string, ShapingOptions?)"/> lays it out, as <see cref="MeasureText(string, ShapingOptions?)"/>
    /// does, without making a string of it: a slice of a larger text or a buffer you reuse.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The shaping options, as for <see cref="Shape(string, ShapingOptions?)"/>; span ranges index
    /// <paramref name="text"/>.</param>
    /// <returns>The widest line's advance, the exact ink bounds, the line count and the vertical metrics.</returns>
    /// <exception cref="ArgumentException"><paramref name="options"/> asks for fallback or a span in another font.</exception>
    /// <exception cref="InvalidOperationException">The font is <c>default(Font)</c>, which has no typeface.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="measure-span"/>
    /// </example>
    public TextMeasure MeasureText(ReadOnlySpan<char> text, ShapingOptions? options = null)
    {
        GlyphRun run = Shape(text, null, options);
        FontExtents extents = Extents;
        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    break;
                case '\r' or '\n' or '\u0085' or '\u2028' or '\u2029':
                    lines++;
                    break;
            }
        }

        return new TextMeasure(run.Advance, run.Bounds, lines, extents.Ascent, extents.Descent, LineHeight);
    }
}
