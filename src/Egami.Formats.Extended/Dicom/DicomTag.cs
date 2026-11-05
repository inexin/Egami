using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// A DICOM data element tag: a group and an element number (PS3.5 section 7.1). Public tags have even groups and are named in
/// PS3.6's data dictionary; odd groups are private.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-tags"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public readonly record struct DicomTag : IComparable<DicomTag>
{
    /// <summary>Initializes a tag from its group and element numbers.</summary>
    /// <param name="group">The group number, such as 0x0010 for patient information.</param>
    /// <param name="element">The element number within the group.</param>
    public DicomTag(ushort group, ushort element)
    {
        Group = group;
        Element = element;
    }

    /// <summary>Gets Pixel Data (7FE0,0010).</summary>
    public static DicomTag PixelData { get; } = new(0x7FE0, 0x0010);

    /// <summary>Gets Specific Character Set (0008,0005), which says how the dataset's text is encoded.</summary>
    public static DicomTag SpecificCharacterSet { get; } = new(0x0008, 0x0005);

    /// <summary>Gets Transfer Syntax UID (0002,0010), in the file meta information.</summary>
    public static DicomTag TransferSyntaxUid { get; } = new(0x0002, 0x0010);

    /// <summary>Gets the group number.</summary>
    public ushort Group { get; }

    /// <summary>Gets the element number.</summary>
    public ushort Element { get; }

    /// <summary>Gets the tag as one number, group in the high 16 bits.</summary>
    public uint Value => ((uint)Group << 16) | Element;

    /// <summary>Gets whether the tag is private: its group is odd.</summary>
    public bool IsPrivate => (Group & 1) == 1;

    /// <summary>Gets the PS3.6 keyword, such as <c>PatientName</c>, or <see langword="null"/> for a tag the dictionary does not name.</summary>
    public string? Keyword => DicomDictionary.Find(this)?.Keyword;

    /// <summary>Gets the PS3.6 name, such as <c>Patient's Name</c>, or <see langword="null"/> for a tag the dictionary does not name.</summary>
    public string? Name => DicomDictionary.Find(this)?.Name;

    /// <summary>Gets whether PS3.6 lists the tag as retired.</summary>
    public bool IsRetired => DicomDictionary.Find(this)?.Retired ?? false;

    /// <summary>Returns the tag a PS3.6 keyword names.</summary>
    /// <param name="keyword">The keyword, such as <c>PatientName</c>; case matters.</param>
    /// <returns>The tag.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keyword"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">PS3.6 has no such keyword.</exception>
    public static DicomTag FromKeyword(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        return TryFromKeyword(keyword, out DicomTag tag) ? tag : throw new KeyNotFoundException($"PS3.6 has no keyword '{keyword}'.");
    }

    /// <summary>Looks up the tag a PS3.6 keyword names.</summary>
    /// <param name="keyword">The keyword, such as <c>PatientName</c>; case matters.</param>
    /// <param name="tag">The tag, when found.</param>
    /// <returns>Whether the keyword is in the dictionary. Keywords of repeating groups, such as <c>OverlayData</c>, give their first group.</returns>
    public static bool TryFromKeyword(string? keyword, out DicomTag tag)
    {
        return DicomDictionary.TryFind(keyword, out tag);
    }

    /// <inheritdoc/>
    public int CompareTo(DicomTag other) => Value.CompareTo(other.Value);

    /// <summary>Returns the tag as PS3.6 writes it, such as <c>(0010,0010)</c>.</summary>
    /// <returns>The tag as text.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({Group:X4},{Element:X4})");

    /// <summary>Compares two tags by group, then element.</summary>
    /// <param name="left">The first tag.</param>
    /// <param name="right">The second tag.</param>
    /// <returns>Whether <paramref name="left"/> comes first.</returns>
    public static bool operator <(DicomTag left, DicomTag right) => left.Value < right.Value;

    /// <summary>Compares two tags by group, then element.</summary>
    /// <param name="left">The first tag.</param>
    /// <param name="right">The second tag.</param>
    /// <returns>Whether <paramref name="left"/> comes later.</returns>
    public static bool operator >(DicomTag left, DicomTag right) => left.Value > right.Value;

    /// <summary>Compares two tags by group, then element.</summary>
    /// <param name="left">The first tag.</param>
    /// <param name="right">The second tag.</param>
    /// <returns>Whether <paramref name="left"/> comes first or is the same.</returns>
    public static bool operator <=(DicomTag left, DicomTag right) => left.Value <= right.Value;

    /// <summary>Compares two tags by group, then element.</summary>
    /// <param name="left">The first tag.</param>
    /// <param name="right">The second tag.</param>
    /// <returns>Whether <paramref name="left"/> comes later or is the same.</returns>
    public static bool operator >=(DicomTag left, DicomTag right) => left.Value >= right.Value;
}

/// <summary>A value representation: how an element's value is encoded (PS3.5 section 6.2).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-values"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public enum DicomVR
{
    /// <summary>Unknown (UN): bytes whose encoding the file does not say; also any code this version does not know.</summary>
    UN,

    /// <summary>Application Entity: up to 16 characters.</summary>
    AE,

    /// <summary>Age String: such as <c>045Y</c>.</summary>
    AS,

    /// <summary>Attribute Tag: tags, 4 bytes each.</summary>
    AT,

    /// <summary>Code String: up to 16 upper-case characters.</summary>
    CS,

    /// <summary>Date: <c>YYYYMMDD</c>.</summary>
    DA,

    /// <summary>Decimal String: decimal numbers as text.</summary>
    DS,

    /// <summary>Date Time: <c>YYYYMMDDHHMMSS.FFFFFF&amp;ZZXX</c>, trailing parts optional.</summary>
    DT,

    /// <summary>Floating Point Double: 8-byte IEEE numbers.</summary>
    FD,

    /// <summary>Floating Point Single: 4-byte IEEE numbers.</summary>
    FL,

    /// <summary>Integer String: integers as text.</summary>
    IS,

    /// <summary>Long String: up to 64 characters.</summary>
    LO,

    /// <summary>Long Text: up to 10240 characters, one value.</summary>
    LT,

    /// <summary>Other Byte: bytes.</summary>
    OB,

    /// <summary>Other Double: 8-byte IEEE numbers.</summary>
    OD,

    /// <summary>Other Float: 4-byte IEEE numbers.</summary>
    OF,

    /// <summary>Other Long: 32-bit words.</summary>
    OL,

    /// <summary>Other Very Long: 64-bit words.</summary>
    OV,

    /// <summary>Other Word: 16-bit words.</summary>
    OW,

    /// <summary>Person Name: components separated by <c>^</c>, groups by <c>=</c>.</summary>
    PN,

    /// <summary>Short String: up to 16 characters.</summary>
    SH,

    /// <summary>Signed Long: 32-bit signed integers.</summary>
    SL,

    /// <summary>Sequence of Items: nested datasets.</summary>
    SQ,

    /// <summary>Signed Short: 16-bit signed integers.</summary>
    SS,

    /// <summary>Short Text: up to 1024 characters, one value.</summary>
    ST,

    /// <summary>Signed Very Long: 64-bit signed integers.</summary>
    SV,

    /// <summary>Time: <c>HHMMSS.FFFFFF</c>, trailing parts optional.</summary>
    TM,

    /// <summary>Unlimited Characters: text of any length, several values.</summary>
    UC,

    /// <summary>Unique Identifier: a dotted UID such as <c>1.2.840.10008.1.2</c>.</summary>
    UI,

    /// <summary>Unsigned Long: 32-bit unsigned integers.</summary>
    UL,

    /// <summary>Universal Resource Identifier: one URI or URL.</summary>
    UR,

    /// <summary>Unsigned Short: 16-bit unsigned integers.</summary>
    US,

    /// <summary>Unlimited Text: text of any length, one value.</summary>
    UT,

    /// <summary>Unsigned Very Long: 64-bit unsigned integers.</summary>
    UV,
}
