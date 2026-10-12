using System.Numerics;

using Egami.Drawing;
using Egami.Svg.Reading;

namespace Egami.Svg.Values;

/// <summary>
/// The size of a document (question 7, resvg's rule): the root's width and height; a percentage, or a missing side (100%), is
/// of the viewBox when there is one, else of 100 by 100, reported. A side that is zero, negative or unreadable falls back the
/// same way, reported.
/// </summary>
internal static class SvgRootSize
{
    public const float DefaultSide = 100;

    public static (Vector2 Size, RectF? ViewBox) Resolve(SvgElement root, float dpi, SvgReport? report)
    {
        RectF? viewBox = null;
        if (root.Attributes["viewBox"] is string vb)
        {
            if (SvgViewBox.TryParse(vb, out RectF box) && box.Width > 0 && box.Height > 0)
            {
                viewBox = box;
            }
            else
            {
                report?.Warn("Svg.BadAttribute", $"The root's viewBox \"{vb}\" is not four numbers with a positive width and height; it is ignored.", root);
            }
        }

        float fontSize = 16;
        if (root.Attributes["font-size"] is string fs && SvgLength.TryParse(fs, out SvgLength fontLength) && fontLength.Unit is SvgUnit.None or SvgUnit.Px or SvgUnit.Pt or SvgUnit.In or SvgUnit.Cm or SvgUnit.Mm or SvgUnit.Pc && fontLength.Value > 0)
        {
            fontSize = fontLength.ToPixels(SvgLengthContext.Default with { Dpi = dpi }, SvgAxis.Other);
        }

        SvgLengthContext context = SvgLengthContext.Default with { Dpi = dpi, FontSize = fontSize, RootFontSize = fontSize };
        bool defaulted = false;
        float width = Side(root, "width", viewBox?.Width, context, SvgAxis.X, report, ref defaulted);
        float height = Side(root, "height", viewBox?.Height, context, SvgAxis.Y, report, ref defaulted);
        if (defaulted)
        {
            report?.Note("Svg.DefaultSize", $"The root has no width, height or viewBox to size it; it is drawn at {width} by {height}.", root);
        }

        return (new Vector2(width, height), viewBox);
    }

    private static float Side(SvgElement root, string name, float? viewBoxSide, in SvgLengthContext context, SvgAxis axis, SvgReport? report, ref bool defaulted)
    {
        SvgLength length = new(100, SvgUnit.Percent);
        if (root.Attributes[name] is string text && !(text.Trim().Equals("auto", StringComparison.Ordinal)))
        {
            if (!SvgLength.TryParse(text, out length) || length.Value <= 0)
            {
                report?.Warn("Svg.BadAttribute", $"The root's {name} \"{text}\" is not a positive length; 100% is used.", root);
                length = new SvgLength(100, SvgUnit.Percent);
            }
        }

        if (length.IsPercent)
        {
            if (viewBoxSide is float side)
            {
                return side * length.Value / 100;
            }

            defaulted = true;
            return DefaultSide * length.Value / 100;
        }

        return length.ToPixels(context, axis);
    }
}
