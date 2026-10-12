namespace Egami.Svg.Values;

/// <summary>The unit of an <see cref="SvgLength"/>.</summary>
internal enum SvgUnit : byte
{
    None,
    Px,
    Percent,
    Em,
    Ex,
    Ch,
    Rem,
    In,
    Cm,
    Mm,
    Q,
    Pt,
    Pc,
    Vw,
    Vh,
    Vmin,
    Vmax,
}

/// <summary>Which viewport dimension a percentage is of: SVG 1.1 section 7.10.</summary>
internal enum SvgAxis : byte
{
    /// <summary>The viewport's width (x, cx, width, rx, dx...).</summary>
    X,

    /// <summary>The viewport's height (y, cy, height, ry, dy...).</summary>
    Y,

    /// <summary>The viewport's normalized diagonal, sqrt((w² + h²) / 2) (r, stroke-width, stroke-dasharray...).</summary>
    Other,
}

/// <summary>What lengths resolve against: resolution, fonts and the nearest viewport.</summary>
internal readonly record struct SvgLengthContext(float Dpi, float FontSize, float RootFontSize, float ViewportWidth, float ViewportHeight, float RootWidth, float RootHeight)
{
    /// <summary>The context at the root of a document whose viewport is not known yet: 96 dpi, 16px fonts, a 100 by 100 viewport.</summary>
    public static SvgLengthContext Default { get; } = new(96, 16, 16, 100, 100, 100, 100);
}

/// <summary>An SVG length: a number and a unit, resolved to user units (pixels) against a <see cref="SvgLengthContext"/>.</summary>
internal readonly record struct SvgLength(float Value, SvgUnit Unit)
{
    public static SvgLength Zero { get; } = new(0, SvgUnit.None);

    public bool IsPercent => Unit == SvgUnit.Percent;

    /// <summary>Reads a whole attribute as a length: a number and an optional unit (case-insensitive), with whitespace around.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out SvgLength length)
    {
        int i = 0;
        SvgNumbers.SkipSpace(text, ref i);
        if (!TryRead(text, ref i, out length))
        {
            return false;
        }

        SvgNumbers.SkipSpace(text, ref i);
        return i == text.Length;
    }

    /// <summary>Reads a length at a position in a list, moving past it.</summary>
    public static bool TryRead(ReadOnlySpan<char> text, ref int position, out SvgLength length)
    {
        int i = position;
        if (!SvgNumbers.TryRead(text, ref i, out double number))
        {
            length = default;
            return false;
        }

        int unitStart = i;
        if (i < text.Length && text[i] == '%')
        {
            i++;
        }
        else
        {
            while (i < text.Length && char.IsAsciiLetter(text[i]))
            {
                i++;
            }
        }

        if (!TryUnit(text[unitStart..i], out SvgUnit unit))
        {
            length = default;
            return false;
        }

        position = i;
        length = new SvgLength((float)number, unit);
        return true;
    }

    /// <summary>Reads a list of lengths separated by whitespace or commas (x, y, dx and dy lists, stroke-dasharray).</summary>
    public static bool TryParseList(ReadOnlySpan<char> text, List<SvgLength> values)
    {
        int i = 0;
        SvgNumbers.SkipSpace(text, ref i);
        while (i < text.Length)
        {
            if (!TryRead(text, ref i, out SvgLength value))
            {
                return false;
            }

            values.Add(value);
            SvgNumbers.SkipSeparator(text, ref i);
        }

        return true;
    }

    /// <summary>Resolves to user units.</summary>
    public float ToPixels(in SvgLengthContext context, SvgAxis axis) => Unit switch
    {
        SvgUnit.None or SvgUnit.Px => Value,
        SvgUnit.Percent => Value / 100f * axis switch
        {
            SvgAxis.X => context.ViewportWidth,
            SvgAxis.Y => context.ViewportHeight,
            _ => MathF.Sqrt(((context.ViewportWidth * context.ViewportWidth) + (context.ViewportHeight * context.ViewportHeight)) / 2),
        },
        SvgUnit.Em => Value * context.FontSize,
        SvgUnit.Ex or SvgUnit.Ch => Value * context.FontSize / 2,
        SvgUnit.Rem => Value * context.RootFontSize,
        SvgUnit.In => Value * context.Dpi,
        SvgUnit.Cm => Value * context.Dpi / 2.54f,
        SvgUnit.Mm => Value * context.Dpi / 25.4f,
        SvgUnit.Q => Value * context.Dpi / 101.6f,
        SvgUnit.Pt => Value * context.Dpi / 72,
        SvgUnit.Pc => Value * context.Dpi / 6,
        SvgUnit.Vw => Value * context.RootWidth / 100,
        SvgUnit.Vh => Value * context.RootHeight / 100,
        SvgUnit.Vmin => Value * Math.Min(context.RootWidth, context.RootHeight) / 100,
        SvgUnit.Vmax => Value * Math.Max(context.RootWidth, context.RootHeight) / 100,
        _ => Value,
    };

    /// <summary>Resolves a length in objectBoundingBox units: a number or percentage is a fraction of the box's side.</summary>
    public float ToFraction() => Unit == SvgUnit.Percent ? Value / 100 : Value;

    private static bool TryUnit(ReadOnlySpan<char> unit, out SvgUnit result)
    {
        result = unit.Length switch
        {
            0 => SvgUnit.None,
            1 when unit[0] == '%' => SvgUnit.Percent,
            1 when unit[0] is 'q' or 'Q' => SvgUnit.Q,
            2 when unit.Equals("px", StringComparison.OrdinalIgnoreCase) => SvgUnit.Px,
            2 when unit.Equals("em", StringComparison.OrdinalIgnoreCase) => SvgUnit.Em,
            2 when unit.Equals("ex", StringComparison.OrdinalIgnoreCase) => SvgUnit.Ex,
            2 when unit.Equals("ch", StringComparison.OrdinalIgnoreCase) => SvgUnit.Ch,
            2 when unit.Equals("in", StringComparison.OrdinalIgnoreCase) => SvgUnit.In,
            2 when unit.Equals("cm", StringComparison.OrdinalIgnoreCase) => SvgUnit.Cm,
            2 when unit.Equals("mm", StringComparison.OrdinalIgnoreCase) => SvgUnit.Mm,
            2 when unit.Equals("pt", StringComparison.OrdinalIgnoreCase) => SvgUnit.Pt,
            2 when unit.Equals("pc", StringComparison.OrdinalIgnoreCase) => SvgUnit.Pc,
            2 when unit.Equals("vw", StringComparison.OrdinalIgnoreCase) => SvgUnit.Vw,
            2 when unit.Equals("vh", StringComparison.OrdinalIgnoreCase) => SvgUnit.Vh,
            3 when unit.Equals("rem", StringComparison.OrdinalIgnoreCase) => SvgUnit.Rem,
            4 when unit.Equals("vmin", StringComparison.OrdinalIgnoreCase) => SvgUnit.Vmin,
            4 when unit.Equals("vmax", StringComparison.OrdinalIgnoreCase) => SvgUnit.Vmax,
            _ => (SvgUnit)255,
        };
        return result != (SvgUnit)255;
    }
}
