using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Egami.Metadata;

/// <summary>
/// An XMP structure: named fields, each a simple value (<see cref="string"/>), an <see cref="XmpArray"/> or another
/// <see cref="XmpStruct"/>, such as Iptc4xmpCore:CreatorContactInfo. <see cref="XmpData"/> holds its top-level properties the
/// same way.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="xmp-values"/>
/// </example>
public class XmpStruct
{
    private readonly List<XmpField> _fields = [];

    /// <summary>Creates an empty structure.</summary>
    public XmpStruct()
    {
    }

    /// <summary>Gets the names of the fields, in order.</summary>
    public IReadOnlyList<XmpName> Names => [.. _fields.Select(f => f.Name)];

    /// <summary>Gets the number of fields.</summary>
    public int Count => _fields.Count;

    internal IXmpOwner? Owner { get; set; }

    internal List<XmpField> Fields => _fields;

    /// <summary>Returns a field's value: a <see cref="string"/>, an <see cref="XmpArray"/> or an <see cref="XmpStruct"/>.</summary>
    /// <param name="name">The field.</param>
    /// <param name="value">The value, or <see langword="null"/>.</param>
    /// <returns>Whether the field is there.</returns>
    public bool TryGet(XmpName name, [NotNullWhen(true)] out object? value)
    {
        value = Find(name)?.Value;
        return value is not null;
    }

    /// <summary>
    /// Returns a field as text: a simple value as it is, a language alternative's x-default item (else its first), an ordered or
    /// unordered array's first item; <see langword="null"/> when there is no text to give.
    /// </summary>
    /// <param name="name">The field.</param>
    /// <returns>The text.</returns>
    public string? GetText(XmpName name) => Find(name)?.Value switch
    {
        string s => s,
        XmpArray { Kind: XmpArrayKind.Alternative } alt => alt.GetLanguage("x-default") ?? (alt.Count > 0 ? alt[0] as string : null),
        XmpArray { Count: > 0 } array => array[0] as string,
        _ => null,
    };

    /// <summary>Returns a simple value as an integer, if it is one.</summary>
    /// <param name="name">The field.</param>
    /// <param name="value">The integer.</param>
    /// <returns>Whether the field is an integer.</returns>
    public bool TryGetInt(XmpName name, out long value)
    {
        value = 0;
        return Find(name)?.Value is string s && long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Returns a simple value written as a rational ("28/10") or a decimal number ("2.8"), as a number.</summary>
    /// <param name="name">The field.</param>
    /// <param name="value">The number.</param>
    /// <returns>Whether the field is a number.</returns>
    public bool TryGetRational(XmpName name, out double value)
    {
        value = 0;
        if (Find(name)?.Value is not string s)
        {
            return false;
        }

        int slash = s.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        if (long.TryParse(s.AsSpan(0, slash).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n)
            && long.TryParse(s.AsSpan(slash + 1).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long d) && d != 0)
        {
            value = (double)n / d;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns a simple value written as an XMP date (ISO 8601: YYYY, YYYY-MM, YYYY-MM-DD, then optionally Thh:mm, :ss and
    /// fractions, and a zone); a date with no zone is taken as UTC.
    /// </summary>
    /// <param name="name">The field.</param>
    /// <param name="value">The date.</param>
    /// <returns>Whether the field is a date.</returns>
    public bool TryGetDate(XmpName name, out DateTimeOffset value)
    {
        value = default;
        return Find(name)?.Value is string s && XmpDates.TryParse(s, out value);
    }

    /// <summary>Sets a field to a simple value, replacing what was there.</summary>
    /// <param name="name">The field.</param>
    /// <param name="value">The text.</param>
    public void Set(XmpName name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Put(name, value);
    }

    /// <summary>Sets a field to an array, replacing what was there. An array that already belongs elsewhere is copied.</summary>
    /// <param name="name">The field.</param>
    /// <param name="value">The array.</param>
    public void Set(XmpName name, XmpArray value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Put(name, value.Owner is null ? value : value.Copy());
    }

    /// <summary>Sets a field to a structure, replacing what was there. A structure that already belongs elsewhere is copied.</summary>
    /// <param name="name">The field.</param>
    /// <param name="value">The structure.</param>
    public void Set(XmpName name, XmpStruct value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Put(name, value.Owner is null && value != this ? value : value.CopyStruct());
    }

    /// <summary>Removes a field.</summary>
    /// <param name="name">The field.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(XmpName name)
    {
        if (_fields.RemoveAll(f => f.Name == name) == 0)
        {
            return false;
        }

        Touch();
        return true;
    }

    /// <summary>Returns an independent copy.</summary>
    /// <returns>The copy.</returns>
    public XmpStruct CopyStruct()
    {
        XmpStruct copy = new();
        CopyFieldsTo(copy);
        return copy;
    }

    internal void CopyFieldsTo(XmpStruct target)
    {
        foreach (XmpField f in _fields)
        {
            target.AddParsed(f.Name, XmpValues.Copy(f.Value), f.Qualifiers);
        }
    }

    internal XmpField? Find(XmpName name) => _fields.Find(f => f.Name == name);

    internal void Touch() => Owner?.Touch();

    /// <summary>Adds a field as read, without marking anything edited.</summary>
    internal void AddParsed(XmpName name, object value, XmpQualifiers? qualifiers)
    {
        XmpValues.Adopt(value, this);
        int at = _fields.FindIndex(f => f.Name == name);
        XmpField field = new(name, value, qualifiers);
        if (at >= 0)
        {
            _fields[at] = field;
        }
        else
        {
            _fields.Add(field);
        }
    }

    private void Put(XmpName name, object value)
    {
        if (string.IsNullOrEmpty(name.Namespace) || string.IsNullOrEmpty(name.Name) || !System.Xml.XmlConvert.IsStartNCNameChar(name.Name[0]) || name.Namespace == XmpNamespaces.Rdf)
        {
            throw new ArgumentException($"{name} is not an XMP property name.", nameof(name));
        }

        XmpValues.Adopt(value, this);
        int at = _fields.FindIndex(f => f.Name == name);
        XmpField field = new(name, value, null);
        if (at >= 0)
        {
            _fields[at] = field;
        }
        else
        {
            _fields.Add(field);
        }

        Touch();
    }
}

/// <summary>What owns an XMP container, told when the container changes so the packet is written again.</summary>
internal interface IXmpOwner
{
    void Touch();
}

/// <summary>A field of a structure or a top-level property: its name, value, and any qualifiers read with it.</summary>
internal sealed record XmpField(XmpName Name, object Value, XmpQualifiers? Qualifiers);

/// <summary>
/// What XMP says about a value besides the value: xml:lang, other qualifiers (kept and written back, with no typed access), and
/// whether a simple value was an rdf:resource URI.
/// </summary>
internal sealed record XmpQualifiers(string? Language, IReadOnlyList<KeyValuePair<XmpName, string>> Others, bool IsUri)
{
    public static readonly XmpQualifiers None = new(null, [], false);

    public bool IsEmpty => Language is null && Others.Count == 0 && !IsUri;
}

/// <summary>Helpers shared by the containers.</summary>
internal static class XmpValues
{
    public static object Copy(object value) => value switch
    {
        XmpArray a => a.Copy(),
        XmpStruct s => s.CopyStruct(),
        _ => value,
    };

    public static void Adopt(object value, object owner)
    {
        IXmpOwner link = owner as IXmpOwner ?? new ChildLink(owner);
        switch (value)
        {
            case XmpArray a:
                a.Owner = link;
                break;
            case XmpStruct s:
                s.Owner = link;
                break;
        }
    }

    private sealed class ChildLink(object parent) : IXmpOwner
    {
        public void Touch()
        {
            switch (parent)
            {
                case XmpArray a:
                    a.Touch();
                    break;
                case XmpStruct s:
                    s.Touch();
                    break;
            }
        }
    }
}
