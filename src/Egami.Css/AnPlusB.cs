namespace Egami.Css;

/// <summary>The An+B microsyntax (CSS Syntax 3 section 6), over tokens.</summary>
internal static class AnPlusB
{
    /// <summary>Parses all of <paramref name="css"/> as An+B.</summary>
    public static bool TryParse(string css, out int a, out int b)
    {
        List<CssComponentValue> items = CssSyntax.ParseComponentValues(css, CssParseOptions.Default);
        int position = 0;
        if (!TryParse(items, ref position, out a, out b))
        {
            return false;
        }

        while (position < items.Count && items[position].IsWhitespace)
        {
            position++;
        }

        return position == items.Count;
    }

    /// <summary>Parses An+B at <paramref name="position"/>, skipping leading whitespace, and leaves the position after it.</summary>
    public static bool TryParse(IReadOnlyList<CssComponentValue> items, ref int position, out int a, out int b)
    {
        a = 0;
        b = 0;
        int i = position;
        while (i < items.Count && items[i].IsWhitespace)
        {
            i++;
        }

        if (i >= items.Count || items[i] is not CssTokenValue { Token: var token })
        {
            return false;
        }

        i++;
        switch (token.Kind)
        {
            case CssTokenKind.Number:
                if (!token.IsInteger)
                {
                    return false;
                }

                b = Clamp(token.Number);
                position = i;
                return true;
            case CssTokenKind.Dimension:
                if (!token.IsInteger)
                {
                    return false;
                }

                a = Clamp(token.Number);
                return AfterN(token.Unit, items, ref i, ref position, ref b);
            case CssTokenKind.Ident:
                {
                    string value = token.Value;
                    if (CssText.EqualsIgnoreAsciiCase(value, "odd"))
                    {
                        a = 2;
                        b = 1;
                        position = i;
                        return true;
                    }

                    if (CssText.EqualsIgnoreAsciiCase(value, "even"))
                    {
                        a = 2;
                        position = i;
                        return true;
                    }

                    if (value.StartsWith('-'))
                    {
                        a = -1;
                        return AfterN(value[1..], items, ref i, ref position, ref b);
                    }

                    a = 1;
                    return AfterN(value, items, ref i, ref position, ref b);
                }

            case CssTokenKind.Delim when token.IsDelim('+'):
                if (i >= items.Count || items[i] is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } || ident.Value.StartsWith('-'))
                {
                    return false;
                }

                i++;
                a = 1;
                return AfterN(ident.Value, items, ref i, ref position, ref b);
            default:
                return false;
        }
    }

    // The rest after A: "n" then an optional B, "n-" then a signless integer, or "n-<digits>".
    private static bool AfterN(string unit, IReadOnlyList<CssComponentValue> items, ref int i, ref int position, ref int b)
    {
        if (unit.Length == 0 || (unit[0] | 0x20) != 'n')
        {
            return false;
        }

        if (unit.Length == 1)
        {
            int j = i;
            SkipWhitespace(items, ref j);
            if (j < items.Count && items[j] is CssTokenValue { Token: var next })
            {
                if (next.Kind == CssTokenKind.Number && next.IsInteger && next.Value.Length > 0 && next.Value[0] is '+' or '-')
                {
                    b = Clamp(next.Number);
                    position = j + 1;
                    return true;
                }

                if (next.IsDelim('+') || next.IsDelim('-'))
                {
                    int k = j + 1;
                    SkipWhitespace(items, ref k);
                    if (k < items.Count && items[k] is CssTokenValue { Token: { Kind: CssTokenKind.Number, IsInteger: true } signless } && signless.Value.Length > 0 && CssText.IsDigit(signless.Value[0]))
                    {
                        b = Clamp(next.IsDelim('-') ? -signless.Number : signless.Number);
                        position = k + 1;
                        return true;
                    }

                    return false;
                }
            }

            position = i;
            return true;
        }

        if (unit[1] != '-')
        {
            return false;
        }

        if (unit.Length == 2)
        {
            int j = i;
            SkipWhitespace(items, ref j);
            if (j < items.Count && items[j] is CssTokenValue { Token: { Kind: CssTokenKind.Number, IsInteger: true } signless } && signless.Value.Length > 0 && CssText.IsDigit(signless.Value[0]))
            {
                b = Clamp(-signless.Number);
                position = j + 1;
                return true;
            }

            return false;
        }

        double digits = 0;
        for (int k = 2; k < unit.Length; k++)
        {
            if (!CssText.IsDigit(unit[k]))
            {
                return false;
            }

            digits = (digits * 10) + (unit[k] - '0');
        }

        b = Clamp(-digits);
        position = i;
        return true;
    }

    private static void SkipWhitespace(IReadOnlyList<CssComponentValue> items, ref int i)
    {
        while (i < items.Count && items[i].IsWhitespace)
        {
            i++;
        }
    }

    private static int Clamp(double value) => value >= int.MaxValue ? int.MaxValue : value <= int.MinValue ? int.MinValue : (int)value;
}
