using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// One data element of a <see cref="DicomDataset"/>: its tag, value representation and value, read as the file stores it and
/// decoded on request by its VR (PS3.5 section 6.2).
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-values"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public sealed class DicomElement
{
    private static readonly DicomDataset[] NoItems = [];

    private readonly bool _bigEndian;
    private readonly DicomCharacterSet _characters;
    private readonly IReadOnlyList<DicomDataset> _items;

    internal DicomElement(DicomTag tag, DicomVR vr, ReadOnlyMemory<byte> value, bool bigEndian, DicomCharacterSet characters, long offset,
        IReadOnlyList<DicomDataset>? items = null, DicomFragments? fragments = null)
    {
        Tag = tag;
        VR = vr;
        RawValue = value;
        _bigEndian = bigEndian;
        _characters = characters;
        Offset = offset;
        _items = items ?? NoItems;
        Fragments = fragments;
    }

    /// <summary>Gets the tag.</summary>
    public DicomTag Tag { get; }

    /// <summary>Gets whether <see cref="RawValue"/> is big-endian, as in a file of Explicit VR Big Endian.</summary>
    internal bool IsBigEndian => _bigEndian;

    /// <summary>
    /// Gets the value representation: the one the file wrote, or for implicit VR files the one PS3.6 gives the tag (<see cref="DicomVR.UN"/>
    /// for private and unknown tags).
    /// </summary>
    public DicomVR VR { get; }

    /// <summary>
    /// Gets the value's bytes as the file stores them, padding included, in the byte order of the file's
    /// <see cref="DicomFile.TransferSyntax"/>. Empty for a sequence and for encapsulated pixel data, whose parts are
    /// <see cref="Items"/> and <see cref="Fragments"/>.
    /// </summary>
    public ReadOnlyMemory<byte> RawValue { get; }

    /// <summary>Gets the byte offset of the element's tag in the dataset as read (after inflating a deflated file).</summary>
    internal long Offset { get; }

    /// <summary>Gets the items of a sequence (<see cref="DicomVR.SQ"/>), each a dataset; empty for other elements.</summary>
    public IReadOnlyList<DicomDataset> Items => _items;

    /// <summary>Gets the fragments of encapsulated (compressed) pixel data, or <see langword="null"/> for any other element.</summary>
    public DicomFragments? Fragments { get; }

    /// <summary>Gets whether the value is empty: no bytes, no items and no fragments.</summary>
    public bool IsEmpty => RawValue.IsEmpty && _items.Count == 0 && Fragments is null;

    /// <summary>
    /// Returns the value as text: text VRs decoded by the dataset's Specific Character Set with trailing padding removed, every
    /// value of a multi-valued string included with its <c>\</c> separators; numbers and tags of binary VRs written out the same
    /// way, such as <c>512\512</c>.
    /// </summary>
    /// <returns>The value as text; empty when the value is empty.</returns>
    /// <exception cref="InvalidOperationException">The VR holds bytes or items, not values: OB, UN, SQ or encapsulated pixel data.</exception>
    public string GetString()
    {
        if (IsText(VR))
        {
            return Text().TrimEnd(' ', '\0');
        }

        return string.Join('\\', GetStrings());
    }

    /// <summary>
    /// Returns each value: a multi-valued string split at its <c>\</c> separators with padding removed, or each number or tag of
    /// a binary VR written out. Long Text, Short Text, Unlimited Text and URIs are one value.
    /// </summary>
    /// <returns>The values; none when the value is empty.</returns>
    /// <exception cref="InvalidOperationException">The VR holds bytes or items, not values: OB, UN, SQ or encapsulated pixel data.</exception>
    public IReadOnlyList<string> GetStrings()
    {
        if (IsText(VR))
        {
            string text = Text();
            if (VR is DicomVR.LT or DicomVR.ST or DicomVR.UT or DicomVR.UR)
            {
                string single = text.TrimEnd(' ', '\0');
                return single.Length == 0 ? [] : [single];
            }

            if (text.TrimEnd(' ', '\0').Length == 0)
            {
                return [];
            }

            return [.. text.Split('\\').Select(v => v.Trim(' ', '\0'))];
        }

        if (VR == DicomVR.AT)
        {
            ReadOnlySpan<byte> bytes = RawValue.Span;
            string[] tags = new string[bytes.Length / 4];
            for (int i = 0; i < tags.Length; i++)
            {
                tags[i] = new DicomTag(U16(bytes[(i * 4)..]), U16(bytes[((i * 4) + 2)..])).ToString();
            }

            return tags;
        }

        if (VR is DicomVR.UV or DicomVR.SV or DicomVR.OV)
        {
            return [.. GetIntegers().Select((v, i) => VR == DicomVR.SV ? v.ToString(CultureInfo.InvariantCulture) : ((ulong)v).ToString(CultureInfo.InvariantCulture))];
        }

        return [.. GetNumbers().Select(v => v.ToString("R", CultureInfo.InvariantCulture))];
    }

    /// <summary>
    /// Returns the numbers of a numeric VR: decimal and integer strings (DS, IS) parsed, and the binary numbers of US, SS, UL,
    /// SL, UV, SV, FL, FD, OW, OL, OV, OF and OD.
    /// </summary>
    /// <returns>The numbers; none when the value is empty.</returns>
    /// <exception cref="InvalidOperationException">The VR holds no numbers.</exception>
    /// <exception cref="FormatException">A DS or IS value is not a number.</exception>
    public double[] GetNumbers()
    {
        ReadOnlySpan<byte> b = RawValue.Span;
        switch (VR)
        {
            case DicomVR.DS:
            case DicomVR.IS:
                return [.. GetStrings().Where(v => v.Length > 0).Select(v => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture))];
            case DicomVR.FL:
            case DicomVR.OF:
                return Read(b, 4, s => (double)(_bigEndian ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s)));
            case DicomVR.FD:
            case DicomVR.OD:
                return Read(b, 8, s => _bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s));
            case DicomVR.US:
            case DicomVR.OW:
            case DicomVR.SS:
            case DicomVR.UL:
            case DicomVR.OL:
            case DicomVR.SL:
            case DicomVR.UV:
            case DicomVR.OV:
            case DicomVR.SV:
                return [.. GetIntegers().Select(v => VR is DicomVR.UV or DicomVR.OV ? (double)(ulong)v : v)];
            default:
                throw new InvalidOperationException($"{Tag} is {VR}, which holds no numbers.");
        }
    }

    /// <summary>
    /// Returns the integers of an integer VR: integer strings (IS) parsed, and the binary integers of US, SS, UL, SL, UV, SV, OW,
    /// OL and OV. UV and OV values above <see cref="long.MaxValue"/> come back as their two's-complement bits.
    /// </summary>
    /// <returns>The integers; none when the value is empty.</returns>
    /// <exception cref="InvalidOperationException">The VR holds no integers.</exception>
    /// <exception cref="FormatException">An IS value is not an integer.</exception>
    public long[] GetIntegers()
    {
        ReadOnlySpan<byte> b = RawValue.Span;
        return VR switch
        {
            DicomVR.IS => [.. GetStrings().Where(v => v.Length > 0).Select(ParseInteger)],
            DicomVR.US or DicomVR.OW => Read(b, 2, s => (long)U16(s)),
            DicomVR.SS => Read(b, 2, s => (long)(short)U16(s)),
            DicomVR.UL or DicomVR.OL => Read(b, 4, s => (long)U32(s)),
            DicomVR.SL => Read(b, 4, s => (long)(int)U32(s)),
            DicomVR.UV or DicomVR.OV or DicomVR.SV => Read(b, 8, s => _bigEndian ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s)),
            _ => throw new InvalidOperationException($"{Tag} is {VR}, which holds no integers."),
        };
    }

    /// <summary>Returns the tags of an Attribute Tag (AT) value.</summary>
    /// <returns>The tags; none when the value is empty.</returns>
    /// <exception cref="InvalidOperationException">The VR is not AT.</exception>
    public DicomTag[] GetTags()
    {
        if (VR != DicomVR.AT)
        {
            throw new InvalidOperationException($"{Tag} is {VR}, not AT.");
        }

        ReadOnlySpan<byte> bytes = RawValue.Span;
        DicomTag[] tags = new DicomTag[bytes.Length / 4];
        for (int i = 0; i < tags.Length; i++)
        {
            tags[i] = new DicomTag(U16(bytes[(i * 4)..]), U16(bytes[((i * 4) + 2)..]));
        }

        return tags;
    }

    /// <summary>Returns the first date of a Date (DA) value: <c>YYYYMMDD</c>, or the older <c>YYYY.MM.DD</c>.</summary>
    /// <returns>The date, or <see langword="null"/> when the value is empty.</returns>
    /// <exception cref="InvalidOperationException">The VR is not DA.</exception>
    /// <exception cref="FormatException">The value is not a date.</exception>
    public DateOnly? GetDate()
    {
        string? value = First(DicomVR.DA);
        if (value is null)
        {
            return null;
        }

        string digits = value.Replace(".", "", StringComparison.Ordinal);
        return DateOnly.ParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture);
    }

    /// <summary>Returns the first time of a Time (TM) value: <c>HHMMSS.FFFFFF</c> with trailing parts optional, or the older <c>HH:MM:SS</c>.</summary>
    /// <returns>The time, or <see langword="null"/> when the value is empty.</returns>
    /// <exception cref="InvalidOperationException">The VR is not TM.</exception>
    /// <exception cref="FormatException">The value is not a time.</exception>
    public TimeOnly? GetTime()
    {
        string? value = First(DicomVR.TM);
        return value is null ? null : ParseTime(value.Replace(":", "", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns the first date and time of a Date Time (DT) value: <c>YYYYMMDDHHMMSS.FFFFFF&amp;ZZXX</c> with trailing parts
    /// optional. A value without its UTC offset (<c>&amp;ZZXX</c>) is given offset zero.
    /// </summary>
    /// <returns>The date and time, or <see langword="null"/> when the value is empty.</returns>
    /// <exception cref="InvalidOperationException">The VR is not DT.</exception>
    /// <exception cref="FormatException">The value is not a date and time.</exception>
    public DateTimeOffset? GetDateTime()
    {
        string? value = First(DicomVR.DT);
        if (value is null)
        {
            return null;
        }

        TimeSpan offset = TimeSpan.Zero;
        int sign = value.IndexOfAny(['+', '-']);
        if (sign >= 0)
        {
            string zone = value[(sign + 1)..];
            if (zone.Length != 4 || !int.TryParse(zone.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
                || !int.TryParse(zone.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
            {
                throw new FormatException($"'{value}' has no valid UTC offset.");
            }

            offset = new TimeSpan(hours, minutes, 0) * (value[sign] == '-' ? -1 : 1);
            value = value[..sign];
        }

        if (value.Length < 4 || !int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year))
        {
            throw new FormatException($"'{value}' is not a DICOM date and time.");
        }

        int month = Part(value, 4, 1), day = Part(value, 6, 1);
        TimeOnly time = value.Length > 8 ? ParseTime(value[8..]) : TimeOnly.MinValue;
        try
        {
            return new DateTimeOffset(new DateTime(new DateOnly(year, month, day), time), offset);
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new FormatException($"'{value}' is not a valid date and time.", e);
        }
    }

    /// <summary>Returns the first name of a Person Name (PN) value.</summary>
    /// <returns>The name, or <see langword="null"/> when the value is empty.</returns>
    /// <exception cref="InvalidOperationException">The VR is not PN.</exception>
    public DicomPersonName? GetPersonName()
    {
        string? value = First(DicomVR.PN);
        return value is null ? null : DicomPersonName.Parse(value);
    }

    /// <summary>Returns the element as a line of a dump, such as <c>(0010,0010) PN PatientName: Doe^John</c>.</summary>
    /// <returns>The element as text.</returns>
    public override string ToString()
    {
        string name = Tag.Keyword ?? (Tag.IsPrivate ? "Private" : "Unknown");
        string value = VR switch
        {
            DicomVR.SQ => $"{_items.Count} items",
            _ when Fragments is not null => $"{Fragments.Fragments.Count} fragments",
            DicomVR.OB or DicomVR.UN or DicomVR.OW or DicomVR.OF or DicomVR.OD or DicomVR.OL or DicomVR.OV => $"{RawValue.Length} bytes",
            _ => GetString(),
        };
        return $"{Tag} {VR} {name}: {value}";
    }

    internal static bool IsText(DicomVR vr)
    {
        return vr is DicomVR.AE or DicomVR.AS or DicomVR.CS or DicomVR.DA or DicomVR.DS or DicomVR.DT or DicomVR.IS or DicomVR.LO
            or DicomVR.LT or DicomVR.PN or DicomVR.SH or DicomVR.ST or DicomVR.TM or DicomVR.UC or DicomVR.UI or DicomVR.UR or DicomVR.UT;
    }

    /// <summary>Returns whether a VR's text is in the dataset's character set rather than the default repertoire (PS3.5 6.1.2.3).</summary>
    private static bool UsesCharacterSet(DicomVR vr)
    {
        return vr is DicomVR.SH or DicomVR.LO or DicomVR.ST or DicomVR.LT or DicomVR.PN or DicomVR.UC or DicomVR.UT;
    }

    /// <summary>
    /// Returns the value as Latin-1 text whatever the VR says, padding removed, or <see langword="null"/> for items: for the
    /// parser's own reads of the transfer syntax and character set, which a damaged file may give the wrong VR.
    /// </summary>
    internal string? LooseText()
    {
        return Fragments is not null || VR == DicomVR.SQ ? null : Encoding.Latin1.GetString(RawValue.Span).Trim(' ', '\0');
    }

    private string Text()
    {
        if (Fragments is not null || VR == DicomVR.SQ)
        {
            throw new InvalidOperationException($"{Tag} holds items, not text.");
        }

        return UsesCharacterSet(VR) ? _characters.Decode(RawValue.Span) : Encoding.Latin1.GetString(RawValue.Span);
    }

    private string? First(DicomVR vr)
    {
        if (VR != vr)
        {
            throw new InvalidOperationException($"{Tag} is {VR}, not {vr}.");
        }

        IReadOnlyList<string> values = GetStrings();
        return values.Count == 0 || values[0].Length == 0 ? null : values[0];
    }

    private static long ParseInteger(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
            ? n
            : throw new FormatException($"'{value}' is not an integer that fits 64 bits.");
    }

    private static TimeOnly ParseTime(string value)
    {
        int hour = Part(value, 0, 0), minute = Part(value, 2, 0), second = Part(value, 4, 0);
        long ticks = 0;
        int dot = value.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            string fraction = value[(dot + 1)..];
            if (fraction.Length is 0 or > 6 || !int.TryParse(fraction, NumberStyles.None, CultureInfo.InvariantCulture, out int f))
            {
                throw new FormatException($"'{value}' has no valid fraction of a second.");
            }

            ticks = f * (long)Math.Pow(10, 7 - fraction.Length);
        }
        else if (value.Length is not (2 or 4 or 6))
        {
            throw new FormatException($"'{value}' is not a DICOM time.");
        }

        if (second == 60)
        {
            second = 59; // a leap second
        }

        if (hour > 23 || minute > 59 || second > 59)
        {
            throw new FormatException($"'{value}' is not a valid time.");
        }

        return new TimeOnly(hour, minute, second).Add(TimeSpan.FromTicks(ticks));
    }

    /// <summary>Reads two digits at <paramref name="at"/>, or <paramref name="missing"/> when the value ends before them.</summary>
    private static int Part(string value, int at, int missing)
    {
        if (value.Length <= at || value[at] == '.')
        {
            return missing;
        }

        return value.Length >= at + 2 && int.TryParse(value.AsSpan(at, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            ? n
            : throw new FormatException($"'{value}' is not a DICOM date or time.");
    }

    private ushort U16(ReadOnlySpan<byte> s) => _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private uint U32(ReadOnlySpan<byte> s) => _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);

    private static T[] Read<T>(ReadOnlySpan<byte> bytes, int size, ReadFunc<T> read)
    {
        T[] values = new T[bytes.Length / size];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = read(bytes.Slice(i * size, size));
        }

        return values;
    }

    private delegate T ReadFunc<T>(ReadOnlySpan<byte> bytes);
}

/// <summary>
/// A Person Name (PN) value: up to three component groups, alphabetic, ideographic and phonetic, separated by <c>=</c>; the
/// alphabetic one split into its five components (PS3.5 section 6.2.1).
/// </summary>
/// <param name="Alphabetic">The alphabetic group, such as <c>Doe^John^^Dr.</c>.</param>
/// <param name="Ideographic">The ideographic group, or empty.</param>
/// <param name="Phonetic">The phonetic group, or empty.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-values"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public sealed record DicomPersonName(string Alphabetic, string Ideographic, string Phonetic)
{
    /// <summary>Gets the family name: the alphabetic group's first component.</summary>
    public string FamilyName => Component(0);

    /// <summary>Gets the given name: the second component.</summary>
    public string GivenName => Component(1);

    /// <summary>Gets the middle name: the third component.</summary>
    public string MiddleName => Component(2);

    /// <summary>Gets the name prefix, such as a title: the fourth component.</summary>
    public string NamePrefix => Component(3);

    /// <summary>Gets the name suffix: the fifth component.</summary>
    public string NameSuffix => Component(4);

    /// <summary>Returns the name as the file writes it, groups joined by <c>=</c> and empty trailing groups left out.</summary>
    /// <returns>The name.</returns>
    public override string ToString() => string.Join('=', new[] { Alphabetic, Ideographic, Phonetic }).TrimEnd('=');

    internal static DicomPersonName Parse(string value)
    {
        string[] groups = value.Split('=');
        return new DicomPersonName(groups[0], groups.Length > 1 ? groups[1] : "", groups.Length > 2 ? groups[2] : "");
    }

    private string Component(int index)
    {
        string[] parts = Alphabetic.Split('^');
        return index < parts.Length ? parts[index].Trim() : "";
    }
}

/// <summary>
/// Encapsulated pixel data (PS3.5 section A.4): the Basic Offset Table, the fragments that follow it, and the Extended Offset
/// Table when the dataset has one. The fragments are compressed data as stored; a frame may span several.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-fragments"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public sealed class DicomFragments
{
    internal DicomFragments(uint[] basicOffsetTable, ReadOnlyMemory<byte>[] fragments)
    {
        BasicOffsetTable = basicOffsetTable;
        Fragments = fragments;
    }

    /// <summary>
    /// Gets the Basic Offset Table: for each frame, the byte offset of its first fragment's item tag from the first fragment's
    /// item tag. Empty when the file left it empty.
    /// </summary>
    public IReadOnlyList<uint> BasicOffsetTable { get; }

    /// <summary>Gets the Extended Offset Table (7FE0,0001): each frame's offset like <see cref="BasicOffsetTable"/>'s, or empty.</summary>
    public IReadOnlyList<ulong> ExtendedOffsetTable { get; internal set; } = [];

    /// <summary>Gets the Extended Offset Table Lengths (7FE0,0002): each frame's length in bytes, or empty.</summary>
    public IReadOnlyList<ulong> ExtendedOffsetTableLengths { get; internal set; } = [];

    /// <summary>Gets the fragments' values, in file order, without their item headers.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Fragments { get; }
}
