using System.Xml;
using System.Xml.Linq;

namespace Egami.Css.Tests;

/// <summary>
/// A small DOM over an XML document for the tests, implementing <see cref="ICssElement"/>. In HTML mode, elements in the XHTML
/// namespace act as HTML elements in an HTML document (their attribute names are lowercased, as the HTML parser would).
/// </summary>
internal sealed class TestElement : ICssElement
{
    public const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";

    private readonly List<CssAttribute> _attributes = [];
    private readonly bool _htmlDocument;

    public TestElement(string localName, string? namespaceUri, bool htmlDocument = false)
    {
        NamespaceUri = string.IsNullOrEmpty(namespaceUri) ? null : namespaceUri;
        LocalName = htmlDocument && NamespaceUri == XhtmlNamespace ? localName.ToLowerInvariant() : localName;
        _htmlDocument = htmlDocument;
    }

    public string LocalName { get; }

    public string? NamespaceUri { get; }

    public TestElement? ParentElement { get; private set; }

    public List<TestElement> Children { get; } = [];

    public bool HasText { get; set; }

    public ICssElement? Parent => ParentElement;

    public ICssElement? PreviousSibling
    {
        get
        {
            if (ParentElement is null)
            {
                return null;
            }

            int index = ParentElement.Children.IndexOf(this);
            return index > 0 ? ParentElement.Children[index - 1] : null;
        }
    }

    public ICssElement? NextSibling
    {
        get
        {
            if (ParentElement is null)
            {
                return null;
            }

            int index = ParentElement.Children.IndexOf(this);
            return index + 1 < ParentElement.Children.Count ? ParentElement.Children[index + 1] : null;
        }
    }

    public ICssElement? FirstChild => Children.Count > 0 ? Children[0] : null;

    public bool IsEmpty => Children.Count == 0 && !HasText;

    public string? Id => GetAttribute("id", null);

    public bool IsHtml => _htmlDocument && NamespaceUri == XhtmlNamespace;

    public IEnumerable<CssAttribute> Attributes => _attributes;

    public bool RightToLeft { get; set; }

    public bool IsRightToLeft => RightToLeft;

    public HashSet<string> States { get; } = new(StringComparer.Ordinal);

    public void SetAttribute(string localName, string value, string? namespaceUri = null)
    {
        if (IsHtml && namespaceUri is null)
        {
            localName = localName.ToLowerInvariant();
        }

        _attributes.RemoveAll(a => a.LocalName == localName && a.NamespaceUri == namespaceUri);
        _attributes.Add(new CssAttribute(namespaceUri, localName, value));
    }

    public string? GetAttribute(string localName, string? namespaceUri)
    {
        foreach (CssAttribute attribute in _attributes)
        {
            if (attribute.LocalName == localName && attribute.NamespaceUri == namespaceUri)
            {
                return attribute.Value;
            }
        }

        return null;
    }

    public bool HasClass(string name)
    {
        string? value = GetAttribute("class", null);
        return value is not null && value.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries).Contains(name);
    }

    public TestElement Append(TestElement child)
    {
        child.ParentElement = this;
        Children.Add(child);
        return this;
    }

    public IEnumerable<TestElement> DescendantsAndSelf()
    {
        yield return this;
        foreach (TestElement child in Children)
        {
            foreach (TestElement descendant in child.DescendantsAndSelf())
            {
                yield return descendant;
            }
        }
    }

    public TestElement? FindById(string id) => DescendantsAndSelf().FirstOrDefault(e => e.Id == id);

    // HTML's meaning of the state pseudo-classes the WPT document uses; the document's URL fragment is #target.
    public bool MatchesPseudoClass(string name)
    {
        if (States.Contains(name))
        {
            return true;
        }

        bool html = NamespaceUri == XhtmlNamespace;
        switch (name)
        {
            case "link" or "any-link":
                return html && LocalName is "a" or "area" && GetAttribute("href", null) is not null;
            case "target":
                return Id == "target";
            case "checked":
                return html && ((LocalName == "input" && GetAttribute("type", null)?.ToLowerInvariant() is "checkbox" or "radio" && GetAttribute("checked", null) is not null)
                    || (LocalName == "option" && GetAttribute("selected", null) is not null));
            case "disabled":
                return html && CanBeDisabled() && IsDisabled();
            case "enabled":
                return html && CanBeDisabled() && !IsDisabled();
            default:
                return false;
        }
    }

    private bool CanBeDisabled() => LocalName is "button" or "input" or "select" or "textarea" or "optgroup" or "option" or "fieldset";

    private bool IsDisabled()
    {
        if (GetAttribute("disabled", null) is not null)
        {
            return true;
        }

        if (LocalName == "option" && ParentElement is { LocalName: "optgroup" } group && group.GetAttribute("disabled", null) is not null)
        {
            return true;
        }

        for (TestElement? ancestor = ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            if (ancestor.LocalName == "fieldset" && ancestor.GetAttribute("disabled", null) is not null)
            {
                TestElement? legend = ancestor.Children.FirstOrDefault(c => c.LocalName == "legend");
                return legend is null || !legend.DescendantsAndSelf().Contains(this);
            }
        }

        return false;
    }

    public override string ToString() => $"<{LocalName} id={Id}>";

    /// <summary>Builds the tree of an XML document.</summary>
    public static TestElement Load(string path, bool htmlDocument)
    {
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Ignore };
        using XmlReader reader = XmlReader.Create(path, settings);
        XDocument document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        return Build(document.Root!, htmlDocument);
    }

    public static TestElement Parse(string xml, bool htmlDocument = false)
    {
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Ignore };
        using XmlReader reader = XmlReader.Create(new StringReader(xml), settings);
        return Build(XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root!, htmlDocument);
    }

    private static TestElement Build(XElement source, bool htmlDocument)
    {
        TestElement element = new(source.Name.LocalName, source.Name.NamespaceName, htmlDocument);
        foreach (XAttribute attribute in source.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            string? ns = attribute.Name.NamespaceName.Length == 0 ? null : attribute.Name.NamespaceName;
            element.SetAttribute(attribute.Name.LocalName, attribute.Value, ns);
        }

        foreach (XNode node in source.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    element.Append(Build(child, htmlDocument));
                    break;
                case XText text when text.Value.Length > 0:
                    element.HasText = true;
                    break;
            }
        }

        return element;
    }
}
