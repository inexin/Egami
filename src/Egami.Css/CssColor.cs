using System.Globalization;

namespace Egami.Css;

/// <summary>
/// A CSS color (CSS Color 4) converted to sRGB: red, green, blue and alpha from 0 to 1, gamma-encoded, clipped to the sRGB gamut;
/// or <c>currentcolor</c>.
/// </summary>
/// <remarks>
/// <see cref="Level"/> says which CSS Color level's syntax the text used, so a consumer that follows an older specification
/// (such as SVG 1.1, which knows only CSS Color 3) can refuse the newer forms.
/// </remarks>
public readonly struct CssColor : IEquatable<CssColor>
{
    private readonly byte _flags;

    /// <summary>Creates an sRGB color.</summary>
    /// <param name="r">Red, 0 to 1.</param>
    /// <param name="g">Green, 0 to 1.</param>
    /// <param name="b">Blue, 0 to 1.</param>
    /// <param name="a">Alpha, 0 (transparent) to 1 (opaque).</param>
    public CssColor(float r, float g, float b, float a = 1)
    {
        R = Clip(r);
        G = Clip(g);
        B = Clip(b);
        A = Clip(a);
        _flags = 3;
    }

    private CssColor(float r, float g, float b, float a, int level, bool currentColor)
    {
        R = Clip(r);
        G = Clip(g);
        B = Clip(b);
        A = Clip(a);
        _flags = (byte)(level | (currentColor ? 0x80 : 0));
    }

    /// <summary>Gets red, 0 to 1 (gamma-encoded sRGB).</summary>
    public float R { get; }

    /// <summary>Gets green, 0 to 1 (gamma-encoded sRGB).</summary>
    public float G { get; }

    /// <summary>Gets blue, 0 to 1 (gamma-encoded sRGB).</summary>
    public float B { get; }

    /// <summary>Gets alpha, 0 (transparent) to 1 (opaque).</summary>
    public float A { get; }

    /// <summary>Gets whether the color is <c>currentcolor</c>: the consumer substitutes the element's <c>color</c>.</summary>
    public bool IsCurrentColor => (_flags & 0x80) != 0;

    /// <summary>
    /// Gets the CSS Color level whose syntax the color was written in: 3 for keywords, <c>#rgb</c> and <c>#rrggbb</c>,
    /// comma-separated <c>rgb()</c>, <c>rgba()</c>, <c>hsl()</c> and <c>hsla()</c> and the CSS2 system colors; 4 for
    /// <c>#rgba</c>, <c>#rrggbbaa</c>, <c>rebeccapurple</c>, space-separated or percentage-alpha <c>rgb()</c> and <c>hsl()</c>,
    /// <c>hwb()</c>, <c>lab()</c>, <c>lch()</c>, <c>oklab()</c>, <c>oklch()</c>, <c>color()</c> and the CSS Color 4 system colors.
    /// </summary>
    public int Level => (_flags & 0x7F) == 0 ? 3 : _flags & 0x7F;

    /// <summary>Gets <c>currentcolor</c>.</summary>
    public static CssColor CurrentColor => new(0, 0, 0, 1, 3, true);

    /// <summary>Gets <c>transparent</c>: black with zero alpha.</summary>
    public static CssColor Transparent => new(0, 0, 0, 0, 3, false);

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

    /// <inheritdoc/>
    public bool Equals(CssColor other) => R == other.R && G == other.G && B == other.B && A == other.A && IsCurrentColor == other.IsCurrentColor;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CssColor other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(R, G, B, A, IsCurrentColor);

    /// <summary>Compares two colors by their components; the syntax level is not compared.</summary>
    /// <param name="left">The first color.</param>
    /// <param name="right">The second color.</param>
    /// <returns>True when they are equal.</returns>
    public static bool operator ==(CssColor left, CssColor right) => left.Equals(right);

    /// <summary>Compares two colors by their components; the syntax level is not compared.</summary>
    /// <param name="left">The first color.</param>
    /// <param name="right">The second color.</param>
    /// <returns>True when they differ.</returns>
    public static bool operator !=(CssColor left, CssColor right) => !left.Equals(right);

    private static byte To8(float v) => (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 255, MidpointRounding.AwayFromZero);

    private static float Clip(float v) => float.IsNaN(v) ? 0 : Math.Clamp(v, 0f, 1f);

    private static CssColor Make(double r, double g, double b, double a, int level) => new((float)r, (float)g, (float)b, (float)a, level, false);

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
                    color = ok ? FromOklab(l, a, b, alphaValue) : FromLab(l, a, b, alphaValue);
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
                    double hue = Hue(c2) * Math.PI / 180;
                    double a = chroma * Math.Cos(hue);
                    double b = chroma * Math.Sin(hue);
                    color = ok ? FromOklab(l, a, b, alphaValue) : FromLab(l, a, b, alphaValue);
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
                    return TryFromColorSpace(space!, x, y, z, alphaValue, out color);
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

    private static CssColor FromLab(double l, double a, double b, double alpha)
    {
        const double Kappa = 24389.0 / 27;
        const double Epsilon = 216.0 / 24389;
        double f1 = (l + 16) / 116;
        double f0 = (a / 500) + f1;
        double f2 = f1 - (b / 200);
        double x = (f0 * f0 * f0) > Epsilon ? f0 * f0 * f0 : ((116 * f0) - 16) / Kappa;
        double y = l > Kappa * Epsilon ? f1 * f1 * f1 : l / Kappa;
        double z = (f2 * f2 * f2) > Epsilon ? f2 * f2 * f2 : ((116 * f2) - 16) / Kappa;

        // D50 white point.
        x *= 0.3457 / 0.3585;
        z *= (1.0 - 0.3457 - 0.3585) / 0.3585;
        (x, y, z) = D50ToD65(x, y, z);
        return FromXyzD65(x, y, z, alpha);
    }

    private static CssColor FromOklab(double l, double a, double b, double alpha)
    {
        double lp = l + (0.3963377773761749 * a) + (0.2158037573099136 * b);
        double mp = l - (0.1055613458156586 * a) - (0.0638541728258133 * b);
        double sp = l - (0.0894841775298119 * a) - (1.2914855480194092 * b);
        double lc = lp * lp * lp;
        double mc = mp * mp * mp;
        double sc = sp * sp * sp;
        double r = (4.0767416621 * lc) - (3.3077115913 * mc) + (0.2309699292 * sc);
        double g = (-1.2684380046 * lc) + (2.6097574011 * mc) - (0.3413193965 * sc);
        double bl = (-0.0041960863 * lc) - (0.7034186147 * mc) + (1.7076147010 * sc);
        return Make(Encode(r), Encode(g), Encode(bl), alpha, 4);
    }

    private static bool TryFromColorSpace(string space, double c0, double c1, double c2, double alpha, out CssColor color)
    {
        double x;
        double y;
        double z;
        switch (space)
        {
            case "srgb":
                color = Make(c0, c1, c2, alpha, 4);
                return true;
            case "srgb-linear":
                color = Make(Encode(c0), Encode(c1), Encode(c2), alpha, 4);
                return true;
            case "display-p3":
                (x, y, z) = Multiply(
                    [0.4865709486482162, 0.26566769316909306, 0.1982172852343625, 0.2289745640697488, 0.6917385218365064, 0.079286914093745, 0, 0.04511338185890264, 1.043944368900976],
                    Decode(c0), Decode(c1), Decode(c2));
                break;
            case "a98-rgb":
                (x, y, z) = Multiply(
                    [0.5766690429101305, 0.1855582379065463, 0.1882286462349947, 0.29734497525053605, 0.6273635662554661, 0.07529145849399788, 0.02703136138641234, 0.07068885253582723, 0.9913375368376388],
                    A98(c0), A98(c1), A98(c2));
                break;
            case "prophoto-rgb":
                (x, y, z) = Multiply(
                    [0.7977604896723027, 0.13518583717574031, 0.0313493495815248, 0.2880711282292934, 0.7118432178101014, 0.00008565396060525902, 0, 0, 0.8251046025104601],
                    ProPhoto(c0), ProPhoto(c1), ProPhoto(c2));
                (x, y, z) = D50ToD65(x, y, z);
                break;
            case "rec2020":
                (x, y, z) = Multiply(
                    [0.6369580483012914, 0.14461690358620832, 0.1688809751641721, 0.2627002120112671, 0.6779980715188708, 0.05930171646986196, 0, 0.028072693049087428, 1.060985057710791],
                    Rec2020(c0), Rec2020(c1), Rec2020(c2));
                break;
            case "xyz" or "xyz-d65":
                (x, y, z) = (c0, c1, c2);
                break;
            case "xyz-d50":
                (x, y, z) = D50ToD65(c0, c1, c2);
                break;
            default:
                color = default;
                return false;
        }

        color = FromXyzD65(x, y, z, alpha);
        return true;

        static double A98(double c) => double.CopySign(Math.Pow(Math.Abs(c), 563.0 / 256), c);

        static double ProPhoto(double c) => Math.Abs(c) <= 16.0 / 512 ? c / 16 : double.CopySign(Math.Pow(Math.Abs(c), 1.8), c);

        static double Rec2020(double c)
        {
            const double Alpha = 1.09929682680944;
            const double Beta = 0.018053968510807;
            return Math.Abs(c) < Beta * 4.5 ? c / 4.5 : double.CopySign(Math.Pow((Math.Abs(c) + Alpha - 1) / Alpha, 1 / 0.45), c);
        }
    }

    private static (double, double, double) Multiply(double[] m, double a, double b, double c)
    {
        return ((m[0] * a) + (m[1] * b) + (m[2] * c), (m[3] * a) + (m[4] * b) + (m[5] * c), (m[6] * a) + (m[7] * b) + (m[8] * c));
    }

    private static (double, double, double) D50ToD65(double x, double y, double z)
    {
        return Multiply(
            [0.955473421488075, -0.02309845494876471, 0.06325924320057072, -0.0283697093338637, 1.0099953980813041, 0.021041441191917323, 0.012314014864481998, -0.020507649298898964, 1.330365926242124],
            x, y, z);
    }

    private static CssColor FromXyzD65(double x, double y, double z, double alpha)
    {
        (double r, double g, double b) = Multiply(
            [3.2409699419045226, -1.537383177570094, -0.4986107602930034, -0.9692436362808796, 1.8759675015077202, 0.04155505740717559, 0.05563007969699366, -0.20397695888897652, 1.0569715142428786],
            x, y, z);
        return Make(Encode(r), Encode(g), Encode(b), alpha, 4);
    }

    // The sRGB transfer function, extended to negative values by symmetry.
    private static double Encode(double c)
    {
        double abs = Math.Abs(c);
        return abs <= 0.0031308 ? 12.92 * c : double.CopySign((1.055 * Math.Pow(abs, 1 / 2.4)) - 0.055, c);
    }

    private static double Decode(double c)
    {
        double abs = Math.Abs(c);
        return abs <= 0.04045 ? c / 12.92 : double.CopySign(Math.Pow((abs + 0.055) / 1.055, 2.4), c);
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
