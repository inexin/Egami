using Egami.Css;
using Egami.Operations;
using Egami.Svg.Values;

namespace Egami.Svg.Styling;

/// <summary>Parsers for SVG property values with their own grammar: font-size, paint-order and mix-blend-mode.</summary>
internal static class SvgStyleParsing
{
    /// <summary>
    /// A font-size: a length (em and percent of the parent's size, rem of the root's), an absolute keyword (medium is 16px, each
    /// step 1.2 times, as CSS Fonts 4's table), or larger and smaller (1.2 times the parent's). Negative sizes are invalid.
    /// </summary>
    public static bool TryFontSize(IReadOnlyList<CssComponentValue> value, float parent, float root, float dpi, out float size)
    {
        size = parent;
        if (SvgStyleValues.Keyword(value) is string keyword)
        {
            float? s = keyword.ToLowerInvariant() switch
            {
                "xx-small" => 9,
                "x-small" => 10,
                "small" => 13,
                "medium" => 16,
                "large" => 18,
                "x-large" => 24,
                "xx-large" => 32,
                "xxx-large" => 48,
                "larger" => parent * 1.2f,
                "smaller" => parent / 1.2f,
                _ => null,
            };
            if (s is float k)
            {
                size = k;
                return true;
            }

            return false;
        }

        if (!SvgStyleValues.TryLength(value, out SvgLength length) || length.Value < 0)
        {
            return false;
        }

        size = length.Unit switch
        {
            SvgUnit.Percent => parent * length.Value / 100,
            SvgUnit.Em => parent * length.Value,
            SvgUnit.Ex or SvgUnit.Ch => parent * length.Value / 2,
            _ => length.ToPixels(new SvgLengthContext(dpi, parent, root, 100, 100, 100, 100), SvgAxis.Other),
        };
        return float.IsFinite(size);
    }

    /// <summary>paint-order: up to three of fill, stroke and markers, each once; the ones left out follow in the default order.</summary>
    public static bool TryPaintOrder(IReadOnlyList<CssComponentValue> value, out SvgPaintOrder order)
    {
        order = SvgPaintOrder.FillStrokeMarkers;
        Span<int> seen = stackalloc int[3];
        int count = 0;
        foreach (CssComponentValue item in value)
        {
            if (item is CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                continue;
            }

            if (item is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } token } || count == 3)
            {
                return false;
            }

            int which = token.Value.ToLowerInvariant() switch { "fill" => 0, "stroke" => 1, "markers" => 2, _ => -1 };
            if (which < 0 || seen[..count].Contains(which))
            {
                return false;
            }

            seen[count++] = which;
        }

        if (count == 0)
        {
            return false;
        }

        for (int which = 0; which < 3 && count < 3; which++)
        {
            if (!seen[..count].Contains(which))
            {
                seen[count++] = which;
            }
        }

        order = (seen[0], seen[1]) switch
        {
            (0, 1) => SvgPaintOrder.FillStrokeMarkers,
            (0, 2) => SvgPaintOrder.FillMarkersStroke,
            (1, 0) => SvgPaintOrder.StrokeFillMarkers,
            (1, 2) => SvgPaintOrder.StrokeMarkersFill,
            (2, 0) => SvgPaintOrder.MarkersFillStroke,
            _ => SvgPaintOrder.MarkersStrokeFill,
        };
        return true;
    }

    /// <summary>mix-blend-mode's keywords (Compositing and Blending 1), plus plus-lighter.</summary>
    public static bool TryBlendMode(string keyword, out BlendMode mode)
    {
        BlendMode? m = keyword.ToLowerInvariant() switch
        {
            "normal" => BlendMode.SourceOver,
            "multiply" => BlendMode.Multiply,
            "screen" => BlendMode.Screen,
            "overlay" => BlendMode.Overlay,
            "darken" => BlendMode.Darken,
            "lighten" => BlendMode.Lighten,
            "color-dodge" => BlendMode.ColorDodge,
            "color-burn" => BlendMode.ColorBurn,
            "hard-light" => BlendMode.HardLight,
            "soft-light" => BlendMode.SoftLight,
            "difference" => BlendMode.Difference,
            "exclusion" => BlendMode.Exclusion,
            "hue" => BlendMode.Hue,
            "saturation" => BlendMode.Saturation,
            "color" => BlendMode.Color,
            "luminosity" => BlendMode.Luminosity,
            "plus-lighter" => BlendMode.Plus,
            _ => null,
        };
        mode = m ?? BlendMode.SourceOver;
        return m is not null;
    }
}
