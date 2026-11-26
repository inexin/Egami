using System.Diagnostics.CodeAnalysis;

namespace Egami.Css;

// Typed getters: each reads one property's computed value as a type, in the TryGetValue style.
public sealed partial class CssComputedStyle
{
    private const double MediumFontSize = 16;
    private const double NormalFontWeight = 400;

    // The absolute-size keywords and their scaling factors from 'medium' (CSS Fonts 4, "font-size").
    private static readonly double[] s_absoluteSizes =
    [
        MediumFontSize * 3 / 5, MediumFontSize * 3 / 4, MediumFontSize * 8 / 9, MediumFontSize,
        MediumFontSize * 6 / 5, MediumFontSize * 3 / 2, MediumFontSize * 2, MediumFontSize * 3,
    ];

    // The resolved font weight, NaN until asked for.
    private double _fontWeight = double.NaN;

    // The resolved font size and the context values it depends on, under a sequence lock: the version is odd while a
    // writer fills them, so a reader on another thread never takes a size with another context's key.
    private int _fontSizeVersion;
    private double _fontSize = double.NaN;
    private double _fontSizeRoot;
    private double _fontSizeWidth;
    private double _fontSizeHeight;

    /// <summary>Gets a property's value as one keyword, such as <c>block</c> for <c>display</c>.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="keyword">The identifier, ASCII-lowercased.</param>
    /// <returns>True when the value is a single identifier.</returns>
    public bool TryGetKeyword(string name, [NotNullWhen(true)] out string? keyword)
    {
        keyword = Single(name) is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } ? CssText.ToAsciiLower(ident.Value) : null;
        return keyword is not null;
    }

    /// <summary>Gets every identifier in a property's value, such as <c>underline</c> and <c>overline</c> for <c>text-decoration-line</c>.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The identifiers at the top level of the value, ASCII-lowercased and in order; empty when there are none.</returns>
    public IReadOnlyList<string> GetKeywords(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Lookup(name) is not { } values)
        {
            return [];
        }

        int count = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is CssTokenValue { Token.Kind: CssTokenKind.Ident })
            {
                count++;
            }
        }

        if (count == 0)
        {
            return [];
        }

        string[] keywords = new string[count];
        count = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
            {
                keywords[count++] = CssText.ToAsciiLower(ident.Value);
            }
        }

        return keywords;
    }

    /// <summary>Gets a property's value as a number, such as <c>1.5</c> for <c>flex-grow</c>; a <c>calc()</c> of numbers is computed.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="number">The number.</param>
    /// <returns>True when the value is one number or a math function of numbers.</returns>
    public bool TryGetNumber(string name, out double number)
    {
        return TryNumber(Single(name), out number, out _);
    }

    /// <summary>
    /// Gets a property's value as an integer, such as <c>z-index</c> or <c>orphans</c>. A <c>calc()</c> of numbers is computed and
    /// rounded to the nearest integer (halves toward positive infinity), as CSS Values 4 says.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The integer, clamped to the range of <see cref="int"/>.</param>
    /// <returns>True when the value is one integer or a math function of numbers.</returns>
    public bool TryGetInteger(string name, out int value)
    {
        value = 0;
        CssComponentValue? single = Single(name);
        if (!TryNumber(single, out double number, out bool isInteger) || (!isInteger && single is CssTokenValue))
        {
            return false;
        }

        double rounded = Math.Floor(number + 0.5);
        value = rounded >= int.MaxValue ? int.MaxValue : rounded <= int.MinValue ? int.MinValue : (int)rounded;
        return true;
    }

    /// <summary>
    /// Gets a property's value as one length or percentage with its unit, such as <c>2em</c> or <c>50%</c>. A bare <c>0</c>
    /// reads as <c>0px</c>; other unitless numbers and math functions give false (use
    /// <see cref="TryResolveLength(string, CssLengthContext, out double)"/> for <c>calc()</c>).
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <param name="length">The length; a percentage has <see cref="CssUnit.Percent"/>.</param>
    /// <returns>True when the value is one length or percentage.</returns>
    public bool TryGetLength(string name, out CssLength length)
    {
        return TryLength(Single(name), out length);
    }

    /// <summary>
    /// Gets a property's value in CSS pixels: a length, a percentage of <see cref="CssLengthContext.PercentBase"/>, or a
    /// <c>calc()</c>, <c>min()</c>, <c>max()</c> or <c>clamp()</c> of them.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <param name="context">What relative units are worth.</param>
    /// <param name="pixels">The length in pixels.</param>
    /// <returns>
    /// True when the value is a length that resolves in the context; false for keywords, for percentages when the context
    /// has no <see cref="CssLengthContext.PercentBase"/>, and for units the context cannot resolve.
    /// </returns>
    public bool TryResolveLength(string name, CssLengthContext context, out double pixels)
    {
        return TryResolve(Single(name), context, out pixels);
    }

    /// <summary>
    /// Gets a property's value as a color, with <c>currentcolor</c> resolved to the element's <c>color</c>. On the <c>color</c>
    /// property itself <c>currentcolor</c> takes the parent's color, as CSS Color 4 says.
    /// </summary>
    /// <param name="name">The property name, such as <c>color</c> or <c>text-decoration-color</c>.</param>
    /// <param name="color">The color; never <see cref="CssColor.CurrentColor"/>.</param>
    /// <returns>True when the value is one color (and, for <c>currentcolor</c>, the element's color resolves).</returns>
    /// <remarks><see cref="TryGetValue"/> with <see cref="CssColor.TryParse(IReadOnlyList{CssComponentValue}, out CssColor)"/> gives the keyword as written.</remarks>
    public bool TryGetColor(string name, out CssColor color)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name != "color")
        {
            if (!TryColor(Single(name), out color))
            {
                return false;
            }

            return !color.IsCurrentColor || TryGetColor("color", out color);
        }

        // 'color' inherits as a resolved color: what the element does not set itself is its parent's.
        if (!_own.TryGetValue(name, out IReadOnlyList<CssComponentValue>? own) || own is null)
        {
            if (_parent is not null)
            {
                return _parent.TryGetColor(name, out color);
            }

            own = Initial(name);
        }

        if (!TryColor(SingleOf(own), out color))
        {
            return false;
        }

        if (!color.IsCurrentColor)
        {
            return true;
        }

        // 'color: currentcolor' is 'color: inherit'; at the root, the initial value.
        if (_parent is not null)
        {
            return _parent.TryGetColor(name, out color);
        }

        return TryColor(SingleOf(Initial(name)), out color) && !color.IsCurrentColor;
    }

    /// <summary>Gets a property's value as a URL, such as the one in <c>list-style-image: url(dot.png)</c>.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="url">The URL.</param>
    /// <returns>True when the value is one <c>url()</c> or <c>src()</c>.</returns>
    public bool TryGetUrl(string name, out CssUrl url)
    {
        url = default;
        return Single(name) is { } value && CssUrl.TryParse(value, out url);
    }

    /// <summary>Gets the <c>font-family</c> list.</summary>
    /// <param name="families">The families, in order of preference.</param>
    /// <returns>True when the computed <c>font-family</c> is a valid list.</returns>
    public bool TryGetFontFamilies([NotNullWhen(true)] out CssFontFamilyList? families)
    {
        families = null;
        return Lookup("font-family") is { } values && CssFontFamilyList.TryParse(values, out families);
    }

    /// <summary>
    /// Gets the computed <c>font-weight</c>, from 1 to 1000: <c>normal</c> is 400 and <c>bold</c> 700, and <c>bolder</c> and
    /// <c>lighter</c> are resolved against the parent's weight with the CSS Fonts 4 table.
    /// </summary>
    /// <returns>The weight; 400 when nothing sets it.</returns>
    public double GetFontWeight()
    {
        double cached = Volatile.Read(ref _fontWeight);
        if (!double.IsNaN(cached))
        {
            return cached;
        }

        double weight = ComputeFontWeight();
        Volatile.Write(ref _fontWeight, weight);
        return weight;
    }

    /// <summary>
    /// Gets the computed <c>font-size</c> in CSS pixels: the absolute-size keywords from <c>medium</c> (16px) with the CSS
    /// Fonts 4 scaling factors, <c>larger</c> and <c>smaller</c> (the next keyword size when the parent's size is one, else
    /// 1.2 times), and <c>em</c>, <c>ex</c>, <c>ch</c> and percentages of the parent's size.
    /// </summary>
    /// <param name="context">
    /// The root's font size (<c>rem</c>) and the viewport. Its <see cref="CssLengthContext.FontSize"/>,
    /// <see cref="CssLengthContext.PercentBase"/>, <see cref="CssLengthContext.XHeight"/> and
    /// <see cref="CssLengthContext.ChWidth"/> are not used: on <c>font-size</c> they refer to the parent's font, which this
    /// computes. On the root element <c>rem</c> is the initial size, 16px.
    /// </param>
    /// <returns>The size in pixels; 16 when nothing sets it. A value that does not resolve takes the parent's size.</returns>
    public double GetFontSize(CssLengthContext context)
    {
        int version = Volatile.Read(ref _fontSizeVersion);
        if ((version & 1) == 0)
        {
            double size = _fontSize;
            double root = _fontSizeRoot;
            double width = _fontSizeWidth;
            double height = _fontSizeHeight;
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _fontSizeVersion) == version && !double.IsNaN(size)
                && root.Equals(context.RootFontSize) && width.Equals(context.ViewportWidth) && height.Equals(context.ViewportHeight))
            {
                return size;
            }
        }

        double computed = ComputeFontSize(context);
        version = Volatile.Read(ref _fontSizeVersion);
        if ((version & 1) == 0 && Interlocked.CompareExchange(ref _fontSizeVersion, version + 1, version) == version)
        {
            _fontSize = computed;
            _fontSizeRoot = context.RootFontSize;
            _fontSizeWidth = context.ViewportWidth;
            _fontSizeHeight = context.ViewportHeight;
            Volatile.Write(ref _fontSizeVersion, version + 2);
        }

        return computed;
    }

    private double ComputeFontWeight()
    {
        const string Name = "font-weight";
        if (!_own.TryGetValue(Name, out IReadOnlyList<CssComponentValue>? own) || own is null)
        {
            if (_parent is not null)
            {
                return _parent.GetFontWeight();
            }

            own = Initial(Name);
        }

        double parentWeight = _parent?.GetFontWeight() ?? NormalFontWeight;
        if (SingleOf(own) is { } value && CssFontWeight.TryParse(value, out CssFontWeight weight))
        {
            return weight.Resolve(parentWeight);
        }

        return parentWeight;
    }

    private double ComputeFontSize(CssLengthContext context)
    {
        const string Name = "font-size";
        if (!_own.TryGetValue(Name, out IReadOnlyList<CssComponentValue>? own) || own is null)
        {
            if (_parent is not null)
            {
                return _parent.GetFontSize(context);
            }

            own = Initial(Name);
        }

        double parentSize = _parent?.GetFontSize(context) ?? MediumFontSize;
        CssComponentValue? value = SingleOf(own);
        if (value is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
        {
            return KeywordFontSize(CssText.ToAsciiLower(ident.Value), parentSize);
        }

        // Relative units refer to the parent's font; at the root, rem is the initial font size.
        CssLengthContext parentContext = context with
        {
            FontSize = parentSize,
            RootFontSize = _parent is null ? MediumFontSize : context.RootFontSize,
            PercentBase = parentSize,
            XHeight = null,
            ChWidth = null,
        };
        return TryResolve(value, parentContext, out double pixels) && pixels >= 0 ? pixels : parentSize;
    }

    private static double KeywordFontSize(string keyword, double parentSize)
    {
        int index = Array.IndexOf(CssFont.AbsoluteSizeKeywords, keyword);
        if (index >= 0)
        {
            return s_absoluteSizes[index];
        }

        int parentIndex = Array.IndexOf(s_absoluteSizes, parentSize);
        return keyword switch
        {
            "larger" => parentIndex >= 0 && parentIndex + 1 < s_absoluteSizes.Length ? s_absoluteSizes[parentIndex + 1] : parentSize * 1.2,
            "smaller" => parentIndex > 0 ? s_absoluteSizes[parentIndex - 1] : parentSize / 1.2,
            _ => parentSize,
        };
    }

    private CssComponentValue? Single(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Lookup(name) is { } values ? SingleOf(values) : null;
    }

    // The one non-whitespace value, without the enumerator a foreach over the interface would allocate.
    private static CssComponentValue? SingleOf(IReadOnlyList<CssComponentValue>? values)
    {
        if (values is null)
        {
            return null;
        }

        CssComponentValue? single = null;
        for (int i = 0; i < values.Count; i++)
        {
            CssComponentValue value = values[i];
            if (value.IsWhitespace)
            {
                continue;
            }

            if (single is not null)
            {
                return null;
            }

            single = value;
        }

        return single;
    }

    private static bool TryNumber(CssComponentValue? value, out double number, out bool isInteger)
    {
        number = 0;
        isInteger = false;
        switch (value)
        {
            case CssTokenValue { Token: { Kind: CssTokenKind.Number } token }:
                number = token.Number;
                isInteger = token.IsInteger;
                return true;
            case CssFunction when CssCalc.TryParse(value, out CssCalc? calc) && calc.IsNumber:
                number = calc.Resolve(static _ => double.NaN);
                return true;
            default:
                return false;
        }
    }

    private static bool TryLength(CssComponentValue? value, out CssLength length)
    {
        length = default;
        if (value is not CssTokenValue || !CssLength.TryParse(value, out length))
        {
            return false;
        }

        if (length.Unit == CssUnit.None)
        {
            if (length.Value != 0)
            {
                return false;
            }

            length = CssLength.Zero;
        }

        return true;
    }

    private static bool TryResolve(CssComponentValue? value, CssLengthContext context, out double pixels)
    {
        pixels = 0;
        if (value is CssFunction)
        {
            return CssCalc.TryParse(value, out CssCalc? calc) && context.TryResolve(calc, out pixels);
        }

        return TryLength(value, out CssLength length) && context.TryResolve(length, out pixels);
    }

    private static bool TryColor(CssComponentValue? value, out CssColor color)
    {
        color = default;
        return value is not null && CssColor.TryParse(value, out color);
    }
}
