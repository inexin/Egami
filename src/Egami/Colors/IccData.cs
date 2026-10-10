using System.Buffers.Binary;
using System.Text;

namespace Egami.Colors;

/// <summary>
/// A parsed ICC profile (ICC.1, versions 2 and 4): the header fields, the description and copyright text, and every tag with its
/// bytes. Read-only; <see cref="ColorProfile.FromIcc(IccData)"/> turns it into an encoding that conversion can use.
/// </summary>
/// <remarks>
/// Parsing never throws. A profile with offsets or sizes outside it, a missing signature, or more bytes than
/// <see cref="SafetyLimits.MaxMetadataBytes"/> is marked damaged; what could be read is available, and <see cref="ToBytes"/>
/// returns the bytes given.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="icc"/>
/// </example>
public sealed class IccData
{
    private const int HeaderSize = 128;

    private readonly byte[] _bytes;
    private readonly List<string> _warnings = [];
    private readonly List<IccTag> _tags = [];

    private IccData(byte[] bytes)
    {
        _bytes = bytes;
    }

    /// <summary>Gets the profile version, such as 2.1 or 4.3.</summary>
    public Version Version { get; private set; } = new(0, 0);

    /// <summary>Gets the profile class.</summary>
    public IccProfileClass ProfileClass { get; private set; }

    /// <summary>Gets the data color space signature without trailing spaces, such as "RGB", "GRAY", "CMYK" or "Lab".</summary>
    public string DataSpace { get; private set; } = "";

    /// <summary>Gets the profile connection space signature without trailing spaces: "XYZ" or "Lab".</summary>
    public string ConnectionSpace { get; private set; } = "";

    /// <summary>
    /// Gets the color model of the data space, or <see langword="null"/> when it is not RGB, gray or CMYK (Egami converts only
    /// those).
    /// </summary>
    public ColorModel? Model => DataSpace switch
    {
        "RGB" => ColorModel.Rgb,
        "GRAY" => ColorModel.Gray,
        "CMYK" => ColorModel.Cmyk,
        _ => null,
    };

    /// <summary>Gets the rendering intent the header suggests.</summary>
    public RenderingIntent RenderingIntent { get; private set; }

    /// <summary>Gets when the profile was created, as UTC, or <see langword="null"/> when the header's date is not a valid one.</summary>
    public DateTime? Created { get; private set; }

    /// <summary>Gets the description (the <c>desc</c> tag, English when it has several languages), or <see langword="null"/>.</summary>
    public string? Description { get; private set; }

    /// <summary>Gets the copyright text (the <c>cprt</c> tag), or <see langword="null"/>.</summary>
    public string? Copyright { get; private set; }

    /// <summary>
    /// Gets the H.273 code points of a version 4.4 <c>cicp</c> tag, which HDR profiles use to name PQ or HLG exactly, or
    /// <see langword="null"/>.
    /// </summary>
    public ColorCodePoints? CodePoints { get; private set; }

    /// <summary>Gets every tag, in the order of the tag table.</summary>
    public IReadOnlyList<IccTag> Tags => _tags;

    /// <summary>
    /// Gets whether the profile was damaged when read: a missing <c>acsp</c> signature, a size or offset outside it, or more
    /// bytes than allowed. What could be read is available.
    /// </summary>
    public bool IsDamaged { get; private set; }

    /// <summary>Gets what was wrong with a damaged profile, as sentences.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Reads an ICC profile. Never throws: a profile larger than <see cref="SafetyLimits.MaxMetadataBytes"/> is kept but not
    /// read, and anything outside the bytes given is skipped; either way the profile is marked damaged.
    /// </summary>
    /// <param name="profile">The profile, as stored in a PNG iCCP chunk, JPEG APP2 segments or a TIFF tag.</param>
    /// <param name="limits">The limits whose <see cref="SafetyLimits.MaxMetadataBytes"/> caps the profile size. Defaults to <see cref="SafetyLimits.Default"/>.</param>
    /// <returns>The parsed profile; <see cref="IsDamaged"/> says whether it was all readable.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="icc"/>
    /// </example>
    public static IccData Parse(ReadOnlySpan<byte> profile, SafetyLimits? limits = null)
    {
        IccData icc = new(profile.ToArray());
        icc.Read((limits ?? SafetyLimits.Default).MaxMetadataBytes);
        return icc;
    }

    /// <summary>Returns the tag with this signature, if the profile has it.</summary>
    /// <param name="signature">The tag signature, such as "rTRC"; trailing spaces are optional.</param>
    /// <param name="tag">The tag, or the default value.</param>
    /// <returns>Whether the profile has the tag.</returns>
    public bool TryGetTag(string signature, out IccTag tag)
    {
        ArgumentNullException.ThrowIfNull(signature);
        signature = signature.TrimEnd(' ');
        foreach (IccTag t in _tags)
        {
            if (t.Signature == signature)
            {
                tag = t;
                return true;
            }
        }

        tag = default;
        return false;
    }

    /// <summary>Returns the profile's bytes, exactly as given to <see cref="Parse"/>.</summary>
    /// <returns>The bytes.</returns>
    public ReadOnlyMemory<byte> ToBytes() => _bytes;

    /// <inheritdoc/>
    public override string ToString() => $"ICC {Version.Major}.{Version.Minor} {ProfileClass} {DataSpace}/{ConnectionSpace}{(Description is null ? "" : $" \"{Description}\"")}";

    internal static string Signature(ReadOnlySpan<byte> four)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = four[i];
            chars[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }

        return new string(chars).TrimEnd(' ');
    }

    internal void Warn(string warning)
    {
        IsDamaged = true;
        _warnings.Add(warning);
    }

    private void Read(long maxBytes)
    {
        ReadOnlySpan<byte> data = _bytes;
        if (data.Length > maxBytes)
        {
            Warn($"The profile is {data.Length} bytes, more than the {maxBytes} allowed; it was kept but not read.");
            return;
        }

        if (data.Length < HeaderSize + 4)
        {
            Warn($"The profile is {data.Length} bytes, too short for a header and tag table.");
            return;
        }

        uint declared = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (declared > data.Length)
        {
            Warn($"The header says {declared} bytes, but the profile has {data.Length}; it may be cut short.");
        }
        else if (declared >= HeaderSize + 4)
        {
            data = data[..(int)declared];
        }

        if (!data.Slice(36, 4).SequenceEqual("acsp"u8))
        {
            Warn("The header has no 'acsp' signature, so this may not be an ICC profile.");
        }

        Version = new Version(data[8], data[9] >> 4, data[9] & 0xF);
        ProfileClass = Signature(data.Slice(12, 4)) switch
        {
            "scnr" => IccProfileClass.Input,
            "mntr" => IccProfileClass.Display,
            "prtr" => IccProfileClass.Output,
            "link" => IccProfileClass.DeviceLink,
            "spac" => IccProfileClass.ColorSpace,
            "abst" => IccProfileClass.Abstract,
            "nmcl" => IccProfileClass.NamedColor,
            _ => IccProfileClass.Unknown,
        };
        DataSpace = Signature(data.Slice(16, 4));
        ConnectionSpace = Signature(data.Slice(20, 4));
        Created = Date(data.Slice(24, 12));
        uint intent = BinaryPrimitives.ReadUInt32BigEndian(data[64..]) & 0xFFFF;
        RenderingIntent = intent <= 3 ? (RenderingIntent)intent : RenderingIntent.Perceptual;

        uint count = BinaryPrimitives.ReadUInt32BigEndian(data[HeaderSize..]);
        long room = (data.Length - HeaderSize - 4) / 12;
        if (count > room)
        {
            Warn($"The tag table lists {count} tags, but there is room for {room}.");
            count = (uint)room;
        }

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = data.Slice(HeaderSize + 4 + (i * 12), 12);
            string signature = Signature(entry);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset > data.Length || size > data.Length - offset || size < 8)
            {
                Warn($"Tag '{signature}' is outside the profile (offset {offset}, size {size}); it was skipped.");
                continue;
            }

            if (TryGetTag(signature, out _))
            {
                Warn($"Tag '{signature}' appears twice; the first was kept.");
                continue;
            }

            _tags.Add(new IccTag(signature, Signature(data.Slice((int)offset, 4)), _bytes.AsMemory((int)offset, (int)size)));
        }

        Description = TextTag("desc");
        Copyright = TextTag("cprt");
        if (TryGetTag("cicp", out IccTag cicp))
        {
            ReadOnlySpan<byte> c = cicp.Data.Span;
            if (cicp.Type == "cicp" && c.Length >= 12)
            {
                CodePoints = new ColorCodePoints(c[8], c[9], c[10], c[11] != 0);
            }
            else
            {
                Warn("The 'cicp' tag is not a cicpType of 12 bytes; it was ignored.");
            }
        }
    }

    private static DateTime? Date(ReadOnlySpan<byte> d)
    {
        int year = BinaryPrimitives.ReadUInt16BigEndian(d), month = BinaryPrimitives.ReadUInt16BigEndian(d[2..]), day = BinaryPrimitives.ReadUInt16BigEndian(d[4..]);
        int hour = BinaryPrimitives.ReadUInt16BigEndian(d[6..]), minute = BinaryPrimitives.ReadUInt16BigEndian(d[8..]), second = BinaryPrimitives.ReadUInt16BigEndian(d[10..]);
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
    }

    private string? TextTag(string signature)
    {
        if (!TryGetTag(signature, out IccTag tag))
        {
            return null;
        }

        string? text = tag.Type switch
        {
            "desc" => TextDescription(tag.Data.Span),
            "mluc" => MultiLocalized(tag.Data.Span),
            "text" => Ascii(tag.Data.Span[8..]),
            _ => null,
        };
        if (text is null)
        {
            Warn($"Tag '{signature}' of type '{tag.Type}' could not be read as text.");
        }

        return text;
    }

    // textDescriptionType (v2): an ASCII count including the terminating zero, then the ASCII text; the Unicode and ScriptCode
    // forms that follow are not needed.
    private static string? TextDescription(ReadOnlySpan<byte> d)
    {
        if (d.Length < 12)
        {
            return null;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(d[8..]);
        return count > d.Length - 12 ? null : Ascii(d.Slice(12, (int)count));
    }

    // multiLocalizedUnicodeType (v4): records of language, country, length and offset; English is preferred, else the first.
    private static string? MultiLocalized(ReadOnlySpan<byte> d)
    {
        if (d.Length < 16)
        {
            return null;
        }

        uint records = BinaryPrimitives.ReadUInt32BigEndian(d[8..]);
        uint recordSize = BinaryPrimitives.ReadUInt32BigEndian(d[12..]);
        if (records == 0 || recordSize < 12 || records > (d.Length - 16) / recordSize)
        {
            return null;
        }

        int chosen = 0;
        for (int i = 0; i < records; i++)
        {
            if (d.Slice(16 + (int)(i * recordSize), 2).SequenceEqual("en"u8))
            {
                chosen = i;
                break;
            }
        }

        ReadOnlySpan<byte> record = d.Slice(16 + (int)(chosen * recordSize), 12);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
        if (offset > d.Length || length > d.Length - offset)
        {
            return null;
        }

        return Encoding.BigEndianUnicode.GetString(d.Slice((int)offset, (int)length & ~1)).TrimEnd('\0');
    }

    private static string Ascii(ReadOnlySpan<byte> d)
    {
        int end = d.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? d : d[..end]);
    }
}
