namespace Egami.Metadata;

/// <summary>The three kinds of XMP array.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="xmp-values"/>
/// </example>
public enum XmpArrayKind
{
    /// <summary>An unordered array (rdf:Bag), such as dc:subject, the keywords.</summary>
    Bag,

    /// <summary>An ordered array (rdf:Seq), such as dc:creator.</summary>
    Sequence,

    /// <summary>Alternatives (rdf:Alt), usually one text per language (xml:lang), such as dc:title.</summary>
    Alternative,
}

/// <summary>
/// An XMP array: items in order, each a simple value (<see cref="string"/>), an <see cref="XmpStruct"/> or another
/// <see cref="XmpArray"/>, with a language for the items of a language alternative.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="xmp-values"/>
/// </example>
public sealed class XmpArray
{
    private readonly List<(object Value, XmpQualifiers? Qualifiers)> _items = [];

    /// <summary>Creates an empty array.</summary>
    /// <param name="kind">Bag, sequence or alternative.</param>
    public XmpArray(XmpArrayKind kind)
    {
        Kind = kind;
    }

    /// <summary>Gets whether the array is a bag, a sequence or alternatives.</summary>
    public XmpArrayKind Kind { get; }

    /// <summary>Gets the number of items.</summary>
    public int Count => _items.Count;

    /// <summary>Gets the simple items as text, in order; structures and nested arrays are left out.</summary>
    public IReadOnlyList<string> Texts => [.. _items.Select(i => i.Value).OfType<string>()];

    internal IXmpOwner? Owner { get; set; }

    internal List<(object Value, XmpQualifiers? Qualifiers)> Items => _items;

    /// <summary>Gets item <paramref name="index"/>: a <see cref="string"/>, an <see cref="XmpStruct"/> or an <see cref="XmpArray"/>.</summary>
    /// <param name="index">The index, from 0.</param>
    /// <returns>The item.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not below <see cref="Count"/>.</exception>
    public object this[int index] => _items[index].Value;

    /// <summary>Creates an array of simple values.</summary>
    /// <param name="kind">Bag or sequence.</param>
    /// <param name="items">The texts.</param>
    /// <returns>The array.</returns>
    public static XmpArray Of(XmpArrayKind kind, params IEnumerable<string> items)
    {
        XmpArray array = new(kind);
        foreach (string item in items)
        {
            array.Add(item);
        }

        return array;
    }

    /// <summary>Returns item <paramref name="index"/>'s language (xml:lang), or <see langword="null"/>.</summary>
    /// <param name="index">The index, from 0.</param>
    /// <returns>The language.</returns>
    public string? LanguageAt(int index) => _items[index].Qualifiers?.Language;

    /// <summary>Returns the text for a language, compared without case; <see langword="null"/> when there is none.</summary>
    /// <param name="language">The language, such as "x-default", "en" or "nb-NO".</param>
    /// <returns>The text.</returns>
    public string? GetLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return _items.Find(i => string.Equals(i.Qualifiers?.Language, language, StringComparison.OrdinalIgnoreCase)).Value as string;
    }

    /// <summary>Sets the text for a language, replacing it or adding it; x-default goes first, as XMP requires.</summary>
    /// <param name="language">The language.</param>
    /// <param name="text">The text.</param>
    public void SetLanguage(string language, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(language);
        ArgumentNullException.ThrowIfNull(text);
        XmpQualifiers qualifiers = new(language, [], false);
        int at = _items.FindIndex(i => string.Equals(i.Qualifiers?.Language, language, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            _items[at] = (text, qualifiers);
        }
        else if (string.Equals(language, "x-default", StringComparison.OrdinalIgnoreCase))
        {
            _items.Insert(0, (text, qualifiers));
        }
        else
        {
            _items.Add((text, qualifiers));
        }

        Touch();
    }

    /// <summary>Adds a simple value.</summary>
    /// <param name="item">The text.</param>
    public void Add(string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add((item, null));
        Touch();
    }

    /// <summary>Adds a structure; one that already belongs elsewhere is copied.</summary>
    /// <param name="item">The structure.</param>
    public void Add(XmpStruct item)
    {
        ArgumentNullException.ThrowIfNull(item);
        XmpStruct own = item.Owner is null ? item : item.Copy();
        XmpValues.Adopt(own, this);
        _items.Add((own, null));
        Touch();
    }

    /// <summary>Adds a nested array; one that already belongs elsewhere is copied.</summary>
    /// <param name="item">The array.</param>
    public void Add(XmpArray item)
    {
        ArgumentNullException.ThrowIfNull(item);
        XmpArray own = item.Owner is null && item != this ? item : item.Copy();
        XmpValues.Adopt(own, this);
        _items.Add((own, null));
        Touch();
    }

    /// <summary>Removes item <paramref name="index"/>.</summary>
    /// <param name="index">The index, from 0.</param>
    public void RemoveAt(int index)
    {
        _items.RemoveAt(index);
        Touch();
    }

    /// <summary>Removes every item.</summary>
    public void Clear()
    {
        _items.Clear();
        Touch();
    }

    /// <summary>Returns an independent copy.</summary>
    /// <returns>The copy.</returns>
    public XmpArray Copy()
    {
        XmpArray copy = new(Kind);
        foreach ((object value, XmpQualifiers? qualifiers) in _items)
        {
            copy.AddParsed(XmpValues.Copy(value), qualifiers);
        }

        return copy;
    }

    internal void AddParsed(object value, XmpQualifiers? qualifiers)
    {
        XmpValues.Adopt(value, this);
        _items.Add((value, qualifiers));
    }

    internal void Touch() => Owner?.Touch();
}
