namespace Egami.Css;

/// <summary>
/// Custom properties and var() substitution for one element (CSS Variables 1): resolves the element's custom properties in
/// dependency order, makes every property in a reference cycle invalid at computed-value time, and substitutes var() in other
/// values with fallbacks.
/// </summary>
internal sealed class CssVariables(
    Dictionary<string, IReadOnlyList<CssComponentValue>> raw,
    Dictionary<string, IReadOnlyList<CssComponentValue>?> own,
    CssComputedStyle? parent)
{
    // Substitution results longer than this many component values are invalid, which stops exponential growth.
    private const int MaxLength = 65536;

    // Deeper chains of custom properties referring to each other are invalid, which bounds the recursion.
    private const int MaxDepth = 200;

    private readonly Dictionary<string, IReadOnlyList<CssComponentValue>> _raw = raw;
    private readonly Dictionary<string, IReadOnlyList<CssComponentValue>?> _own = own;
    private readonly CssComputedStyle? _parent = parent;
    private readonly Dictionary<string, int> _state = new(StringComparer.Ordinal);
    private readonly List<string> _stack = [];
    private readonly HashSet<string> _cyclic = new(StringComparer.Ordinal);

    public static bool ContainsVar(IReadOnlyList<CssComponentValue> values)
    {
        foreach (CssComponentValue value in values)
        {
            switch (value)
            {
                case CssFunction function:
                    if (function.NameIs("var") || ContainsVar(function.Arguments))
                    {
                        return true;
                    }

                    break;
                case CssSimpleBlock block:
                    if (ContainsVar(block.Content))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    public void ResolveAll()
    {
        foreach (string name in _raw.Keys)
        {
            Resolve(name);
        }
    }

    private void Resolve(string name)
    {
        _state.TryGetValue(name, out int state);
        if (state == 2)
        {
            return;
        }

        if (state == 1)
        {
            int index = _stack.LastIndexOf(name);
            for (int i = index; i < _stack.Count; i++)
            {
                _cyclic.Add(_stack[i]);
            }

            return;
        }

        IReadOnlyList<CssComponentValue> value = _raw[name];
        if (_stack.Count >= MaxDepth)
        {
            _state[name] = 2;
            _own[name] = null;
            return;
        }

        _state[name] = 1;
        _stack.Add(name);
        List<string> references = [];
        CollectReferences(value, references);
        foreach (string reference in references)
        {
            if (_raw.ContainsKey(reference))
            {
                Resolve(reference);
            }
        }

        _stack.RemoveAt(_stack.Count - 1);
        _state[name] = 2;
        _own[name] = _cyclic.Contains(name) ? null : (ContainsVar(value) ? Substitute(value) : value);
    }

    // Every custom property a value refers to, including inside var() fallbacks.
    private static void CollectReferences(IReadOnlyList<CssComponentValue> values, List<string> references)
    {
        foreach (CssComponentValue value in values)
        {
            switch (value)
            {
                case CssFunction function:
                    if (function.NameIs("var") && ParseVar(function, out string? name, out _))
                    {
                        references.Add(name);
                    }

                    CollectReferences(function.Arguments, references);
                    break;
                case CssSimpleBlock block:
                    CollectReferences(block.Content, references);
                    break;
            }
        }
    }

    // var( <custom-property-name> [ , <declaration-value>? ]? )
    private static bool ParseVar(CssFunction function, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name, out List<CssComponentValue>? fallback)
    {
        name = null;
        fallback = null;
        IReadOnlyList<CssComponentValue> arguments = function.Arguments;
        int i = 0;
        while (i < arguments.Count && arguments[i].IsWhitespace)
        {
            i++;
        }

        if (i >= arguments.Count || arguments[i] is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } || !ident.Value.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        i++;
        while (i < arguments.Count && arguments[i].IsWhitespace)
        {
            i++;
        }

        if (i < arguments.Count)
        {
            if (!arguments[i].IsToken(CssTokenKind.Comma))
            {
                return false;
            }

            fallback = [];
            for (int k = i + 1; k < arguments.Count; k++)
            {
                fallback.Add(arguments[k]);
            }

            fallback = CssSyntax.Trim(fallback);
        }

        name = ident.Value;
        return true;
    }

    private IReadOnlyList<CssComponentValue>? Lookup(string name)
    {
        if (_own.TryGetValue(name, out IReadOnlyList<CssComponentValue>? value))
        {
            return value;
        }

        if (_raw.ContainsKey(name))
        {
            // Not resolved yet: only during a cycle, which makes it invalid.
            return null;
        }

        return _parent?.Lookup(name);
    }

    /// <summary>Substitutes var() in a value; null when a reference fails without a fallback or the result is too long.</summary>
    public IReadOnlyList<CssComponentValue>? Substitute(IReadOnlyList<CssComponentValue> values)
    {
        List<CssComponentValue> result = [];
        int budget = MaxLength;
        return Substitute(values, result, ref budget, 0) ? result : null;
    }

    private bool Substitute(IReadOnlyList<CssComponentValue> values, List<CssComponentValue> result, ref int budget, int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        foreach (CssComponentValue value in values)
        {
            if (--budget < 0)
            {
                return false;
            }

            switch (value)
            {
                case CssFunction function when function.NameIs("var"):
                    {
                        if (!ParseVar(function, out string? name, out List<CssComponentValue>? fallback))
                        {
                            return false;
                        }

                        IReadOnlyList<CssComponentValue>? referenced = Lookup(name);
                        if (referenced is not null)
                        {
                            budget -= referenced.Count;
                            if (budget < 0)
                            {
                                return false;
                            }

                            result.AddRange(referenced);
                        }
                        else if (fallback is null || !Substitute(fallback, result, ref budget, depth + 1))
                        {
                            return false;
                        }

                        break;
                    }

                case CssFunction function when ContainsVar(function.Arguments):
                    {
                        List<CssComponentValue> arguments = [];
                        if (!Substitute(function.Arguments, arguments, ref budget, depth + 1))
                        {
                            return false;
                        }

                        result.Add(function with { Arguments = arguments });
                        break;
                    }

                case CssSimpleBlock block when ContainsVar(block.Content):
                    {
                        List<CssComponentValue> content = [];
                        if (!Substitute(block.Content, content, ref budget, depth + 1))
                        {
                            return false;
                        }

                        result.Add(block with { Content = content });
                        break;
                    }

                default:
                    result.Add(value);
                    break;
            }
        }

        return true;
    }
}
