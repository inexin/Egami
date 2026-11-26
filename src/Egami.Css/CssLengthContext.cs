namespace Egami.Css;

/// <summary>
/// What relative lengths are worth where a value is used, in CSS pixels: the element's font size and the root's, the viewport,
/// and the reference length percentages take. <see cref="CssComputedStyle.TryResolveLength(string, CssLengthContext, out double)"/>
/// and <see cref="TryResolve(CssLength, out double)"/> turn lengths, percentages and <c>calc()</c> into pixels with it.
/// </summary>
/// <remarks>
/// Without font data, <c>ex</c> and <c>ch</c> are half an <c>em</c> and <c>ic</c> one <c>em</c>, as CSS Values 4 allows;
/// <see cref="XHeight"/> and <see cref="ChWidth"/> give the font's real metrics when the consumer knows them. <c>cap</c>,
/// <c>rcap</c> and <c>rlh</c>, <c>lh</c> without <see cref="LineHeight"/>, and percentages without <see cref="PercentBase"/>
/// do not resolve. The viewport units of the small, large and dynamic viewports, and <c>vi</c> and <c>vb</c>, take the one
/// viewport given, in a horizontal writing mode.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="getters"/>
/// </example>
public readonly record struct CssLengthContext
{
    /// <summary>Creates a context.</summary>
    /// <param name="fontSize">The element's font size in pixels (<c>em</c>).</param>
    /// <param name="rootFontSize">The root element's font size in pixels (<c>rem</c>).</param>
    /// <param name="viewportWidth">The viewport width in pixels (<c>vw</c>).</param>
    /// <param name="viewportHeight">The viewport height in pixels (<c>vh</c>).</param>
    public CssLengthContext(double fontSize, double rootFontSize, double viewportWidth, double viewportHeight)
    {
        FontSize = fontSize;
        RootFontSize = rootFontSize;
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
    }

    /// <summary>Gets the element's font size in pixels: what <c>em</c> is worth.</summary>
    public double FontSize { get; init; }

    /// <summary>Gets the root element's font size in pixels: what <c>rem</c> is worth.</summary>
    public double RootFontSize { get; init; }

    /// <summary>Gets the viewport width in pixels: a hundred <c>vw</c>.</summary>
    public double ViewportWidth { get; init; }

    /// <summary>Gets the viewport height in pixels: a hundred <c>vh</c>.</summary>
    public double ViewportHeight { get; init; }

    /// <summary>
    /// Gets the reference length a percentage is a percentage of, in pixels (such as the containing block's width), or
    /// <see langword="null"/> when percentages do not resolve.
    /// </summary>
    public double? PercentBase { get; init; }

    /// <summary>Gets the element's line height in pixels (<c>lh</c>), or <see langword="null"/> when <c>lh</c> does not resolve.</summary>
    public double? LineHeight { get; init; }

    /// <summary>Gets the font's x-height in pixels (<c>ex</c>), or <see langword="null"/> for half the font size.</summary>
    public double? XHeight { get; init; }

    /// <summary>Gets the advance of the font's "0" in pixels (<c>ch</c>), or <see langword="null"/> for half the font size.</summary>
    public double? ChWidth { get; init; }

    /// <summary>Converts a length or percentage to pixels.</summary>
    /// <param name="length">The length. A unitless number resolves only when it is zero.</param>
    /// <param name="pixels">The length in pixels.</param>
    /// <returns>True when the unit resolves in this context.</returns>
    public bool TryResolve(CssLength length, out double pixels)
    {
        if (length.Unit == CssUnit.None)
        {
            pixels = 0;
            return length.Value == 0;
        }

        double perUnit = PixelsPerUnit(length.Unit);
        pixels = length.Value * perUnit;
        return !double.IsNaN(pixels);
    }

    /// <summary>Resolves a length, percentage or math function to pixels.</summary>
    /// <param name="calc">The value. A plain number does not resolve, as it is not a length.</param>
    /// <param name="pixels">The length in pixels.</param>
    /// <returns>True when every term resolves in this context.</returns>
    public bool TryResolve(CssCalc calc, out double pixels)
    {
        ArgumentNullException.ThrowIfNull(calc);
        pixels = 0;
        if (calc.IsNumber)
        {
            return false;
        }

        Resolver resolver = new(this);
        double result = calc.Resolve(resolver.PixelsPerUnit);
        if (resolver.Failed)
        {
            return false;
        }

        pixels = result;
        return true;
    }

    /// <summary>Gets the pixels one unit is worth here, or NaN when the unit does not resolve.</summary>
    internal double PixelsPerUnit(CssUnit unit)
    {
        double absolute = CssLength.AbsolutePixelsPerUnit(unit);
        if (!double.IsNaN(absolute))
        {
            return absolute;
        }

        return unit switch
        {
            CssUnit.Percent => PercentBase is { } basis ? basis / 100 : double.NaN,
            CssUnit.Em => FontSize,
            CssUnit.Rem => RootFontSize,
            CssUnit.Ex => XHeight ?? FontSize / 2,
            CssUnit.Rex or CssUnit.Rch => RootFontSize / 2,
            CssUnit.Ch => ChWidth ?? FontSize / 2,
            CssUnit.Ic => FontSize,
            CssUnit.Ric => RootFontSize,
            CssUnit.Lh => LineHeight ?? double.NaN,
            CssUnit.Vw or CssUnit.Vi or CssUnit.Svw or CssUnit.Lvw or CssUnit.Dvw => ViewportWidth / 100,
            CssUnit.Vh or CssUnit.Vb or CssUnit.Svh or CssUnit.Lvh or CssUnit.Dvh => ViewportHeight / 100,
            CssUnit.Vmin => Math.Min(ViewportWidth, ViewportHeight) / 100,
            CssUnit.Vmax => Math.Max(ViewportWidth, ViewportHeight) / 100,
            _ => double.NaN,
        };
    }

    // Remembers whether a calc() term asked for a unit that does not resolve (CssCalc turns NaN into 0).
    private sealed class Resolver(CssLengthContext context)
    {
        public bool Failed { get; private set; }

        public double PixelsPerUnit(CssUnit unit)
        {
            double value = context.PixelsPerUnit(unit);
            if (double.IsNaN(value))
            {
                Failed = true;
            }

            return value;
        }
    }
}
