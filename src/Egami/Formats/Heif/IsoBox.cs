using System.Buffers.Binary;

namespace Egami.Formats.Heif;

/// <summary>A box of ISO/IEC 14496-12 (ISO BMFF): its type and where its header and contents lie in the buffer read.</summary>
/// <param name="Type">The four-character type, big-endian.</param>
/// <param name="Start">Where the box starts.</param>
/// <param name="ContentStart">Where its contents start, after the header (and the extended type of a uuid box).</param>
/// <param name="End">Where the box ends.</param>
internal readonly record struct IsoBox(uint Type, int Start, int ContentStart, int End)
{
    /// <summary>Gets the size of the box's contents.</summary>
    internal int ContentLength => End - ContentStart;

    /// <summary>Gets the box's contents.</summary>
    internal ReadOnlySpan<byte> Content(ReadOnlySpan<byte> buffer) => buffer[ContentStart..End];

    /// <summary>Gets the box's contents.</summary>
    internal ReadOnlyMemory<byte> Content(ReadOnlyMemory<byte> buffer) => buffer[ContentStart..End];

    /// <inheritdoc/>
    public override string ToString() => $"{FourCC.ToString(Type)} at {Start}, {End - Start} bytes";
}

/// <summary>Four-character codes as big-endian numbers, as ISO BMFF stores box and item types.</summary>
internal static class FourCC
{
    internal const uint Ftyp = 0x66747970;
    internal const uint Meta = 0x6D657461;
    internal const uint Hdlr = 0x68646C72;
    internal const uint Pitm = 0x7069746D;
    internal const uint Iloc = 0x696C6F63;
    internal const uint Iinf = 0x69696E66;
    internal const uint Infe = 0x696E6665;
    internal const uint Iref = 0x69726566;
    internal const uint Iprp = 0x69707270;
    internal const uint Ipco = 0x6970636F;
    internal const uint Ipma = 0x69706D61;
    internal const uint Idat = 0x69646174;
    internal const uint Grpl = 0x6772706C;
    internal const uint Uuid = 0x75756964;
    internal const uint Mime = 0x6D696D65;
    internal const uint Uri = 0x75726920;
    internal const uint Pict = 0x70696374;

    /// <summary>Gets the code of four ASCII characters.</summary>
    internal static uint Of(ReadOnlySpan<char> text) => (uint)text[0] << 24 | (uint)text[1] << 16 | (uint)text[2] << 8 | text[3];

    /// <summary>Gets the code as text, with characters outside printable ASCII escaped.</summary>
    internal static string ToString(uint code)
    {
        Span<char> text = stackalloc char[16];
        int n = 0;
        for (int shift = 24; shift >= 0; shift -= 8)
        {
            byte b = (byte)(code >> shift);
            if (b is >= 0x20 and < 0x7F)
            {
                text[n++] = (char)b;
            }
            else
            {
                text[n++] = '\\';
                text[n++] = 'x';
                text[n++] = "0123456789abcdef"[b >> 4];
                text[n++] = "0123456789abcdef"[b & 15];
            }
        }

        return new string(text[..n]);
    }
}

/// <summary>
/// Reads the boxes of ISO/IEC 14496-12 from a buffer, one level at a time: 32-bit sizes, 64-bit large sizes, size 0 (to the end of
/// the enclosing box) and uuid types. A box that runs past its parent, or a size too small for its header, is corrupt.
/// </summary>
internal ref struct IsoBoxReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly int _end;
    private readonly string _formatName;
    private readonly long _origin;
    private int _position;

    /// <summary>Starts reading the boxes between <paramref name="start"/> and <paramref name="end"/>.</summary>
    /// <param name="buffer">The bytes read.</param>
    /// <param name="start">Where the first box starts.</param>
    /// <param name="end">Where the enclosing box's contents end.</param>
    /// <param name="formatName">The format named in a <see cref="CorruptDataException"/>.</param>
    /// <param name="origin">Where <paramref name="buffer"/> starts in the file, for the offsets of errors.</param>
    internal IsoBoxReader(ReadOnlySpan<byte> buffer, int start, int end, string formatName, long origin = 0)
    {
        _buffer = buffer;
        _position = start;
        _end = end;
        _formatName = formatName;
        _origin = origin;
    }

    /// <summary>Reads the next box; returns <see langword="false"/> at the end of the enclosing box.</summary>
    internal bool Next(out IsoBox box)
    {
        box = default;
        if (_position >= _end)
        {
            return false;
        }

        if (_end - _position < 8)
        {
            throw Corrupt(_position, "A box header is cut off.");
        }

        ReadOnlySpan<byte> head = _buffer[_position..];
        long size = BinaryPrimitives.ReadUInt32BigEndian(head);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
        int header = 8;
        if (size == 1)
        {
            if (_end - _position < 16)
            {
                throw Corrupt(_position, $"The large size of a \"{FourCC.ToString(type)}\" box is cut off.");
            }

            ulong large = BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
            size = large > long.MaxValue ? long.MaxValue : (long)large;
            header = 16;
        }
        else if (size == 0)
        {
            size = _end - _position;
        }

        if (type == FourCC.Uuid)
        {
            header += 16;
        }

        if (size < header)
        {
            throw Corrupt(_position, $"A \"{FourCC.ToString(type)}\" box's size, {size}, is smaller than its header.", truncation: false);
        }

        if (size > _end - _position)
        {
            throw Corrupt(_position, $"A \"{FourCC.ToString(type)}\" box of {size} bytes runs past its end ({_end - _position} bytes left).");
        }

        box = new IsoBox(type, _position, _position + header, _position + (int)size);
        _position = box.End;
        return true;
    }

    private readonly CorruptDataException Corrupt(int position, string problem, bool truncation = true) =>
        new(_formatName, _origin + position, problem) { IsTruncation = truncation };
}
