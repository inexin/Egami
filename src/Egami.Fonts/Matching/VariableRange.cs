using Egami.Fonts.Variations;

namespace Egami.Fonts.Matching;

/// <summary>The style ranges of a variable face: weight, width (CSS percent) and slant, and whether it has an italic axis.</summary>
internal sealed record VariableRange(
    float WeightMin,
    float WeightMax,
    float WidthMin,
    float WidthMax,
    float SlantMin,
    float SlantMax,
    bool HasWeight,
    bool HasWidth,
    bool HasSlant,
    bool HasItalic,
    FontSlant BaseSlant)
{
    /// <summary>The CSS default oblique angle, in degrees.</summary>
    public const float ObliqueAngle = 14;

    public static VariableRange Of(VariationSpace space, FontStyle style)
    {
        (float wMin, float wMax, bool hasW) = Range(space, FontVariation.WeightAxis, style.Weight);
        (float dMin, float dMax, bool hasD) = Range(space, FontVariation.WidthAxis, StyleMatcher.WidthPercentOf(style.Width));
        (float sMin, float sMax, bool hasS) = Range(space, FontVariation.SlantAxis, 0);
        (_, float iMax, bool hasI) = Range(space, FontVariation.ItalicAxis, 0);
        return new VariableRange(wMin, wMax, dMin, dMax, sMin, sMax, hasW, hasD, hasS, hasI && iMax >= 1, style.Slant);
    }

    /// <summary>The style a variable face takes at a position, as a <see cref="Typeface"/> there reports it.</summary>
    public static FontStyle StyleAt(VariationSpace space, ReadOnlySpan<float> user, FontStyle style)
    {
        int weight = style.Weight, width = style.Width;
        FontSlant slant = style.Slant;
        if (space.AxisIndex(FontVariation.WeightAxis) is int w and >= 0)
        {
            weight = Math.Clamp((int)MathF.Round(user[w]), 1, 1000);
        }

        if (space.AxisIndex(FontVariation.WidthAxis) is int d and >= 0)
        {
            width = StyleMatcher.WidthClassOf(user[d]);
        }

        if (space.AxisIndex(FontVariation.ItalicAxis) is int i and >= 0 && user[i] >= 0.5f)
        {
            slant = FontSlant.Italic;
        }
        else if (space.AxisIndex(FontVariation.SlantAxis) is int s and >= 0 && user[s] != 0)
        {
            slant = FontSlant.Oblique;
        }

        return new FontStyle(weight, width, slant);
    }

    /// <summary>
    /// The style the face takes for a wanted one (CSS Fonts 4: a range that covers the request matches it exactly, else its nearest
    /// end), and the axis values that put it there.
    /// </summary>
    public (FontStyle Style, FontVariation[] Variations) Fit(FontStyle wanted)
    {
        var variations = new List<FontVariation>();
        int weight = wanted.ClampedWeight;
        if (HasWeight)
        {
            float w = Math.Clamp(weight, WeightMin, WeightMax);
            variations.Add(FontVariation.Weight(w));
            weight = (int)MathF.Round(w);
        }
        else
        {
            weight = (int)WeightMin;
        }

        int width = wanted.ClampedWidth;
        if (HasWidth)
        {
            float percent = Math.Clamp(StyleMatcher.WidthPercentOf(width), WidthMin, WidthMax);
            variations.Add(FontVariation.Width(percent));
            width = percent == StyleMatcher.WidthPercentOf(width) ? width : StyleMatcher.WidthClassOf(percent);
        }
        else
        {
            width = StyleMatcher.WidthClassOf(WidthMin);
        }

        bool canItalic = HasItalic || BaseSlant == FontSlant.Italic;
        bool canOblique = (HasSlant && SlantMin < 0) || BaseSlant == FontSlant.Oblique;
        bool canUpright = BaseSlant == FontSlant.Upright || (HasSlant && SlantMin <= 0 && SlantMax >= 0 && BaseSlant != FontSlant.Italic);
        FontSlant slant = wanted.Slant switch
        {
            FontSlant.Italic => canItalic ? FontSlant.Italic : canOblique ? FontSlant.Oblique : FontSlant.Upright,
            FontSlant.Oblique => canOblique ? FontSlant.Oblique : canItalic ? FontSlant.Italic : FontSlant.Upright,
            _ => canUpright ? FontSlant.Upright : canOblique ? FontSlant.Oblique : FontSlant.Italic,
        };
        if (HasItalic)
        {
            variations.Add(FontVariation.Italic(slant == FontSlant.Italic ? 1 : 0));
        }

        if (HasSlant)
        {
            variations.Add(FontVariation.Slant(slant == FontSlant.Oblique && BaseSlant != FontSlant.Oblique ? Math.Clamp(-ObliqueAngle, SlantMin, SlantMax) : Math.Clamp(0, SlantMin, SlantMax)));
        }

        return (new FontStyle(weight, width, slant), [.. variations]);
    }

    private static (float Min, float Max, bool Has) Range(VariationSpace space, FontTag tag, float fallback)
    {
        int i = space.AxisIndex(tag);
        return i < 0 ? (fallback, fallback, false) : (space.Axes[i].Min, space.Axes[i].Max, true);
    }
}
