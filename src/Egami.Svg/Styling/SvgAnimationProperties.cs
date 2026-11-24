using Egami.Css;
using Egami.Svg.Values;

namespace Egami.Svg.Styling;

/// <summary>
/// The properties of CSS Animations 1 and 2 (<c>animation-*</c> and the <c>animation</c> shorthand), and those of CSS Transforms 2
/// and Motion Path 1 that move an element (<c>translate</c>, <c>rotate</c>, <c>scale</c>, <c>offset-path</c>,
/// <c>offset-distance</c>, <c>offset-rotate</c>), for SVG's registry.
/// </summary>
internal static class SvgAnimationProperties
{
    /// <summary>The animation longhands with their initial values, in the order the shorthand resets them.</summary>
    public static readonly (string Name, string Initial)[] Longhands =
    [
        ("animation-name", "none"),
        ("animation-duration", "0s"),
        ("animation-timing-function", "ease"),
        ("animation-delay", "0s"),
        ("animation-iteration-count", "1"),
        ("animation-direction", "normal"),
        ("animation-fill-mode", "none"),
        ("animation-play-state", "running"),
        ("animation-composition", "replace"),
    ];

    /// <summary>Registers the properties and the shorthand.</summary>
    public static void Register(CssPropertyRegistry registry)
    {
        registry.Register(new CssPropertyDefinition("animation-name", false, "none") { Validate = v => List(v, IsName) });
        registry.Register(new CssPropertyDefinition("animation-duration", false, "0s") { Validate = v => List(v, i => TryTime(i, out double t) && t >= 0) });
        registry.Register(new CssPropertyDefinition("animation-timing-function", false, "ease") { Validate = v => List(v, i => CssEasingFunction.TryParse(i, out _)) });
        registry.Register(new CssPropertyDefinition("animation-delay", false, "0s") { Validate = v => List(v, i => TryTime(i, out _)) });
        registry.Register(new CssPropertyDefinition("animation-iteration-count", false, "1") { Validate = v => List(v, i => TryIterations(i, out _)) });
        registry.Register(new CssPropertyDefinition("animation-direction", false, "normal") { Validate = v => List(v, Keywords("normal", "reverse", "alternate", "alternate-reverse")) });
        registry.Register(new CssPropertyDefinition("animation-fill-mode", false, "none") { Validate = v => List(v, Keywords("none", "forwards", "backwards", "both")) });
        registry.Register(new CssPropertyDefinition("animation-play-state", false, "running") { Validate = v => List(v, Keywords("running", "paused")) });
        registry.Register(new CssPropertyDefinition("animation-composition", false, "replace") { Validate = v => List(v, Keywords("replace", "add", "accumulate")) });
        registry.RegisterShorthand(new CssShorthandDefinition("animation", [.. Longhands.Select(l => l.Name)], Expand));

        registry.Register(new CssPropertyDefinition("translate", false, "none") { Validate = Translate });
        registry.Register(new CssPropertyDefinition("rotate", false, "none") { Validate = Rotate });
        registry.Register(new CssPropertyDefinition("scale", false, "none") { Validate = Scale });
        registry.Register(new CssPropertyDefinition("offset-path", false, "none") { Validate = OffsetPath });
        registry.Register(new CssPropertyDefinition("offset-distance", false, "0") { Validate = v => SvgStyleValues.TryLength(v, out _) });
        registry.Register(new CssPropertyDefinition("offset-rotate", false, "auto") { Validate = v => TryOffsetRotate(v, out _, out _) });
    }

    /// <summary>Splits a comma-separated list into its items, whitespace trimmed; null when an item is empty.</summary>
    public static List<List<CssComponentValue>>? Split(IReadOnlyList<CssComponentValue> values)
    {
        List<List<CssComponentValue>> items = [[]];
        foreach (CssComponentValue value in values)
        {
            if (value is CssTokenValue { Token.Kind: CssTokenKind.Comma })
            {
                items.Add([]);
            }
            else if (value is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace } || items[^1].Count > 0)
            {
                items[^1].Add(value);
            }
        }

        foreach (List<CssComponentValue> item in items)
        {
            while (item.Count > 0 && item[^1] is CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                item.RemoveAt(item.Count - 1);
            }

            if (item.Count == 0)
            {
                return null;
            }
        }

        return items;
    }

    /// <summary>A time in seconds: <c>2s</c> or <c>150ms</c>.</summary>
    public static bool TryTime(IReadOnlyList<CssComponentValue> values, out double seconds)
    {
        seconds = 0;
        return SvgStyleValues.Single(values) is CssComponentValue single && TryTime(single, out seconds);
    }

    /// <summary>A time in seconds: <c>2s</c> or <c>150ms</c>.</summary>
    public static bool TryTime(CssComponentValue value, out double seconds)
    {
        seconds = 0;
        if (value is not CssTokenValue { Token: { Kind: CssTokenKind.Dimension } token } || !double.IsFinite(token.Number))
        {
            return false;
        }

        if (token.Unit.Equals("s", StringComparison.OrdinalIgnoreCase))
        {
            seconds = token.Number;
            return true;
        }

        if (token.Unit.Equals("ms", StringComparison.OrdinalIgnoreCase))
        {
            seconds = token.Number / 1000;
            return true;
        }

        return false;
    }

    /// <summary>An iteration count: a non-negative number or <c>infinite</c>.</summary>
    public static bool TryIterations(IReadOnlyList<CssComponentValue> values, out double count)
    {
        count = 1;
        return SvgStyleValues.Single(values) is CssComponentValue single && TryIterations(single, out count);
    }

    private static bool TryIterations(CssComponentValue value, out double count)
    {
        count = 1;
        if (Is(value, "infinite"))
        {
            count = double.PositiveInfinity;
            return true;
        }

        if (value is CssTokenValue { Token: { Kind: CssTokenKind.Number } token } && token.Number >= 0 && double.IsFinite(token.Number))
        {
            count = token.Number;
            return true;
        }

        return false;
    }

    /// <summary>offset-rotate: <c>auto</c>, <c>reverse</c>, an angle, or <c>auto</c> or <c>reverse</c> with an angle.</summary>
    public static bool TryOffsetRotate(IReadOnlyList<CssComponentValue> values, out bool auto, out double degrees)
    {
        auto = false;
        degrees = 0;
        bool sawAuto = false;
        bool sawAngle = false;
        foreach (CssComponentValue value in values)
        {
            if (value is CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                continue;
            }

            if (!sawAuto && (Is(value, "auto") || Is(value, "reverse")))
            {
                sawAuto = true;
                auto = true;
                degrees += Is(value, "reverse") ? 180 : 0;
            }
            else if (!sawAngle && CssAngle.TryParse(value, allowNumber: false, out CssAngle angle) && double.IsFinite(angle.Degrees))
            {
                sawAngle = true;
                degrees += angle.Degrees;
            }
            else
            {
                return false;
            }
        }

        return sawAuto || sawAngle;
    }

    // animation: <single-animation>#. Each part goes to the first longhand, in the grammar's order, that takes it and has none
    // yet: the first time is the duration and the second the delay, and a keyword such as 'ease' or 'none' is the name only
    // when the longhand it belongs to is taken (CSS Animations 1).
    private static IReadOnlyList<CssDeclaration>? Expand(IReadOnlyList<CssComponentValue> value)
    {
        List<List<CssComponentValue>>? items = Split(value);
        if (items is null)
        {
            return null;
        }

        List<string>[] lists = [.. Longhands.Select(_ => new List<string>())];
        foreach (List<CssComponentValue> item in items)
        {
            string?[] parts = new string?[Longhands.Length];
            foreach (CssComponentValue part in item)
            {
                if (part is CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
                {
                    continue;
                }

                int slot = -1;
                foreach (int candidate in Slots(part))
                {
                    if (parts[candidate] is null)
                    {
                        slot = candidate;
                        break;
                    }
                }

                if (slot < 0)
                {
                    return null;
                }

                parts[slot] = part.ToString();
            }

            for (int i = 0; i < Longhands.Length; i++)
            {
                lists[i].Add(parts[i] ?? Longhands[i].Initial);
            }
        }

        return [.. Longhands.Select((l, i) => new CssDeclaration(l.Name, CssComponentValue.ParseList(string.Join(", ", lists[i]))))];
    }

    // The longhands a part of the shorthand may set, in the order it tries them.
    private static IEnumerable<int> Slots(CssComponentValue part)
    {
        if (TryTime(part, out _))
        {
            yield return 1;
            yield return 3;
        }

        if (CssEasingFunction.TryParse([part], out _))
        {
            yield return 2;
        }

        if (TryIterations(part, out _))
        {
            yield return 4;
        }

        if (Keywords("normal", "reverse", "alternate", "alternate-reverse")([part]))
        {
            yield return 5;
        }

        if (Keywords("none", "forwards", "backwards", "both")([part]))
        {
            yield return 6;
        }

        if (Keywords("running", "paused")([part]))
        {
            yield return 7;
        }

        if (IsName([part]))
        {
            yield return 0;
        }
    }

    private static bool Is(CssComponentValue value, string keyword) =>
        value is CssTokenValue { Token: { Kind: CssTokenKind.Ident } token } && token.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool List(IReadOnlyList<CssComponentValue> values, Func<IReadOnlyList<CssComponentValue>, bool> item) =>
        Split(values) is { } items && items.TrueForAll(i => item(i));

    private static bool IsName(IReadOnlyList<CssComponentValue> values) =>
        SvgStyleValues.Single(values) is CssTokenValue { Token: { Kind: CssTokenKind.Ident or CssTokenKind.String } token }
        && (token.Kind == CssTokenKind.String || token.Value.ToLowerInvariant() is not ("initial" or "inherit" or "unset" or "default" or "revert" or "revert-layer"));

    private static Func<IReadOnlyList<CssComponentValue>, bool> Keywords(params string[] allowed) =>
        v => SvgStyleValues.Keyword(v) is string k && Array.Exists(allowed, a => a.Equals(k, StringComparison.OrdinalIgnoreCase));

    // translate: none | <length-percentage> [<length-percentage> <length>?]?; a third (z) length must be 0 to stay 2D.
    private static bool Translate(IReadOnlyList<CssComponentValue> values) =>
        SvgStyleValues.IsKeyword(values, "none") || TryTranslate(values, out _, out _);

    /// <summary>The x and y of a translate value; false for none or an invalid or 3D one.</summary>
    public static bool TryTranslate(IReadOnlyList<CssComponentValue> values, out SvgLength x, out SvgLength y)
    {
        x = default;
        y = new SvgLength(0, SvgUnit.Px);
        List<SvgLength> lengths = [];
        foreach (CssComponentValue value in values)
        {
            if (value is CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                continue;
            }

            // A length or percentage, or a calc() mixing them; a number only as 0.
            if (!SvgStyleValues.TryLength(value, out SvgLength length) || !float.IsFinite(length.Value) || (length.Unit == SvgUnit.None && length.Value != 0))
            {
                return false;
            }

            lengths.Add(length);
        }

        if (lengths.Count is < 1 or > 3 || (lengths.Count == 3 && (lengths[2].Value != 0 || lengths[2].Offset != 0 || lengths[2].IsPercent)))
        {
            return false;
        }

        x = lengths[0];
        if (lengths.Count > 1)
        {
            y = lengths[1];
        }

        return true;
    }

    // rotate: none | <angle> | z <angle>; rotations about other axes are 3D and not drawn.
    private static bool Rotate(IReadOnlyList<CssComponentValue> values) =>
        SvgStyleValues.IsKeyword(values, "none") || TryRotate(values, out _);

    /// <summary>The angle of a 2D rotate value; false for none or an invalid or 3D one.</summary>
    public static bool TryRotate(IReadOnlyList<CssComponentValue> values, out double degrees)
    {
        degrees = 0;
        bool angle = false;
        bool axis = false;
        foreach (CssComponentValue value in values)
        {
            if (value is CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                continue;
            }

            if (!axis && !angle && Is(value, "z"))
            {
                axis = true;
            }
            else if (!angle && CssAngle.TryParse(value, allowNumber: false, out CssAngle a) && double.IsFinite(a.Degrees))
            {
                angle = true;
                degrees = a.Degrees;
            }
            else
            {
                return false;
            }
        }

        return angle;
    }

    // scale: none | [<number> | <percentage>]{1,3}; a third (z) factor must be 1 to stay 2D.
    private static bool Scale(IReadOnlyList<CssComponentValue> values) =>
        SvgStyleValues.IsKeyword(values, "none") || TryScale(values, out _, out _);

    /// <summary>The x and y factors of a scale value; false for none or an invalid or 3D one.</summary>
    public static bool TryScale(IReadOnlyList<CssComponentValue> values, out double x, out double y)
    {
        x = y = 1;
        List<double> factors = [];
        foreach (CssComponentValue value in values)
        {
            switch (value)
            {
                case CssTokenValue { Token.Kind: CssTokenKind.Whitespace }:
                    continue;
                case CssTokenValue { Token: { Kind: CssTokenKind.Number } n } when double.IsFinite(n.Number):
                    factors.Add(n.Number);
                    break;
                case CssTokenValue { Token: { Kind: CssTokenKind.Percentage } p } when double.IsFinite(p.Number):
                    factors.Add(p.Number / 100);
                    break;
                default:
                    return false;
            }
        }

        if (factors.Count is < 1 or > 3 || (factors.Count == 3 && factors[2] != 1))
        {
            return false;
        }

        x = factors[0];
        y = factors.Count > 1 ? factors[1] : factors[0];
        return true;
    }

    // offset-path: none | path(<string>). Other shapes, ray() and url() are not drawn.
    private static bool OffsetPath(IReadOnlyList<CssComponentValue> values) =>
        SvgStyleValues.IsKeyword(values, "none") || TryPathFunction(values, out _);

    /// <summary>The path data of a <c>path()</c> function (with an optional fill rule first); false for anything else.</summary>
    public static bool TryPathFunction(IReadOnlyList<CssComponentValue> values, out string data)
    {
        data = "";
        if (SvgStyleValues.Single(values) is not CssFunction function || !function.NameIs("path"))
        {
            return false;
        }

        CssComponentValue? last = null;
        foreach (CssComponentValue argument in function.Arguments)
        {
            if (argument is not CssTokenValue { Token.Kind: CssTokenKind.Whitespace or CssTokenKind.Comma })
            {
                last = argument;
            }
        }

        if (last is CssTokenValue { Token: { Kind: CssTokenKind.String } text })
        {
            data = text.Value;
            return true;
        }

        return false;
    }
}
