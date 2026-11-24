namespace Egami.Css;

/// <summary>
/// An <c>@keyframes</c> rule (CSS Animations 1): a named list of keyframes an <c>animation-name</c> refers to. The
/// <c>-webkit-keyframes</c> spelling parses to the same rule.
/// </summary>
/// <remarks>
/// The rule keeps the keyframes as written, in order. Several keyframes may share an offset; CSS Animations 1 lets their
/// declarations cascade, the later one winning per property. When several <c>@keyframes</c> rules share a name, the last one
/// in the document wins.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="keyframes"/>
/// </example>
public sealed class CssKeyframesRule : CssRule
{
    internal CssKeyframesRule(string name, IReadOnlyList<CssKeyframe> keyframes)
    {
        Name = name;
        Keyframes = keyframes;
    }

    /// <summary>Gets the name, as written: case matters, as for every custom identifier.</summary>
    public string Name { get; }

    /// <summary>Gets the keyframes, in order. Keyframes whose selector is invalid are dropped.</summary>
    public IReadOnlyList<CssKeyframe> Keyframes { get; }

    // The prelude: a <custom-ident> other than 'none' and the CSS-wide keywords, or a string.
    internal static string? ParseName(IReadOnlyList<CssComponentValue> prelude)
    {
        if (prelude.Count != 1 || prelude[0] is not CssTokenValue { Token: var token })
        {
            return null;
        }

        if (token.Kind == CssTokenKind.String)
        {
            return token.Value;
        }

        if (token.Kind != CssTokenKind.Ident)
        {
            return null;
        }

        string lower = CssText.ToAsciiLower(token.Value);
        return lower is "none" or "inherit" or "initial" or "unset" or "revert" or "revert-layer" or "default" ? null : token.Value;
    }
}

/// <summary>One keyframe of an <c>@keyframes</c> rule: its offsets and declarations, such as <c>from, 50% { opacity: 0 }</c>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="keyframes"/>
/// </example>
public sealed class CssKeyframe
{
    internal CssKeyframe(IReadOnlyList<double> offsets, IReadOnlyList<CssDeclaration> declarations)
    {
        Offsets = offsets;
        Declarations = declarations;
    }

    /// <summary>
    /// Gets the offsets the keyframe selector names, as fractions from 0 to 1, in the order written: <c>from</c> is 0,
    /// <c>to</c> is 1 and <c>50%</c> is 0.5.
    /// </summary>
    public IReadOnlyList<double> Offsets { get; }

    /// <summary>
    /// Gets the declarations, in order. Declarations marked <c>!important</c> are dropped, as CSS Animations 1 says; the
    /// consumer ignores the properties that cannot be animated.
    /// </summary>
    public IReadOnlyList<CssDeclaration> Declarations { get; }

    /// <summary>Gets the 1-based line the keyframe starts on, or 0 when unknown.</summary>
    public int Line { get; internal init; }

    /// <summary>Gets the 1-based column the keyframe starts at, or 0 when unknown.</summary>
    public int Column { get; internal init; }

    // <keyframe-selector>#: from, to or a percentage from 0% to 100%. Null when any selector is invalid.
    internal static List<double>? ParseSelectors(IReadOnlyList<CssComponentValue> prelude)
    {
        List<double> offsets = [];
        bool expectValue = true;
        foreach (CssComponentValue item in prelude)
        {
            if (item.IsWhitespace)
            {
                continue;
            }

            if (!expectValue)
            {
                if (!item.IsToken(CssTokenKind.Comma))
                {
                    return null;
                }

                expectValue = true;
                continue;
            }

            if (item is not CssTokenValue { Token: var token })
            {
                return null;
            }

            double offset;
            if (token.IsIdent("from"))
            {
                offset = 0;
            }
            else if (token.IsIdent("to"))
            {
                offset = 1;
            }
            else if (token.Kind == CssTokenKind.Percentage && token.Number >= 0 && token.Number <= 100)
            {
                offset = token.Number / 100;
            }
            else
            {
                return null;
            }

            offsets.Add(offset);
            expectValue = false;
        }

        return offsets.Count == 0 || expectValue ? null : offsets;
    }
}
