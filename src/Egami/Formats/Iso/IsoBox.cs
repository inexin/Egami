using System.Buffers.Binary;

namespace Egami.Formats.Iso;

/// <summary>
/// A four-character code as ISO/IEC 14496-12 (ISO BMFF) stores box, brand, handler and sample entry types: four bytes read
/// as a big-endian number.
/// </summary>
/// <param name="Value">The code as a big-endian number, so "ftyp" is 0x66747970.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/IsoBoxes.cs" region="walk"/>
/// </example>
public readonly record struct FourCC(uint Value)
{
    /// <summary>Gets the code of four characters, each U+0000 to U+00FF, such as "moov" or "mp4a".</summary>
    /// <param name="text">The four characters.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="text"/> is not four characters, or one is above U+00FF.</exception>
    public static FourCC Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out FourCC code) ? code : throw new FormatException($"\"{text}\" is not four characters from U+0000 to U+00FF.");
    }

    /// <summary>Tries to get the code of four characters, each U+0000 to U+00FF.</summary>
    /// <param name="text">The four characters.</param>
    /// <param name="code">The code, or the default when the text is not a code.</param>
    /// <returns>Whether <paramref name="text"/> is a code.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out FourCC code)
    {
        code = default;
        if (text.Length != 4 || text[0] > 0xFF || text[1] > 0xFF || text[2] > 0xFF || text[3] > 0xFF)
        {
            return false;
        }

        code = new FourCC((uint)text[0] << 24 | (uint)text[1] << 16 | (uint)text[2] << 8 | text[3]);
        return true;
    }

    /// <summary>Gets the code as a big-endian number.</summary>
    /// <param name="code">The code.</param>
    public static implicit operator uint(FourCC code) => code.Value;

    /// <summary>Gets the code as text, with bytes outside printable ASCII written as \x followed by two hex digits.</summary>
    /// <returns>The text, such as "moov" or "\xa9nam".</returns>
    public override string ToString()
    {
        Span<char> text = stackalloc char[16];
        int n = 0;
        for (int shift = 24; shift >= 0; shift -= 8)
        {
            byte b = (byte)(Value >> shift);
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
/// A box of ISO/IEC 14496-12 (ISO BMFF), as MP4, QuickTime, HEIF and AVIF files are made of: its type and where its header and
/// contents lie. Positions are byte offsets in the file when the reader was told where its data starts, else in the data read.
/// </summary>
/// <param name="Type">The box type, such as "moov".</param>
/// <param name="Start">Where the box starts: its size field.</param>
/// <param name="ContentStart">Where its contents start, after the size, the type, a large size and a uuid box's extended type.</param>
/// <param name="End">Where the box ends.</param>
/// <param name="UserType">A uuid box's extended type, or <see langword="null"/> for every other box.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/IsoBoxes.cs" region="walk"/>
/// </example>
public readonly record struct IsoBox(FourCC Type, long Start, long ContentStart, long End, Guid? UserType = null)
{
    /// <summary>Gets the size of the whole box, header included.</summary>
    public long Size => End - Start;

    /// <summary>Gets the size of the box's contents.</summary>
    public long ContentLength => End - ContentStart;

    /// <summary>Gets <see cref="Start"/> as an index into a buffer of less than 2 GB.</summary>
    internal int StartIndex => (int)Start;

    /// <summary>Gets <see cref="ContentStart"/> as an index into a buffer of less than 2 GB.</summary>
    internal int ContentIndex => (int)ContentStart;

    /// <summary>Gets <see cref="End"/> as an index into a buffer of less than 2 GB.</summary>
    internal int EndIndex => (int)End;

    /// <summary>Gets the box's contents from a buffer whose positions the box uses.</summary>
    internal ReadOnlySpan<byte> Content(ReadOnlySpan<byte> buffer) => buffer[ContentIndex..EndIndex];

    /// <summary>Gets the box's contents from a buffer whose positions the box uses.</summary>
    internal ReadOnlyMemory<byte> Content(ReadOnlyMemory<byte> buffer) => buffer[ContentIndex..EndIndex];

    /// <inheritdoc/>
    public override string ToString() => $"{Type} at {Start}, {Size} bytes";
}

/// <summary>
/// Reads the boxes of ISO/IEC 14496-12 from bytes in memory, one level at a time: 32-bit sizes, 64-bit large sizes, size 0 (to
/// the end of the enclosing box) and uuid boxes. A box that runs past its parent, or whose size is too small for its header,
/// raises a <see cref="CorruptDataException"/>; nothing is allocated.
/// </summary>
/// <remarks>
/// To walk a large file, read its top-level box headers from the stream with <see cref="ReadHeader"/>, load the boxes you need
/// (such as moov) and walk each with a reader told where the box starts in the file, so every position it reports is a file
/// offset.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/IsoBoxes.cs" region="walk"/>
/// </example>
public ref struct IsoBoxReader
{
    /// <summary>The most bytes a box header takes: size, type, large size and a uuid box's extended type.</summary>
    public const int MaxHeaderSize = 32;

    private const uint Uuid = 0x75756964;

    private readonly ReadOnlySpan<byte> _data;
    private readonly int _end;
    private readonly string _formatName;
    private readonly long _offset;
    private readonly long _errorOrigin;
    private int _position;

    /// <summary>Starts reading the boxes of <paramref name="data"/>, whose positions are reported from 0.</summary>
    /// <param name="data">The boxes, such as a whole file or the contents of one box.</param>
    /// <param name="formatName">The format named in a <see cref="CorruptDataException"/>, such as "MP4".</param>
    /// <exception cref="ArgumentNullException"><paramref name="formatName"/> is <see langword="null"/>.</exception>
    public IsoBoxReader(ReadOnlySpan<byte> data, string formatName)
        : this(data, formatName, 0)
    {
    }

    /// <summary>Starts reading the boxes of <paramref name="data"/>, which starts at <paramref name="offset"/> in the file.</summary>
    /// <param name="data">The boxes, such as a moov box read from a file.</param>
    /// <param name="formatName">The format named in a <see cref="CorruptDataException"/>, such as "MP4".</param>
    /// <param name="offset">Where <paramref name="data"/> starts in the file; boxes and errors report positions from there.</param>
    /// <exception cref="ArgumentNullException"><paramref name="formatName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    public IsoBoxReader(ReadOnlySpan<byte> data, string formatName, long offset)
    {
        ArgumentNullException.ThrowIfNull(formatName);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _data = data;
        _end = data.Length;
        _formatName = formatName;
        _offset = offset;
        _errorOrigin = offset;
    }

    /// <summary>Starts reading the boxes between <paramref name="start"/> and <paramref name="end"/>, which boxes report as their positions.</summary>
    /// <param name="buffer">The bytes read.</param>
    /// <param name="start">Where the first box starts.</param>
    /// <param name="end">Where the enclosing box's contents end.</param>
    /// <param name="formatName">The format named in a <see cref="CorruptDataException"/>.</param>
    /// <param name="origin">Where <paramref name="buffer"/> starts in the file, for the offsets of errors only.</param>
    internal IsoBoxReader(ReadOnlySpan<byte> buffer, int start, int end, string formatName, long origin = 0)
    {
        _data = buffer;
        _position = start;
        _end = end;
        _formatName = formatName;
        _errorOrigin = origin;
    }

    private IsoBoxReader(ReadOnlySpan<byte> data, int start, int end, string formatName, long offset, long errorOrigin)
    {
        _data = data;
        _position = start;
        _end = end;
        _formatName = formatName;
        _offset = offset;
        _errorOrigin = errorOrigin;
    }

    /// <summary>Reads a box header from the first bytes of a box, for walking boxes in a stream without reading them into memory.</summary>
    /// <param name="header">The bytes at <paramref name="position"/>: <see cref="MaxHeaderSize"/> of them, or all that remain before <paramref name="end"/>.</param>
    /// <param name="position">Where the box starts in the file.</param>
    /// <param name="end">Where the enclosing box ends, or the file's length for a top-level box (<see cref="long.MaxValue"/> when unknown).</param>
    /// <param name="formatName">The format named in a <see cref="CorruptDataException"/>.</param>
    /// <returns>The box. A size of 0 makes it run to <paramref name="end"/>.</returns>
    /// <exception cref="CorruptDataException">The header is cut off, its size is smaller than the header, or the box runs past <paramref name="end"/>.</exception>
    public static IsoBox ReadHeader(ReadOnlySpan<byte> header, long position, long end, string formatName)
    {
        ArgumentNullException.ThrowIfNull(formatName);
        return Parse(header, position, end - position, formatName, position);
    }

    /// <summary>Reads the next box; returns <see langword="false"/> at the end of the data or the enclosing box.</summary>
    /// <param name="box">The box read.</param>
    /// <returns>Whether there was another box.</returns>
    /// <exception cref="CorruptDataException">The box's header is cut off, its size is smaller than its header, or it runs past its parent.</exception>
    public bool Next(out IsoBox box)
    {
        box = default;
        if (_position >= _end)
        {
            return false;
        }

        IsoBox found = Parse(_data[_position.._end], _offset + _position, _end - _position, _formatName, _errorOrigin + _position);
        box = found;
        _position += (int)found.Size;
        return true;
    }

    /// <summary>Returns a reader over the boxes inside <paramref name="box"/>, a box this reader returned.</summary>
    /// <param name="box">The parent box.</param>
    /// <param name="skip">
    /// How many bytes of the contents come before the first child: 0 for a plain container such as moov, 4 for a full box's
    /// version and flags (meta), 8 for stsd's version, flags and entry count.
    /// </param>
    /// <returns>The reader of the children.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skip"/> is negative, or <paramref name="box"/> lies outside this reader's data.</exception>
    /// <exception cref="CorruptDataException">The box's contents are shorter than <paramref name="skip"/>.</exception>
    public readonly IsoBoxReader Children(in IsoBox box, int skip = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        (int start, int end) = Range(box);
        if (end - start < skip)
        {
            throw new CorruptDataException(_formatName, _errorOrigin + start, $"A \"{box.Type}\" box is too short for its {skip}-byte header fields.") { IsTruncation = true };
        }

        return new IsoBoxReader(_data, start + skip, end, _formatName, _offset, _errorOrigin);
    }

    /// <summary>Gets the contents of <paramref name="box"/>, a box this reader returned.</summary>
    /// <param name="box">The box.</param>
    /// <returns>The bytes after its header.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="box"/> lies outside this reader's data.</exception>
    public readonly ReadOnlySpan<byte> Content(in IsoBox box)
    {
        (int start, int end) = Range(box);
        return _data[start..end];
    }

    private readonly (int Start, int End) Range(in IsoBox box)
    {
        long start = box.ContentStart - _offset, end = box.End - _offset;
        if (start < 0 || end > _data.Length || start > end)
        {
            throw new ArgumentOutOfRangeException(nameof(box), box, "The box lies outside this reader's data.");
        }

        return ((int)start, (int)end);
    }

    private static IsoBox Parse(ReadOnlySpan<byte> head, long position, long left, string formatName, long errorPosition)
    {
        if (left < 8 || head.Length < 8)
        {
            throw Corrupt(formatName, errorPosition, "A box header is cut off.", truncation: true);
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(head);
        FourCC type = new(BinaryPrimitives.ReadUInt32BigEndian(head[4..]));
        int header = 8;
        if (size == 1)
        {
            if (left < 16 || head.Length < 16)
            {
                throw Corrupt(formatName, errorPosition, $"The large size of a \"{type}\" box is cut off.", truncation: true);
            }

            ulong large = BinaryPrimitives.ReadUInt64BigEndian(head[8..]);
            size = large > long.MaxValue ? long.MaxValue : (long)large;
            header = 16;
        }
        else if (size == 0)
        {
            size = left;
        }

        Guid? userType = null;
        if (type.Value == Uuid)
        {
            if (left < header + 16 || head.Length < header + 16)
            {
                throw Corrupt(formatName, errorPosition, "The extended type of a \"uuid\" box is cut off.", truncation: true);
            }

            userType = new Guid(head.Slice(header, 16), bigEndian: true);
            header += 16;
        }

        if (size < header)
        {
            throw Corrupt(formatName, errorPosition, $"A \"{type}\" box's size, {size}, is smaller than its header.", truncation: false);
        }

        if (size > left)
        {
            throw Corrupt(formatName, errorPosition, $"A \"{type}\" box of {size} bytes runs past its end ({left} bytes left).", truncation: true);
        }

        return new IsoBox(type, position, position + header, position + size, userType);
    }

    private static CorruptDataException Corrupt(string formatName, long position, string problem, bool truncation) =>
        new(formatName, position, problem) { IsTruncation = truncation };
}
