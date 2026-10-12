namespace Egami.Css;

/// <summary>The environment media queries are evaluated against: the media type, the viewport and the user's preferences.</summary>
public sealed record class CssMediaContext
{
    /// <summary>Gets the default context: a 1024 × 768 screen at 1 dppx, light color scheme, 8 bits per color channel.</summary>
    public static CssMediaContext Default { get; } = new();

    /// <summary>Gets the media type, such as <c>screen</c> (the default) or <c>print</c>.</summary>
    public string MediaType { get; init; } = "screen";

    /// <summary>Gets the viewport width in CSS pixels. Defaults to 1024.</summary>
    public double Width { get; init; } = 1024;

    /// <summary>Gets the viewport height in CSS pixels. Defaults to 768.</summary>
    public double Height { get; init; } = 768;

    /// <summary>Gets the resolution in dots per CSS pixel (dppx). Defaults to 1.</summary>
    public double Resolution { get; init; } = 1;

    /// <summary>Gets the <c>prefers-color-scheme</c> value: <c>light</c> (the default) or <c>dark</c>.</summary>
    public string PrefersColorScheme { get; init; } = "light";

    /// <summary>Gets whether the user prefers reduced motion (<c>prefers-reduced-motion: reduce</c>). Defaults to false.</summary>
    public bool PrefersReducedMotion { get; init; }

    /// <summary>Gets the bits per color channel for the <c>color</c> feature; 0 for a monochrome device. Defaults to 8.</summary>
    public int Color { get; init; } = 8;

    /// <summary>Gets the size of <c>em</c> and <c>rem</c> in media queries, in CSS pixels: the initial font size. Defaults to 16.</summary>
    public double FontSize { get; init; } = 16;

    /// <summary>
    /// Gets a hook for media features the context does not know, such as <c>hover</c> or <c>pointer</c>: it receives the feature
    /// name in lowercase and the value (or <see langword="null"/> in a boolean context such as <c>(hover)</c>), and returns
    /// whether it matches, or <see langword="null"/> when unknown (which evaluates as false).
    /// </summary>
    public Func<string, IReadOnlyList<CssComponentValue>?, bool?>? Feature { get; init; }
}

/// <summary>A media query list (Media Queries 4), such as <c>screen and (min-width: 600px), print</c>.</summary>
/// <remarks>
/// A query that does not parse becomes <c>not all</c> without affecting the others; a feature the context does not know, or
/// any syntax Media Queries 4 reserves for the future, evaluates as unknown, which counts as false. An empty list matches.
/// </remarks>
public sealed class CssMediaQueryList
{
    private readonly MediaQuery[] _queries;

    private CssMediaQueryList(MediaQuery[] queries, string text)
    {
        _queries = queries;
        MediaText = text;
    }

    /// <summary>Gets the media query list as CSS text.</summary>
    public string MediaText { get; }

    /// <summary>Gets the number of media queries in the list.</summary>
    public int Count => _queries.Length;

    /// <summary>Parses a media query list. Never throws: a query that does not parse becomes <c>not all</c>.</summary>
    /// <param name="text">The media query list, such as <c>(prefers-color-scheme: dark)</c>.</param>
    /// <returns>The list.</returns>
    public static CssMediaQueryList Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default));
    }

    internal static CssMediaQueryList Parse(IReadOnlyList<CssComponentValue> items)
    {
        List<MediaQuery> queries = [];
        int start = 0;
        bool any = false;
        for (int i = 0; i <= items.Count; i++)
        {
            if (i < items.Count && !items[i].IsToken(CssTokenKind.Comma))
            {
                any |= !items[i].IsWhitespace;
                continue;
            }

            List<CssComponentValue> slice = [];
            for (int k = start; k < i; k++)
            {
                slice.Add(items[k]);
            }

            queries.Add(MediaQuery.Parse(CssSyntax.Trim(slice)));
            start = i + 1;
        }

        if (!any && queries.Count == 1)
        {
            queries.Clear();
        }

        return new CssMediaQueryList([.. queries], string.Join(", ", queries.Select(q => q.Text)));
    }

    /// <summary>Evaluates the list: true when any query matches, or when the list is empty.</summary>
    /// <param name="context">The media context.</param>
    /// <returns>True when the list matches.</returns>
    public bool Matches(CssMediaContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_queries.Length == 0)
        {
            return true;
        }

        foreach (MediaQuery query in _queries)
        {
            if (query.Evaluate(context))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the list as CSS text.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => MediaText;
}

/// <summary>One media query: an optional type with <c>not</c> or <c>only</c>, and a condition.</summary>
internal sealed class MediaQuery
{
    private bool _invalid;
    private bool _not;
    private string? _type;
    private MediaNode? _condition;

    public string Text { get; private set; } = "";

    private static readonly HashSet<string> s_reservedTypes = new(StringComparer.Ordinal) { "only", "not", "and", "or", "layer" };

    public static MediaQuery Parse(List<CssComponentValue> items)
    {
        MediaQuery query = new();
        query.Text = CssComponentValue.Serialize(items);
        MediaCursor cursor = new(items);
        if (!query.TryParse(ref cursor) || !cursor.AtEnd)
        {
            query._invalid = true;
            query.Text = "not all";
        }

        return query;
    }

    private bool TryParse(ref MediaCursor cursor)
    {
        cursor.SkipWhitespace();
        if (cursor.Current is CssTokenValue { Token: { Kind: CssTokenKind.Ident } first })
        {
            string word = CssText.ToAsciiLower(first.Value);
            if (word == "not" && cursor.PeekNonWhitespace(1) is CssSimpleBlock or CssFunction)
            {
                _condition = MediaNode.ParseCondition(ref cursor, allowOr: true);
                return _condition is not null;
            }

            if (word is "not" or "only")
            {
                _not = word == "not";
                cursor.Advance();
                cursor.SkipWhitespace();
                if (cursor.Current is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } typeToken })
                {
                    return false;
                }

                word = CssText.ToAsciiLower(typeToken.Value);
            }

            if (s_reservedTypes.Contains(word))
            {
                return false;
            }

            _type = word;
            cursor.Advance();
            cursor.SkipWhitespace();
            if (cursor.AtEnd)
            {
                return true;
            }

            if (!cursor.Current!.IsIdent("and"))
            {
                return false;
            }

            cursor.Advance();
            if (!cursor.SkipWhitespace())
            {
                return false;
            }

            _condition = MediaNode.ParseCondition(ref cursor, allowOr: false);
            return _condition is not null;
        }

        _condition = MediaNode.ParseCondition(ref cursor, allowOr: true);
        return _condition is not null;
    }

    public bool Evaluate(CssMediaContext context)
    {
        if (_invalid)
        {
            return false;
        }

        bool? result = true;
        if (_type is not null && _type != "all")
        {
            result = CssText.EqualsIgnoreAsciiCase(_type, context.MediaType);
        }

        if (_condition is not null && result == true)
        {
            result = _condition.Evaluate(context);
        }

        if (_not)
        {
            result = !result;
        }

        return result == true;
    }
}

/// <summary>A cursor over a media query's component values.</summary>
internal struct MediaCursor(IReadOnlyList<CssComponentValue> items)
{
    private readonly IReadOnlyList<CssComponentValue> _items = items;
    private int _pos;

    public readonly bool AtEnd => _pos >= _items.Count;

    public readonly CssComponentValue? Current => _pos < _items.Count ? _items[_pos] : null;

    public void Advance() => _pos++;

    public bool SkipWhitespace()
    {
        bool any = false;
        while (_pos < _items.Count && _items[_pos].IsWhitespace)
        {
            _pos++;
            any = true;
        }

        return any;
    }

    public readonly CssComponentValue? PeekNonWhitespace(int offset)
    {
        int i = _pos + offset;
        while (i < _items.Count && _items[i].IsWhitespace)
        {
            i++;
        }

        return i < _items.Count ? _items[i] : null;
    }
}

/// <summary>A media condition, evaluated with three-valued logic: null is unknown.</summary>
internal abstract class MediaNode
{
    public abstract bool? Evaluate(CssMediaContext context);

    // <media-condition>, or <media-condition-without-or> when allowOr is false.
    public static MediaNode? ParseCondition(ref MediaCursor cursor, bool allowOr)
    {
        cursor.SkipWhitespace();
        if (cursor.Current is { } first && first.IsIdent("not"))
        {
            cursor.Advance();
            if (!cursor.SkipWhitespace())
            {
                return null;
            }

            MediaNode? operand = ParseInParens(ref cursor);
            return operand is null ? null : new NotNode(operand);
        }

        MediaNode? left = ParseInParens(ref cursor);
        if (left is null)
        {
            return null;
        }

        List<MediaNode> operands = [left];
        string? joiner = null;
        while (true)
        {
            MediaCursor save = cursor;
            bool whitespace = cursor.SkipWhitespace();
            if (cursor.AtEnd || !whitespace || cursor.Current is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } word })
            {
                cursor = save;
                break;
            }

            string lower = CssText.ToAsciiLower(word.Value);
            if (lower is not ("and" or "or") || (joiner is not null && joiner != lower) || (lower == "or" && !allowOr))
            {
                return null;
            }

            joiner = lower;
            cursor.Advance();
            if (!cursor.SkipWhitespace())
            {
                return null;
            }

            MediaNode? next = ParseInParens(ref cursor);
            if (next is null)
            {
                return null;
            }

            operands.Add(next);
        }

        return operands.Count == 1 ? left : new JunctionNode(joiner == "and", [.. operands]);
    }

    // <media-in-parens>: a parenthesized condition, a media feature, or <general-enclosed> (unknown).
    private static MediaNode? ParseInParens(ref MediaCursor cursor)
    {
        switch (cursor.Current)
        {
            case CssFunction:
                cursor.Advance();
                return UnknownNode.Instance;
            case CssSimpleBlock { OpenToken: '(' } block:
                {
                    cursor.Advance();
                    MediaCursor inner = new(block.Content);
                    MediaNode? condition = ParseCondition(ref inner, allowOr: true);
                    inner.SkipWhitespace();
                    if (condition is not null && inner.AtEnd)
                    {
                        return condition;
                    }

                    return FeatureNode.Parse(block.Content) ?? (MediaNode)UnknownNode.Instance;
                }

            default:
                return null;
        }
    }

    private sealed class NotNode(MediaNode operand) : MediaNode
    {
        public override bool? Evaluate(CssMediaContext context) => !operand.Evaluate(context);
    }

    private sealed class JunctionNode(bool isAnd, MediaNode[] operands) : MediaNode
    {
        public override bool? Evaluate(CssMediaContext context)
        {
            bool unknown = false;
            foreach (MediaNode operand in operands)
            {
                bool? value = operand.Evaluate(context);
                if (value is null)
                {
                    unknown = true;
                }
                else if (value.Value != isAnd)
                {
                    return value;
                }
            }

            return unknown ? null : isAnd;
        }
    }

    private sealed class UnknownNode : MediaNode
    {
        public static readonly UnknownNode Instance = new();

        public override bool? Evaluate(CssMediaContext context) => null;
    }
}

/// <summary>A media feature: <c>(name)</c>, <c>(name: value)</c> or a range such as <c>(400px &lt;= width &lt; 700px)</c>.</summary>
internal sealed class FeatureNode : MediaNode
{
    private enum Op : byte
    {
        Equal,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
    }

    private enum ValueType : byte
    {
        Length,
        Ratio,
        Resolution,
        Integer,
        Discrete,
    }

    private string _name = "";
    private IReadOnlyList<CssComponentValue>? _value;
    private bool _min;
    private bool _max;

    // A range: up to two comparisons, each "value op feature", with the operator turned to read from the value's side.
    private IReadOnlyList<CssComponentValue>? _lowValue;
    private Op _lowOp;
    private IReadOnlyList<CssComponentValue>? _highValue;
    private Op _highOp;
    private bool _range;

    public static FeatureNode? Parse(IReadOnlyList<CssComponentValue> content)
    {
        List<CssComponentValue> items = CssSyntax.Trim(content);
        if (items.Count == 0)
        {
            return null;
        }

        // (name) and (name: value).
        if (items[0] is CssTokenValue { Token: { Kind: CssTokenKind.Ident } nameToken })
        {
            int i = 1;
            while (i < items.Count && items[i].IsWhitespace)
            {
                i++;
            }

            if (i == items.Count)
            {
                return Named(nameToken.Value, null);
            }

            if (items[i].IsToken(CssTokenKind.Colon))
            {
                List<CssComponentValue> value = CssSyntax.Trim(items.GetRange(i + 1, items.Count - i - 1));
                return value.Count == 0 ? null : Named(nameToken.Value, value);
            }
        }

        // Range forms: split at the comparison operators.
        List<List<CssComponentValue>> parts = [[]];
        List<Op> ops = [];
        for (int i = 0; i < items.Count; i++)
        {
            CssComponentValue item = items[i];
            if (item.IsDelim('<') || item.IsDelim('>') || item.IsDelim('='))
            {
                bool orEqual = !item.IsDelim('=') && i + 1 < items.Count && items[i + 1].IsDelim('=');
                ops.Add(item.IsDelim('=') ? Op.Equal : item.IsDelim('<') ? (orEqual ? Op.LessOrEqual : Op.Less) : (orEqual ? Op.GreaterOrEqual : Op.Greater));
                if (orEqual)
                {
                    i++;
                }

                parts.Add([]);
                continue;
            }

            parts[^1].Add(item);
        }

        if (ops.Count is < 1 or > 2)
        {
            return null;
        }

        List<List<CssComponentValue>> trimmed = parts.Select(p => CssSyntax.Trim(p)).ToList();
        if (trimmed.Any(p => p.Count == 0))
        {
            return null;
        }

        FeatureNode node = new() { _range = true };
        if (ops.Count == 1)
        {
            if (IsName(trimmed[0], out string? name))
            {
                // name op value: value (reversed op) name.
                node._name = name;
                node._lowValue = trimmed[1];
                node._lowOp = Reverse(ops[0]);
            }
            else if (IsName(trimmed[1], out name))
            {
                node._name = name;
                node._lowValue = trimmed[0];
                node._lowOp = ops[0];
            }
            else
            {
                return null;
            }

            return node;
        }

        // value op name op value: both operators must point the same way, and neither may be '='.
        bool less = ops[0] is Op.Less or Op.LessOrEqual;
        bool greater = ops[0] is Op.Greater or Op.GreaterOrEqual;
        bool sameWay = less ? ops[1] is Op.Less or Op.LessOrEqual : greater && ops[1] is Op.Greater or Op.GreaterOrEqual;
        if (!sameWay || !IsName(trimmed[1], out string? middle))
        {
            return null;
        }

        node._name = middle;
        node._lowValue = trimmed[0];
        node._lowOp = ops[0];
        node._highValue = trimmed[2];
        node._highOp = Reverse(ops[1]);
        return node;

        static bool IsName(List<CssComponentValue> part, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name)
        {
            name = part is [CssTokenValue { Token: { Kind: CssTokenKind.Ident } token }] ? CssText.ToAsciiLower(token.Value) : null;
            return name is not null;
        }

        static Op Reverse(Op op) => op switch
        {
            Op.Less => Op.Greater,
            Op.LessOrEqual => Op.GreaterOrEqual,
            Op.Greater => Op.Less,
            Op.GreaterOrEqual => Op.LessOrEqual,
            _ => Op.Equal,
        };
    }

    private static FeatureNode Named(string name, IReadOnlyList<CssComponentValue>? value)
    {
        string lower = CssText.ToAsciiLower(name);
        FeatureNode node = new() { _value = value };
        if (value is not null && lower.StartsWith("min-", StringComparison.Ordinal))
        {
            node._min = true;
            lower = lower[4..];
        }
        else if (value is not null && lower.StartsWith("max-", StringComparison.Ordinal))
        {
            node._max = true;
            lower = lower[4..];
        }

        node._name = lower;
        return node;
    }

    private static ValueType? TypeOf(string name) => name switch
    {
        "width" or "height" or "device-width" or "device-height" => ValueType.Length,
        "aspect-ratio" or "device-aspect-ratio" => ValueType.Ratio,
        "resolution" => ValueType.Resolution,
        "color" or "color-index" or "monochrome" => ValueType.Integer,
        "orientation" or "grid" or "prefers-color-scheme" or "prefers-reduced-motion" => ValueType.Discrete,
        _ => null,
    };

    public override bool? Evaluate(CssMediaContext context)
    {
        ValueType? type = TypeOf(_name);
        if (type is null)
        {
            if (_range || _min || _max)
            {
                return null;
            }

            return context.Feature?.Invoke(_name, _value);
        }

        if ((_min || _max || _range) && type == ValueType.Discrete)
        {
            return null;
        }

        double actual = _name switch
        {
            "width" or "device-width" => context.Width,
            "height" or "device-height" => context.Height,
            "aspect-ratio" or "device-aspect-ratio" => context.Height > 0 ? context.Width / context.Height : double.PositiveInfinity,
            "resolution" => context.Resolution,
            "color" => context.Color,
            _ => 0,
        };

        if (_range)
        {
            bool? low = Compare(_lowValue!, _lowOp, actual, type.Value, context);
            if (low != true || _highValue is null)
            {
                return low;
            }

            return Compare(_highValue, _highOp, actual, type.Value, context);
        }

        if (_value is null)
        {
            return _name switch
            {
                "orientation" or "prefers-color-scheme" => true,
                "prefers-reduced-motion" => context.PrefersReducedMotion,
                "grid" => false,
                _ => actual != 0,
            };
        }

        if (type == ValueType.Discrete)
        {
            if (_value is not [CssTokenValue { Token: var token }])
            {
                return null;
            }

            switch (_name)
            {
                case "grid":
                    return token is { Kind: CssTokenKind.Number, IsInteger: true, Number: 0 or 1 } ? token.Number == 0 : null;
                case "orientation":
                    if (token.IsIdent("portrait"))
                    {
                        return context.Height >= context.Width;
                    }

                    return token.IsIdent("landscape") ? context.Width > context.Height : null;
                case "prefers-color-scheme":
                    if (token.IsIdent("light") || token.IsIdent("dark"))
                    {
                        return CssText.EqualsIgnoreAsciiCase(token.Value, context.PrefersColorScheme);
                    }

                    return null;
                default:
                    if (token.IsIdent("reduce"))
                    {
                        return context.PrefersReducedMotion;
                    }

                    return token.IsIdent("no-preference") ? !context.PrefersReducedMotion : null;
            }
        }

        Op op = _min ? Op.LessOrEqual : _max ? Op.GreaterOrEqual : Op.Equal;
        return Compare(_value, op, actual, type.Value, context);
    }

    // Evaluates "value op actual".
    private static bool? Compare(IReadOnlyList<CssComponentValue> value, Op op, double actual, ValueType type, CssMediaContext context)
    {
        double? expected = type switch
        {
            ValueType.Length => Length(value, context),
            ValueType.Ratio => Ratio(value),
            ValueType.Resolution => Resolution(value),
            ValueType.Integer => value is [CssTokenValue { Token: { Kind: CssTokenKind.Number, IsInteger: true } integer }] && integer.Number >= 0 ? integer.Number : null,
            _ => null,
        };
        if (expected is not { } v)
        {
            return null;
        }

        const double Epsilon = 1e-9;
        return op switch
        {
            Op.Equal => Math.Abs(v - actual) <= Epsilon * Math.Max(1, Math.Abs(actual)),
            Op.Less => v < actual,
            Op.LessOrEqual => v <= actual + Epsilon,
            Op.Greater => v > actual,
            _ => v >= actual - Epsilon,
        };
    }

    private static double? Length(IReadOnlyList<CssComponentValue> value, CssMediaContext context)
    {
        if (value is not [var single])
        {
            return null;
        }

        if (single is CssFunction function)
        {
            if (!CssCalc.TryParse(function, out CssCalc? calc) || calc.HasPercentage)
            {
                return null;
            }

            double result = calc.Resolve(unit => PixelsPerUnit(unit, context));
            return double.IsNaN(result) ? null : result;
        }

        if (single is not CssTokenValue { Token: var token })
        {
            return null;
        }

        if (token.Kind == CssTokenKind.Number && token.Number == 0)
        {
            return 0;
        }

        if (token.Kind != CssTokenKind.Dimension || !CssLength.TryParseUnit(token.Unit, out CssUnit unit))
        {
            return null;
        }

        double perUnit = PixelsPerUnit(unit, context);
        return double.IsNaN(perUnit) ? null : token.Number * perUnit;
    }

    private static double PixelsPerUnit(CssUnit unit, CssMediaContext context)
    {
        double absolute = CssLength.AbsolutePixelsPerUnit(unit);
        if (!double.IsNaN(absolute))
        {
            return absolute;
        }

        return unit switch
        {
            CssUnit.Em or CssUnit.Rem => context.FontSize,
            CssUnit.Ex or CssUnit.Rex or CssUnit.Ch or CssUnit.Rch => context.FontSize / 2,
            CssUnit.Vw or CssUnit.Svw or CssUnit.Lvw or CssUnit.Dvw or CssUnit.Vi => context.Width / 100,
            CssUnit.Vh or CssUnit.Svh or CssUnit.Lvh or CssUnit.Dvh or CssUnit.Vb => context.Height / 100,
            CssUnit.Vmin => Math.Min(context.Width, context.Height) / 100,
            CssUnit.Vmax => Math.Max(context.Width, context.Height) / 100,
            _ => double.NaN,
        };
    }

    private static double? Ratio(IReadOnlyList<CssComponentValue> value)
    {
        List<double> numbers = [];
        bool slash = false;
        foreach (CssComponentValue item in value)
        {
            if (item.IsWhitespace)
            {
                continue;
            }

            if (item.IsDelim('/') && numbers.Count == 1 && !slash)
            {
                slash = true;
                continue;
            }

            if (item is CssTokenValue { Token: { Kind: CssTokenKind.Number } number } && number.Number >= 0)
            {
                numbers.Add(number.Number);
                continue;
            }

            return null;
        }

        if (numbers.Count == 1 && !slash)
        {
            return numbers[0];
        }

        if (numbers.Count != 2 || !slash)
        {
            return null;
        }

        return numbers[1] == 0 ? double.PositiveInfinity : numbers[0] / numbers[1];
    }

    private static double? Resolution(IReadOnlyList<CssComponentValue> value)
    {
        if (value is not [CssTokenValue { Token: { Kind: CssTokenKind.Dimension } token }] || token.Number < 0)
        {
            return null;
        }

        return CssText.ToAsciiLower(token.Unit) switch
        {
            "dppx" or "x" => token.Number,
            "dpi" => token.Number / 96,
            "dpcm" => token.Number * 2.54 / 96,
            _ => null,
        };
    }
}
