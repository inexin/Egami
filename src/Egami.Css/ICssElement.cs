namespace Egami.Css;

/// <summary>An attribute of an element, as <see cref="ICssElement.Attributes"/> lists it.</summary>
/// <param name="NamespaceUri">The attribute's namespace URI, or <see langword="null"/> for none.</param>
/// <param name="LocalName">The attribute's local name.</param>
/// <param name="Value">The attribute's value.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="element"/>
/// </example>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "A DOM attribute, not a .NET attribute.")]
public readonly record struct CssAttribute(string? NamespaceUri, string LocalName, string Value);

/// <summary>
/// An element that selectors match against: implemented by a document tree such as Egami.Svg's or an HTML DOM.
/// </summary>
/// <remarks>
/// Matching compares elements by reference (for <c>:scope</c> and <c>:has()</c>), so a tree must return the same object for the
/// same element every time. The members are called often while matching; they should not allocate.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="element"/>
/// </example>
public interface ICssElement
{
    /// <summary>The XML namespace, used for <c>xml:lang</c>.</summary>
    public const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

    /// <summary>Gets the element's local name, such as <c>rect</c>.</summary>
    string LocalName { get; }

    /// <summary>Gets the element's namespace URI, or <see langword="null"/> for none.</summary>
    string? NamespaceUri { get; }

    /// <summary>Gets the parent element, or <see langword="null"/> for the root element.</summary>
    ICssElement? Parent { get; }

    /// <summary>Gets the previous sibling element (skipping text and other nodes), or <see langword="null"/>.</summary>
    ICssElement? PreviousSibling { get; }

    /// <summary>Gets the next sibling element (skipping text and other nodes), or <see langword="null"/>.</summary>
    ICssElement? NextSibling { get; }

    /// <summary>Gets the first child element, or <see langword="null"/>.</summary>
    ICssElement? FirstChild { get; }

    /// <summary>Gets whether the element has no child elements and no text other than an empty string, for <c>:empty</c>.</summary>
    bool IsEmpty { get; }

    /// <summary>Gets the element's ID, or <see langword="null"/>.</summary>
    string? Id { get; }

    /// <summary>Gets whether the element has the class <paramref name="name"/> (compared case-sensitively).</summary>
    /// <param name="name">The class name.</param>
    /// <returns>True when the element has the class.</returns>
    bool HasClass(string name);

    /// <summary>Gets an attribute's value.</summary>
    /// <param name="localName">The attribute's local name.</param>
    /// <param name="namespaceUri">The attribute's namespace URI, or <see langword="null"/> for an attribute in no namespace.</param>
    /// <returns>The value, or <see langword="null"/> when the element has no such attribute.</returns>
    string? GetAttribute(string localName, string? namespaceUri);

    /// <summary>Gets every attribute of the element, for attribute selectors with any namespace (<c>[*|name]</c>).</summary>
    IEnumerable<CssAttribute> Attributes { get; }

    /// <summary>
    /// Gets the element's class attribute: whitespace-separated class names that must agree with <see cref="HasClass(string)"/>.
    /// The cascade reads it to find the rules that may match. Defaults to the <c>class</c> attribute in no namespace.
    /// </summary>
    string? ClassName => GetAttribute("class", null);

    /// <summary>
    /// Gets whether the element is an HTML element in an HTML document. Type selectors and attribute names then match ASCII
    /// case-insensitively, and the values of the attributes HTML lists (such as <c>type</c>) too. Defaults to false.
    /// </summary>
    bool IsHtml => false;

    /// <summary>
    /// Gets the element's language for <c>:lang()</c>, or <see langword="null"/> when unknown. Defaults to the nearest
    /// <c>xml:lang</c> or <c>lang</c> attribute on the element or an ancestor, <c>xml:lang</c> first.
    /// </summary>
    string? Language
    {
        get
        {
            for (ICssElement? element = this; element is not null; element = element.Parent)
            {
                string? language = element.GetAttribute("lang", XmlNamespace) ?? element.GetAttribute("lang", null);
                if (language is not null)
                {
                    return language;
                }
            }

            return null;
        }
    }

    /// <summary>Gets whether the element's directionality is right-to-left, for <c>:dir()</c>. Defaults to false.</summary>
    bool IsRightToLeft => false;

    /// <summary>
    /// Gets whether the element is in a state a pseudo-class names, such as <c>hover</c>, <c>focus</c>, <c>checked</c>,
    /// <c>link</c>, <c>visited</c>, <c>any-link</c>, <c>target</c> or <c>disabled</c>. Defaults to false for every state.
    /// </summary>
    /// <param name="name">The pseudo-class name in lowercase, without the colon.</param>
    /// <returns>True when the element is in that state.</returns>
    bool MatchesPseudoClass(string name) => false;
}
