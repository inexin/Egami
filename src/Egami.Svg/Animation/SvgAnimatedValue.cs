using System.Globalization;
using System.Text;

using Egami.Css;
using Egami.Svg.Values;

namespace Egami.Svg.Animation;

/// <summary>How an animated attribute's values are read, added and interpolated (SVG Animations Level 2, "Animatable data types").</summary>
internal enum SvgValueKind : byte
{
    /// <summary>Keywords, references and anything else: values change in steps.</summary>
    Discrete,

    /// <summary>Numbers, lengths and lists of them (points, viewBox, stroke-dasharray).</summary>
    Numeric,

    /// <summary>A color, or a paint that is a color.</summary>
    Color,

    /// <summary>Path data.</summary>
    Path,

    /// <summary>One animateTransform transform: a type and its parameters.</summary>
    Transform,
}

/// <summary>
/// A value an animation interpolates: it can be interpolated towards another of the same shape, added to one (additive and
/// accumulate), measured against one (paced timing) and written back as attribute text.
/// </summary>
internal abstract class SvgAnimatedValue
{
    /// <summary>Formats a number for attribute text: invariant, at most six decimals, no exponent.</summary>
    public static string Format(double value)
    {
        if (Math.Abs(value) < 5e-7)
        {
            return "0";
        }

        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a value of a kind; null when the text is not one.</summary>
    public static SvgAnimatedValue? Parse(SvgValueKind kind, string text, string? transformType = null) => kind switch
    {
        SvgValueKind.Numeric => SvgNumericValue.TryParse(text),
        SvgValueKind.Color => SvgColorValue.TryParse(text),
        SvgValueKind.Path => SvgPathValue.TryParse(text),
        SvgValueKind.Transform => SvgTransformValue.TryParse(transformType ?? "translate", text),
        _ => null,
    };

    /// <summary>The value at <paramref name="p"/> of the way from this one to <paramref name="to"/>; null when their shapes differ.</summary>
    public abstract SvgAnimatedValue? Interpolate(SvgAnimatedValue to, double p);

    /// <summary>This value plus <paramref name="times"/> times <paramref name="other"/>; null when their shapes differ.</summary>
    public abstract SvgAnimatedValue? Add(SvgAnimatedValue other, double times = 1);

    /// <summary>The distance to another value for paced timing; NaN when the kind has none or the shapes differ.</summary>
    public abstract double Distance(SvgAnimatedValue other);

    /// <summary>A value of the same shape that adds nothing (the start of a by animation).</summary>
    public abstract SvgAnimatedValue Zero();

    /// <summary>The value as attribute text.</summary>
    public abstract string ToText();

    /// <inheritdoc/>
    public override string ToString() => ToText();
}

/// <summary>A number, a length, or a list of them; items of the same unit, or of absolute units, interpolate.</summary>
internal sealed class SvgNumericValue(SvgNumericValue.Item[] items) : SvgAnimatedValue
{
    public readonly record struct Item(double Value, SvgUnit Unit);

    public Item[] Items { get; } = items;

    public static SvgNumericValue? TryParse(string text)
    {
        List<Item> items = [];
        ReadOnlySpan<char> s = text;
        int i = 0;
        SvgNumbers.SkipSpace(s, ref i);
        while (i < s.Length)
        {
            if (!SvgNumbers.TryRead(s, ref i, out double number))
            {
                return null;
            }

            int unitStart = i;
            if (i < s.Length && s[i] == '%')
            {
                i++;
            }
            else
            {
                while (i < s.Length && char.IsAsciiLetter(s[i]))
                {
                    i++;
                }
            }

            if (!SvgLength.TryParse(string.Concat("0", s[unitStart..i]), out SvgLength unit))
            {
                return null;
            }

            items.Add(new Item(number, unit.Unit));
            SvgNumbers.SkipSeparator(s, ref i);
        }

        return items.Count == 0 ? null : new SvgNumericValue([.. items]);
    }

    public override SvgAnimatedValue? Interpolate(SvgAnimatedValue to, double p) =>
        Combine(to, (a, b) => a + ((b - a) * p));

    public override SvgAnimatedValue? Add(SvgAnimatedValue other, double times = 1) =>
        Combine(other, (a, b) => a + (b * times));

    public override double Distance(SvgAnimatedValue other)
    {
        if (other is not SvgNumericValue o || o.Items.Length != Items.Length)
        {
            return double.NaN;
        }

        double sum = 0;
        for (int i = 0; i < Items.Length; i++)
        {
            if (!Pair(Items[i], o.Items[i], out double a, out double b, out _))
            {
                return double.NaN;
            }

            sum += (b - a) * (b - a);
        }

        return Math.Sqrt(sum);
    }

    public override SvgAnimatedValue Zero() => new SvgNumericValue([.. Items.Select(i => i with { Value = 0 })]);

    public override string ToText()
    {
        StringBuilder b = new();
        foreach (Item item in Items)
        {
            if (b.Length > 0)
            {
                b.Append(' ');
            }

            b.Append(Format(item.Value)).Append(UnitText(item.Unit));
        }

        return b.ToString();
    }

    private SvgNumericValue? Combine(SvgAnimatedValue other, Func<double, double, double> f)
    {
        if (other is not SvgNumericValue o || o.Items.Length != Items.Length)
        {
            return null;
        }

        Item[] result = new Item[Items.Length];
        for (int i = 0; i < Items.Length; i++)
        {
            if (!Pair(Items[i], o.Items[i], out double a, out double b, out SvgUnit unit))
            {
                return null;
            }

            result[i] = new Item(f(a, b), unit);
        }

        return new SvgNumericValue(result);
    }

    /// <summary>Two items in one unit: the same unit (px and none count as one), or both absolute, then in px.</summary>
    private static bool Pair(Item x, Item y, out double a, out double b, out SvgUnit unit)
    {
        SvgUnit ux = x.Unit == SvgUnit.Px ? SvgUnit.None : x.Unit;
        SvgUnit uy = y.Unit == SvgUnit.Px ? SvgUnit.None : y.Unit;
        if (ux == uy)
        {
            (a, b, unit) = (x.Value, y.Value, x.Unit == y.Unit ? x.Unit : SvgUnit.None);
            return true;
        }

        if (Absolute(ux, out double sx) && Absolute(uy, out double sy))
        {
            (a, b, unit) = (x.Value * sx, y.Value * sy, SvgUnit.None);
            return true;
        }

        (a, b, unit) = (0, 0, SvgUnit.None);
        return false;
    }

    /// <summary>Pixels per unit of an absolute unit at CSS's 96 pixels per inch.</summary>
    private static bool Absolute(SvgUnit unit, out double scale)
    {
        scale = unit switch
        {
            SvgUnit.None => 1,
            SvgUnit.In => 96,
            SvgUnit.Cm => 96 / 2.54,
            SvgUnit.Mm => 96 / 25.4,
            SvgUnit.Q => 96 / 101.6,
            SvgUnit.Pt => 96.0 / 72,
            SvgUnit.Pc => 16,
            _ => 0,
        };
        return scale > 0;
    }

    private static string UnitText(SvgUnit unit) => unit switch
    {
        SvgUnit.Px => "px",
        SvgUnit.Percent => "%",
        SvgUnit.Em => "em",
        SvgUnit.Ex => "ex",
        SvgUnit.Ch => "ch",
        SvgUnit.Rem => "rem",
        SvgUnit.In => "in",
        SvgUnit.Cm => "cm",
        SvgUnit.Mm => "mm",
        SvgUnit.Q => "Q",
        SvgUnit.Pt => "pt",
        SvgUnit.Pc => "pc",
        SvgUnit.Vw => "vw",
        SvgUnit.Vh => "vh",
        SvgUnit.Vmin => "vmin",
        SvgUnit.Vmax => "vmax",
        _ => "",
    };
}

/// <summary>An sRGB color with alpha, components from 0 to 1; sums may leave that range until the value is written.</summary>
internal sealed class SvgColorValue(double r, double g, double b, double a) : SvgAnimatedValue
{
    public double R { get; } = r;

    public double G { get; } = g;

    public double B { get; } = b;

    public double A { get; } = a;

    public static SvgColorValue? TryParse(string text)
    {
        if (!CssColor.TryParse(text.Trim(), out CssColor color) || color.IsCurrentColor)
        {
            return null;
        }

        return new SvgColorValue(color.R, color.G, color.B, color.A);
    }

    public override SvgAnimatedValue? Interpolate(SvgAnimatedValue to, double p) =>
        to is SvgColorValue c ? new SvgColorValue(R + ((c.R - R) * p), G + ((c.G - G) * p), B + ((c.B - B) * p), A + ((c.A - A) * p)) : null;

    public override SvgAnimatedValue? Add(SvgAnimatedValue other, double times = 1) =>
        other is SvgColorValue c ? new SvgColorValue(R + (c.R * times), G + (c.G * times), B + (c.B * times), A + (c.A * times)) : null;

    /// <summary>The distance in RGB space, in 0 to 255 units, as SVG 1.1's paced color animation measures it.</summary>
    public override double Distance(SvgAnimatedValue other)
    {
        if (other is not SvgColorValue c)
        {
            return double.NaN;
        }

        double dr = (c.R - R) * 255, dg = (c.G - G) * 255, db = (c.B - B) * 255;
        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    public override SvgAnimatedValue Zero() => new SvgColorValue(0, 0, 0, 0);

    /// <summary>Writes rgb() or rgba() with 8-bit channels, clamped, as browsers round animated colors.</summary>
    public override string ToText()
    {
        int r = To8(R), g = To8(G), b = To8(B);
        double a = Math.Clamp(A, 0, 1);
        return a >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"rgb({r}, {g}, {b})")
            : string.Create(CultureInfo.InvariantCulture, $"rgba({r}, {g}, {b}, {Format(a)})");
    }

    private static int To8(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
}

/// <summary>Path data as commands and their numbers; paths with the same commands interpolate number by number.</summary>
internal sealed class SvgPathValue(SvgPathValue.Segment[] segments) : SvgAnimatedValue
{
    public readonly record struct Segment(char Command, double[] Args);

    public Segment[] Segments { get; } = segments;

    /// <summary>Reads path data strictly: implicit repeats become explicit commands (after a moveto, linetos).</summary>
    public static SvgPathValue? TryParse(string text)
    {
        List<Segment> segments = [];
        ReadOnlySpan<char> s = text;
        int i = 0;
        char command = '\0';
        SvgNumbers.SkipSpace(s, ref i);
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsAsciiLetter(c))
            {
                if (Arity(c) < 0 || (segments.Count == 0 && c is not ('M' or 'm')))
                {
                    return null;
                }

                command = c;
                i++;
                if (c is 'Z' or 'z')
                {
                    segments.Add(new Segment('Z', []));
                    SvgNumbers.SkipSeparator(s, ref i);
                    continue;
                }
            }
            else if (command is '\0' or 'Z' or 'z')
            {
                return null;
            }
            else if (command is 'M' or 'm')
            {
                command = command == 'M' ? 'L' : 'l';
            }

            int arity = Arity(command);
            double[] args = new double[arity];
            for (int k = 0; k < arity; k++)
            {
                SvgNumbers.SkipSeparator(s, ref i);
                if (char.ToUpperInvariant(command) == 'A' && k is 3 or 4)
                {
                    // Arc flags are one character each and need no separator.
                    if (i >= s.Length || s[i] is not ('0' or '1'))
                    {
                        return null;
                    }

                    args[k] = s[i] - '0';
                    i++;
                }
                else if (!SvgNumbers.TryRead(s, ref i, out args[k]))
                {
                    return null;
                }
            }

            segments.Add(new Segment(command, args));
            SvgNumbers.SkipSeparator(s, ref i);
        }

        return segments.Count == 0 ? null : new SvgPathValue([.. segments]);
    }

    public override SvgAnimatedValue? Interpolate(SvgAnimatedValue to, double p) =>
        Combine(to, (a, b, flag) => flag ? (a + ((b - a) * p) != 0 ? 1 : 0) : a + ((b - a) * p));

    public override SvgAnimatedValue? Add(SvgAnimatedValue other, double times = 1) =>
        Combine(other, (a, b, flag) => flag ? (a + (b * times) != 0 ? 1 : 0) : a + (b * times));

    public override double Distance(SvgAnimatedValue other) => double.NaN;

    public override SvgAnimatedValue Zero() => new SvgPathValue([.. Segments.Select(s => s with { Args = new double[s.Args.Length] })]);

    public override string ToText()
    {
        StringBuilder b = new();
        foreach (Segment segment in Segments)
        {
            if (b.Length > 0)
            {
                b.Append(' ');
            }

            b.Append(segment.Command);
            for (int k = 0; k < segment.Args.Length; k++)
            {
                b.Append(k == 0 ? "" : " ").Append(Format(segment.Args[k]));
            }
        }

        return b.ToString();
    }

    /// <summary>Combines two paths command by command: as they are when the letters match, in absolute form when only case differs.</summary>
    private SvgPathValue? Combine(SvgAnimatedValue other, Func<double, double, bool, double> f)
    {
        if (other is not SvgPathValue o || o.Segments.Length != Segments.Length)
        {
            return null;
        }

        Segment[] x = Segments, y = o.Segments;
        bool same = true;
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i].Command != y[i].Command)
            {
                if (char.ToUpperInvariant(x[i].Command) != char.ToUpperInvariant(y[i].Command))
                {
                    return null;
                }

                same = false;
            }
        }

        if (!same)
        {
            x = Absolute(x);
            y = Absolute(y);
        }

        Segment[] result = new Segment[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            double[] args = new double[x[i].Args.Length];
            bool arc = char.ToUpperInvariant(x[i].Command) == 'A';
            for (int k = 0; k < args.Length; k++)
            {
                args[k] = f(x[i].Args[k], y[i].Args[k], arc && k is 3 or 4);
            }

            result[i] = new Segment(x[i].Command, args);
        }

        return new SvgPathValue(result);
    }

    /// <summary>The same path with every command absolute.</summary>
    private static Segment[] Absolute(Segment[] segments)
    {
        Segment[] result = new Segment[segments.Length];
        double cx = 0, cy = 0, sx = 0, sy = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            char c = segments[i].Command;
            double[] a = (double[])segments[i].Args.Clone();
            bool relative = char.IsAsciiLetterLower(c);
            char upper = char.ToUpperInvariant(c);
            switch (upper)
            {
                case 'H':
                    a[0] += relative ? cx : 0;
                    cx = a[0];
                    break;
                case 'V':
                    a[0] += relative ? cy : 0;
                    cy = a[0];
                    break;
                case 'A':
                    if (relative)
                    {
                        a[5] += cx;
                        a[6] += cy;
                    }

                    (cx, cy) = (a[5], a[6]);
                    break;
                case 'Z':
                    (cx, cy) = (sx, sy);
                    break;
                default:
                    if (relative)
                    {
                        for (int k = 0; k < a.Length; k += 2)
                        {
                            a[k] += cx;
                            a[k + 1] += cy;
                        }
                    }

                    (cx, cy) = (a[^2], a[^1]);
                    if (upper == 'M')
                    {
                        (sx, sy) = (cx, cy);
                    }

                    break;
            }

            result[i] = new Segment(upper, a);
        }

        return result;
    }

    private static int Arity(char command) => char.ToUpperInvariant(command) switch
    {
        'M' or 'L' or 'T' => 2,
        'H' or 'V' => 1,
        'C' => 6,
        'S' or 'Q' => 4,
        'A' => 7,
        'Z' => 0,
        _ => -1,
    };
}

/// <summary>
/// One transform of animateTransform's type: translate (tx, ty), scale (sx, sy), rotate (angle, cx, cy), skewX or skewY
/// (angle). Missing parameters take SVG's defaults (ty 0, sy = sx, cx and cy 0).
/// </summary>
internal sealed class SvgTransformValue(string type, double[] parameters) : SvgAnimatedValue
{
    public string Type { get; } = type;

    public double[] Parameters { get; } = parameters;

    public static SvgTransformValue? TryParse(string type, string text)
    {
        List<double> numbers = [];
        if (!SvgNumbers.TryParseList(text, numbers) || numbers.Count == 0)
        {
            return null;
        }

        double[]? p = (type, numbers.Count) switch
        {
            ("translate", 1) => [numbers[0], 0],
            ("translate", 2) => [numbers[0], numbers[1]],
            ("scale", 1) => [numbers[0], numbers[0]],
            ("scale", 2) => [numbers[0], numbers[1]],
            ("rotate", 1) => [numbers[0], 0, 0],
            ("rotate", 3) => [numbers[0], numbers[1], numbers[2]],
            ("skewX" or "skewY", 1) => [numbers[0]],
            _ => null,
        };
        return p is null ? null : new SvgTransformValue(type, p);
    }

    /// <summary>
    /// The transform a to animation starts from: the underlying transform list when it is one transform of the same type, else
    /// the identity of the type (scale 1, no move, no turn about the to value's center, no skew).
    /// </summary>
    public static SvgTransformValue From(string? underlying, SvgTransformValue to)
    {
        string text = underlying?.Trim() ?? "";
        int open = text.IndexOf('(', StringComparison.Ordinal);
        if (open > 0 && text.EndsWith(')') && text.IndexOf(')', StringComparison.Ordinal) == text.Length - 1 &&
            text[..open].Trim() == to.Type && TryParse(to.Type, text[(open + 1)..^1]) is SvgTransformValue same)
        {
            return same;
        }

        return to.Type switch
        {
            "scale" => new SvgTransformValue("scale", [1, 1]),
            "rotate" => new SvgTransformValue("rotate", [0, to.Parameters[1], to.Parameters[2]]),
            _ => (SvgTransformValue)to.Zero(),
        };
    }

    public override SvgAnimatedValue? Interpolate(SvgAnimatedValue to, double p) =>
        to is SvgTransformValue t && t.Type == Type ? new SvgTransformValue(Type, [.. Parameters.Select((v, i) => v + ((t.Parameters[i] - v) * p))]) : null;

    public override SvgAnimatedValue? Add(SvgAnimatedValue other, double times = 1) =>
        other is SvgTransformValue t && t.Type == Type ? new SvgTransformValue(Type, [.. Parameters.Select((v, i) => v + (t.Parameters[i] * times))]) : null;

    /// <summary>SVG 1.1's paced distances: Euclidean for translate and scale, the angle alone for rotate and skew.</summary>
    public override double Distance(SvgAnimatedValue other)
    {
        if (other is not SvgTransformValue t || t.Type != Type)
        {
            return double.NaN;
        }

        return Type is "translate" or "scale"
            ? Math.Sqrt(Math.Pow(t.Parameters[0] - Parameters[0], 2) + Math.Pow(t.Parameters[1] - Parameters[1], 2))
            : Math.Abs(t.Parameters[0] - Parameters[0]);
    }

    public override SvgAnimatedValue Zero() => new SvgTransformValue(Type, new double[Parameters.Length]);

    public override string ToText() => $"{Type}({string.Join(' ', Parameters.Select(Format))})";
}
