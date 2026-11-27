using System.Globalization;

namespace Egami.Css;

/// <summary>
/// A CSS color (CSS Color 4), or <c>currentcolor</c>: its components in the color space it was written in
/// (<see cref="Space"/>, <see cref="C0"/>, <see cref="C1"/>, <see cref="C2"/>), and the same color converted to sRGB as red,
/// green, blue and alpha from 0 to 1, gamma-encoded, each channel clipped to the sRGB gamut.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Level"/> says which CSS Color level's syntax the text used, so a consumer that follows an older specification
/// (such as SVG 1.1, which knows only CSS Color 3) can refuse the newer forms.
/// </para>
/// <para>
/// A color outside the sRGB gamut, such as <c>color(display-p3 1 0 0)</c>, keeps its components unclipped;
/// <see cref="R"/>, <see cref="G"/> and <see cref="B"/> clip each channel, and <see cref="ToSrgbGamutMapped"/> maps it into the
/// gamut as CSS Color 4 does instead. Two colors are equal when their space, components and alpha are, so
/// <c>color(display-p3 1 0 0)</c> is not <c>rgb(255 0 0)</c>.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="colors"/>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="widegamut"/>
/// </example>
public readonly struct CssColor : IEquatable<CssColor>
{
    private const int CurrentColorFlag = 0x80;
    private const int LegacyFlag = 0x40;
    private const int LevelMask = 0x0F;

    // What IsInSrgbGamut lets through: rounding in the conversions, far below what an 8- or 16-bit channel resolves.
    private const double GamutTolerance = 1e-6;

    private readonly byte _flags;
    private readonly byte _space;

    /// <summary>Creates an sRGB color.</summary>
    /// <param name="r">Red, 0 to 1.</param>
    /// <param name="g">Green, 0 to 1.</param>
    /// <param name="b">Blue, 0 to 1.</param>
    /// <param name="a">Alpha, 0 (transparent) to 1 (opaque).</param>
    /// <remarks>The components keep the values given; <see cref="R"/>, <see cref="G"/> and <see cref="B"/> are clipped to 0 to 1.</remarks>
    public CssColor(float r, float g, float b, float a = 1)
        : this(CssColorSpace.Srgb, r, g, b, a, 3, true, false)
    {
    }

    private CssColor(CssColorSpace space, double c0, double c1, double c2, double alpha, int level, bool legacy, bool currentColor)
    {
        // +0.0 turns -0 into 0, so equal colors hash alike; NaN (from arithmetic on infinities) becomes 0, as 'none' does.
        C0 = double.IsNaN(c0) ? 0 : c0 + 0.0;
        C1 = double.IsNaN(c1) ? 0 : c1 + 0.0;
        C2 = double.IsNaN(c2) ? 0 : c2 + 0.0;
        (double r, double g, double b) = CssColorConversion.ToSrgb(space, C0, C1, C2);
        R = Clip(r);
        G = Clip(g);
        B = Clip(b);
        A = Clip((float)alpha);
        _space = (byte)space;
        _flags = (byte)(level | (legacy ? LegacyFlag : 0) | (currentColor ? CurrentColorFlag : 0));
    }

    /// <summary>Gets red, 0 to 1 (gamma-encoded sRGB, clipped).</summary>
    public float R { get; }

    /// <summary>Gets green, 0 to 1 (gamma-encoded sRGB, clipped).</summary>
    public float G { get; }

    /// <summary>Gets blue, 0 to 1 (gamma-encoded sRGB, clipped).</summary>
    public float B { get; }

    /// <summary>Gets alpha, 0 (transparent) to 1 (opaque).</summary>
    public float A { get; }

    /// <summary>Gets the color space the color was written in: <see cref="CssColorSpace.Srgb"/> for hex, named colors, <c>rgb()</c>, <c>hsl()</c> and <c>hwb()</c>.</summary>
    public CssColorSpace Space => (CssColorSpace)_space;

    /// <summary>Gets the first component in <see cref="Space"/>, unclipped: red, X or lightness.</summary>
    public double C0 { get; }

    /// <summary>Gets the second component in <see cref="Space"/>, unclipped: green, Y, a or chroma.</summary>
    public double C1 { get; }

    /// <summary>Gets the third component in <see cref="Space"/>, unclipped: blue, Z, b or hue in degrees.</summary>
    public double C2 { get; }

    /// <summary>Gets whether the color is <c>currentcolor</c>: the consumer substitutes the element's <c>color</c>.</summary>
    public bool IsCurrentColor => (_flags & CurrentColorFlag) != 0;

    /// <summary>
    /// Gets the CSS Color level whose syntax the color was written in: 3 for keywords, <c>#rgb</c> and <c>#rrggbb</c>,
    /// comma-separated <c>rgb()</c>, <c>rgba()</c>, <c>hsl()</c> and <c>hsla()</c> and the CSS2 system colors; 4 for
    /// <c>#rgba</c>, <c>#rrggbbaa</c>, <c>rebeccapurple</c>, space-separated or percentage-alpha <c>rgb()</c> and <c>hsl()</c>,
    /// <c>hwb()</c>, <c>lab()</c>, <c>lch()</c>, <c>oklab()</c>, <c>oklch()</c>, <c>color()</c> and the CSS Color 4 system colors.
    /// </summary>
    public int Level => (_flags & LevelMask) == 0 ? 3 : _flags & LevelMask;

    /// <summary>
    /// Gets whether the color is inside the sRGB gamut (within rounding), so <see cref="R"/>, <see cref="G"/> and
    /// <see cref="B"/> are the color itself rather than a clipped one. True for <c>currentcolor</c>.
    /// </summary>
    public bool IsInSrgbGamut
    {
        get
        {
            (double r, double g, double b) = CssColorConversion.ToSrgb(Space, C0, C1, C2);
            return InGamut(r, g, b);
        }
    }

    /// <summary>Gets <c>currentcolor</c>.</summary>
    public static CssColor CurrentColor => new(CssColorSpace.Srgb, 0, 0, 0, 1, 3, true, true);

    /// <summary>Gets <c>transparent</c>: black with zero alpha.</summary>
    public static CssColor Transparent => new(CssColorSpace.Srgb, 0, 0, 0, 0, 3, true, false);

    /// <summary>Creates a color from its components in a color space, as <c>color()</c>, <c>lab()</c> or <c>oklch()</c> give them.</summary>
    /// <param name="space">The color space.</param>
    /// <param name="c0">The first component: red, X or lightness (0 to 100 for Lab and LCH, 0 to 1 for Oklab and OkLCh).</param>
    /// <param name="c1">The second component: green, Y, a or chroma.</param>
    /// <param name="c2">The third component: blue, Z, b or hue in degrees.</param>
    /// <param name="alpha">Alpha, 0 (transparent) to 1 (opaque).</param>
    /// <returns>The color, at CSS Color level 4. The components are kept as given, out of gamut too; NaN becomes 0.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="space"/> is not a defined color space.</exception>
    public static CssColor FromColorSpace(CssColorSpace space, double c0, double c1, double c2, double alpha = 1)
    {
        if ((uint)space > (uint)CssColorSpace.Oklch)
        {
            throw new ArgumentOutOfRangeException(nameof(space));
        }

        return new CssColor(space, c0, c1, c2, double.IsNaN(alpha) ? 0 : alpha, 4, false, false);
    }

    /// <summary>Converts the color's components to another color space, unclipped.</summary>
    /// <param name="space">The color space.</param>
    /// <returns>The three components in that space (hues in degrees, 0 to 360).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="space"/> is not a defined color space.</exception>
    public (double C0, double C1, double C2) ToColorSpace(CssColorSpace space)
    {
        if ((uint)space > (uint)CssColorSpace.Oklch)
        {
            throw new ArgumentOutOfRangeException(nameof(space));
        }

        return CssColorConversion.Convert(Space, C0, C1, C2, space);
    }

    /// <summary>
    /// Maps the color into the sRGB gamut as CSS Color 4 does (section 13.2, "CSS gamut mapping to an RGB destination"): it
    /// lowers the chroma in OkLCh, holding lightness and hue, until the color clipped to the gamut is within a just-noticeable
    /// difference (deltaEOK 0.02) of it. Colors in the gamut, and <c>currentcolor</c>, come back as they are.
    /// </summary>
    /// <returns>An sRGB color (<see cref="CssColorSpace.Srgb"/>) whose components are inside the gamut, with the same alpha.</returns>
    public CssColor ToSrgbGamutMapped()
    {
        if (IsCurrentColor)
        {
            return this;
        }

        float alpha = A;
        int level = Level;
        (double r, double g, double b) = CssColorConversion.ToSrgb(Space, C0, C1, C2);
        if (InGamut(r, g, b))
        {
            return Space == CssColorSpace.Srgb ? this : MakeSrgb(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
        }

        (double lightness, double chroma, double hue) = CssColorConversion.Convert(Space, C0, C1, C2, CssColorSpace.Oklch);
        if (lightness >= 1)
        {
            return MakeSrgb(1, 1, 1);
        }

        if (lightness <= 0)
        {
            return MakeSrgb(0, 0, 0);
        }

        const double JustNoticeable = 0.02;
        const double Epsilon = 0.0001;
        (double R, double G, double B) clipped = ClipAll(r, g, b);
        if (DeltaEOk(clipped, lightness, chroma, hue) < JustNoticeable)
        {
            return MakeSrgb(clipped.R, clipped.G, clipped.B);
        }

        // Binary search on chroma: 'low' stays in gamut (or, once the clipped color came within the difference, close enough);
        // 'high' does not.
        double low = 0;
        double high = chroma;
        bool lowInGamut = true;
        while (high - low > Epsilon)
        {
            double middle = (low + high) / 2;
            (double cr, double cg, double cb) = CssColorConversion.ToSrgb(CssColorSpace.Oklch, lightness, middle, hue);
            if (lowInGamut && InGamut(cr, cg, cb))
            {
                low = middle;
                continue;
            }

            clipped = ClipAll(cr, cg, cb);
            double delta = DeltaEOk(clipped, lightness, middle, hue);
            if (delta < JustNoticeable)
            {
                if (JustNoticeable - delta < Epsilon)
                {
                    break;
                }

                lowInGamut = false;
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return MakeSrgb(clipped.R, clipped.G, clipped.B);

        CssColor MakeSrgb(double red, double green, double blue) => new(CssColorSpace.Srgb, red, green, blue, alpha, level, false, false);

        static (double R, double G, double B) ClipAll(double red, double green, double blue) => (Math.Clamp(red, 0, 1), Math.Clamp(green, 0, 1), Math.Clamp(blue, 0, 1));

        // The Euclidean distance in Oklab between a clipped sRGB color and an OkLCh one.
        static double DeltaEOk((double R, double G, double B) srgb, double lightness, double chroma, double hue)
        {
            (double l1, double a1, double b1) = CssColorConversion.SrgbToOklab(srgb.R, srgb.G, srgb.B);
            (double a2, double b2) = CssColorConversion.FromPolar(chroma, hue);
            double dl = l1 - lightness;
            double da = a1 - a2;
            double db = b1 - b2;
            return Math.Sqrt((dl * dl) + (da * da) + (db * db));
        }
    }

    /// <summary>Parses a color, such as <c>red</c>, <c>#ff000080</c>, <c>rgb(255 0 0 / 50%)</c> or <c>oklch(62.8% 0.26 29)</c>.</summary>
    /// <param name="text">The CSS text (ASCII case-insensitive).</param>
    /// <param name="color">The color.</param>
    /// <returns>True when the text is one valid color.</returns>
    public static bool TryParse(string text, out CssColor color)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), out color);
    }

    /// <summary>Parses component values as one color (whitespace around it is ignored).</summary>
    /// <param name="values">The component values.</param>
    /// <param name="color">The color.</param>
    /// <returns>True when the values are one valid color.</returns>
    public static bool TryParse(IReadOnlyList<CssComponentValue> values, out CssColor color)
    {
        ArgumentNullException.ThrowIfNull(values);
        color = default;
        CssComponentValue? single = CssLength.Single(values);
        return single is not null && TryParse(single, out color);
    }

    /// <summary>Parses one component value as a color.</summary>
    /// <param name="value">The component value: an identifier, a hash or a color function.</param>
    /// <param name="color">The color.</param>
    /// <returns>True when the value is a valid color.</returns>
    public static bool TryParse(CssComponentValue value, out CssColor color)
    {
        ArgumentNullException.ThrowIfNull(value);
        color = default;
        switch (value)
        {
            case CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident }:
                return TryParseKeyword(ident.Value, out color);
            case CssTokenValue { Token: { Kind: CssTokenKind.Hash } hash }:
                return TryParseHex(hash.Value, out color);
            case CssFunction function:
                return TryParseFunction(function, out color);
            default:
                return false;
        }
    }

    /// <summary>Converts to 8-bit sRGB components, rounded.</summary>
    /// <returns>Red, green, blue and alpha, 0 to 255.</returns>
    public (byte R, byte G, byte B, byte A) ToRgba8() => (To8(R), To8(G), To8(B), To8(A));

    /// <summary>Serializes the color as CSS Color 4 does for sRGB: <c>rgb(r, g, b)</c> or <c>rgba(r, g, b, a)</c>, or <c>currentcolor</c>.</summary>
    /// <returns>The text.</returns>
    /// <remarks>A color outside the sRGB gamut is written clipped; <see cref="ToString(bool)"/> can keep its color space.</remarks>
    public override string ToString()
    {
        if (IsCurrentColor)
        {
            return "currentcolor";
        }

        (byte r, byte g, byte b, byte a) = ToRgba8();
        if (a == 255)
        {
            return string.Create(CultureInfo.InvariantCulture, $"rgb({r}, {g}, {b})");
        }

        double alpha = Math.Round(a / 255.0, 2);
        if (To8((float)alpha) != a)
        {
            alpha = Math.Round(a / 255.0, 3);
        }

        return string.Create(CultureInfo.InvariantCulture, $"rgba({r}, {g}, {b}, {alpha})");
    }

    /// <summary>
    /// Serializes the color, in its own color space when <paramref name="keepSpace"/> is set, as CSS Color 4 does:
    /// <c>color(display-p3 1 0 0)</c>, <c>lab(52.3 40.1 59.9)</c>, <c>oklch(0.628 0.258 29.2 / 0.5)</c>. Hex, named colors,
    /// <c>rgb()</c>, <c>hsl()</c> and <c>hwb()</c> keep the <c>rgb()</c> form of <see cref="ToString()"/>.
    /// </summary>
    /// <param name="keepSpace">Whether to keep the color space; false gives <see cref="ToString()"/>.</param>
    /// <returns>The text. Components are written with up to six decimals.</returns>
    public string ToString(bool keepSpace)
    {
        if (!keepSpace || IsCurrentColor || (Space == CssColorSpace.Srgb && (_flags & LegacyFlag) != 0))
        {
            return ToString();
        }

        string alpha = A >= 1 ? "" : " / " + Number(A);
        string components = Number(C0) + " " + Number(C1) + " " + Number(C2);
        return Space switch
        {
            CssColorSpace.Lab => "lab(" + components + alpha + ")",
            CssColorSpace.Lch => "lch(" + components + alpha + ")",
            CssColorSpace.Oklab => "oklab(" + components + alpha + ")",
            CssColorSpace.Oklch => "oklch(" + components + alpha + ")",
            _ => "color(" + SpaceName(Space) + " " + components + alpha + ")",
        };

        static string Number(double value)
        {
            double rounded = Math.Round(value, 6) + 0.0;
            return rounded.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }

    /// <inheritdoc/>
    public bool Equals(CssColor other)
    {
        return _space == other._space && C0.Equals(other.C0) && C1.Equals(other.C1) && C2.Equals(other.C2) && A == other.A && IsCurrentColor == other.IsCurrentColor;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CssColor other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_space, C0, C1, C2, A, IsCurrentColor);

    /// <summary>Compares two colors by their color space, components and alpha; the syntax level is not compared.</summary>
    /// <param name="left">The first color.</param>
    /// <param name="right">The second color.</param>
    /// <returns>True when they are equal.</returns>
    public static bool operator ==(CssColor left, CssColor right) => left.Equals(right);

    /// <summary>Compares two colors by their color space, components and alpha; the syntax level is not compared.</summary>
    /// <param name="left">The first color.</param>
    /// <param name="right">The second color.</param>
    /// <returns>True when they differ.</returns>
    public static bool operator !=(CssColor left, CssColor right) => !left.Equals(right);

    private static string SpaceName(CssColorSpace space) => space switch
    {
        CssColorSpace.Srgb => "srgb",
        CssColorSpace.SrgbLinear => "srgb-linear",
        CssColorSpace.DisplayP3 => "display-p3",
        CssColorSpace.A98Rgb => "a98-rgb",
        CssColorSpace.ProPhotoRgb => "prophoto-rgb",
        CssColorSpace.Rec2020 => "rec2020",
        CssColorSpace.XyzD50 => "xyz-d50",
        _ => "xyz-d65",
    };

    private static bool InGamut(double r, double g, double b)
    {
        const double Low = -GamutTolerance;
        const double High = 1 + GamutTolerance;
        return r is >= Low and <= High && g is >= Low and <= High && b is >= Low and <= High;
    }

    private static byte To8(float v) => (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 255, MidpointRounding.AwayFromZero);

    private static float Clip(double v) => double.IsNaN(v) ? 0 : (float)Math.Clamp(v, 0, 1);

    private static float Clip(float v) => float.IsNaN(v) ? 0 : Math.Clamp(v, 0f, 1f);

    // A legacy sRGB color: hex, keyword, rgb(), hsl() or hwb().
    private static CssColor Make(double r, double g, double b, double a, int level) => new(CssColorSpace.Srgb, r, g, b, a, level, true, false);

    private static CssColor Make(CssColorSpace space, double c0, double c1, double c2, double a) => new(space, c0, c1, c2, a, 4, false, false);

    private static bool TryParseKeyword(string name, out CssColor color)
    {
        string lower = CssText.ToAsciiLower(name);
        if (lower == "currentcolor")
        {
            color = CurrentColor;
            return true;
        }

        if (lower == "transparent")
        {
            color = Transparent;
            return true;
        }

        if (CssNamedColors.TryGet(lower, out uint rgb, out int level))
        {
            color = Make(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0, 1, level);
            return true;
        }

        color = default;
        return false;
    }

    private static bool TryParseHex(string hex, out CssColor color)
    {
        color = default;
        if (hex.Length is not (3 or 4 or 6 or 8))
        {
            return false;
        }

        foreach (char c in hex)
        {
            if (!CssText.IsHexDigit(c))
            {
                return false;
            }
        }

        int Digit(int i) => CssText.HexValue(hex[i]);
        if (hex.Length <= 4)
        {
            double a = hex.Length == 4 ? Digit(3) * 17 / 255.0 : 1;
            color = Make(Digit(0) * 17 / 255.0, Digit(1) * 17 / 255.0, Digit(2) * 17 / 255.0, a, hex.Length == 4 ? 4 : 3);
        }
        else
        {
            double a = hex.Length == 8 ? ((Digit(6) * 16) + Digit(7)) / 255.0 : 1;
            color = Make(((Digit(0) * 16) + Digit(1)) / 255.0, ((Digit(2) * 16) + Digit(3)) / 255.0, ((Digit(4) * 16) + Digit(5)) / 255.0, a, hex.Length == 8 ? 4 : 3);
        }

        return true;
    }

    // One channel of a color function: a number, percentage, angle or 'none'.
    private readonly record struct Channel(double Value, bool Percent, bool None, bool Angle);

    private static bool TryParseFunction(CssFunction function, out CssColor color)
    {
        color = default;
        string name = CssText.ToAsciiLower(function.Name);
        if (name is not ("rgb" or "rgba" or "hsl" or "hsla" or "hwb" or "lab" or "lch" or "oklab" or "oklch" or "color"))
        {
            return false;
        }

        // Split the arguments: comma-separated (legacy) or space-separated with an optional "/ alpha" (modern).
        List<CssComponentValue> items = [];
        foreach (CssComponentValue argument in function.Arguments)
        {
            if (!argument.IsWhitespace)
            {
                items.Add(argument);
            }
        }

        string? space = null;
        if (name == "color")
        {
            if (items.Count == 0 || items[0] is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } spaceToken })
            {
                return false;
            }

            space = CssText.ToAsciiLower(spaceToken.Value);
            items.RemoveAt(0);
        }

        bool legacy = items.Exists(i => i.IsToken(CssTokenKind.Comma));
        List<Channel> channels = [];
        Channel? alpha = null;
        if (legacy)
        {
            if (name is not ("rgb" or "rgba" or "hsl" or "hsla"))
            {
                return false;
            }

            for (int i = 0; i < items.Count; i++)
            {
                if ((i % 2 == 1) != items[i].IsToken(CssTokenKind.Comma))
                {
                    return false;
                }

                if (i % 2 == 0)
                {
                    if (!TryChannel(items[i], out Channel channel) || channel.None)
                    {
                        return false;
                    }

                    channels.Add(channel);
                }
            }

            if (items.Count % 2 == 0 || channels.Count is not (3 or 4))
            {
                return false;
            }
        }
        else
        {
            int slash = items.FindIndex(i => i.IsDelim('/'));
            int end = slash < 0 ? items.Count : slash;
            for (int i = 0; i < end; i++)
            {
                if (!TryChannel(items[i], out Channel channel))
                {
                    return false;
                }

                channels.Add(channel);
            }

            if (channels.Count != 3)
            {
                return false;
            }

            if (slash >= 0)
            {
                if (items.Count != slash + 2 || !TryChannel(items[slash + 1], out Channel a) || a.Angle)
                {
                    return false;
                }

                alpha = a;
            }
        }

        if (legacy && channels.Count == 4)
        {
            alpha = channels[3];
            channels.RemoveAt(3);
            if (alpha.Value.Angle)
            {
                return false;
            }
        }

        double alphaValue = alpha is { } av ? (av.None ? 0 : av.Percent ? av.Value / 100 : av.Value) : 1;
        alphaValue = Math.Clamp(alphaValue, 0, 1);
        int level = legacy && (name is "rgb" or "hsl" ? alpha is null : alpha is not null) && alpha is not { Percent: true } ? 3 : 4;

        Channel c0 = channels[0];
        Channel c1 = channels[1];
        Channel c2 = channels[2];
        switch (name)
        {
            case "rgb" or "rgba":
                {
                    if (c0.Angle || c1.Angle || c2.Angle)
                    {
                        return false;
                    }

                    if (legacy && (c0.Percent != c1.Percent || c1.Percent != c2.Percent))
                    {
                        return false;
                    }

                    if (!legacy && name == "rgba")
                    {
                        level = 4;
                    }

                    color = Make(Rgb(c0), Rgb(c1), Rgb(c2), alphaValue, legacy ? level : 4);
                    return true;

                    static double Rgb(Channel c) => c.None ? 0 : c.Percent ? Math.Clamp(c.Value / 100, 0, 1) : Math.Clamp(c.Value / 255, 0, 1);
                }

            case "hsl" or "hsla":
                {
                    if (c1.Angle || c2.Angle || (legacy && (c0.Percent || !c1.Percent || !c2.Percent)) || c0.Percent)
                    {
                        return false;
                    }

                    double s = c1.None ? 0 : Math.Clamp(c1.Value, 0, 100);
                    double l = c2.None ? 0 : Math.Clamp(c2.Value, 0, 100);
                    (double r, double g, double b) = HslToRgb(Hue(c0), s / 100, l / 100);
                    color = Make(r, g, b, alphaValue, legacy ? level : 4);
                    return true;
                }

            case "hwb":
                {
                    if (c0.Percent || c1.Angle || c2.Angle)
                    {
                        return false;
                    }

                    double white = Math.Clamp(c1.None ? 0 : c1.Value / 100, 0, 1);
                    double black = Math.Clamp(c2.None ? 0 : c2.Value / 100, 0, 1);
                    double r;
                    double g;
                    double b;
                    if (white + black >= 1)
                    {
                        r = g = b = white / (white + black);
                    }
                    else
                    {
                        (r, g, b) = HslToRgb(Hue(c0), 1, 0.5);
                        r = (r * (1 - white - black)) + white;
                        g = (g * (1 - white - black)) + white;
                        b = (b * (1 - white - black)) + white;
                    }

                    color = Make(r, g, b, alphaValue, 4);
                    return true;
                }

            case "lab" or "oklab":
                {
                    if (c0.Angle || c1.Angle || c2.Angle)
                    {
                        return false;
                    }

                    bool ok = name == "oklab";
                    double l = c0.None ? 0 : c0.Percent ? c0.Value / 100 * (ok ? 1 : 100) : c0.Value;
                    double scale = ok ? 0.4 : 125;
                    double a = c1.None ? 0 : c1.Percent ? c1.Value / 100 * scale : c1.Value;
                    double b = c2.None ? 0 : c2.Percent ? c2.Value / 100 * scale : c2.Value;
                    l = Math.Clamp(l, 0, ok ? 1 : 100);
                    color = Make(ok ? CssColorSpace.Oklab : CssColorSpace.Lab, l, a, b, alphaValue);
                    return true;
                }

            case "lch" or "oklch":
                {
                    if (c0.Angle || c1.Angle || c2.Percent)
                    {
                        return false;
                    }

                    bool ok = name == "oklch";
                    double l = c0.None ? 0 : c0.Percent ? c0.Value / 100 * (ok ? 1 : 100) : c0.Value;
                    double chroma = c1.None ? 0 : c1.Percent ? c1.Value / 100 * (ok ? 0.4 : 150) : c1.Value;
                    l = Math.Clamp(l, 0, ok ? 1 : 100);
                    chroma = Math.Max(0, chroma);
                    color = Make(ok ? CssColorSpace.Oklch : CssColorSpace.Lch, l, chroma, Hue(c2), alphaValue);
                    return true;
                }

            default:
                {
                    if (c0.Angle || c1.Angle || c2.Angle)
                    {
                        return false;
                    }

                    double x = c0.None ? 0 : c0.Percent ? c0.Value / 100 : c0.Value;
                    double y = c1.None ? 0 : c1.Percent ? c1.Value / 100 : c1.Value;
                    double z = c2.None ? 0 : c2.Percent ? c2.Value / 100 : c2.Value;
                    CssColorSpace? colorSpace = space switch
                    {
                        "srgb" => CssColorSpace.Srgb,
                        "srgb-linear" => CssColorSpace.SrgbLinear,
                        "display-p3" => CssColorSpace.DisplayP3,
                        "a98-rgb" => CssColorSpace.A98Rgb,
                        "prophoto-rgb" => CssColorSpace.ProPhotoRgb,
                        "rec2020" => CssColorSpace.Rec2020,
                        "xyz" or "xyz-d65" => CssColorSpace.XyzD65,
                        "xyz-d50" => CssColorSpace.XyzD50,
                        _ => null,
                    };
                    if (colorSpace is null)
                    {
                        return false;
                    }

                    color = Make(colorSpace.Value, x, y, z, alphaValue);
                    return true;
                }
        }
    }

    private static bool TryChannel(CssComponentValue value, out Channel channel)
    {
        channel = default;
        if (value is CssFunction function && CssCalc.TryParse(function, out CssCalc? calc) && calc.IsNumber)
        {
            channel = new Channel(calc.Resolve(static _ => double.NaN), false, false, false);
            return true;
        }

        if (value is not CssTokenValue { Token: var token })
        {
            return false;
        }

        switch (token.Kind)
        {
            case CssTokenKind.Number:
                channel = new Channel(token.Number, false, false, false);
                return true;
            case CssTokenKind.Percentage:
                channel = new Channel(token.Number, true, false, false);
                return true;
            case CssTokenKind.Dimension when CssAngle.DegreesPerUnit(token.Unit) is { } perUnit:
                channel = new Channel(token.Number * perUnit, false, false, true);
                return true;
            case CssTokenKind.Ident when token.IsIdent("none"):
                channel = new Channel(0, false, true, false);
                return true;
            default:
                return false;
        }
    }

    private static double Hue(Channel c)
    {
        if (c.None)
        {
            return 0;
        }

        double hue = c.Value % 360;
        return hue < 0 ? hue + 360 : hue;
    }

    private static (double R, double G, double B) HslToRgb(double hue, double saturation, double lightness)
    {
        double F(double n)
        {
            double k = (n + (hue / 30)) % 12;
            double a = saturation * Math.Min(lightness, 1 - lightness);
            return lightness - (a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)));
        }

        return (F(0), F(8), F(4));
    }
}

/// <summary>The 148 named colors of CSS Color 4 and the system colors, with the CSS Color level that introduced each.</summary>
internal static class CssNamedColors
{
    private static readonly Dictionary<string, (uint Rgb, int Level)> s_colors = Build();

    public static bool TryGet(string lowercaseName, out uint rgb, out int level)
    {
        if (s_colors.TryGetValue(lowercaseName, out (uint Rgb, int Level) entry))
        {
            rgb = entry.Rgb;
            level = entry.Level;
            return true;
        }

        rgb = 0;
        level = 0;
        return false;
    }

    public static IEnumerable<string> Names => s_colors.Keys;

    private static Dictionary<string, (uint, int)> Build()
    {
        const string Table =
            "aliceblue f0f8ff antiquewhite faebd7 aqua 00ffff aquamarine 7fffd4 azure f0ffff beige f5f5dc bisque ffe4c4 black 000000 " +
            "blanchedalmond ffebcd blue 0000ff blueviolet 8a2be2 brown a52a2a burlywood deb887 cadetblue 5f9ea0 chartreuse 7fff00 " +
            "chocolate d2691e coral ff7f50 cornflowerblue 6495ed cornsilk fff8dc crimson dc143c cyan 00ffff darkblue 00008b " +
            "darkcyan 008b8b darkgoldenrod b8860b darkgray a9a9a9 darkgreen 006400 darkgrey a9a9a9 darkkhaki bdb76b darkmagenta 8b008b " +
            "darkolivegreen 556b2f darkorange ff8c00 darkorchid 9932cc darkred 8b0000 darksalmon e9967a darkseagreen 8fbc8f " +
            "darkslateblue 483d8b darkslategray 2f4f4f darkslategrey 2f4f4f darkturquoise 00ced1 darkviolet 9400d3 deeppink ff1493 " +
            "deepskyblue 00bfff dimgray 696969 dimgrey 696969 dodgerblue 1e90ff firebrick b22222 floralwhite fffaf0 forestgreen 228b22 " +
            "fuchsia ff00ff gainsboro dcdcdc ghostwhite f8f8ff gold ffd700 goldenrod daa520 gray 808080 green 008000 greenyellow adff2f " +
            "grey 808080 honeydew f0fff0 hotpink ff69b4 indianred cd5c5c indigo 4b0082 ivory fffff0 khaki f0e68c lavender e6e6fa " +
            "lavenderblush fff0f5 lawngreen 7cfc00 lemonchiffon fffacd lightblue add8e6 lightcoral f08080 lightcyan e0ffff " +
            "lightgoldenrodyellow fafad2 lightgray d3d3d3 lightgreen 90ee90 lightgrey d3d3d3 lightpink ffb6c1 lightsalmon ffa07a " +
            "lightseagreen 20b2aa lightskyblue 87cefa lightslategray 778899 lightslategrey 778899 lightsteelblue b0c4de " +
            "lightyellow ffffe0 lime 00ff00 limegreen 32cd32 linen faf0e6 magenta ff00ff maroon 800000 mediumaquamarine 66cdaa " +
            "mediumblue 0000cd mediumorchid ba55d3 mediumpurple 9370db mediumseagreen 3cb371 mediumslateblue 7b68ee " +
            "mediumspringgreen 00fa9a mediumturquoise 48d1cc mediumvioletred c71585 midnightblue 191970 mintcream f5fffa " +
            "mistyrose ffe4e1 moccasin ffe4b5 navajowhite ffdead navy 000080 oldlace fdf5e6 olive 808000 olivedrab 6b8e23 " +
            "orange ffa500 orangered ff4500 orchid da70d6 palegoldenrod eee8aa palegreen 98fb98 paleturquoise afeeee " +
            "palevioletred db7093 papayawhip ffefd5 peachpuff ffdab9 peru cd853f pink ffc0cb plum dda0dd powderblue b0e0e6 " +
            "purple 800080 red ff0000 rosybrown bc8f8f royalblue 4169e1 saddlebrown 8b4513 salmon fa8072 sandybrown f4a460 " +
            "seagreen 2e8b57 seashell fff5ee sienna a0522d silver c0c0c0 skyblue 87ceeb slateblue 6a5acd slategray 708090 " +
            "slategrey 708090 snow fffafa springgreen 00ff7f steelblue 4682b4 tan d2b48c teal 008080 thistle d8bfd8 tomato ff6347 " +
            "turquoise 40e0d0 violet ee82ee wheat f5deb3 white ffffff whitesmoke f5f5f5 yellow ffff00 yellowgreen 9acd32";

        // CSS Color 4 system colors, light scheme; the CSS2 ones map to them as CSS Color 4 section 6.2 says.
        const string System4 =
            "canvas ffffff canvastext 000000 linktext 0000ee visitedtext 551a8b activetext ff0000 buttonborder 767676 field ffffff " +
            "fieldtext 000000 selecteditem 3399ff selecteditemtext ffffff mark ffff00 marktext 000000 accentcolor 0078d7 " +
            "accentcolortext ffffff";
        const string System3 =
            "buttonface efefef buttontext 000000 graytext 808080 highlight 3399ff highlighttext ffffff activeborder 767676 " +
            "activecaption ffffff appworkspace ffffff background ffffff buttonhighlight efefef buttonshadow efefef captiontext 000000 " +
            "inactiveborder 767676 inactivecaption ffffff inactivecaptiontext 808080 infobackground ffffff infotext 000000 menu ffffff " +
            "menutext 000000 scrollbar ffffff threeddarkshadow 767676 threedface efefef threedhighlight 767676 threedlightshadow 767676 " +
            "threedshadow 767676 window ffffff windowframe 767676 windowtext 000000";

        Dictionary<string, (uint, int)> colors = new(StringComparer.Ordinal);
        Add(Table, 3);
        Add(System4, 4);
        Add(System3, 3);
        colors["rebeccapurple"] = (0x663399, 4);
        return colors;

        void Add(string table, int level)
        {
            string[] parts = table.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                colors[parts[i]] = (uint.Parse(parts[i + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture), level);
            }
        }
    }
}
