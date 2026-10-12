namespace Egami.Css;

internal enum Combinator : byte
{
    Descendant,
    Child,
    NextSibling,
    SubsequentSibling,
}

internal enum NamespaceMode : byte
{
    Any,
    None,
    Specific,
}

/// <summary>What matching needs besides the element: the scoping root for <c>:scope</c> and the anchor of a <c>:has()</c>.</summary>
internal readonly struct MatchContext(ICssElement? scope, ICssElement? anchor)
{
    public ICssElement? Scope { get; } = scope;

    public ICssElement? Anchor { get; } = anchor;
}

/// <summary>A compound selector: an optional type selector and the simple selectors that follow it.</summary>
internal sealed class Compound
{
    public string? LocalName { get; set; }

    public string? LocalNameLower { get; set; }

    public NamespaceMode NamespaceMode { get; set; }

    public string? NamespaceUri { get; set; }

    public SimpleSelector[] Simples { get; set; } = [];

    // The leftmost compound of a relative selector in :has(): matches only the anchor element.
    public bool IsAnchor { get; init; }

    public bool Matches(ICssElement element, in MatchContext context)
    {
        if (IsAnchor)
        {
            return ReferenceEquals(element, context.Anchor);
        }

        if (LocalName is not null)
        {
            string name = element.LocalName;
            if (!string.Equals(name, element.IsHtml ? LocalNameLower : LocalName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        switch (NamespaceMode)
        {
            case NamespaceMode.None:
                if (!string.IsNullOrEmpty(element.NamespaceUri))
                {
                    return false;
                }

                break;
            case NamespaceMode.Specific:
                if (!string.Equals(element.NamespaceUri, NamespaceUri, StringComparison.Ordinal))
                {
                    return false;
                }

                break;
        }

        SimpleSelector[] simples = Simples;
        for (int i = 0; i < simples.Length; i++)
        {
            if (!simples[i].Matches(element, context))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A complex selector, stored right to left for matching: <c>Compounds[0]</c> is the subject.</summary>
internal sealed class ComplexSelector
{
    public required Compound[] Compounds { get; init; }

    // Combinators[i] relates Compounds[i] to Compounds[i + 1], the compound on its left.
    public required Combinator[] Combinators { get; init; }

    public string? PseudoElement { get; init; }

    public CssSpecificity Specificity { get; init; }

    public bool HasTreeCombinator
    {
        get
        {
            foreach (Combinator combinator in Combinators)
            {
                if (combinator is Combinator.Descendant or Combinator.Child)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>Right-to-left matching, with the early exits that keep descendant combinators from backtracking exponentially.</summary>
internal static class SelectorMatcher
{
    private enum Result : byte
    {
        Matches,
        FailsLocally,
        FailsAllSiblings,
        FailsCompletely,
    }

    public static bool Matches(ComplexSelector selector, ICssElement element, in MatchContext context)
    {
        return Match(selector, 0, element, context) == Result.Matches;
    }

    public static bool MatchesAny(ComplexSelector[] selectors, ICssElement element, in MatchContext context)
    {
        for (int i = 0; i < selectors.Length; i++)
        {
            if (Match(selectors[i], 0, element, context) == Result.Matches)
            {
                return true;
            }
        }

        return false;
    }

    private static Result Match(ComplexSelector selector, int index, ICssElement element, in MatchContext context)
    {
        if (!selector.Compounds[index].Matches(element, context))
        {
            return Result.FailsLocally;
        }

        if (index == selector.Compounds.Length - 1)
        {
            return Result.Matches;
        }

        switch (selector.Combinators[index])
        {
            case Combinator.Descendant:
                for (ICssElement? ancestor = element.Parent; ancestor is not null; ancestor = ancestor.Parent)
                {
                    Result result = Match(selector, index + 1, ancestor, context);
                    if (result is Result.Matches or Result.FailsCompletely)
                    {
                        return result;
                    }
                }

                return Result.FailsCompletely;
            case Combinator.Child:
                {
                    ICssElement? parent = element.Parent;
                    if (parent is null)
                    {
                        return Result.FailsCompletely;
                    }

                    Result result = Match(selector, index + 1, parent, context);
                    return result is Result.Matches or Result.FailsCompletely ? result : Result.FailsAllSiblings;
                }

            case Combinator.NextSibling:
                {
                    ICssElement? sibling = element.PreviousSibling;
                    return sibling is null ? Result.FailsAllSiblings : Match(selector, index + 1, sibling, context);
                }

            default:
                for (ICssElement? sibling = element.PreviousSibling; sibling is not null; sibling = sibling.PreviousSibling)
                {
                    Result result = Match(selector, index + 1, sibling, context);
                    if (result != Result.FailsLocally)
                    {
                        return result;
                    }
                }

                return Result.FailsAllSiblings;
        }
    }

    // :has(): whether some element related to the anchor as the relative selectors say matches one of them.
    public static bool MatchesRelative(ComplexSelector[] selectors, ICssElement anchor, ICssElement? scope)
    {
        MatchContext context = new(scope, anchor);
        foreach (ComplexSelector selector in selectors)
        {
            Combinator leading = selector.Combinators[^1];
            bool simple = selector.Compounds.Length == 2;
            if (leading is Combinator.Descendant or Combinator.Child)
            {
                if (simple && leading == Combinator.Child)
                {
                    for (ICssElement? child = anchor.FirstChild; child is not null; child = child.NextSibling)
                    {
                        if (Matches(selector, child, context))
                        {
                            return true;
                        }
                    }

                    continue;
                }

                for (ICssElement? node = anchor.FirstChild; node is not null; node = NextInSubtree(node, anchor))
                {
                    if (Matches(selector, node, context))
                    {
                        return true;
                    }
                }
            }
            else
            {
                bool subtrees = selector.HasTreeCombinator;
                for (ICssElement? sibling = anchor.NextSibling; sibling is not null; sibling = sibling.NextSibling)
                {
                    if (Matches(selector, sibling, context))
                    {
                        return true;
                    }

                    if (subtrees)
                    {
                        for (ICssElement? node = sibling.FirstChild; node is not null; node = NextInSubtree(node, sibling))
                        {
                            if (Matches(selector, node, context))
                            {
                                return true;
                            }
                        }
                    }

                    if (simple && leading == Combinator.NextSibling)
                    {
                        break;
                    }
                }
            }
        }

        return false;
    }

    // The next element after node in document order that is still inside root's subtree.
    private static ICssElement? NextInSubtree(ICssElement node, ICssElement root)
    {
        if (node.FirstChild is { } child)
        {
            return child;
        }

        for (ICssElement? current = node; current is not null && !ReferenceEquals(current, root); current = current.Parent)
        {
            if (current.NextSibling is { } next)
            {
                return next;
            }
        }

        return null;
    }
}

/// <summary>A simple selector after the type selector: ID, class, attribute or pseudo-class.</summary>
internal abstract class SimpleSelector
{
    public abstract bool Matches(ICssElement element, in MatchContext context);
}

internal sealed class IdSelector(string id) : SimpleSelector
{
    public string Id { get; } = id;

    public override bool Matches(ICssElement element, in MatchContext context) => string.Equals(element.Id, Id, StringComparison.Ordinal);
}

internal sealed class ClassSelector(string name) : SimpleSelector
{
    public string Name { get; } = name;

    public override bool Matches(ICssElement element, in MatchContext context) => element.HasClass(Name);
}

internal enum AttributeOperator : byte
{
    Exists,
    Equals,
    Includes,
    DashMatch,
    Prefix,
    Suffix,
    Substring,
}

internal enum AttributeCase : byte
{
    Default,
    Insensitive,
    Sensitive,
}

internal sealed class AttributeSelector : SimpleSelector
{
    // The attributes whose values HTML compares ASCII case-insensitively in attribute selectors (HTML, "Case-sensitivity of selectors").
    private static readonly HashSet<string> s_htmlCaseInsensitive = new(StringComparer.Ordinal)
    {
        "accept", "accept-charset", "align", "alink", "axis", "bgcolor", "charset", "checked", "clear", "codetype", "color",
        "compact", "declare", "defer", "dir", "direction", "disabled", "enctype", "face", "frame", "hreflang", "http-equiv",
        "lang", "language", "link", "media", "method", "multiple", "nohref", "noresize", "noshade", "nowrap", "readonly", "rel",
        "rev", "rules", "scope", "scrolling", "selected", "shape", "target", "text", "type", "valign", "valuetype", "vlink",
    };

    public required string LocalName { get; init; }

    public required string LocalNameLower { get; init; }

    public NamespaceMode NamespaceMode { get; init; }

    public string? NamespaceUri { get; init; }

    public AttributeOperator Operator { get; init; }

    public string Value { get; init; } = "";

    public AttributeCase Case { get; init; }

    public override bool Matches(ICssElement element, in MatchContext context)
    {
        bool html = element.IsHtml;
        string name = html ? LocalNameLower : LocalName;
        if (NamespaceMode == NamespaceMode.Any)
        {
            foreach (CssAttribute attribute in element.Attributes)
            {
                if (string.Equals(attribute.LocalName, name, StringComparison.Ordinal) && MatchesValue(attribute.Value, html && attribute.NamespaceUri is null))
                {
                    return true;
                }
            }

            return false;
        }

        string? value = element.GetAttribute(name, NamespaceMode == NamespaceMode.Specific ? NamespaceUri : null);
        return value is not null && MatchesValue(value, html && NamespaceMode == NamespaceMode.None);
    }

    private bool MatchesValue(string actual, bool htmlNoNamespace)
    {
        if (Operator == AttributeOperator.Exists)
        {
            return true;
        }

        bool insensitive = Case == AttributeCase.Insensitive || (Case == AttributeCase.Default && htmlNoNamespace && s_htmlCaseInsensitive.Contains(LocalNameLower));
        ReadOnlySpan<char> a = actual;
        ReadOnlySpan<char> v = Value;
        switch (Operator)
        {
            case AttributeOperator.Equals:
                return Eq(a, v, insensitive);
            case AttributeOperator.Includes:
                if (v.Length == 0 || v.IndexOfAny(" \t\n\r\f") >= 0)
                {
                    return false;
                }

                while (a.Length > 0)
                {
                    int start = 0;
                    while (start < a.Length && CssText.IsWhitespace(a[start]))
                    {
                        start++;
                    }

                    a = a[start..];
                    int end = 0;
                    while (end < a.Length && !CssText.IsWhitespace(a[end]))
                    {
                        end++;
                    }

                    if (end > 0 && Eq(a[..end], v, insensitive))
                    {
                        return true;
                    }

                    a = a[end..];
                }

                return false;
            case AttributeOperator.DashMatch:
                return Eq(a, v, insensitive) || (a.Length > v.Length && a[v.Length] == '-' && Eq(a[..v.Length], v, insensitive));
            case AttributeOperator.Prefix:
                return v.Length > 0 && a.Length >= v.Length && Eq(a[..v.Length], v, insensitive);
            case AttributeOperator.Suffix:
                return v.Length > 0 && a.Length >= v.Length && Eq(a[(a.Length - v.Length)..], v, insensitive);
            default:
                if (v.Length == 0)
                {
                    return false;
                }

                return insensitive ? CssText.IndexOfIgnoreAsciiCase(a, v) >= 0 : a.Contains(v, StringComparison.Ordinal);
        }

        static bool Eq(ReadOnlySpan<char> x, ReadOnlySpan<char> y, bool insensitive)
        {
            return insensitive ? CssText.EqualsIgnoreAsciiCase(x, y) : x.SequenceEqual(y);
        }
    }
}

internal enum StructuralKind : byte
{
    Root,
    Empty,
    FirstChild,
    LastChild,
    OnlyChild,
    FirstOfType,
    LastOfType,
    OnlyOfType,
    Scope,
    Defined,
    Never,
}

internal sealed class StructuralSelector(StructuralKind kind) : SimpleSelector
{
    public StructuralKind Kind { get; } = kind;

    public override bool Matches(ICssElement element, in MatchContext context)
    {
        switch (Kind)
        {
            case StructuralKind.Root:
                return element.Parent is null;
            case StructuralKind.Empty:
                return element.IsEmpty;
            case StructuralKind.FirstChild:
                return element.PreviousSibling is null;
            case StructuralKind.LastChild:
                return element.NextSibling is null;
            case StructuralKind.OnlyChild:
                return element.PreviousSibling is null && element.NextSibling is null;
            case StructuralKind.FirstOfType:
                return FirstOfType(element, previous: true);
            case StructuralKind.LastOfType:
                return FirstOfType(element, previous: false);
            case StructuralKind.OnlyOfType:
                return FirstOfType(element, previous: true) && FirstOfType(element, previous: false);
            case StructuralKind.Scope:
                return context.Scope is not null ? ReferenceEquals(element, context.Scope) : element.Parent is null;
            case StructuralKind.Defined:
                return true;
            default:
                return false;
        }
    }

    private static bool FirstOfType(ICssElement element, bool previous)
    {
        for (ICssElement? sibling = previous ? element.PreviousSibling : element.NextSibling; sibling is not null; sibling = previous ? sibling.PreviousSibling : sibling.NextSibling)
        {
            if (SameType(sibling, element))
            {
                return false;
            }
        }

        return true;
    }

    public static bool SameType(ICssElement a, ICssElement b)
    {
        return string.Equals(a.LocalName, b.LocalName, StringComparison.Ordinal) && string.Equals(a.NamespaceUri ?? "", b.NamespaceUri ?? "", StringComparison.Ordinal);
    }
}

internal sealed class NthSelector : SimpleSelector
{
    public int A { get; init; }

    public int B { get; init; }

    public bool Last { get; init; }

    public bool OfType { get; init; }

    public ComplexSelector[]? Of { get; init; }

    public override bool Matches(ICssElement element, in MatchContext context)
    {
        if (Of is not null && !SelectorMatcher.MatchesAny(Of, element, context))
        {
            return false;
        }

        long index = 1;
        for (ICssElement? sibling = Last ? element.NextSibling : element.PreviousSibling; sibling is not null; sibling = Last ? sibling.NextSibling : sibling.PreviousSibling)
        {
            if (OfType ? StructuralSelector.SameType(sibling, element) : Of is null || SelectorMatcher.MatchesAny(Of, sibling, context))
            {
                index++;
            }
        }

        return MatchesIndex(A, B, index);
    }

    public static bool MatchesIndex(long a, long b, long index)
    {
        if (a == 0)
        {
            return index == b;
        }

        long difference = index - b;
        return difference % a == 0 && difference / a >= 0;
    }
}

internal enum LogicalKind : byte
{
    Is,
    Where,
    Not,
}

internal sealed class LogicalSelector(LogicalKind kind, ComplexSelector[] selectors) : SimpleSelector
{
    public LogicalKind Kind { get; } = kind;

    public ComplexSelector[] Selectors { get; } = selectors;

    public override bool Matches(ICssElement element, in MatchContext context)
    {
        bool any = SelectorMatcher.MatchesAny(Selectors, element, context);
        return Kind == LogicalKind.Not ? !any : any;
    }
}

internal sealed class HasSelector(ComplexSelector[] selectors) : SimpleSelector
{
    public ComplexSelector[] Selectors { get; } = selectors;

    public override bool Matches(ICssElement element, in MatchContext context) => SelectorMatcher.MatchesRelative(Selectors, element, context.Scope);
}

internal sealed class LangSelector(string[] ranges) : SimpleSelector
{
    public string[] Ranges { get; } = ranges;

    public override bool Matches(ICssElement element, in MatchContext context)
    {
        string? language = element.Language;
        if (language is null)
        {
            return false;
        }

        foreach (string range in Ranges)
        {
            if (MatchesRange(language, range))
            {
                return true;
            }
        }

        return false;
    }

    // Extended filtering (RFC 4647 section 3.3.2), as Selectors 4 :lang() uses it.
    public static bool MatchesRange(string tag, string range)
    {
        if (range.Length == 0)
        {
            return tag.Length == 0;
        }

        if (tag.Length == 0)
        {
            return false;
        }

        string[] r = range.Split('-');
        string[] t = tag.Split('-');
        if (r[0] != "*" && !CssText.EqualsIgnoreAsciiCase(r[0], t[0]))
        {
            return false;
        }

        int i = 1;
        int j = 1;
        while (i < r.Length)
        {
            if (r[i] == "*")
            {
                i++;
                continue;
            }

            if (r[i].Length == 0 || j >= t.Length)
            {
                return false;
            }

            if (CssText.EqualsIgnoreAsciiCase(r[i], t[j]))
            {
                i++;
                j++;
                continue;
            }

            if (t[j].Length == 1)
            {
                return false;
            }

            j++;
        }

        return true;
    }
}

internal sealed class DirSelector(bool? rightToLeft) : SimpleSelector
{
    public bool? RightToLeft { get; } = rightToLeft;

    public override bool Matches(ICssElement element, in MatchContext context) => RightToLeft is { } rtl && element.IsRightToLeft == rtl;
}

internal sealed class StateSelector(string name) : SimpleSelector
{
    public string Name { get; } = name;

    public override bool Matches(ICssElement element, in MatchContext context) => element.MatchesPseudoClass(Name);
}
