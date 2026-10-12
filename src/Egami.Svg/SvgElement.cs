using System.Text;

namespace Egami.Svg;

/// <summary>
/// An element of an SVG document's editable tree: its name and namespace, attributes and content, like XDocument's elements.
/// Unknown elements and foreign namespaces (Inkscape's, Illustrator's metadata) are kept, so a save writes them back.
/// </summary>
/// <remarks>
/// Every change marks the document's render tree for rebuilding on the next draw. The tree is not safe for writes from several
/// threads at once; a document that is not being edited can be drawn on many threads.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="edit"/>
/// </example>
public sealed partial class SvgElement : SvgContent
{
    /// <summary>Initializes a new instance of the <see cref="SvgElement"/> class in the SVG namespace.</summary>
    /// <param name="name">The local name, such as <c>rect</c>.</param>
    public SvgElement(string name)
        : this(name, SvgNamespaces.Svg)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SvgElement"/> class.</summary>
    /// <param name="name">The local name.</param>
    /// <param name="namespaceUri">The namespace, or <see langword="null"/> for none.</param>
    /// <param name="prefix">The prefix a save writes, or <see langword="null"/> for the default namespace.</param>
    public SvgElement(string name, string? namespaceUri, string? prefix = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Namespace = string.IsNullOrEmpty(namespaceUri) ? null : namespaceUri;
        Prefix = string.IsNullOrEmpty(prefix) ? null : prefix;
        Attributes = new SvgAttributeCollection(this);
        Children = new SvgContentList(this);
    }

    /// <summary>Gets the local name, such as <c>rect</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the namespace: <see cref="SvgNamespaces.Svg"/> for SVG elements, or <see langword="null"/> for none.</summary>
    public string? Namespace { get; }

    /// <summary>Gets the prefix a save writes, or <see langword="null"/> when the element is in the default namespace.</summary>
    public string? Prefix { get; }

    /// <summary>Gets the attributes, in file order.</summary>
    public SvgAttributeCollection Attributes { get; }

    /// <summary>Gets the content, in file order: child elements, text, comments and processing instructions.</summary>
    public SvgContentList Children { get; }

    /// <summary>Gets or sets the <c>id</c> attribute; <see langword="null"/> when absent.</summary>
    public string? Id
    {
        get => Attributes.Get("id");
        set => Attributes.Set("id", value);
    }

    /// <summary>
    /// Gets or sets the text of this element and its descendants, joined in order. Setting replaces all content with one text item.
    /// </summary>
    public string Text
    {
        get
        {
            StringBuilder text = new();
            AppendText(this, text);
            return text.ToString();
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Children.Clear();
            if (value.Length > 0)
            {
                Children.Add(new SvgTextContent(value));
            }
        }
    }

    /// <summary>Gets a value indicating whether this is an element in the SVG namespace.</summary>
    public bool IsSvg => string.Equals(Namespace, SvgNamespaces.Svg, StringComparison.Ordinal);

    /// <summary>Gets whether this is an SVG element, counting elements in no namespace in a document whose root has none.</summary>
    internal bool IsSvgOrBare(SvgDocument? document) => IsSvg || (Namespace is null && document?.NoNamespace == true);

    /// <summary>Gets the document this element is the root of, when it is one.</summary>
    internal SvgDocument? OwnerDocument { get; set; }

    /// <summary>Gets the child elements, in order, skipping text and comments.</summary>
    /// <returns>The child elements.</returns>
    public IEnumerable<SvgElement> Elements()
    {
        foreach (SvgContent item in Children)
        {
            if (item is SvgElement element)
            {
                yield return element;
            }
        }
    }

    /// <summary>Gets the child elements with a local name, in the SVG namespace.</summary>
    /// <param name="name">The local name, such as <c>stop</c>.</param>
    /// <returns>The matching child elements.</returns>
    public IEnumerable<SvgElement> Elements(string name)
    {
        foreach (SvgContent item in Children)
        {
            if (item is SvgElement element && element.IsSvg && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                yield return element;
            }
        }
    }

    /// <summary>Gets every element below this one, depth first in file order, not including this one.</summary>
    /// <returns>The descendant elements.</returns>
    public IEnumerable<SvgElement> Descendants()
    {
        Stack<(SvgElement Element, int Index)> stack = new();
        stack.Push((this, 0));
        while (stack.Count > 0)
        {
            (SvgElement parent, int index) = stack.Pop();
            SvgContentList children = parent.Children;
            for (int i = index; i < children.Count; i++)
            {
                if (children[i] is SvgElement child)
                {
                    stack.Push((parent, i + 1));
                    stack.Push((child, 0));
                    yield return child;
                    break;
                }
            }
        }
    }

    /// <inheritdoc/>
    public override SvgContent Clone()
    {
        SvgElement copy = new(Name, Namespace, Prefix) { Line = Line, Column = Column };
        foreach (SvgAttribute attribute in Attributes)
        {
            copy.Attributes.AddRead(attribute);
        }

        foreach (SvgContent item in Children)
        {
            copy.Children.AddRead(item.Clone());
        }

        return copy;
    }

    /// <inheritdoc/>
    public override string ToString() => $"<{(Prefix is null ? "" : Prefix + ":")}{Name}{(Id is string id ? $" id=\"{id}\"" : "")}>";

    /// <summary>Called by the attribute collection after a change.</summary>
    internal void AttributesChanged() => Document?.Touch();

    /// <summary>Called by the content list after a change.</summary>
    internal void ContentChanged() => Document?.Touch();

    private static void AppendText(SvgElement element, StringBuilder text)
    {
        foreach (SvgContent item in element.Children)
        {
            if (item is SvgTextContent t)
            {
                text.Append(t.Value);
            }
            else if (item is SvgElement child)
            {
                AppendText(child, text);
            }
        }
    }
}
