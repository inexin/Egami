using System.Buffers.Binary;
using System.Text;

namespace Egami.Formats.Extended.Dpx;

/// <summary>One image element of a DPX file (ST 268 image information header, 72 bytes each).</summary>
internal sealed class DpxElement
{
    public int Index { get; init; }

    public bool Signed { get; init; }

    public uint LowCode { get; init; }

    public uint HighCode { get; init; }

    public int Descriptor { get; init; }

    public DpxTransfer Transfer { get; init; }

    public DpxTransfer Colorimetric { get; init; }

    public int Bits { get; init; }

    public int Packing { get; init; }

    public int Encoding { get; init; }

    public long Offset { get; set; }

    public uint EndOfLinePadding { get; init; }

    public string Description { get; init; } = "";
}

/// <summary>
/// The DPX header, written from SMPTE ST 268 (versions 1 and 2): the file information (768 bytes), the image information with
/// eight image elements (640 bytes), the orientation header (256 bytes) and, when the image data starts at 2048 or later, the
/// film (256 bytes) and television (128 bytes) headers. Every field is in the file's byte order.
/// </summary>
internal sealed class DpxHeader
{
    public const int GenericLength = 1664;
    public const int Length = 2048;
    private const uint Undefined = 0xFFFFFFFF;

    public bool BigEndian { get; private init; }

    public long ImageOffset { get; private init; }

    public long FileSize { get; private init; }

    public int Width { get; private init; }

    public int Height { get; private init; }

    public int Orientation { get; private init; }

    public DpxElement[] Elements { get; private init; } = [];

    /// <summary>Gets the header bytes read: 1664 to 2048 of them.</summary>
    public int Read { get; private init; }

    /// <summary>Gets the details every element shares; <see cref="Details"/> fills in an element's own.</summary>
    private DpxDetails Shared { get; init; } = new();

    /// <summary>Returns whether the data starts with "SDPX" or "XPDS".</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 4 && (header[..4].SequenceEqual("SDPX"u8) || header[..4].SequenceEqual("XPDS"u8));
    }

    /// <summary>Returns the details of one element.</summary>
    public DpxDetails Details(DpxElement element)
    {
        DpxDetails details = (DpxDetails)Shared.Copy();
        details.Element = element.Index;
        details.Descriptor = element.Descriptor;
        details.BitsPerSample = element.Bits;
        details.Packing = element.Packing;
        details.RunLength = element.Encoding == 1;
        details.Transfer = element.Transfer;
        details.Colorimetric = element.Colorimetric;
        details.Description = element.Description;
        return details;
    }

    /// <summary>Reads the header from the start of <paramref name="input"/>, leaving it after the bytes read.</summary>
    public static DpxHeader ReadFrom(EgamiInput input)
    {
        byte[] bytes = new byte[Length];
        input.ReadExactly(bytes.AsSpan(0, 8));
        if (!Matches(bytes))
        {
            throw input.Corrupt(0, "The DPX signature is wrong.");
        }

        bool big = bytes[0] == (byte)'S';
        uint offset = U32(bytes, 4, big);
        if (offset < GenericLength - 256)
        {
            throw input.Corrupt(4, $"The image data offset {offset} is inside the header.");
        }

        int read = (int)Math.Min(offset, Length);
        input.ReadExactly(bytes.AsSpan(8, read - 8));
        int count = U16(bytes, 770, big);
        int width = checked((int)Math.Min(U32(bytes, 772, big), int.MaxValue));
        int height = checked((int)Math.Min(U32(bytes, 776, big), int.MaxValue));
        if (count is 0 or > 8)
        {
            throw input.Corrupt(770, $"The file says it has {count} image elements; DPX has 1 to 8.");
        }

        if (width == 0 || height == 0)
        {
            throw input.Corrupt(772, $"The image size {width}×{height} is invalid.");
        }

        DpxElement[] elements = new DpxElement[count];
        for (int i = 0; i < count; i++)
        {
            int at = 780 + (i * 72);
            uint data = U32(bytes, at + 28, big);
            elements[i] = new DpxElement
            {
                Index = i,
                Signed = U32(bytes, at, big) == 1,
                LowCode = U32(bytes, at + 4, big),
                HighCode = U32(bytes, at + 12, big),
                Descriptor = bytes[at + 20],
                Transfer = (DpxTransfer)bytes[at + 21],
                Colorimetric = (DpxTransfer)bytes[at + 22],
                Bits = bytes[at + 23],
                Packing = U16(bytes, at + 24, big),
                Encoding = U16(bytes, at + 26, big),
                Offset = data is 0 or Undefined ? (i == 0 ? offset : -1) : data,
                EndOfLinePadding = U32(bytes, at + 32, big) is var pad && pad != Undefined ? pad : 0,
                Description = Text(bytes, at + 40, 32),
            };
        }

        DpxDetails shared = new()
        {
            Version = Text(bytes, 8, 8),
            BigEndian = big,
            ElementCount = count,
            Orientation = U16(bytes, 768, big),
            FileName = Text(bytes, 36, 100),
            CreationTime = Text(bytes, 136, 24),
            Creator = Text(bytes, 160, 100),
            Project = Text(bytes, 260, 200),
            Copyright = Text(bytes, 460, 200),
            InputDevice = read >= 1588 ? Text(bytes, 1556, 32) : "",
        };
        if (read >= 1920)
        {
            shared.FilmManufacturer = Text(bytes, 1664, 2);
            shared.FilmType = Text(bytes, 1666, 2);
            shared.FilmOffset = Text(bytes, 1668, 2);
            shared.FilmPrefix = Text(bytes, 1670, 6);
            shared.FilmCount = Text(bytes, 1676, 4);
            shared.FilmFormat = Text(bytes, 1680, 32);
            shared.FramePosition = Defined(U32(bytes, 1712, big));
            shared.SequenceLength = Defined(U32(bytes, 1716, big));
            shared.FrameRate = Defined(F32(bytes, 1724, big));
            shared.FrameId = Text(bytes, 1732, 32);
            shared.SlateInfo = Text(bytes, 1764, 100);
        }

        if (read >= Length)
        {
            if (Defined(U32(bytes, 1920, big)) is uint timecode)
            {
                shared.Timecode = FormatTimecode(timecode);
            }

            shared.UserBits = Defined(U32(bytes, 1924, big));
            shared.FrameRate ??= Defined(F32(bytes, 1940, big));
        }

        return new DpxHeader
        {
            BigEndian = big,
            ImageOffset = offset,
            FileSize = U32(bytes, 16, big),
            Width = width,
            Height = height,
            Orientation = shared.Orientation,
            Elements = elements,
            Read = read,
            Shared = shared,
        };
    }

    internal static string FormatTimecode(uint bcd)
    {
        Span<char> text = stackalloc char[11];
        for (int i = 0, n = 0; i < 8; i++)
        {
            int digit = (int)(bcd >> (28 - (i * 4))) & 0xF;
            text[n++] = (char)('0' + Math.Min(digit, 9));
            if (i % 2 == 1 && i < 7)
            {
                text[n++] = ':';
            }
        }

        return new string(text);
    }

    /// <summary>Parses "hh:mm:ss:ff" (any separators) into BCD; <see langword="null"/> when it is not eight digits.</summary>
    internal static uint? ParseTimecode(string? text)
    {
        if (text is null)
        {
            return null;
        }

        uint bcd = 0;
        int digits = 0;
        foreach (char c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                bcd = (bcd << 4) | (uint)(c - '0');
                digits++;
            }
        }

        return digits == 8 ? bcd : null;
    }

    internal static uint U32(ReadOnlySpan<byte> bytes, int at, bool big)
    {
        return big ? BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    }

    internal static int U16(ReadOnlySpan<byte> bytes, int at, bool big)
    {
        return big ? BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
    }

    internal static float F32(ReadOnlySpan<byte> bytes, int at, bool big)
    {
        return big ? BinaryPrimitives.ReadSingleBigEndian(bytes[at..]) : BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]);
    }

    internal static string Text(ReadOnlySpan<byte> bytes, int at, int length)
    {
        ReadOnlySpan<byte> field = bytes.Slice(at, length);
        int end = field.IndexOf((byte)0);
        field = end < 0 ? field : field[..end];
        if (field.Length > 0 && field[0] == 0xFF)
        {
            return "";
        }

        return Encoding.Latin1.GetString(field).Trim();
    }

    private static uint? Defined(uint value) => value == Undefined ? null : value;

    private static float? Defined(float value) => float.IsFinite(value) && BitConverter.SingleToUInt32Bits(value) != Undefined ? value : null;
}
