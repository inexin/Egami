namespace Egami.Css;

/// <summary>An at-rule as CSS Syntax 3 parses it, before its prelude and block are interpreted.</summary>
internal sealed record RawAtRule(string Name, IReadOnlyList<CssComponentValue> Prelude, CssSimpleBlock? Block, int Line, int Column);

/// <summary>A qualified rule as CSS Syntax 3 parses it, before its prelude and block are interpreted.</summary>
internal sealed record RawQualifiedRule(IReadOnlyList<CssComponentValue> Prelude, CssSimpleBlock Block, int Line, int Column);

/// <summary>
/// The CSS Syntax 3 parser (section 5). Tokens are first grouped into component values (functions and simple blocks), which
/// gives the same result as the specification's token-level algorithms; the rule and declaration algorithms then run over the
/// component values.
/// </summary>
internal static class CssSyntax
{
    private static readonly IReadOnlyList<CssComponentValue> s_empty = [];

    /// <summary>Stands in a rule or block-contents list for something that did not parse (the error was reported).</summary>
    public static readonly object Invalid = new();

    public static List<CssComponentValue> ParseComponentValues(string css, CssParseOptions options)
    {
        IReadOnlyList<CssToken> tokens = CssTokenizer.Tokenize(css, options);
        int index = 0;
        return ConsumeComponentValues(tokens, ref index, null, 0, options);
    }

    // Consumes component values until the closing token (consumed) or the end of the tokens.
    private static List<CssComponentValue> ConsumeComponentValues(IReadOnlyList<CssToken> tokens, ref int index, CssTokenKind? closing, int depth, CssParseOptions options)
    {
        List<CssComponentValue> values = [];
        while (index < tokens.Count)
        {
            CssToken token = tokens[index];
            if (closing is not null && token.Kind == closing)
            {
                index++;
                return values;
            }

            values.Add(ConsumeComponentValue(tokens, ref index, depth, options));
        }

        if (closing is not null && tokens.Count > 0)
        {
            CssToken last = tokens[^1];
            options.Report(last.Line, last.Column, "A block or function not closed before the end of the input.");
        }

        return values;
    }

    private static CssComponentValue ConsumeComponentValue(IReadOnlyList<CssToken> tokens, ref int index, int depth, CssParseOptions options)
    {
        CssToken token = tokens[index++];
        CssTokenKind? closing = token.Kind switch
        {
            CssTokenKind.LeftCurly => CssTokenKind.RightCurly,
            CssTokenKind.LeftSquare => CssTokenKind.RightSquare,
            CssTokenKind.LeftParenthesis or CssTokenKind.Function => CssTokenKind.RightParenthesis,
            _ => null,
        };
        if (closing is null)
        {
            return new CssTokenValue(token);
        }

        IReadOnlyList<CssComponentValue> content;
        if (depth >= options.MaxNestingDepth)
        {
            options.Report(token.Line, token.Column, "Blocks nested too deeply; the innermost block was dropped.");
            SkipBlock(tokens, ref index);
            content = s_empty;
        }
        else
        {
            content = ConsumeComponentValues(tokens, ref index, closing, depth + 1, options);
        }

        if (token.Kind == CssTokenKind.Function)
        {
            return new CssFunction(token.Value, content) { StartLine = token.Line, StartColumn = token.Column };
        }

        char open = token.Kind switch { CssTokenKind.LeftCurly => '{', CssTokenKind.LeftSquare => '[', _ => '(' };
        return new CssSimpleBlock(open, content) { StartLine = token.Line, StartColumn = token.Column };
    }

    // Skips a block whose opening token was consumed, without recursion: a stack of the closing tokens expected.
    private static void SkipBlock(IReadOnlyList<CssToken> tokens, ref int index)
    {
        Stack<CssTokenKind> expected = new();
        expected.Push(Closing(tokens[index - 1].Kind));
        while (index < tokens.Count && expected.Count > 0)
        {
            CssTokenKind kind = tokens[index++].Kind;
            if (kind == expected.Peek())
            {
                expected.Pop();
            }
            else if (kind is CssTokenKind.LeftCurly or CssTokenKind.LeftSquare or CssTokenKind.LeftParenthesis or CssTokenKind.Function)
            {
                expected.Push(Closing(kind));
            }
        }

        static CssTokenKind Closing(CssTokenKind kind) => kind switch
        {
            CssTokenKind.LeftCurly => CssTokenKind.RightCurly,
            CssTokenKind.LeftSquare => CssTokenKind.RightSquare,
            _ => CssTokenKind.RightParenthesis,
        };
    }

    public static List<CssComponentValue> Trim(IReadOnlyList<CssComponentValue> values)
    {
        int start = 0;
        int end = values.Count;
        while (start < end && values[start].IsWhitespace)
        {
            start++;
        }

        while (end > start && values[end - 1].IsWhitespace)
        {
            end--;
        }

        List<CssComponentValue> result = new(end - start);
        for (int i = start; i < end; i++)
        {
            result.Add(values[i]);
        }

        return result;
    }

    /// <summary>A cursor over component values running the CSS Syntax 3 rule and declaration algorithms.</summary>
    internal sealed class Parser(IReadOnlyList<CssComponentValue> items, CssParseOptions options)
    {
        private readonly IReadOnlyList<CssComponentValue> _items = items;
        private readonly CssParseOptions _options = options;
        private int _pos;

        private bool AtEnd => _pos >= _items.Count;

        private CssComponentValue Current => _items[_pos];

        public void Error(CssComponentValue? at, string message) => _options.Report(at?.Line ?? 0, at?.Column ?? 0, message);

        private void SkipWhitespace()
        {
            while (!AtEnd && Current.IsWhitespace)
            {
                _pos++;
            }
        }

        // "Consume a stylesheet's contents" (topLevel) or "consume a list of rules" for a group rule's block.
        public List<object> ConsumeRuleList(bool topLevel)
        {
            List<object> rules = [];
            while (!AtEnd)
            {
                CssComponentValue item = Current;
                if (item.IsWhitespace)
                {
                    _pos++;
                }
                else if (topLevel && (item.IsToken(CssTokenKind.Cdo) || item.IsToken(CssTokenKind.Cdc)))
                {
                    _pos++;
                }
                else if (item.IsToken(CssTokenKind.AtKeyword))
                {
                    rules.Add(ConsumeAtRule(nested: false));
                }
                else
                {
                    rules.Add((object?)ConsumeQualifiedRule(nested: false) ?? Invalid);
                }
            }

            return rules;
        }

        public RawAtRule ConsumeAtRule(bool nested)
        {
            CssToken keyword = ((CssTokenValue)_items[_pos++]).Token;
            List<CssComponentValue> prelude = [];
            while (!AtEnd)
            {
                CssComponentValue item = Current;
                if (item.IsToken(CssTokenKind.Semicolon))
                {
                    _pos++;
                    return new RawAtRule(keyword.Value, prelude, null, keyword.Line, keyword.Column);
                }

                if (item.IsToken(CssTokenKind.RightCurly))
                {
                    Error(item, "An unexpected '}' in an at-rule's prelude.");
                    if (nested)
                    {
                        return new RawAtRule(keyword.Value, prelude, null, keyword.Line, keyword.Column);
                    }

                    prelude.Add(item);
                    _pos++;
                    continue;
                }

                if (item is CssSimpleBlock { OpenToken: '{' } block)
                {
                    _pos++;
                    return new RawAtRule(keyword.Value, prelude, block, keyword.Line, keyword.Column);
                }

                prelude.Add(item);
                _pos++;
            }

            if (!nested)
            {
                _options.Report(keyword.Line, keyword.Column, $"The at-rule @{keyword.Value} ends with the input.");
            }

            return new RawAtRule(keyword.Value, prelude, null, keyword.Line, keyword.Column);
        }

        public RawQualifiedRule? ConsumeQualifiedRule(bool nested)
        {
            CssComponentValue first = Current;
            List<CssComponentValue> prelude = [];
            while (!AtEnd)
            {
                CssComponentValue item = Current;
                if (nested && item.IsToken(CssTokenKind.Semicolon))
                {
                    Error(first, "An invalid declaration was dropped.");
                    return null;
                }

                if (item.IsToken(CssTokenKind.RightCurly))
                {
                    Error(item, "An unexpected '}'.");
                    if (nested)
                    {
                        return null;
                    }

                    prelude.Add(item);
                    _pos++;
                    continue;
                }

                if (item is CssSimpleBlock { OpenToken: '{' } block)
                {
                    _pos++;
                    if (LooksLikeCustomProperty(prelude))
                    {
                        Error(first, "A custom property declaration where a rule was expected; it was dropped.");
                        return null;
                    }

                    return new RawQualifiedRule(prelude, block, first.Line, first.Column);
                }

                prelude.Add(item);
                _pos++;
            }

            Error(first, "A rule without a block at the end of the input; it was dropped.");
            return null;
        }

        private static bool LooksLikeCustomProperty(List<CssComponentValue> prelude)
        {
            int i = 0;
            while (i < prelude.Count && prelude[i].IsWhitespace)
            {
                i++;
            }

            if (i >= prelude.Count || prelude[i] is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } name } || !name.Value.StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            i++;
            while (i < prelude.Count && prelude[i].IsWhitespace)
            {
                i++;
            }

            return i < prelude.Count && prelude[i].IsToken(CssTokenKind.Colon);
        }

        // "Consume a block's contents": declarations, and the rules nested among them (at-rules and qualified rules), in order,
        // with Invalid for what did not parse.
        public void ConsumeBlockContents(List<object> items)
        {
            while (!AtEnd)
            {
                CssComponentValue item = Current;
                if (item.IsWhitespace || item.IsToken(CssTokenKind.Semicolon))
                {
                    _pos++;
                }
                else if (item.IsToken(CssTokenKind.RightCurly))
                {
                    // Only possible in a declaration list parsed on its own, such as a style attribute: there is no block to end.
                    Error(item, "An unexpected '}'.");
                    items.Add(Invalid);
                    _pos++;
                }
                else if (item.IsToken(CssTokenKind.AtKeyword))
                {
                    items.Add(ConsumeAtRule(nested: true));
                }
                else
                {
                    int mark = _pos;
                    if (ConsumeDeclaration(nested: true, reportErrors: false) is { } declaration)
                    {
                        items.Add(declaration);
                        continue;
                    }

                    _pos = mark;
                    items.Add((object?)ConsumeQualifiedRule(nested: true) ?? Invalid);
                }
            }
        }

        // "Consume a declaration". Returns null (with the input consumed to the next ';') when there is none.
        public CssDeclaration? ConsumeDeclaration(bool nested, bool reportErrors = true, bool toEnd = false)
        {
            CssComponentValue first = Current;
            if (first is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } nameToken })
            {
                if (reportErrors)
                {
                    Error(first, "A declaration must start with a property name.");
                }

                ConsumeBadDeclaration();
                return null;
            }

            _pos++;
            SkipWhitespace();
            if (AtEnd || !Current.IsToken(CssTokenKind.Colon))
            {
                if (reportErrors)
                {
                    Error(first, $"The declaration '{nameToken.Value}' has no ':'.");
                }

                ConsumeBadDeclaration();
                return null;
            }

            _pos++;
            List<CssComponentValue> value = [];
            while (!AtEnd && (toEnd || !Current.IsToken(CssTokenKind.Semicolon)))
            {
                value.Add(Current);
                _pos++;
            }

            bool isCustom = nameToken.Value.StartsWith("--", StringComparison.Ordinal);
            CssDeclaration declaration = MakeDeclaration(nameToken.Value, value, first.Line, first.Column);
            if (!isCustom && nested && HasBlockAndMore(declaration.Value))
            {
                return null;
            }

            return declaration;
        }

        private void ConsumeBadDeclaration()
        {
            while (!AtEnd)
            {
                if (Current.IsToken(CssTokenKind.Semicolon))
                {
                    _pos++;
                    return;
                }

                _pos++;
            }
        }

        private static bool HasBlockAndMore(IReadOnlyList<CssComponentValue> value)
        {
            bool block = false;
            bool other = false;
            foreach (CssComponentValue item in value)
            {
                if (item is CssSimpleBlock { OpenToken: '{' })
                {
                    block = true;
                }
                else if (!item.IsWhitespace)
                {
                    other = true;
                }
            }

            return block && other;
        }

        // The remaining items as one component value, for "parse a component value".
        public CssComponentValue? ConsumeOneValue()
        {
            SkipWhitespace();
            if (AtEnd)
            {
                return null;
            }

            CssComponentValue value = Current;
            _pos++;
            SkipWhitespace();
            return AtEnd ? value : null;
        }

        public bool SkipWhitespaceToEnd()
        {
            SkipWhitespace();
            return AtEnd;
        }

        public bool PeekIsAtKeyword => !AtEnd && Current.IsToken(CssTokenKind.AtKeyword);
    }

    // Builds a declaration from a name and its raw value: whitespace trimmed and !important removed.
    public static CssDeclaration MakeDeclaration(string name, List<CssComponentValue> value, int line, int column)
    {
        List<CssComponentValue> trimmed = Trim(value);
        bool important = false;
        int last = trimmed.Count - 1;
        if (last >= 0 && trimmed[last] is CssTokenValue { Token: { Kind: CssTokenKind.Ident } word } && CssText.EqualsIgnoreAsciiCase(word.Value, "important"))
        {
            int bang = last - 1;
            while (bang >= 0 && trimmed[bang].IsWhitespace)
            {
                bang--;
            }

            if (bang >= 0 && trimmed[bang].IsDelim('!'))
            {
                important = true;
                trimmed.RemoveRange(bang, trimmed.Count - bang);
                trimmed = Trim(trimmed);
            }
        }

        string normalized = name.StartsWith("--", StringComparison.Ordinal) ? name : CssText.ToAsciiLower(name);
        return new CssDeclaration(normalized, trimmed, important) { Line = line, Column = column };
    }
}
