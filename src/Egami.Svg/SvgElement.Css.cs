using Egami.Css;

namespace Egami.Svg;

/// <summary>An SVG element is a CSS element, so Egami.Css's selectors match it (question 5).</summary>
public sealed partial class SvgElement : ICssElement
{
    private int _indexHint;

    string ICssElement.LocalName => Name;

    string? ICssElement.NamespaceUri => Namespace ?? (Document?.NoNamespace == true ? SvgNamespaces.Svg : null);

    ICssElement? ICssElement.Parent => Parent;

    ICssElement? ICssElement.PreviousSibling
    {
        get
        {
            if (Parent is not SvgElement parent)
            {
                return null;
            }

            SvgContentList siblings = parent.Children;
            for (int i = IndexInParent(siblings) - 1; i >= 0; i--)
            {
                if (siblings[i] is SvgElement element)
                {
                    return element;
                }
            }

            return null;
        }
    }

    ICssElement? ICssElement.NextSibling
    {
        get
        {
            if (Parent is not SvgElement parent)
            {
                return null;
            }

            SvgContentList siblings = parent.Children;
            for (int i = IndexInParent(siblings) + 1; i < siblings.Count; i++)
            {
                if (siblings[i] is SvgElement element)
                {
                    return element;
                }
            }

            return null;
        }
    }

    ICssElement? ICssElement.FirstChild
    {
        get
        {
            foreach (SvgContent item in Children)
            {
                if (item is SvgElement element)
                {
                    return element;
                }
            }

            return null;
        }
    }

    bool ICssElement.IsEmpty
    {
        get
        {
            foreach (SvgContent item in Children)
            {
                if (item is SvgElement || (item is SvgTextContent text && text.Value.Length > 0))
                {
                    return false;
                }
            }

            return true;
        }
    }

    string? ICssElement.Id => Id;

    IEnumerable<CssAttribute> ICssElement.Attributes
    {
        get
        {
            foreach (SvgAttribute attribute in Attributes)
            {
                yield return new CssAttribute(attribute.Namespace, attribute.Name, attribute.Value);
            }
        }
    }

    bool ICssElement.HasClass(string name)
    {
        string? classes = Attributes["class"];
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

    string? ICssElement.GetAttribute(string localName, string? namespaceUri) => Attributes.Get(localName, namespaceUri);

    /// <summary>This element's index in its parent's content, from a hint kept between calls (selectors walk siblings often).</summary>
    private int IndexInParent(SvgContentList siblings)
    {
        if ((uint)_indexHint < (uint)siblings.Count && ReferenceEquals(siblings[_indexHint], this))
        {
            return _indexHint;
        }

        _indexHint = siblings.IndexOf(this);
        return _indexHint;
    }
}
