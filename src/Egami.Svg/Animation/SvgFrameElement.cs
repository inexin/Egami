using Egami.Css;

namespace Egami.Svg.Animation;

/// <summary>
/// An element as selectors see it at one moment of its animations: its class attribute, and that of every element selectors
/// walk to from it, is the animated one where an animation sets it. Everything else is the element's own. The frame keeps one
/// per element, so selectors that compare elements by reference still work.
/// </summary>
internal sealed class SvgFrameElement(SvgElement element, SvgAnimationFrame frame) : ICssElement
{
    private ICssElement Inner => element;

    public string LocalName => Inner.LocalName;

    public string? NamespaceUri => Inner.NamespaceUri;

    public ICssElement? Parent => Wrap(Inner.Parent);

    public ICssElement? PreviousSibling => Wrap(Inner.PreviousSibling);

    public ICssElement? NextSibling => Wrap(Inner.NextSibling);

    public ICssElement? FirstChild => Wrap(Inner.FirstChild);

    public bool IsEmpty => Inner.IsEmpty;

    public string? Id => Inner.Id;

    public string? ClassName => frame.TryGet(element, "class", out string? animated) ? animated : element.Attributes["class"];

    public IEnumerable<CssAttribute> Attributes
    {
        get
        {
            bool animated = frame.TryGet(element, "class", out string? classes);
            bool seen = false;
            foreach (CssAttribute attribute in Inner.Attributes)
            {
                if (animated && attribute.NamespaceUri is null && attribute.LocalName == "class")
                {
                    seen = true;
                    yield return attribute with { Value = classes! };
                }
                else
                {
                    yield return attribute;
                }
            }

            if (animated && !seen)
            {
                yield return new CssAttribute(null, "class", classes!);
            }
        }
    }

    public bool HasClass(string name)
    {
        string? classes = ClassName;
        if (classes is null)
        {
            return false;
        }

        foreach (Range range in classes.AsSpan().SplitAny(" \t\r\n\f"))
        {
            if (classes.AsSpan()[range].SequenceEqual(name))
            {
                return true;
            }
        }

        return false;
    }

    public string? GetAttribute(string localName, string? namespaceUri) =>
        namespaceUri is null && localName == "class" ? ClassName : Inner.GetAttribute(localName, namespaceUri);

    public string? Language => Inner.Language;

    public bool IsRightToLeft => Inner.IsRightToLeft;

    public bool MatchesPseudoClass(string name) => Inner.MatchesPseudoClass(name);

    private ICssElement? Wrap(ICssElement? other) => other is SvgElement e ? frame.CssElement(e) : other;
}
