using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Egami.Css;

/// <summary>One transform function of a <see cref="CssTransformList"/>.</summary>
public abstract record class CssTransformFunction
{
    private protected CssTransformFunction()
    {
    }

    /// <summary>Gets the function as a matrix.</summary>
    /// <param name="resolve">Converts a length (possibly a percentage) to pixels; the flag is true for the x axis.</param>
    /// <returns>The matrix (identity for an unsupported function).</returns>
    public abstract Matrix3x2 ToMatrix(Func<CssLength, bool, double> resolve);
}

/// <summary><c>matrix(a, b, c, d, e, f)</c>.</summary>
/// <param name="A">The x scale.</param>
/// <param name="B">The y skew.</param>
/// <param name="C">The x skew.</param>
/// <param name="D">The y scale.</param>
/// <param name="E">The x translation.</param>
/// <param name="F">The y translation.</param>
public sealed record class CssMatrixTransform(double A, double B, double C, double D, double E, double F) : CssTransformFunction
{
    /// <inheritdoc/>
    public override Matrix3x2 ToMatrix(Func<CssLength, bool, double> resolve) => new((float)A, (float)B, (float)C, (float)D, (float)E, (float)F);
}

/// <summary><c>translate()</c>, <c>translateX()</c> or <c>translateY()</c>.</summary>
/// <param name="X">The x translation.</param>
/// <param name="Y">The y translation.</param>
public sealed record class CssTranslateTransform(CssLength X, CssLength Y) : CssTransformFunction
{
    /// <inheritdoc/>
    public override Matrix3x2 ToMatrix(Func<CssLength, bool, double> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return Matrix3x2.CreateTranslation((float)resolve(X, true), (float)resolve(Y, false));
    }
}

/// <summary><c>scale()</c>, <c>scaleX()</c> or <c>scaleY()</c> (percentages are converted to factors).</summary>
/// <param name="X">The x factor.</param>
/// <param name="Y">The y factor.</param>
public sealed record class CssScaleTransform(double X, double Y) : CssTransformFunction
{
    /// <inheritdoc/>
    public override Matrix3x2 ToMatrix(Func<CssLength, bool, double> resolve) => Matrix3x2.CreateScale((float)X, (float)Y);
}

/// <summary><c>rotate(angle)</c>, or SVG's <c>rotate(angle cx cy)</c> about a center.</summary>
/// <param name="Degrees">The angle in degrees, clockwise in a y-down coordinate system.</param>
/// <param name="CenterX">The x of the center (SVG), or <see langword="null"/> for the origin.</param>
/// <param name="CenterY">The y of the center (SVG), or <see langword="null"/> for the origin.</param>
public sealed record class CssRotateTransform(double Degrees, double? CenterX = null, double? CenterY = null) : CssTransformFunction
{
    /// <inheritdoc/>
    public override Matrix3x2 ToMatrix(Func<CssLength, bool, double> resolve)
    {
        float radians = (float)(Degrees * Math.PI / 180);
        return Matrix3x2.CreateRotation(radians, new Vector2((float)(CenterX ?? 0), (float)(CenterY ?? 0)));
    }
}

/// <summary><c>skew()</c>, <c>skewX()</c> or <c>skewY()</c>.</summary>
/// <param name="XDegrees">The x skew angle in degrees.</param>
/// <param name="YDegrees">The y skew angle in degrees.</param>
public sealed record class CssSkewTransform(double XDegrees, double YDegrees) : CssTransformFunction
{
    /// <inheritdoc/>
    public override Matrix3x2 ToMatrix(Func<CssLength, bool, double> resolve)
    {
        return new Matrix3x2(1, (float)Math.Tan(YDegrees * Math.PI / 180), (float)Math.Tan(XDegrees * Math.PI / 180), 1, 0, 0);
    }
}

/// <summary>A 3D transform function (such as <c>rotateX()</c> or <c>matrix3d()</c>) that parsed but a 2D renderer cannot apply.</summary>
/// <param name="Name">The function name in lowercase.</param>
/// <param name="Arguments">The arguments as written.</param>
public sealed record class CssUnsupportedTransform(string Name, IReadOnlyList<CssComponentValue> Arguments) : CssTransformFunction
{
    /// <inheritdoc/>
    public override Matrix3x2 ToMatrix(Func<CssLength, bool, double> resolve) => Matrix3x2.Identity;
}

/// <summary>
/// A CSS <c>transform</c> value (CSS Transforms 1), or SVG's <c>transform</c> attribute: a list of transform functions applied
/// left to right, as nested coordinate systems.
/// </summary>
public sealed class CssTransformList
{
    private static readonly HashSet<string> s_3d = new(StringComparer.Ordinal)
    {
        "matrix3d", "translate3d", "translatez", "scale3d", "scalez", "rotate3d", "rotatex", "rotatey", "perspective",
    };

    private CssTransformList(IReadOnlyList<CssTransformFunction> functions)
    {
        Functions = functions;
    }

    /// <summary>Gets the transform functions, in order; empty for <c>none</c>.</summary>
    public IReadOnlyList<CssTransformFunction> Functions { get; }

    /// <summary>Gets whether the list is <c>none</c> (no functions).</summary>
    public bool IsNone => Functions.Count == 0;

    /// <summary>Gets whether a 3D function was parsed that <see cref="ToMatrix(Func{CssLength, bool, double})"/> ignores.</summary>
    public bool HasUnsupported => Functions.Any(f => f is CssUnsupportedTransform);

    /// <summary>Parses a CSS <c>transform</c> value, such as <c>translate(10px, 50%) rotate(45deg)</c> or <c>none</c>.</summary>
    /// <param name="text">The CSS text.</param>
    /// <param name="list">The transform list.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParse(string text, [NotNullWhen(true)] out CssTransformList? list)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), out list);
    }

    /// <summary>Parses component values as a CSS <c>transform</c> value.</summary>
    /// <param name="values">The component values.</param>
    /// <param name="list">The transform list.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParse(IReadOnlyList<CssComponentValue> values, [NotNullWhen(true)] out CssTransformList? list)
    {
        ArgumentNullException.ThrowIfNull(values);
        return TryParse(values, svg: false, out list);
    }

    /// <summary>
    /// Parses SVG's <c>transform</c> attribute: unitless numbers for lengths and angles, arguments separated by commas or
    /// whitespace, optional commas between functions, and <c>rotate(angle cx cy)</c>. CSS units and functions are accepted too,
    /// as SVG 2 allows.
    /// </summary>
    /// <param name="text">The attribute value, such as <c>translate(10 20) rotate(45 5 5)</c>.</param>
    /// <param name="list">The transform list.</param>
    /// <returns>True when valid; an invalid attribute means no transform.</returns>
    public static bool TryParseSvg(string text, [NotNullWhen(true)] out CssTransformList? list)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), svg: true, out list);
    }

    private static bool TryParse(IReadOnlyList<CssComponentValue> values, bool svg, [NotNullWhen(true)] out CssTransformList? list)
    {
        list = null;
        List<CssComponentValue> items = CssSyntax.Trim(values);
        if (items is [var none] && none.IsIdent("none"))
        {
            list = new CssTransformList([]);
            return true;
        }

        if (items.Count == 0)
        {
            if (svg)
            {
                list = new CssTransformList([]);
                return true;
            }

            return false;
        }

        List<CssTransformFunction> functions = [];
        bool needSeparatorOk = true;
        for (int i = 0; i < items.Count; i++)
        {
            CssComponentValue item = items[i];
            if (item.IsWhitespace)
            {
                continue;
            }

            if (svg && item.IsToken(CssTokenKind.Comma))
            {
                if (needSeparatorOk || functions.Count == 0)
                {
                    return false;
                }

                needSeparatorOk = true;
                continue;
            }

            string name;
            IReadOnlyList<CssComponentValue> arguments;
            if (item is CssFunction function)
            {
                name = function.Name;
                arguments = function.Arguments;
            }
            else if (svg && item is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
            {
                // SVG 1.1 allows whitespace between the name and the parenthesis.
                int j = i + 1;
                while (j < items.Count && items[j].IsWhitespace)
                {
                    j++;
                }

                if (j >= items.Count || items[j] is not CssSimpleBlock { OpenToken: '(' } block)
                {
                    return false;
                }

                name = ident.Value;
                arguments = block.Content;
                i = j;
            }
            else
            {
                return false;
            }

            CssTransformFunction? parsed = ParseFunction(CssText.ToAsciiLower(name), arguments, svg);
            if (parsed is null)
            {
                return false;
            }

            functions.Add(parsed);
            needSeparatorOk = false;
        }

        if (svg && needSeparatorOk && functions.Count > 0)
        {
            return false;
        }

        list = new CssTransformList(functions);
        return true;
    }

    private static CssTransformFunction? ParseFunction(string name, IReadOnlyList<CssComponentValue> arguments, bool svg)
    {
        List<CssComponentValue>? args = SplitArguments(arguments, svg);
        if (args is null)
        {
            return null;
        }

        if (s_3d.Contains(name))
        {
            return new CssUnsupportedTransform(name, arguments);
        }

        switch (name)
        {
            case "matrix":
                {
                    if (args.Count != 6)
                    {
                        return null;
                    }

                    double[] m = new double[6];
                    for (int i = 0; i < 6; i++)
                    {
                        if (Number(args[i]) is not { } n)
                        {
                            return null;
                        }

                        m[i] = n;
                    }

                    return new CssMatrixTransform(m[0], m[1], m[2], m[3], m[4], m[5]);
                }

            case "translate" or "translatex" or "translatey":
                {
                    if (args.Count < 1 || args.Count > (name == "translate" ? 2 : 1))
                    {
                        return null;
                    }

                    CssLength? x = Length(args[0], svg);
                    CssLength? y = args.Count == 2 ? Length(args[1], svg) : new CssLength(0, CssUnit.Px);
                    if (x is null || y is null)
                    {
                        return null;
                    }

                    return name switch
                    {
                        "translatex" => new CssTranslateTransform(x.Value, CssLength.Zero),
                        "translatey" => new CssTranslateTransform(CssLength.Zero, x.Value),
                        _ => new CssTranslateTransform(x.Value, y.Value),
                    };
                }

            case "scale" or "scalex" or "scaley":
                {
                    if (args.Count < 1 || args.Count > (name == "scale" ? 2 : 1))
                    {
                        return null;
                    }

                    double? x = Factor(args[0]);
                    double? y = args.Count == 2 ? Factor(args[1]) : x;
                    if (x is null || y is null)
                    {
                        return null;
                    }

                    return name switch
                    {
                        "scalex" => new CssScaleTransform(x.Value, 1),
                        "scaley" => new CssScaleTransform(1, x.Value),
                        _ => new CssScaleTransform(x.Value, y.Value),
                    };
                }

            case "rotate" or "rotatez":
                {
                    if (args.Count == 3 && svg && name == "rotate")
                    {
                        if (Angle(args[0], svg) is not { } a || Number(args[1]) is not { } cx || Number(args[2]) is not { } cy)
                        {
                            return null;
                        }

                        return new CssRotateTransform(a, cx, cy);
                    }

                    return args.Count == 1 && Angle(args[0], svg) is { } angle ? new CssRotateTransform(angle) : null;
                }

            case "skew":
                {
                    if (args.Count is < 1 or > 2)
                    {
                        return null;
                    }

                    double? x = Angle(args[0], svg);
                    double? y = args.Count == 2 ? Angle(args[1], svg) : 0;
                    return x is null || y is null ? null : new CssSkewTransform(x.Value, y.Value);
                }

            case "skewx" or "skewy":
                {
                    if (args.Count != 1 || Angle(args[0], svg) is not { } angle)
                    {
                        return null;
                    }

                    return name == "skewx" ? new CssSkewTransform(angle, 0) : new CssSkewTransform(0, angle);
                }

            default:
                return null;
        }
    }

    // CSS separates arguments with commas; SVG with commas and/or whitespace.
    private static List<CssComponentValue>? SplitArguments(IReadOnlyList<CssComponentValue> arguments, bool svg)
    {
        List<CssComponentValue> result = [];
        bool expectValue = true;
        bool sawComma = false;
        foreach (CssComponentValue item in arguments)
        {
            if (item.IsWhitespace)
            {
                continue;
            }

            if (item.IsToken(CssTokenKind.Comma))
            {
                if (expectValue)
                {
                    return null;
                }

                expectValue = true;
                sawComma = true;
                continue;
            }

            if (!expectValue && !svg)
            {
                return null;
            }

            result.Add(item);
            expectValue = false;
        }

        if (expectValue && (sawComma || result.Count > 0))
        {
            return null;
        }

        return result;
    }

    private static double? Number(CssComponentValue value)
    {
        if (value is CssTokenValue { Token: { Kind: CssTokenKind.Number } token })
        {
            return token.Number;
        }

        return value is CssFunction function && CssCalc.TryParse(function, out CssCalc? calc) && calc.IsNumber ? calc.Resolve(static _ => double.NaN) : null;
    }

    private static double? Factor(CssComponentValue value)
    {
        if (value is CssTokenValue { Token: { Kind: CssTokenKind.Percentage } percentage })
        {
            return percentage.Number / 100;
        }

        return Number(value);
    }

    private static CssLength? Length(CssComponentValue value, bool svg)
    {
        if (!CssLength.TryParse(value, out CssLength length))
        {
            return null;
        }

        if (length.Unit == CssUnit.None)
        {
            return svg || length.Value == 0 ? new CssLength(length.Value, CssUnit.Px) : null;
        }

        return length;
    }

    private static double? Angle(CssComponentValue value, bool svg)
    {
        if (CssAngle.TryParse(value, svg, out CssAngle angle))
        {
            return angle.Degrees;
        }

        return value is CssTokenValue { Token: { Kind: CssTokenKind.Number, Number: 0 } } ? 0 : null;
    }

    /// <summary>Combines the functions into one matrix, the first function outermost.</summary>
    /// <param name="resolve">Converts a length (possibly a percentage of the reference box) to pixels; the flag is true for the x axis.</param>
    /// <returns>The matrix, for row vectors as System.Numerics uses them.</returns>
    public Matrix3x2 ToMatrix(Func<CssLength, bool, double> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        Matrix3x2 result = Matrix3x2.Identity;
        foreach (CssTransformFunction function in Functions)
        {
            result = function.ToMatrix(resolve) * result;
        }

        return result;
    }

    /// <summary>Combines the functions into one matrix, with lengths converted by their absolute units (a percentage counts as 0).</summary>
    /// <returns>The matrix.</returns>
    public Matrix3x2 ToMatrix() => ToMatrix(static (length, _) => length.TryToPixels(out double px) ? px : 0);
}

/// <summary>A <c>transform-origin</c> value.</summary>
/// <param name="X">The x position: a length or a percentage of the reference box width.</param>
/// <param name="Y">The y position: a length or a percentage of the reference box height.</param>
/// <param name="Z">The z offset, a length.</param>
public readonly record struct CssTransformOrigin(CssLength X, CssLength Y, CssLength Z)
{
    /// <summary>Gets the initial value: <c>50% 50% 0</c>.</summary>
    public static CssTransformOrigin Center => new(new CssLength(50, CssUnit.Percent), new CssLength(50, CssUnit.Percent), CssLength.Zero);

    /// <summary>Parses a <c>transform-origin</c> value, such as <c>left top</c>, <c>50% 10px</c> or <c>right bottom 5px</c>.</summary>
    /// <param name="text">The CSS text.</param>
    /// <param name="origin">The origin.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParse(string text, out CssTransformOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), out origin);
    }

    /// <summary>Parses component values as a <c>transform-origin</c> value.</summary>
    /// <param name="values">The component values.</param>
    /// <param name="origin">The origin.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParse(IReadOnlyList<CssComponentValue> values, out CssTransformOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(values);
        origin = Center;
        List<CssComponentValue> items = [.. values.Where(v => !v.IsWhitespace)];
        if (items.Count is < 1 or > 3)
        {
            return false;
        }

        CssLength half = new(50, CssUnit.Percent);
        (CssLength? Length, string? Keyword)[] parts = new (CssLength?, string?)[Math.Min(items.Count, 2)];
        for (int i = 0; i < parts.Length; i++)
        {
            if (items[i] is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
            {
                string word = CssText.ToAsciiLower(ident.Value);
                if (word is not ("left" or "right" or "top" or "bottom" or "center"))
                {
                    return false;
                }

                parts[i] = (null, word);
            }
            else if (CssLength.TryParse(items[i], out CssLength length) && (length.Unit != CssUnit.None || length.Value == 0))
            {
                parts[i] = (length.Unit == CssUnit.None ? CssLength.Zero : length, null);
            }
            else
            {
                return false;
            }
        }

        CssLength x;
        CssLength y;
        if (parts.Length == 1)
        {
            (CssLength? length, string? keyword) = parts[0];
            x = keyword switch { "left" => new(0, CssUnit.Percent), "right" => new(100, CssUnit.Percent), "top" or "bottom" or "center" => half, _ => length!.Value };
            y = keyword switch { "top" => new(0, CssUnit.Percent), "bottom" => new(100, CssUnit.Percent), _ => half };
        }
        else
        {
            (CssLength? l0, string? k0) = parts[0];
            (CssLength? l1, string? k1) = parts[1];

            // Two keywords may come in either order; otherwise the first is horizontal and the second vertical.
            if (k0 is "top" or "bottom" || k1 is "left" or "right")
            {
                if (l0 is not null || l1 is not null || k0 is "left" or "right" || k1 is "top" or "bottom")
                {
                    return false;
                }

                (k0, k1) = (k1, k0);
            }

            if (k0 is "top" or "bottom" || k1 is "left" or "right")
            {
                return false;
            }

            x = k0 switch { "left" => new(0, CssUnit.Percent), "right" => new(100, CssUnit.Percent), "center" => half, _ => l0!.Value };
            y = k1 switch { "top" => new(0, CssUnit.Percent), "bottom" => new(100, CssUnit.Percent), "center" => half, _ => l1!.Value };
        }

        CssLength z = CssLength.Zero;
        if (items.Count == 3)
        {
            if (!CssLength.TryParse(items[2], out z) || z.Unit == CssUnit.Percent || (z.Unit == CssUnit.None && z.Value != 0))
            {
                return false;
            }

            if (z.Unit == CssUnit.None)
            {
                z = CssLength.Zero;
            }
        }

        origin = new CssTransformOrigin(x, y, z);
        return true;
    }
}
