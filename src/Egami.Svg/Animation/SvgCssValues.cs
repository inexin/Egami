using System.Globalization;
using System.Numerics;
using System.Text;

using Egami.Css;
using Egami.Svg.Styling;

namespace Egami.Svg.Animation;

/// <summary>What resolves relative values while CSS animation values are combined: font size, viewport and currentColor.</summary>
internal readonly record struct SvgCssValueContext(double FontSize, double RootFontSize, double ViewportWidth, double ViewportHeight, CssColor CurrentColor);

/// <summary>
/// Interpolates and composites the computed values of CSS properties, as CSS animations do (CSS Values 4, CSS Transforms 2,
/// CSS Color 4, Motion Path 1, Web Animations 1): numbers and lengths (units converted where they can be), colors in
/// premultiplied sRGB, transform lists (function by function, the rest through matrix decomposition), the individual transform
/// properties, path() data with the same commands, offset-rotate, visibility; anything else, custom properties included, flips
/// halfway. Values are CSS text in, CSS text out.
/// </summary>
internal static class SvgCssValues
{
    private static readonly HashSet<string> s_colors = new(StringComparer.Ordinal)
    {
        "color", "stop-color", "flood-color", "lighting-color", "background-color", "text-decoration-color",
    };

    private static readonly HashSet<string> s_opacities = new(StringComparer.Ordinal)
    {
        "opacity", "fill-opacity", "stroke-opacity", "stop-opacity", "flood-opacity",
    };

    private static readonly HashSet<string> s_lengths = new(StringComparer.Ordinal)
    {
        "stroke-width", "stroke-dashoffset", "stroke-miterlimit", "cx", "cy", "r", "rx", "ry", "x", "y", "width", "height",
        "font-size", "letter-spacing", "word-spacing", "offset-distance", "baseline-shift", "transform-origin", "stroke-dasharray",
        "font-weight",
    };

    /// <summary>The value at <paramref name="p"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static string Interpolate(string property, string from, string to, double p, in SvgCssValueContext context)
    {
        if (from == to)
        {
            return from;
        }

        string? result = property switch
        {
            "fill" or "stroke" => Colors(from, to, context, (a, b) => Mix(a, b, p)),
            "transform" => Transforms(from, to, p, context),
            "translate" => TranslateProperty(from, to, context, (a, b) => a + ((b - a) * p)),
            "rotate" => RotateProperty(from, to, (a, b) => a + ((b - a) * p)),
            "scale" => ScaleProperty(from, to, (a, b) => a + ((b - a) * p)),
            "d" or "offset-path" => Paths(from, to, p),
            "offset-rotate" => OffsetRotate(from, to, (a, b) => a + ((b - a) * p)),
            "visibility" => Visibility(from, to, p),
            _ when s_colors.Contains(property) => Colors(from, to, context, (a, b) => Mix(a, b, p)),
            _ when s_opacities.Contains(property) => Lengths(Opacity(from), Opacity(to), context, false, (a, b) => a + ((b - a) * p)),
            _ when s_lengths.Contains(property) => Lengths(Weight(property, from), Weight(property, to), context, property == "stroke-dasharray", (a, b) => a + ((b - a) * p)),
            _ => null,
        };
        return result ?? (p < 0.5 ? from : to);
    }

    /// <summary>
    /// Combines a keyframe value with the value below it (Web Animations 1, "Effect composition"): replace keeps the value;
    /// add puts it after the underlying one (transform lists join, numbers sum); accumulate adds the parts (transform functions
    /// of the same kind sum, scales add their excess over 1). Values that cannot be combined replace.
    /// </summary>
    public static string Composite(string property, string underlying, string value, SvgCssComposite op, in SvgCssValueContext context)
    {
        if (op == SvgCssComposite.Replace)
        {
            return value;
        }

        bool accumulate = op == SvgCssComposite.Accumulate;
        string? result = property switch
        {
            "fill" or "stroke" => Colors(underlying, value, context, Sum),
            "transform" => AddTransforms(underlying, value, accumulate),
            "translate" => TranslateProperty(underlying, value, context, (a, b) => a + b),
            "rotate" => RotateProperty(underlying, value, (a, b) => a + b),
            "scale" => ScaleProperty(underlying, value, accumulate ? (a, b) => a + b - 1 : (a, b) => a * b),
            "offset-rotate" => OffsetRotate(underlying, value, (a, b) => a + b),
            _ when s_colors.Contains(property) => Colors(underlying, value, context, Sum),
            _ when s_opacities.Contains(property) => Lengths(Opacity(underlying), Opacity(value), context, false, (a, b) => a + b),
            _ when s_lengths.Contains(property) && property != "stroke-dasharray" => Lengths(Weight(property, underlying), Weight(property, value), context, false, (a, b) => a + b),
            _ => null,
        };
        return result ?? value;
    }

    private static string Format(double value) => SvgAnimatedValue.Format(value);

    private static IReadOnlyList<CssComponentValue> Parse(string text) => CssComponentValue.ParseList(text);

    private static List<CssComponentValue> Significant(string text) =>
        [.. CssComponentValue.ParseList(text).Where(v => v is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace or CssTokenKind.Comma })];

    // ---- Lengths and numbers ----

    private static string Opacity(string text) =>
        CssLength.TryParse(text, out CssLength l) && l.Unit == CssUnit.Percent ? Format(l.Value / 100) : text;

    private static string Weight(string property, string text)
    {
        if (property != "font-weight")
        {
            return text;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "normal" => "400",
            "bold" => "700",
            _ => text,
        };
    }

    /// <summary>Lists of lengths or numbers item by item; null when an item is not one or units cannot meet.</summary>
    private static string? Lengths(string a, string b, in SvgCssValueContext context, bool repeat, Func<double, double, double> f)
    {
        List<LengthPercentage>? x = LengthList(a, context);
        List<LengthPercentage>? y = LengthList(b, context);
        if (x is null || y is null || x.Count == 0 || y.Count == 0)
        {
            return null;
        }

        if (x.Count != y.Count)
        {
            if (!repeat)
            {
                return null;
            }

            // stroke-dasharray: both lists repeat to the least common multiple of their lengths (CSS Values 4 'repeatable list').
            // In long: two long lists can have a least common multiple past int.
            long lcm = (long)x.Count / Gcd(x.Count, y.Count) * y.Count;
            if (lcm > MaxRepeatedList)
            {
                return null;
            }

            x = Repeat(x, (int)lcm);
            y = Repeat(y, (int)lcm);
        }

        StringBuilder builder = new();
        for (int i = 0; i < x.Count; i++)
        {
            if (Combine(x[i], y[i], context, f) is not LengthPercentage r)
            {
                return null;
            }

            builder.Append(i == 0 ? "" : " ").Append(r.ToText(false));
        }

        return builder.ToString();
    }

    /// <summary>The longest list two repeatable lists of different lengths are repeated to before they interpolate; longer ones change halfway.</summary>
    internal const int MaxRepeatedList = 1000;

    private static List<LengthPercentage> Repeat(List<LengthPercentage> list, int count) => [.. Enumerable.Range(0, count).Select(i => list[i % list.Count])];

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private static List<LengthPercentage>? LengthList(string text, in SvgCssValueContext context)
    {
        List<LengthPercentage> lengths = [];
        foreach (CssComponentValue value in Significant(text))
        {
            if (ReadLength(value, context) is not LengthPercentage length)
            {
                return null;
            }

            lengths.Add(length);
        }

        return lengths;
    }

    private static LengthPercentage? ReadLength(CssComponentValue value, in SvgCssValueContext context)
    {
        if (CssLength.TryParse(value, out CssLength length))
        {
            return double.IsFinite(length.Value) ? LengthPercentage.Of(length) : null;
        }

        if (value is not CssFunction || !CssCalc.TryParse(value, out CssCalc? calc) || calc.IsNumber)
        {
            return null;
        }

        // calc() is linear in its percentages: evaluated with a percent worth 0 it gives the length part, with 1 the sum.
        bool unknown = false;
        SvgCssValueContext c = context;
        double Evaluate(double percent) => calc.Resolve(unit =>
        {
            if (unit == CssUnit.Percent)
            {
                return percent;
            }

            double? px = Pixels(new CssLength(1, unit), c);
            unknown |= px is null;
            return px ?? 0;
        });

        double pixels = Evaluate(0);
        double percentage = Evaluate(1) - pixels;
        return unknown || !double.IsFinite(pixels) || !double.IsFinite(percentage) ? null : LengthPercentage.Mix(percentage, pixels);
    }

    // Two length-percentages combined by f: in one unit where they share one, else as a percentage and a px length mixed.
    private static LengthPercentage? Combine(LengthPercentage a, LengthPercentage b, in SvgCssValueContext context, Func<double, double, double> f)
    {
        if (!a.Mixed && !b.Mixed && CombineLengths(a.Plain, b.Plain, context, f) is CssLength plain)
        {
            return LengthPercentage.Of(plain);
        }

        if (Split(a, context) is not (double pa, double xa) || Split(b, context) is not (double pb, double xb))
        {
            return null;
        }

        return LengthPercentage.Mix(f(pa, pb), f(xa, xb));
    }

    private static (double Percent, double Pixels)? Split(LengthPercentage value, in SvgCssValueContext context)
    {
        if (value.Mixed)
        {
            return (value.Percent, value.Pixels);
        }

        if (value.Plain.Unit == CssUnit.Percent)
        {
            return (value.Plain.Value, 0);
        }

        return Pixels(value.Plain, context) is double px ? (0, px) : null;
    }

    /// <summary>
    /// Two lengths combined in one unit: the same unit (px and unitless count as one), the unit of the non-zero one when the
    /// other is zero, else both in px (font and viewport units resolved through the context). A percentage and another unit have
    /// no result here; <see cref="Combine"/> keeps them apart, as calc() does.
    /// </summary>
    public static CssLength? CombineLengths(CssLength a, CssLength b, in SvgCssValueContext context, Func<double, double, double> f)
    {
        CssUnit ua = a.Unit == CssUnit.None ? CssUnit.Px : a.Unit;
        CssUnit ub = b.Unit == CssUnit.None ? CssUnit.Px : b.Unit;
        if (ua == ub)
        {
            return new CssLength(f(a.Value, b.Value), a.Unit == b.Unit ? a.Unit : CssUnit.Px);
        }

        if (a.Value == 0)
        {
            return new CssLength(f(0, b.Value), b.Unit);
        }

        if (b.Value == 0)
        {
            return new CssLength(f(a.Value, 0), a.Unit);
        }

        if (Pixels(a, context) is double pa && Pixels(b, context) is double pb)
        {
            return new CssLength(f(pa, pb), CssUnit.Px);
        }

        return null;
    }

    private static double? Pixels(CssLength length, in SvgCssValueContext context) => length.Unit switch
    {
        CssUnit.None or CssUnit.Px => length.Value,
        CssUnit.In => length.Value * 96,
        CssUnit.Cm => length.Value * 96 / 2.54,
        CssUnit.Mm => length.Value * 96 / 25.4,
        CssUnit.Q => length.Value * 96 / 101.6,
        CssUnit.Pt => length.Value * 96 / 72,
        CssUnit.Pc => length.Value * 16,
        CssUnit.Em => length.Value * context.FontSize,
        CssUnit.Rem => length.Value * context.RootFontSize,
        CssUnit.Ex or CssUnit.Ch => length.Value * context.FontSize / 2,
        CssUnit.Vw => length.Value * context.ViewportWidth / 100,
        CssUnit.Vh => length.Value * context.ViewportHeight / 100,
        CssUnit.Vmin => length.Value * Math.Min(context.ViewportWidth, context.ViewportHeight) / 100,
        CssUnit.Vmax => length.Value * Math.Max(context.ViewportWidth, context.ViewportHeight) / 100,
        _ => null,
    };

    private static string LengthText(CssLength length) => length.Unit switch
    {
        CssUnit.None => Format(length.Value),
        CssUnit.Percent => Format(length.Value) + "%",
        _ => Format(length.Value) + UnitName(length),
    };

    private static string UnitName(CssLength length)
    {
        string text = new CssLength(1, length.Unit).ToString();
        return text[1..];
    }

    // ---- Colors ----

    private static (double R, double G, double B, double A) Mix((double R, double G, double B, double A) a, (double R, double G, double B, double A) b, double p) =>
        (a.R + ((b.R - a.R) * p), a.G + ((b.G - a.G) * p), a.B + ((b.B - a.B) * p), a.A + ((b.A - a.A) * p));

    private static (double R, double G, double B, double A) Sum((double R, double G, double B, double A) a, (double R, double G, double B, double A) b) =>
        (a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);

    /// <summary>Two colors combined in premultiplied sRGB (CSS Color 4, "Interpolating with alpha"); null when either is not a color.</summary>
    private static string? Colors(string a, string b, in SvgCssValueContext context, Func<(double R, double G, double B, double A), (double R, double G, double B, double A), (double R, double G, double B, double A)> f)
    {
        if (!TryColor(a, context, out CssColor x) || !TryColor(b, context, out CssColor y))
        {
            return null;
        }

        (double r, double g, double bl, double al) = f((x.R * x.A, x.G * x.A, x.B * x.A, x.A), (y.R * y.A, y.G * y.A, y.B * y.A, y.A));
        double alpha = Math.Clamp(al, 0, 1);
        if (alpha <= 0)
        {
            return "rgba(0, 0, 0, 0)";
        }

        // Un-premultiply by the alpha the result has: an add can sum past 1, and the color then keeps its full channels.
        return new SvgColorValue(Math.Clamp(r / alpha, 0, 1), Math.Clamp(g / alpha, 0, 1), Math.Clamp(bl / alpha, 0, 1), alpha).ToText();
    }

    private static bool TryColor(string text, in SvgCssValueContext context, out CssColor color)
    {
        if (!CssColor.TryParse(text.Trim(), out color))
        {
            return false;
        }

        if (color.IsCurrentColor)
        {
            color = context.CurrentColor;
        }

        return true;
    }

    // ---- Visibility, paths, offset-rotate ----

    private static string? Visibility(string a, string b, double p)
    {
        bool va = a.Trim().Equals("visible", StringComparison.OrdinalIgnoreCase);
        bool vb = b.Trim().Equals("visible", StringComparison.OrdinalIgnoreCase);
        if (va == vb)
        {
            return null;
        }

        // Visible throughout when either end is (CSS Values 4 says visibility interpolates as a discrete step biased to visible).
        return p <= 0 ? a : p >= 1 ? b : va ? a : b;
    }

    private static string? Paths(string a, string b, double p)
    {
        if (!SvgAnimationProperties.TryPathFunction(Parse(a), out string x) || !SvgAnimationProperties.TryPathFunction(Parse(b), out string y)
            || SvgPathValue.TryParse(x) is not SvgPathValue px || SvgPathValue.TryParse(y) is not SvgPathValue py
            || px.Interpolate(py, p) is not SvgAnimatedValue mixed)
        {
            return null;
        }

        return "path(\"" + mixed.ToText() + "\")";
    }

    private static string? OffsetRotate(string a, string b, Func<double, double, double> f)
    {
        if (!SvgAnimationProperties.TryOffsetRotate(Parse(a), out bool autoA, out double da)
            || !SvgAnimationProperties.TryOffsetRotate(Parse(b), out bool autoB, out double db) || autoA != autoB)
        {
            return null;
        }

        string angle = Format(f(da, db)) + "deg";
        return autoA ? "auto " + angle : angle;
    }

    // ---- The individual transform properties ----

    private static string? TranslateProperty(string a, string b, in SvgCssValueContext context, Func<double, double, double> f)
    {
        if (!TryTranslate(a, context, out LengthPercentage ax, out LengthPercentage ay) || !TryTranslate(b, context, out LengthPercentage bx, out LengthPercentage by)
            || Combine(ax, bx, context, f) is not LengthPercentage rx || Combine(ay, by, context, f) is not LengthPercentage ry)
        {
            return null;
        }

        return rx.ToText(true) + " " + ry.ToText(true);
    }

    // The x and y of a translate value (none is 0 0), checked as the property's grammar says.
    private static bool TryTranslate(string text, in SvgCssValueContext context, out LengthPercentage x, out LengthPercentage y)
    {
        x = y = LengthPercentage.Of(new CssLength(0, CssUnit.Px));
        if (IsNone(text))
        {
            return true;
        }

        if (!SvgAnimationProperties.TryTranslate(Parse(text), out _, out _))
        {
            return false;
        }

        List<CssComponentValue> items = Significant(text);
        if (ReadLength(items[0], context) is not LengthPercentage first)
        {
            return false;
        }

        x = first;
        if (items.Count > 1)
        {
            if (ReadLength(items[1], context) is not LengthPercentage second)
            {
                return false;
            }

            y = second;
        }

        return true;
    }

    // A length in a translate: unitless numbers are not lengths there, so they are written in px.
    private static string PixelText(CssLength length) => length.Unit == CssUnit.None ? Format(length.Value) + "px" : LengthText(length);

    private static string? RotateProperty(string a, string b, Func<double, double, double> f)
    {
        double x = 0, y = 0;
        if ((!IsNone(a) && !SvgAnimationProperties.TryRotate(Parse(a), out x)) || (!IsNone(b) && !SvgAnimationProperties.TryRotate(Parse(b), out y)))
        {
            return null;
        }

        return Format(f(x, y)) + "deg";
    }

    private static string? ScaleProperty(string a, string b, Func<double, double, double> f)
    {
        double ax = 1, ay = 1, bx = 1, by = 1;
        if ((!IsNone(a) && !SvgAnimationProperties.TryScale(Parse(a), out ax, out ay)) || (!IsNone(b) && !SvgAnimationProperties.TryScale(Parse(b), out bx, out by)))
        {
            return null;
        }

        return Format(f(ax, bx)) + " " + Format(f(ay, by));
    }

    private static bool IsNone(string text) => text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);

    // ---- Transform lists ----

    /// <summary>
    /// Two transform lists interpolated as CSS Transforms 2 says: none stands for identity functions; the shorter list is padded
    /// with identities; functions of the same kind interpolate their arguments up to the first pair that differs, and what is
    /// left of both lists from there interpolates as one matrix each, through 2D decomposition.
    /// </summary>
    private static string? Transforms(string a, string b, double p, in SvgCssValueContext context)
    {
        if (TransformList(a) is not List<CssTransformFunction> x || TransformList(b) is not List<CssTransformFunction> y)
        {
            return null;
        }

        if (x.Count == 0 && y.Count == 0)
        {
            return "none";
        }

        while (x.Count < y.Count)
        {
            x.Add(Identity(y[x.Count]));
        }

        while (y.Count < x.Count)
        {
            y.Add(Identity(x[y.Count]));
        }

        List<string> parts = [];
        int i = 0;
        for (; i < x.Count; i++)
        {
            if (InterpolateFunction(x[i], y[i], p, context) is not string part)
            {
                break;
            }

            parts.Add(part);
        }

        if (i < x.Count)
        {
            if (Matrix(x, i) is not Matrix3x2 mx || Matrix(y, i) is not Matrix3x2 my)
            {
                return null;
            }

            parts.Add(MatrixText(InterpolateMatrix(mx, my, p)));
        }

        return string.Join(" ", parts);
    }

    private static List<CssTransformFunction>? TransformList(string text)
    {
        if (IsNone(text))
        {
            return [];
        }

        if (!CssTransformList.TryParse(text, out CssTransformList? list) || list.HasUnsupported)
        {
            return null;
        }

        return [.. list.Functions];
    }

    private static CssTransformFunction Identity(CssTransformFunction function) => function switch
    {
        CssTranslateTransform => new CssTranslateTransform(new CssLength(0, CssUnit.Px), new CssLength(0, CssUnit.Px)),
        CssScaleTransform => new CssScaleTransform(1, 1),
        CssRotateTransform r => new CssRotateTransform(0, r.CenterX, r.CenterY),
        CssSkewTransform => new CssSkewTransform(0, 0),
        _ => new CssMatrixTransform(1, 0, 0, 1, 0, 0),
    };

    private static string? InterpolateFunction(CssTransformFunction a, CssTransformFunction b, double p, in SvgCssValueContext context)
    {
        Func<double, double, double> f = (x, y) => x + ((y - x) * p);
        switch (a, b)
        {
            case (CssTranslateTransform ta, CssTranslateTransform tb):
                return CombineLengths(ta.X, tb.X, context, f) is CssLength x && CombineLengths(ta.Y, tb.Y, context, f) is CssLength y
                    ? "translate(" + PixelText(x) + ", " + PixelText(y) + ")"
                    : null;
            case (CssScaleTransform sa, CssScaleTransform sb):
                return "scale(" + Format(f(sa.X, sb.X)) + ", " + Format(f(sa.Y, sb.Y)) + ")";
            case (CssRotateTransform ra, CssRotateTransform rb) when NoCenter(ra) && NoCenter(rb):
                return RotateText(f(ra.Degrees, rb.Degrees));
            case (CssSkewTransform ka, CssSkewTransform kb):
                return "skew(" + Format(f(ka.XDegrees, kb.XDegrees)) + "deg, " + Format(f(ka.YDegrees, kb.YDegrees)) + "deg)";
            case (CssMatrixTransform ma, CssMatrixTransform mb):
                return MatrixText(InterpolateMatrix(ToMatrix(ma), ToMatrix(mb), p));
            default:
                return null;
        }
    }

    private static string RotateText(double degrees) => "rotate(" + Format(degrees) + "deg)";

    private static bool NoCenter(CssRotateTransform r) => (r.CenterX ?? 0) == 0 && (r.CenterY ?? 0) == 0;

    /// <summary>The product of a list's functions from an index on; null when one has a length the matrix cannot take.</summary>
    private static Matrix3x2? Matrix(List<CssTransformFunction> functions, int start)
    {
        Matrix3x2 m = Matrix3x2.Identity;
        for (int i = start; i < functions.Count; i++)
        {
            Matrix3x2 f;
            switch (functions[i])
            {
                case CssTranslateTransform t:
                    if (!IsAbsolute(t.X) || !IsAbsolute(t.Y))
                    {
                        return null;
                    }

                    f = Matrix3x2.CreateTranslation((float)Pixels(t.X, default)!.Value, (float)Pixels(t.Y, default)!.Value);
                    break;
                case CssScaleTransform s:
                    f = Matrix3x2.CreateScale((float)s.X, (float)s.Y);
                    break;
                case CssRotateTransform r:
                    f = Matrix3x2.CreateRotation((float)(r.Degrees * Math.PI / 180), new Vector2((float)(r.CenterX ?? 0), (float)(r.CenterY ?? 0)));
                    break;
                case CssSkewTransform k:
                    f = Matrix3x2.CreateSkew((float)(k.XDegrees * Math.PI / 180), (float)(k.YDegrees * Math.PI / 180));
                    break;
                case CssMatrixTransform mt:
                    f = ToMatrix(mt);
                    break;
                default:
                    return null;
            }

            // Functions apply right to left; row vectors multiply the other way round.
            m = f * m;
        }

        return m;
    }

    private static bool IsAbsolute(CssLength length) => length.Unit is CssUnit.None or CssUnit.Px or CssUnit.In or CssUnit.Cm or CssUnit.Mm or CssUnit.Q or CssUnit.Pt or CssUnit.Pc;

    private static Matrix3x2 ToMatrix(CssMatrixTransform m) => new((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F);

    private static string MatrixText(Matrix3x2 m) =>
        string.Create(CultureInfo.InvariantCulture, $"matrix({Format(m.M11)}, {Format(m.M12)}, {Format(m.M21)}, {Format(m.M22)}, {Format(m.M31)}, {Format(m.M32)})");

    /// <summary>
    /// Interpolates two 2D matrices (CSS Transforms 1, "Interpolation of 2D matrices"): each is decomposed into translation,
    /// scale, rotation and skew, the parts interpolate (the rotation the shorter way round) and the result is recomposed.
    /// </summary>
    public static Matrix3x2 InterpolateMatrix(Matrix3x2 a, Matrix3x2 b, double p)
    {
        Decomposed x = Decompose(a);
        Decomposed y = Decompose(b);

        // One flipped on the x axis and the other on the y axis: flip both axes of one instead and turn it half a circle,
        // which is the same matrix.
        if ((x.ScaleX < 0 && y.ScaleY < 0) || (x.ScaleY < 0 && y.ScaleX < 0))
        {
            x = x with { ScaleX = -x.ScaleX, ScaleY = -x.ScaleY, Angle = x.Angle < 0 ? x.Angle + Math.PI : x.Angle - Math.PI };
        }

        double ax = x.Angle, ay = y.Angle;
        if (Math.Abs(ax - ay) > Math.PI)
        {
            if (ax > ay)
            {
                ax -= 2 * Math.PI;
            }
            else
            {
                ay -= 2 * Math.PI;
            }
        }

        double L(double u, double v) => u + ((v - u) * p);
        Decomposed r = new(L(x.TranslateX, y.TranslateX), L(x.TranslateY, y.TranslateY), L(x.ScaleX, y.ScaleX), L(x.ScaleY, y.ScaleY), L(ax, ay), L(x.Skew, y.Skew));
        return Recompose(r);
    }

    // A 2D matrix as CSS Transforms 1 decomposes it: translation, scale, rotation angle (radians) and the skew factor.
    private readonly record struct Decomposed(double TranslateX, double TranslateY, double ScaleX, double ScaleY, double Angle, double Skew);

    // CSS Transforms 1, "Decomposing a 2D matrix", on the rows of the linear part (row 0 is where the x axis goes, row 1 the y
    // axis): scale x is the length of row 0, which is then normalized; the skew is row 0 . row 1, taken out of row 1; scale y
    // is the length of what is left, which is normalized and divides the skew; a negative determinant flips x; the angle is
    // that of row 0.
    private static Decomposed Decompose(Matrix3x2 m)
    {
        double r0x = m.M11, r0y = m.M12, r1x = m.M21, r1y = m.M22;
        double sx = Math.Sqrt((r0x * r0x) + (r0y * r0y));
        if (sx != 0)
        {
            r0x /= sx;
            r0y /= sx;
        }

        double skew = (r0x * r1x) + (r0y * r1y);
        r1x -= skew * r0x;
        r1y -= skew * r0y;
        double sy = Math.Sqrt((r1x * r1x) + (r1y * r1y));
        if (sy != 0)
        {
            r1x /= sy;
            r1y /= sy;
            skew /= sy;
        }

        if ((r0x * r1y) - (r0y * r1x) < 0)
        {
            sx = -sx;
            r0x = -r0x;
            r0y = -r0y;
            skew = -skew;
        }

        double angle = sx == 0 && sy == 0 ? 0 : Math.Atan2(r0y, r0x);
        return new Decomposed(m.M31, m.M32, sx, sy, angle, skew);
    }

    // CSS Transforms 1, "Recomposing to a 2D matrix": scale, then skew, then rotate, then translate. Row 0 is the rotated x axis
    // times scale x; row 1 is the skew along it plus the rotated y axis, times scale y.
    private static Matrix3x2 Recompose(Decomposed d)
    {
        double cos = Math.Cos(d.Angle), sin = Math.Sin(d.Angle);
        double a = d.ScaleX * cos, b = d.ScaleX * sin;
        double c = d.ScaleY * ((d.Skew * cos) - sin);
        double dd = d.ScaleY * ((d.Skew * sin) + cos);
        return new Matrix3x2((float)a, (float)b, (float)c, (float)dd, (float)d.TranslateX, (float)d.TranslateY);
    }

    private static string? AddTransforms(string underlying, string value, bool accumulate)
    {
        if (IsNone(value))
        {
            return underlying;
        }

        if (IsNone(underlying))
        {
            return value;
        }

        if (accumulate && TransformList(underlying) is { } x && TransformList(value) is { } y && x.Count == y.Count)
        {
            List<string> parts = [];
            for (int i = 0; i < x.Count; i++)
            {
                string? part = (x[i], y[i]) switch
                {
                    (CssTranslateTransform ta, CssTranslateTransform tb) when ta.X.Unit == tb.X.Unit && ta.Y.Unit == tb.Y.Unit =>
                        "translate(" + PixelText(new CssLength(ta.X.Value + tb.X.Value, ta.X.Unit)) + ", " + PixelText(new CssLength(ta.Y.Value + tb.Y.Value, ta.Y.Unit)) + ")",
                    (CssScaleTransform sa, CssScaleTransform sb) => "scale(" + Format(sa.X + sb.X - 1) + ", " + Format(sa.Y + sb.Y - 1) + ")",
                    (CssRotateTransform ra, CssRotateTransform rb) when NoCenter(ra) && NoCenter(rb) => RotateText(ra.Degrees + rb.Degrees),
                    (CssSkewTransform ka, CssSkewTransform kb) => "skew(" + Format(ka.XDegrees + kb.XDegrees) + "deg, " + Format(ka.YDegrees + kb.YDegrees) + "deg)",
                    _ => null,
                };
                if (part is null)
                {
                    return underlying + " " + value;
                }

                parts.Add(part);
            }

            return string.Join(" ", parts);
        }

        return underlying + " " + value;
    }

    /// <summary>
    /// A length, percentage or number as an animation combines it: a plain value in its own unit, or (Mixed) a percentage
    /// and a length in px together, as calc() holds them (CSS Values 4, "Combination of length-percentage"). The mixed form
    /// keeps the percentage unresolved; whoever uses the value resolves it against its own reference.
    /// </summary>
    private readonly record struct LengthPercentage(CssLength Plain, double Percent, double Pixels, bool Mixed)
    {
        public static LengthPercentage Of(CssLength plain) => new(plain, 0, 0, false);

        public static LengthPercentage Mix(double percent, double pixels) => new(default, percent, pixels, true);

        /// <summary>The value as CSS text; <paramref name="pxNumbers"/> writes unitless numbers in px (where they are not lengths).</summary>
        public string ToText(bool pxNumbers)
        {
            if (!Mixed)
            {
                return pxNumbers ? PixelText(Plain) : LengthText(Plain);
            }

            if (Pixels == 0)
            {
                return Format(Percent) + "%";
            }

            if (Percent == 0)
            {
                return Format(Pixels) + "px";
            }

            return "calc(" + Format(Percent) + "% " + (Pixels < 0 ? "- " : "+ ") + Format(Math.Abs(Pixels)) + "px)";
        }
    }
}
