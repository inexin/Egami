using System.Text;

namespace Egami.Metadata;

/// <summary>One IPTC-IIM dataset as stored: its key and its bytes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="iptc-values"/>
/// </example>
public sealed class IptcEntry
{
    private readonly ReadOnlyMemory<byte> _value;
    private bool _utf8;

    internal IptcEntry(IptcKey key, ReadOnlyMemory<byte> value, bool utf8)
    {
        Key = key;
        _value = value;
        _utf8 = utf8;
    }

    /// <summary>Gets the record and dataset.</summary>
    public IptcKey Key { get; }

    /// <summary>Gets the value's bytes as stored.</summary>
    public ReadOnlyMemory<byte> RawValue => _value;

    /// <summary>
    /// Returns the value as text: UTF-8 when the block's coded character set (1:90) says so, otherwise UTF-8 when the bytes are
    /// valid UTF-8 and Latin-1 when they are not, the rule <see cref="ExifEntry.AsString"/> uses.
    /// </summary>
    /// <returns>The text.</returns>
    public string AsString() => Decode(_value.Span, _utf8);

    /// <inheritdoc/>
    public override string ToString() => $"{Key}: \"{AsString()}\"";

    internal static string Decode(ReadOnlySpan<byte> bytes, bool utf8) =>
        utf8 || System.Text.Unicode.Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);

    internal IptcEntry WithCharset(bool utf8) => utf8 == _utf8 ? this : new(Key, _value, utf8);

    /// <summary>Marks a freshly parsed entry as UTF-8, once the whole block has been read.</summary>
    internal void MarkUtf8() => _utf8 = true;
}
