using Egami.Fonts.Shaping;

namespace Egami.Fonts;

/// <summary>Which way a math glyph grows: up and down for delimiters and radicals, left and right for arrows and accents.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="math"/>
/// </example>
public enum MathDirection
{
    /// <summary>Taller variants and vertical assemblies, such as for parentheses, braces and radical signs.</summary>
    Vertical = 0,

    /// <summary>Wider variants and horizontal assemblies, such as for arrows, overbraces and wide accents.</summary>
    Horizontal = 1,
}

/// <summary>A corner of a glyph's math kerning: where a superscript (top) or subscript (bottom) attaches, on the right or left.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="math"/>
/// </example>
public enum MathKernCorner
{
    /// <summary>The top right, where a superscript attaches.</summary>
    TopRight = 0,

    /// <summary>The top left, where a prescript superscript attaches.</summary>
    TopLeft = 1,

    /// <summary>The bottom right, where a subscript attaches.</summary>
    BottomRight = 2,

    /// <summary>The bottom left, where a prescript subscript attaches.</summary>
    BottomLeft = 3,
}

/// <summary>A larger form of a math glyph: the glyph and its full height (vertical) or width (horizontal), in font units.</summary>
/// <param name="Glyph">The variant's glyph.</param>
/// <param name="Advance">Its full advance in the direction it grows, in font units.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="math"/>
/// </example>
public readonly record struct MathGlyphVariant(ushort Glyph, float Advance);

/// <summary>
/// One part of a glyph assembly, such as the top hook, an extender or the middle of a brace. Parts are listed bottom to top for a
/// vertical assembly and left to right for a horizontal one; consecutive parts overlap by up to their connector lengths.
/// </summary>
/// <param name="Glyph">The part's glyph.</param>
/// <param name="StartConnectorLength">How much of the part, at its start, may overlap the part before it, in font units.</param>
/// <param name="EndConnectorLength">How much of the part, at its end, may overlap the part after it, in font units.</param>
/// <param name="FullAdvance">The part's full advance in the direction of the assembly, in font units.</param>
/// <param name="IsExtender">Whether the part may be repeated, or left out, to reach the size wanted.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="math"/>
/// </example>
public readonly record struct MathGlyphPart(ushort Glyph, float StartConnectorLength, float EndConnectorLength, float FullAdvance, bool IsExtender);

/// <summary>How to build a math glyph of any size from parts, when none of its variants is large enough.</summary>
/// <param name="ItalicsCorrection">The italics correction of the assembled glyph, in font units.</param>
/// <param name="Parts">The parts, bottom to top or left to right.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="math"/>
/// </example>
public sealed record class MathGlyphAssembly(float ItalicsCorrection, IReadOnlyList<MathGlyphPart> Parts);

/// <summary>
/// The math layout data of a face, from its OpenType <c>MATH</c> table: the constants that place scripts, fractions, radicals and
/// limits, per-glyph italics corrections, top accent attachments and kerning, and the larger variants and assemblies of
/// stretchy glyphs. Get it from <see cref="Typeface.Math"/>. Every distance is in font units (see
/// <see cref="Typeface.UnitsPerEm"/>), as the face's design gives it: device table and variation adjustments are not applied.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="math"/>
/// </example>
public sealed class TypefaceMath
{
    private const int ValueRecordSize = 4;
    private const int ConstantsSize = 214;

    private readonly ReadOnlyMemory<byte> data;
    private readonly int glyphInfo;
    private readonly int variants;

    private TypefaceMath(ReadOnlyMemory<byte> data, MathConstants constants)
    {
        this.data = data;
        ReadOnlySpan<byte> d = data.Span;
        Constants = constants;
        glyphInfo = OtData.Off16(d, 0, 6);
        variants = OtData.Off16(d, 0, 8);
        MinConnectorOverlap = variants == 0 ? 0 : OtData.U16(d, variants);
    }

    /// <summary>Gets the face's math constants.</summary>
    public MathConstants Constants { get; }

    /// <summary>Gets how much, at least, the parts of a glyph assembly overlap, in font units.</summary>
    public float MinConnectorOverlap { get; }

    /// <summary>Reads a <c>MATH</c> table; null when it is too short or not version 1.</summary>
    internal static TypefaceMath? Read(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> d = data.Span;
        if (d.Length < 10 || OtData.U16(d, 0) != 1)
        {
            return null;
        }

        int constants = OtData.Off16(d, 0, 4);
        return new TypefaceMath(data, constants == 0 || constants + ConstantsSize > d.Length ? new MathConstants() : MathConstants.Read(d, constants));
    }

    /// <summary>
    /// The italics correction of <paramref name="glyph"/>: how far a superscript or a limit moves right of a slanted glyph, in font
    /// units; null when the face gives none.
    /// </summary>
    /// <param name="glyph">The glyph.</param>
    public float? GetItalicsCorrection(ushort glyph) => CoveredValue(OtData.Off16(data.Span, glyphInfo, glyphInfo + 0), glyph);

    /// <summary>
    /// Where an accent above <paramref name="glyph"/> centres, from the glyph's origin, in font units; null when the face gives
    /// none, and the accent then centres on the glyph's advance.
    /// </summary>
    /// <param name="glyph">The glyph.</param>
    public float? GetTopAccentAttachment(ushort glyph) => CoveredValue(OtData.Off16(data.Span, glyphInfo, glyphInfo + 2), glyph);

    /// <summary>Whether <paramref name="glyph"/> is an extended shape, such as a large operator, whose scripts are placed by its ink.</summary>
    /// <param name="glyph">The glyph.</param>
    public bool IsExtendedShape(ushort glyph)
    {
        ReadOnlySpan<byte> d = data.Span;
        return glyphInfo != 0 && OtData.Coverage(d, OtData.Off16(d, glyphInfo, glyphInfo + 4), glyph) >= 0;
    }

    /// <summary>
    /// The kerning at one corner of <paramref name="glyph"/> for a script whose nearest edge is at <paramref name="height"/>
    /// (from the baseline, in font units): positive moves the script away, negative closer. 0 when the face gives none.
    /// </summary>
    /// <param name="glyph">The glyph.</param>
    /// <param name="corner">The corner.</param>
    /// <param name="height">The height at which the script meets the glyph.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="corner"/> is not a defined corner.</exception>
    public float GetKerning(ushort glyph, MathKernCorner corner, float height)
    {
        if (!Enum.IsDefined(corner))
        {
            throw new ArgumentOutOfRangeException(nameof(corner), corner, "Not a corner.");
        }

        ReadOnlySpan<byte> d = data.Span;
        int info = glyphInfo == 0 ? 0 : OtData.Off16(d, glyphInfo, glyphInfo + 6);
        if (info == 0)
        {
            return 0;
        }

        int index = OtData.Coverage(d, OtData.Off16(d, info, info), glyph);
        if (index < 0 || index >= OtData.U16(d, info + 2))
        {
            return 0;
        }

        int kern = OtData.Off16(d, info, info + 4 + (index * 8) + ((int)corner * 2));
        if (kern == 0)
        {
            return 0;
        }

        // The heights split the glyph into bands, the lowest below the first height, each with its kern.
        int count = OtData.U16(d, kern);
        int band = 0;
        while (band < count && height >= OtData.S16(d, kern + 2 + (band * ValueRecordSize)))
        {
            band++;
        }

        return OtData.S16(d, kern + 2 + (count * ValueRecordSize) + (band * ValueRecordSize));
    }

    /// <summary>
    /// The larger forms of <paramref name="glyph"/> in one direction, smallest first; the first is usually the glyph itself. Empty
    /// when the glyph does not grow that way.
    /// </summary>
    /// <param name="glyph">The glyph.</param>
    /// <param name="direction">The direction it grows.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction"/> is not a defined direction.</exception>
    public IReadOnlyList<MathGlyphVariant> GetVariants(ushort glyph, MathDirection direction)
    {
        ReadOnlySpan<byte> d = data.Span;
        int construction = Construction(d, glyph, direction);
        if (construction == 0)
        {
            return [];
        }

        int count = OtData.U16(d, construction + 2);
        count = Math.Min(count, Math.Max(0, (d.Length - construction - 4) / 4));
        MathGlyphVariant[] result = new MathGlyphVariant[count];
        for (int i = 0; i < count; i++)
        {
            int at = construction + 4 + (i * 4);
            result[i] = new MathGlyphVariant(OtData.U16(d, at), OtData.U16(d, at + 2));
        }

        return result;
    }

    /// <summary>How to build <paramref name="glyph"/> from parts in one direction; null when the face gives no assembly for it.</summary>
    /// <param name="glyph">The glyph.</param>
    /// <param name="direction">The direction it grows.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction"/> is not a defined direction.</exception>
    public MathGlyphAssembly? GetAssembly(ushort glyph, MathDirection direction)
    {
        ReadOnlySpan<byte> d = data.Span;
        int construction = Construction(d, glyph, direction);
        int assembly = construction == 0 ? 0 : OtData.Off16(d, construction, construction);
        if (assembly == 0)
        {
            return null;
        }

        int count = OtData.U16(d, assembly + 4);
        count = Math.Min(count, Math.Max(0, (d.Length - assembly - 6) / 10));
        MathGlyphPart[] parts = new MathGlyphPart[count];
        for (int i = 0; i < count; i++)
        {
            int at = assembly + 6 + (i * 10);
            parts[i] = new MathGlyphPart(OtData.U16(d, at), OtData.U16(d, at + 2), OtData.U16(d, at + 4), OtData.U16(d, at + 6), (OtData.U16(d, at + 8) & 1) != 0);
        }

        return new MathGlyphAssembly(OtData.S16(d, assembly), parts);
    }

    private int Construction(ReadOnlySpan<byte> d, ushort glyph, MathDirection direction)
    {
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Not a direction.");
        }

        if (variants == 0)
        {
            return 0;
        }

        bool vertical = direction == MathDirection.Vertical;
        int coverage = OtData.Off16(d, variants, variants + (vertical ? 2 : 4));
        int index = OtData.Coverage(d, coverage, glyph);
        int vertCount = OtData.U16(d, variants + 6);
        int count = vertical ? vertCount : OtData.U16(d, variants + 8);
        if (index < 0 || index >= count)
        {
            return 0;
        }

        return OtData.Off16(d, variants, variants + 10 + ((vertical ? index : vertCount + index) * 2));
    }

    /// <summary>The value a coverage-indexed list of value records gives a glyph (italics corrections, top accent attachments).</summary>
    private float? CoveredValue(int table, ushort glyph)
    {
        ReadOnlySpan<byte> d = data.Span;
        if (glyphInfo == 0 || table == 0)
        {
            return null;
        }

        int index = OtData.Coverage(d, OtData.Off16(d, table, table), glyph);
        if (index < 0 || index >= OtData.U16(d, table + 2) || table + 4 + ((index + 1) * ValueRecordSize) > d.Length)
        {
            return null;
        }

        return OtData.S16(d, table + 4 + (index * ValueRecordSize));
    }
}
