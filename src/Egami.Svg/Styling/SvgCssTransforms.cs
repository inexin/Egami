using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Svg.Values;

namespace Egami.Svg.Styling;

/// <summary>
/// The CSS transform property (CSS Transforms 1, 2D functions) and transform-origin, for SVG elements styled by CSS. Lengths
/// may carry units; percentages in translate and transform-origin are of the reference box transform-box selects: the
/// element's fill box, or the nearest viewport (view-box, SVG's default).
/// </summary>
internal static class SvgCssTransforms
{
    /// <summary>
    /// Reads a transform list into a matrix. Lengths in em, rem and viewport units resolve against <paramref name="lengths"/>
    /// (the element's font size and the viewport it is drawn in); percentages in translate against <paramref name="box"/>.
    /// </summary>
    public static bool TryResolve(IReadOnlyList<CssComponentValue> value, RectF box, in SvgLengthContext lengths, out Matrix3x2 matrix)
    {
        matrix = Matrix3x2.Identity;
        List<float> args = [];
        foreach (CssComponentValue item in value)
        {
            if (item is CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                continue;
            }

            if (item is not CssFunction function)
            {
                return false;
            }

            args.Clear();
            string name = function.Name.ToLowerInvariant();
            bool angles = name is "rotate" or "skew" or "skewx" or "skewy";
            int index = 0;
            foreach (CssComponentValue arg in function.Arguments)
            {
                if (arg is CssTokenValue { Token.Kind: CssTokenKind.Whitespace or CssTokenKind.Comma })
                {
                    continue;
                }

                if (!TryArgument(arg, name, index, angles, box, lengths, out float number))
                {
                    return false;
                }

                args.Add(number);
                index++;
            }

            Matrix3x2? m = (name, args.Count) switch
            {
                ("matrix", 6) => new Matrix3x2(args[0], args[1], args[2], args[3], args[4], args[5]),
                ("translate", 1) => Matrix3x2.CreateTranslation(args[0], 0),
                ("translate", 2) => Matrix3x2.CreateTranslation(args[0], args[1]),
                ("translatex", 1) => Matrix3x2.CreateTranslation(args[0], 0),
                ("translatey", 1) => Matrix3x2.CreateTranslation(0, args[0]),
                ("scale", 1) => Matrix3x2.CreateScale(args[0]),
                ("scale", 2) => Matrix3x2.CreateScale(args[0], args[1]),
                ("scalex", 1) => Matrix3x2.CreateScale(args[0], 1),
                ("scaley", 1) => Matrix3x2.CreateScale(1, args[0]),
                ("rotate", 1) => SvgTransforms.Rotate(args[0]),
                ("skew", 1) => new Matrix3x2(1, 0, MathF.Tan(args[0] * MathF.PI / 180), 1, 0, 0),
                ("skew", 2) => new Matrix3x2(1, MathF.Tan(args[1] * MathF.PI / 180), MathF.Tan(args[0] * MathF.PI / 180), 1, 0, 0),
                ("skewx", 1) => new Matrix3x2(1, 0, MathF.Tan(args[0] * MathF.PI / 180), 1, 0, 0),
                ("skewy", 1) => new Matrix3x2(1, MathF.Tan(args[0] * MathF.PI / 180), 0, 1, 0, 0),
                _ => null,
            };
            if (m is not Matrix3x2 step)
            {
                return false;
            }

            matrix = step * matrix;
        }

        return true;
    }

    /// <summary>
    /// The individual transform properties and the motion path's offset (CSS Transforms 2, Motion Path 1) as one matrix to put
    /// before the transform property's: translate, then rotate, then scale, then the offset, from the outside in. Percentages
    /// in translate are of the reference box; the offset moves the transform origin, the anchor, to the point offset-distance
    /// along offset-path (in the parent's user space) and turns it as offset-rotate says. Lengths in em and viewport units
    /// resolve against <paramref name="lengths"/>: the element's font size and the viewport.
    /// </summary>
    public static Matrix3x2 Individual(SvgRareStyle style, RectF box, Vector2 origin, in SvgLengthContext lengths)
    {
        Matrix3x2 m = Matrix3x2.Identity;
        if (style.OffsetPath is string data && Offset(data, style.OffsetDistance, style.OffsetRotate, origin, lengths) is Matrix3x2 offset)
        {
            m = offset;
        }

        if (style.Scale is { } scale && SvgAnimationProperties.TryScale(scale, out double sx, out double sy))
        {
            m *= Matrix3x2.CreateScale((float)sx, (float)sy);
        }

        if (style.Rotate is { } rotate && SvgAnimationProperties.TryRotate(rotate, out double degrees))
        {
            m *= SvgTransforms.Rotate((float)degrees);
        }

        if (style.Translate is { } translate && SvgAnimationProperties.TryTranslate(translate, out SvgLength x, out SvgLength y))
        {
            m *= Matrix3x2.CreateTranslation(Pixels(x, box.Width, lengths), Pixels(y, box.Height, lengths));
        }

        return m;
    }

    // A translate length: a percentage (with any length mixed in) is of the reference box's side.
    private static float Pixels(SvgLength length, float reference, in SvgLengthContext lengths) =>
        length.IsPercent ? length.Of(reference) : length.ToPixels(lengths, SvgAxis.Other);

    // The offset transform: rotate about the anchor, then move the anchor onto the path (within the transform-origin frame).
    private static Matrix3x2? Offset(string data, IReadOnlyList<CssComponentValue>? distanceValue, IReadOnlyList<CssComponentValue>? rotateValue, Vector2 anchor, in SvgLengthContext lengths)
    {
        if (!Path.TryParseSvg(data, out Path? path) || path is null)
        {
            return null;
        }

        PathMeasure measure = new(path);
        float total = measure.TotalLength;
        if (!(total > 0) || !float.IsFinite(total))
        {
            return null;
        }

        float distance = 0;
        if (distanceValue is not null && SvgStyleValues.TryLength(distanceValue, out SvgLength d))
        {
            distance = d.IsPercent ? d.Of(total) : d.ToPixels(lengths, SvgAxis.Other);
        }

        // A closed path wraps the distance round; an open one clamps it to its ends.
        bool closed = measure.Contours.Count > 0 && measure.Contours.All(c => c.IsClosed);
        distance = closed ? ((distance % total) + total) % total : Math.Clamp(distance, 0, total);

        // The contour the distance falls on.
        int index = 0;
        float local = distance;
        while (index < measure.Contours.Count - 1 && local > measure.Contours[index].Length)
        {
            local -= measure.Contours[index].Length;
            index++;
        }

        ContourMeasure contour = measure.Contours[index];
        Vector2 point = contour.GetPosition(local);
        bool auto = true;
        double degrees = 0;
        if (rotateValue is not null && !SvgAnimationProperties.TryOffsetRotate(rotateValue, out auto, out degrees))
        {
            (auto, degrees) = (true, 0);
        }

        if (auto)
        {
            Vector2 tangent = contour.GetTangent(local);
            degrees += Math.Atan2(tangent.Y, tangent.X) * 180 / Math.PI;
        }

        return SvgTransforms.Rotate((float)degrees) * Matrix3x2.CreateTranslation(point - anchor);
    }

    /// <summary>transform-origin: one or two keywords, lengths or percentages of the reference box (a third, z, is ignored).</summary>
    public static bool TryOrigin(IReadOnlyList<CssComponentValue> value, RectF box, out Vector2 origin)
    {
        origin = default;
        List<CssComponentValue> items = [.. value.Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace })];
        if (items.Count is < 1 or > 3)
        {
            return false;
        }

        float? x = null, y = null;
        foreach (CssComponentValue item in items.Take(2))
        {
            if (item is CssTokenValue { Token: { Kind: CssTokenKind.Ident } t })
            {
                switch (t.Value.ToLowerInvariant())
                {
                    case "left": x = box.Left; break;
                    case "right": x = box.Right; break;
                    case "top": y = box.Top; break;
                    case "bottom": y = box.Bottom; break;
                    case "center":
                        if (x is null)
                        {
                            x = box.Center.X;
                        }
                        else
                        {
                            y = box.Center.Y;
                        }

                        break;
                    default: return false;
                }

                continue;
            }

            if (!SvgStyleValues.TryLength(item, out SvgLength length))
            {
                return false;
            }

            if (x is null)
            {
                x = length.IsPercent ? box.X + length.Of(box.Width) : length.Value;
            }
            else
            {
                y = length.IsPercent ? box.Y + length.Of(box.Height) : length.Value;
            }
        }

        origin = new Vector2(x ?? box.Center.X, y ?? box.Center.Y);
        return true;
    }

    private static bool TryArgument(CssComponentValue arg, string function, int index, bool angles, RectF box, in SvgLengthContext lengths, out float number)
    {
        number = 0;
        if (arg is not CssTokenValue { Token: var token })
        {
            return false;
        }

        if (angles)
        {
            if (CssAngle.TryParse(arg, allowNumber: token.Kind == CssTokenKind.Number && token.Number == 0, out CssAngle angle))
            {
                number = (float)angle.Degrees;
                return true;
            }

            return false;
        }

        switch (token.Kind)
        {
            case CssTokenKind.Number:
                number = (float)token.Number;
                return true;
            case CssTokenKind.Percentage when function is "scale" or "scalex" or "scaley":
                number = (float)token.Number / 100;
                return true;
            case CssTokenKind.Percentage when function is "translate" or "translatex" or "translatey":
                bool vertical = function == "translatey" || (function == "translate" && index == 1);
                number = (float)token.Number / 100 * (vertical ? box.Height : box.Width);
                return true;
            case CssTokenKind.Dimension when SvgStyleValues.TryLength(arg, out SvgLength length):
                number = length.ToPixels(lengths, SvgAxis.Other);
                return true;
            default:
                return false;
        }
    }
}
