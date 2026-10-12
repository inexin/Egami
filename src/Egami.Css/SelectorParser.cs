namespace Egami.Css;

/// <summary>Parses Selectors 4 selectors from component values. Returns null for an invalid selector; never throws.</summary>
internal sealed class SelectorParser
{
    // The pseudo-classes whose state the document knows: matched through ICssElement.MatchesPseudoClass.
    private static readonly HashSet<string> s_statePseudoClasses = new(StringComparer.Ordinal)
    {
        "hover", "active", "focus", "focus-visible", "focus-within", "target", "target-within", "link", "visited", "any-link",
        "local-link", "checked", "indeterminate", "default", "disabled", "enabled", "read-only", "read-write", "placeholder-shown",
        "required", "optional", "valid", "invalid", "in-range", "out-of-range", "user-valid", "user-invalid",
        "autofill", "playing", "paused", "seeking", "buffering", "stalled", "muted", "volume-locked", "fullscreen", "modal",
        "picture-in-picture", "popover-open", "open", "closed", "blank", "current", "past", "future",
    };

    private static readonly HashSet<string> s_userActionPseudoClasses = new(StringComparer.Ordinal)
    {
        "hover", "active", "focus", "focus-visible", "focus-within",
    };

    private static readonly HashSet<string> s_pseudoElements = new(StringComparer.Ordinal)
    {
        "before", "after", "first-line", "first-letter", "marker", "placeholder", "selection", "backdrop", "file-selector-button",
        "grammar-error", "spelling-error", "target-text", "cue", "details-content",
    };

    private readonly CssNamespaces? _namespaces;

    private SelectorParser(CssNamespaces? namespaces)
    {
        _namespaces = namespaces;
    }

    public static ComplexSelector[]? ParseList(IReadOnlyList<CssComponentValue> items, CssNamespaces? namespaces, List<(int Start, int End)>? ranges = null)
    {
        SelectorParser parser = new(namespaces);
        return parser.ParseList(items, nested: false, forgiving: false, relative: false, inHas: false, ranges);
    }

    public static ComplexSelector[]? ParseRelativeList(IReadOnlyList<CssComponentValue> items, CssNamespaces? namespaces)
    {
        SelectorParser parser = new(namespaces);
        return parser.ParseList(items, nested: true, forgiving: false, relative: true, inHas: true, null);
    }

    private ComplexSelector[]? ParseList(IReadOnlyList<CssComponentValue> items, bool nested, bool forgiving, bool relative, bool inHas, List<(int Start, int End)>? ranges)
    {
        List<ComplexSelector> selectors = [];
        int start = 0;
        for (int i = 0; i <= items.Count; i++)
        {
            if (i < items.Count && !items[i].IsToken(CssTokenKind.Comma))
            {
                continue;
            }

            ComplexSelector? selector = ParseComplex(items, start, i, nested, relative, inHas);
            if (selector is null)
            {
                if (!forgiving)
                {
                    return null;
                }
            }
            else
            {
                selectors.Add(selector);
                ranges?.Add((start, i));
            }

            start = i + 1;
        }

        return [.. selectors];
    }

    private ComplexSelector? ParseComplex(IReadOnlyList<CssComponentValue> items, int start, int end, bool nested, bool relative, bool inHas)
    {
        Cursor cursor = new(items, start, end);
        cursor.SkipWhitespace();
        List<Compound> compounds = [];
        List<Combinator> combinators = [];
        Combinator leading = Combinator.Descendant;
        if (relative && cursor.TryCombinator(out Combinator first))
        {
            leading = first;
            cursor.SkipWhitespace();
        }

        string? pseudoElement = null;
        CssSpecificity specificity = default;
        while (true)
        {
            if (pseudoElement is not null)
            {
                return null;
            }

            Compound? compound = ParseCompound(ref cursor, nested || relative, inHas, ref specificity, out pseudoElement);
            if (compound is null)
            {
                return null;
            }

            if (pseudoElement is not null && (nested || relative))
            {
                return null;
            }

            compounds.Add(compound);
            bool whitespace = cursor.SkipWhitespace();
            if (cursor.AtEnd)
            {
                break;
            }

            if (cursor.TryCombinator(out Combinator combinator))
            {
                cursor.SkipWhitespace();
                if (cursor.AtEnd)
                {
                    return null;
                }
            }
            else if (whitespace)
            {
                combinator = Combinator.Descendant;
            }
            else
            {
                return null;
            }

            combinators.Add(combinator);
        }

        if (relative)
        {
            compounds.Insert(0, new Compound { IsAnchor = true });
            combinators.Insert(0, leading);
        }

        compounds.Reverse();
        combinators.Reverse();
        return new ComplexSelector
        {
            Compounds = [.. compounds],
            Combinators = [.. combinators],
            PseudoElement = pseudoElement,
            Specificity = specificity,
        };
    }

    // A compound selector at the cursor; null when invalid. Stops at whitespace, a combinator or the end.
    private Compound? ParseCompound(ref Cursor cursor, bool nested, bool inHas, ref CssSpecificity specificity, out string? pseudoElement)
    {
        pseudoElement = null;
        Compound compound = new();
        bool hasType = false;
        int before = cursor.Position;
        if (!TryParseTypeSelector(ref cursor, compound, ref hasType))
        {
            return null;
        }

        if (hasType && compound.LocalName is not null)
        {
            specificity += new CssSpecificity(0, 0, 1);
        }

        List<SimpleSelector> simples = [];
        while (!cursor.AtEnd)
        {
            CssComponentValue item = cursor.Current;
            if (pseudoElement is not null)
            {
                // After a pseudo-element only user-action pseudo-classes may follow; they are checked on the originating element.
                if (item.IsToken(CssTokenKind.Colon) && cursor.Peek(1) is CssTokenValue { Token.Kind: CssTokenKind.Ident } next
                    && s_userActionPseudoClasses.Contains(CssText.ToAsciiLower(next.Token.Value)))
                {
                    simples.Add(new StateSelector(CssText.ToAsciiLower(next.Token.Value)));
                    specificity += new CssSpecificity(0, 1, 0);
                    cursor.Advance(2);
                    continue;
                }

                if (item.IsWhitespace || IsCombinator(item))
                {
                    break;
                }

                return null;
            }

            if (item is CssTokenValue { Token: { Kind: CssTokenKind.Hash } hash })
            {
                if (!hash.IsIdHash)
                {
                    return null;
                }

                simples.Add(new IdSelector(hash.Value));
                specificity += new CssSpecificity(1, 0, 0);
                cursor.Advance(1);
            }
            else if (item.IsDelim('.'))
            {
                if (cursor.Peek(1) is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } name })
                {
                    return null;
                }

                simples.Add(new ClassSelector(name.Value));
                specificity += new CssSpecificity(0, 1, 0);
                cursor.Advance(2);
            }
            else if (item is CssSimpleBlock { OpenToken: '[' } block)
            {
                AttributeSelector? attribute = ParseAttribute(block.Content);
                if (attribute is null)
                {
                    return null;
                }

                simples.Add(attribute);
                specificity += new CssSpecificity(0, 1, 0);
                cursor.Advance(1);
            }
            else if (item.IsToken(CssTokenKind.Colon))
            {
                if (cursor.Peek(1) is { } second && second.IsToken(CssTokenKind.Colon))
                {
                    pseudoElement = ParsePseudoElement(cursor.Peek(2));
                    if (pseudoElement is null)
                    {
                        return null;
                    }

                    specificity += new CssSpecificity(0, 0, 1);
                    cursor.Advance(3);
                    continue;
                }

                CssComponentValue? name = cursor.Peek(1);
                if (name is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
                {
                    string lower = CssText.ToAsciiLower(ident.Value);
                    if (lower is "before" or "after" or "first-line" or "first-letter")
                    {
                        pseudoElement = lower;
                        specificity += new CssSpecificity(0, 0, 1);
                        cursor.Advance(2);
                        continue;
                    }

                    SimpleSelector? pseudo = ParsePseudoClass(lower);
                    if (pseudo is null)
                    {
                        return null;
                    }

                    simples.Add(pseudo);
                    specificity += new CssSpecificity(0, 1, 0);
                    cursor.Advance(2);
                }
                else if (name is CssFunction function)
                {
                    SimpleSelector? pseudo = ParseFunctionalPseudoClass(function, inHas, ref specificity);
                    if (pseudo is null)
                    {
                        return null;
                    }

                    simples.Add(pseudo);
                    cursor.Advance(2);
                }
                else
                {
                    return null;
                }
            }
            else
            {
                break;
            }
        }

        if (cursor.Position == before)
        {
            return null;
        }

        if (!hasType && !nested && _namespaces?.DefaultNamespace is { } defaultNamespace)
        {
            compound.NamespaceMode = defaultNamespace.Length == 0 ? NamespaceMode.None : NamespaceMode.Specific;
            compound.NamespaceUri = defaultNamespace;
        }

        compound.Simples = [.. simples];
        return compound;
    }

    private static bool IsCombinator(CssComponentValue item) => item.IsDelim('>') || item.IsDelim('+') || item.IsDelim('~');

    // ns|E, *|E, |E, E, ns|*, *|*, |*, *.
    private bool TryParseTypeSelector(ref Cursor cursor, Compound compound, ref bool hasType)
    {
        CssComponentValue? first = cursor.Peek(0);
        CssComponentValue? second = cursor.Peek(1);
        CssComponentValue? third = cursor.Peek(2);
        bool firstIsName = first is CssTokenValue { Token.Kind: CssTokenKind.Ident } || (first is not null && first.IsDelim('*'));
        if (first is not null && first.IsDelim('|'))
        {
            if (!IsNameOrStar(second))
            {
                return false;
            }

            SetName(compound, second!);
            compound.NamespaceMode = NamespaceMode.None;
            hasType = true;
            cursor.Advance(2);
            return true;
        }

        if (!firstIsName)
        {
            return true;
        }

        if (second is not null && second.IsDelim('|') && !(third is not null && third.IsDelim('=')))
        {
            if (!IsNameOrStar(third))
            {
                return false;
            }

            if (first!.IsDelim('*'))
            {
                compound.NamespaceMode = NamespaceMode.Any;
            }
            else
            {
                string prefix = ((CssTokenValue)first).Token.Value;
                if (_namespaces is null || !_namespaces.TryGetNamespace(prefix, out string uri))
                {
                    return false;
                }

                compound.NamespaceMode = uri.Length == 0 ? NamespaceMode.None : NamespaceMode.Specific;
                compound.NamespaceUri = uri;
            }

            SetName(compound, third!);
            hasType = true;
            cursor.Advance(3);
            return true;
        }

        SetName(compound, first!);
        if (_namespaces?.DefaultNamespace is { } defaultNamespace)
        {
            compound.NamespaceMode = defaultNamespace.Length == 0 ? NamespaceMode.None : NamespaceMode.Specific;
            compound.NamespaceUri = defaultNamespace;
        }

        hasType = true;
        cursor.Advance(1);
        return true;

        static bool IsNameOrStar(CssComponentValue? value) => value is CssTokenValue { Token.Kind: CssTokenKind.Ident } || (value is not null && value.IsDelim('*'));

        static void SetName(Compound compound, CssComponentValue value)
        {
            if (value is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
            {
                compound.LocalName = ident.Value;
                compound.LocalNameLower = CssText.ToAsciiLower(ident.Value);
            }
        }
    }

    private AttributeSelector? ParseAttribute(IReadOnlyList<CssComponentValue> content)
    {
        Cursor cursor = new(content, 0, content.Count);
        cursor.SkipWhitespace();
        CssComponentValue? first = cursor.Peek(0);
        CssComponentValue? second = cursor.Peek(1);
        CssComponentValue? third = cursor.Peek(2);
        NamespaceMode mode = NamespaceMode.None;
        string? namespaceUri = null;
        string name;
        if (first is not null && first.IsDelim('|') && second is CssTokenValue { Token: { Kind: CssTokenKind.Ident } afterBar })
        {
            name = afterBar.Value;
            cursor.Advance(2);
        }
        else if (first is not null && first.IsDelim('*') && second is not null && second.IsDelim('|') && third is CssTokenValue { Token: { Kind: CssTokenKind.Ident } anyName })
        {
            mode = NamespaceMode.Any;
            name = anyName.Value;
            cursor.Advance(3);
        }
        else if (first is CssTokenValue { Token: { Kind: CssTokenKind.Ident } firstIdent })
        {
            if (second is not null && second.IsDelim('|') && third is CssTokenValue { Token: { Kind: CssTokenKind.Ident } prefixedName })
            {
                if (_namespaces is null || !_namespaces.TryGetNamespace(firstIdent.Value, out string uri))
                {
                    return null;
                }

                mode = uri.Length == 0 ? NamespaceMode.None : NamespaceMode.Specific;
                namespaceUri = uri;
                name = prefixedName.Value;
                cursor.Advance(3);
            }
            else
            {
                name = firstIdent.Value;
                cursor.Advance(1);
            }
        }
        else
        {
            return null;
        }

        cursor.SkipWhitespace();
        if (cursor.AtEnd)
        {
            return new AttributeSelector { LocalName = name, LocalNameLower = CssText.ToAsciiLower(name), NamespaceMode = mode, NamespaceUri = namespaceUri };
        }

        AttributeOperator op;
        CssComponentValue current = cursor.Current;
        if (current.IsDelim('='))
        {
            op = AttributeOperator.Equals;
            cursor.Advance(1);
        }
        else
        {
            op = current is CssTokenValue { Token.Kind: CssTokenKind.Delim } delim ? delim.Token.Value switch
            {
                "~" => AttributeOperator.Includes,
                "|" => AttributeOperator.DashMatch,
                "^" => AttributeOperator.Prefix,
                "$" => AttributeOperator.Suffix,
                "*" => AttributeOperator.Substring,
                _ => AttributeOperator.Exists,
            } : AttributeOperator.Exists;
            if (op == AttributeOperator.Exists || cursor.Peek(1) is not { } equals || !equals.IsDelim('='))
            {
                return null;
            }

            cursor.Advance(2);
        }

        cursor.SkipWhitespace();
        if (cursor.AtEnd || cursor.Current is not CssTokenValue { Token: { Kind: CssTokenKind.Ident or CssTokenKind.String } valueToken })
        {
            return null;
        }

        cursor.Advance(1);
        cursor.SkipWhitespace();
        AttributeCase flag = AttributeCase.Default;
        if (!cursor.AtEnd)
        {
            if (cursor.Current is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } modifier })
            {
                return null;
            }

            if (CssText.EqualsIgnoreAsciiCase(modifier.Value, "i"))
            {
                flag = AttributeCase.Insensitive;
            }
            else if (CssText.EqualsIgnoreAsciiCase(modifier.Value, "s"))
            {
                flag = AttributeCase.Sensitive;
            }
            else
            {
                return null;
            }

            cursor.Advance(1);
            cursor.SkipWhitespace();
            if (!cursor.AtEnd)
            {
                return null;
            }
        }

        return new AttributeSelector
        {
            LocalName = name,
            LocalNameLower = CssText.ToAsciiLower(name),
            NamespaceMode = mode,
            NamespaceUri = namespaceUri,
            Operator = op,
            Value = valueToken.Value,
            Case = flag,
        };
    }

    private string? ParsePseudoElement(CssComponentValue? value)
    {
        if (value is CssFunction slotted && slotted.NameIs("slotted"))
        {
            // ::slotted(<compound-selector>): Shadow DOM, which ICssElement does not model; it parses and never matches.
            ComplexSelector[]? argument = ParseList(slotted.Arguments, nested: true, forgiving: false, relative: false, inHas: false, null);
            return argument is [{ Compounds.Length: 1, PseudoElement: null }] ? "slotted(" + CssComponentValue.Serialize(CssSyntax.Trim(slotted.Arguments)) + ")" : null;
        }

        if (value is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
        {
            string lower = CssText.ToAsciiLower(ident.Value);
            return s_pseudoElements.Contains(lower) || lower.StartsWith("-webkit-", StringComparison.Ordinal) ? lower : null;
        }

        if (value is CssFunction function && (function.NameIs("highlight") || function.NameIs("part")))
        {
            Cursor cursor = new(function.Arguments, 0, function.Arguments.Count);
            cursor.SkipWhitespace();
            int count = 0;
            while (!cursor.AtEnd)
            {
                if (cursor.Current is not CssTokenValue { Token.Kind: CssTokenKind.Ident })
                {
                    return null;
                }

                count++;
                cursor.Advance(1);
                cursor.SkipWhitespace();
            }

            if (count == 0 || (count > 1 && function.NameIs("highlight")))
            {
                return null;
            }

            return CssText.ToAsciiLower(function.Name) + "(" + CssComponentValue.Serialize(CssSyntax.Trim(function.Arguments)) + ")";
        }

        return null;
    }

    private static SimpleSelector? ParsePseudoClass(string name)
    {
        return name switch
        {
            "root" => new StructuralSelector(StructuralKind.Root),
            "empty" => new StructuralSelector(StructuralKind.Empty),
            "first-child" => new StructuralSelector(StructuralKind.FirstChild),
            "last-child" => new StructuralSelector(StructuralKind.LastChild),
            "only-child" => new StructuralSelector(StructuralKind.OnlyChild),
            "first-of-type" => new StructuralSelector(StructuralKind.FirstOfType),
            "last-of-type" => new StructuralSelector(StructuralKind.LastOfType),
            "only-of-type" => new StructuralSelector(StructuralKind.OnlyOfType),
            "scope" => new StructuralSelector(StructuralKind.Scope),
            "defined" => new StructuralSelector(StructuralKind.Defined),
            _ => s_statePseudoClasses.Contains(name) ? new StateSelector(name) : null,
        };
    }

    private SimpleSelector? ParseFunctionalPseudoClass(CssFunction function, bool inHas, ref CssSpecificity specificity)
    {
        string name = CssText.ToAsciiLower(function.Name);
        IReadOnlyList<CssComponentValue> arguments = function.Arguments;
        switch (name)
        {
            case "is" or "where":
                {
                    // A forgiving list still has to be <any-value>: no bad strings, bad urls or unmatched closing brackets.
                    if (HasBadTokens(arguments))
                    {
                        return null;
                    }

                    ComplexSelector[]? list = ParseList(arguments, nested: true, forgiving: true, relative: false, inHas, null);
                    if (list is null)
                    {
                        return null;
                    }

                    if (name == "is")
                    {
                        specificity += Max(list);
                    }

                    return new LogicalSelector(name == "is" ? LogicalKind.Is : LogicalKind.Where, list);
                }

            case "not":
                {
                    ComplexSelector[]? list = ParseList(arguments, nested: true, forgiving: false, relative: false, inHas, null);
                    if (list is null || list.Length == 0)
                    {
                        return null;
                    }

                    specificity += Max(list);
                    return new LogicalSelector(LogicalKind.Not, list);
                }

            case "has":
                {
                    if (inHas)
                    {
                        return null;
                    }

                    ComplexSelector[]? list = ParseList(arguments, nested: true, forgiving: false, relative: true, inHas: true, null);
                    if (list is null || list.Length == 0)
                    {
                        return null;
                    }

                    specificity += Max(list);
                    return new HasSelector(list);
                }

            case "nth-child" or "nth-last-child" or "nth-of-type" or "nth-last-of-type":
                {
                    int position = 0;
                    if (!AnPlusB.TryParse(arguments, ref position, out int a, out int b))
                    {
                        return null;
                    }

                    Cursor cursor = new(arguments, position, arguments.Count);
                    bool whitespace = cursor.SkipWhitespace();
                    ComplexSelector[]? of = null;
                    if (!cursor.AtEnd)
                    {
                        if (!whitespace || name is "nth-of-type" or "nth-last-of-type" || !cursor.Current.IsIdent("of") || cursor.Peek(1) is not { IsWhitespace: true })
                        {
                            return null;
                        }

                        List<CssComponentValue> rest = [];
                        for (int i = cursor.Position + 1; i < arguments.Count; i++)
                        {
                            rest.Add(arguments[i]);
                        }

                        of = ParseList(rest, nested: true, forgiving: false, relative: false, inHas, null);
                        if (of is null || of.Length == 0)
                        {
                            return null;
                        }

                        specificity += Max(of);
                    }

                    specificity += new CssSpecificity(0, 1, 0);
                    return new NthSelector { A = a, B = b, Last = name.Contains("last", StringComparison.Ordinal), OfType = name.EndsWith("of-type", StringComparison.Ordinal), Of = of };
                }

            case "lang":
                {
                    List<string> ranges = [];
                    Cursor cursor = new(arguments, 0, arguments.Count);
                    while (true)
                    {
                        cursor.SkipWhitespace();
                        if (cursor.AtEnd || cursor.Current is not CssTokenValue { Token: { Kind: CssTokenKind.Ident or CssTokenKind.String } range })
                        {
                            return null;
                        }

                        ranges.Add(range.Value);
                        cursor.Advance(1);
                        cursor.SkipWhitespace();
                        if (cursor.AtEnd)
                        {
                            break;
                        }

                        if (!cursor.Current.IsToken(CssTokenKind.Comma))
                        {
                            return null;
                        }

                        cursor.Advance(1);
                    }

                    specificity += new CssSpecificity(0, 1, 0);
                    return new LangSelector([.. ranges]);
                }

            case "dir":
                {
                    Cursor cursor = new(arguments, 0, arguments.Count);
                    cursor.SkipWhitespace();
                    if (cursor.AtEnd || cursor.Current is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } direction })
                    {
                        return null;
                    }

                    cursor.Advance(1);
                    cursor.SkipWhitespace();
                    if (!cursor.AtEnd)
                    {
                        return null;
                    }

                    specificity += new CssSpecificity(0, 1, 0);
                    bool? rtl = CssText.ToAsciiLower(direction.Value) switch { "ltr" => false, "rtl" => true, _ => null };
                    return new DirSelector(rtl);
                }

            default:
                return null;
        }
    }

    private static bool HasBadTokens(IReadOnlyList<CssComponentValue> values)
    {
        foreach (CssComponentValue value in values)
        {
            switch (value)
            {
                case CssTokenValue { Token.Kind: CssTokenKind.BadString or CssTokenKind.BadUrl or CssTokenKind.RightParenthesis or CssTokenKind.RightSquare or CssTokenKind.RightCurly }:
                    return true;
                case CssFunction function when HasBadTokens(function.Arguments):
                    return true;
                case CssSimpleBlock block when HasBadTokens(block.Content):
                    return true;
            }
        }

        return false;
    }

    private static CssSpecificity Max(ComplexSelector[] list)
    {
        CssSpecificity max = default;
        foreach (ComplexSelector selector in list)
        {
            if (selector.Specificity > max)
            {
                max = selector.Specificity;
            }
        }

        return max;
    }

    /// <summary>A position in a slice of component values.</summary>
    private struct Cursor(IReadOnlyList<CssComponentValue> items, int start, int end)
    {
        private readonly IReadOnlyList<CssComponentValue> _items = items;
        private readonly int _end = end;

        public int Position { get; private set; } = start;

        public readonly bool AtEnd => Position >= _end;

        public readonly CssComponentValue Current => _items[Position];

        public readonly CssComponentValue? Peek(int offset) => Position + offset < _end ? _items[Position + offset] : null;

        public void Advance(int count) => Position += count;

        public bool SkipWhitespace()
        {
            bool any = false;
            while (!AtEnd && _items[Position].IsWhitespace)
            {
                Position++;
                any = true;
            }

            return any;
        }

        public bool TryCombinator(out Combinator combinator)
        {
            combinator = Combinator.Descendant;
            if (AtEnd)
            {
                return false;
            }

            CssComponentValue item = Current;
            if (item.IsDelim('>'))
            {
                combinator = Combinator.Child;
            }
            else if (item.IsDelim('+'))
            {
                combinator = Combinator.NextSibling;
            }
            else if (item.IsDelim('~'))
            {
                combinator = Combinator.SubsequentSibling;
            }
            else
            {
                return false;
            }

            Position++;
            return true;
        }
    }
}
