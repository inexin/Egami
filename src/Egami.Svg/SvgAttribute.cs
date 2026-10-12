using System.Collections;

namespace Egami.Svg;

/// <summary>An attribute of an <see cref="SvgElement"/>: its name, namespace, the prefix it was written with, and its value.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="edit"/>
/// </example>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "An XML attribute, as XAttribute; not a .NET attribute.")]
public sealed class SvgAttribute
{
    /// <summary>Initializes a new instance of the <see cref="SvgAttribute"/> class.</summary>
    /// <param name="name">The local name, such as <c>fill</c> or <c>href</c>.</param>
    /// <param name="value">The value, with entities expanded.</param>
    /// <param name="namespaceUri">The namespace, such as <see cref="SvgNamespaces.XLink"/>, or <see langword="null"/> for none.</param>
    /// <param name="prefix">The prefix a save writes, such as <c>xlink</c>, or <see langword="null"/> to pick one.</param>
    public SvgAttribute(string name, string value, string? namespaceUri = null, string? prefix = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name;
        Value = value;
        Namespace = string.IsNullOrEmpty(namespaceUri) ? null : namespaceUri;
        Prefix = string.IsNullOrEmpty(prefix) ? null : prefix;
    }

    /// <summary>Gets the local name, such as <c>fill</c> or <c>href</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the namespace, or <see langword="null"/> for an attribute in no namespace, as most SVG attributes are.</summary>
    public string? Namespace { get; }

    /// <summary>Gets the prefix a save writes, or <see langword="null"/>.</summary>
    public string? Prefix { get; }

    /// <summary>Gets the value, with entities expanded.</summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{(Prefix is null ? "" : Prefix + ":")}{Name}=\"{Value}\"";

    internal bool Is(string name, string? namespaceUri) =>
        string.Equals(Name, name, StringComparison.Ordinal) && string.Equals(Namespace, namespaceUri, StringComparison.Ordinal);
}

/// <summary>An element's attributes, in file order. Setting, adding and removing mark the document's render tree for rebuilding.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="edit"/>
/// </example>
public sealed class SvgAttributeCollection : IReadOnlyList<SvgAttribute>
{
    private readonly SvgElement _owner;
    private readonly List<SvgAttribute> _items = [];

    internal SvgAttributeCollection(SvgElement owner) => _owner = owner;

    /// <summary>Gets the number of attributes.</summary>
    public int Count => _items.Count;

    /// <summary>Gets the attribute at an index, in file order.</summary>
    /// <param name="index">The index.</param>
    public SvgAttribute this[int index] => _items[index];

    /// <summary>Gets or sets the value of an attribute in no namespace; <see langword="null"/> when absent, and setting <see langword="null"/> removes it.</summary>
    /// <param name="name">The local name, such as <c>fill</c>.</param>
    public string? this[string name]
    {
        get => Get(name);
        set => Set(name, value);
    }

    /// <summary>Gets the value of an attribute, or <see langword="null"/> when the element has none of that name.</summary>
    /// <param name="name">The local name.</param>
    /// <param name="namespaceUri">The namespace, or <see langword="null"/> for none.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    public string? Get(string name, string? namespaceUri = null)
    {
        int i = IndexOf(name, namespaceUri);
        return i < 0 ? null : _items[i].Value;
    }

    /// <summary>
    /// Sets an attribute, replacing one of the same name and namespace in place, or adding it at the end; <see langword="null"/>
    /// removes it.
    /// </summary>
    /// <param name="name">The local name.</param>
    /// <param name="value">The value, or <see langword="null"/> to remove the attribute.</param>
    /// <param name="namespaceUri">The namespace, or <see langword="null"/> for none.</param>
    /// <param name="prefix">The prefix a save writes for an attribute in a namespace, or <see langword="null"/> to keep or pick one.</param>
    public void Set(string name, string? value, string? namespaceUri = null, string? prefix = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (value is null)
        {
            Remove(name, namespaceUri);
            return;
        }

        int i = IndexOf(name, namespaceUri);
        if (i >= 0)
        {
            _items[i] = new SvgAttribute(name, value, namespaceUri, prefix ?? _items[i].Prefix);
        }
        else
        {
            _items.Add(new SvgAttribute(name, value, namespaceUri, prefix));
        }

        _owner.AttributesChanged();
    }

    /// <summary>Removes an attribute.</summary>
    /// <param name="name">The local name.</param>
    /// <param name="namespaceUri">The namespace, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> if the element had the attribute.</returns>
    public bool Remove(string name, string? namespaceUri = null)
    {
        int i = IndexOf(name, namespaceUri);
        if (i < 0)
        {
            return false;
        }

        _items.RemoveAt(i);
        _owner.AttributesChanged();
        return true;
    }

    /// <summary>Gets whether the element has an attribute.</summary>
    /// <param name="name">The local name.</param>
    /// <param name="namespaceUri">The namespace, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> if it does.</returns>
    public bool Contains(string name, string? namespaceUri = null) => IndexOf(name, namespaceUri) >= 0;

    /// <inheritdoc/>
    public IEnumerator<SvgAttribute> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Adds an attribute read from a file (XML allows no duplicates, so the reader has already refused them).</summary>
    internal void AddRead(SvgAttribute attribute) => _items.Add(attribute);

    private int IndexOf(string name, string? namespaceUri)
    {
        namespaceUri = string.IsNullOrEmpty(namespaceUri) ? null : namespaceUri;
        List<SvgAttribute> items = _items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Is(name, namespaceUri))
            {
                return i;
            }
        }

        return -1;
    }
}
