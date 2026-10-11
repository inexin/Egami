using System.Buffers;
using System.Buffers.Binary;

namespace Egami.Formats.Tiff;

/// <summary>
/// A TIFF file's bytes, read at offsets: straight from a memory stream's buffer, through seeks on any other seekable stream,
/// or from a copy of the whole file when the stream cannot seek (the IFD often comes last).
/// </summary>
internal sealed class TiffSource : IDisposable
{
    private readonly EgamiInput? _input;
    private byte[]? _rented;
    private readonly ArraySegment<byte> _memory;

    private TiffSource(EgamiInput? input, ArraySegment<byte> memory, long length, byte[]? rented)
    {
        _input = input;
        _memory = memory;
        Length = length;
        _rented = rented;
    }

    /// <summary>Gets the length of the file.</summary>
    public long Length { get; }

    /// <summary>Gets whether the whole file was copied because the stream cannot seek.</summary>
    public bool Buffered => _rented is not null;

    /// <summary>Gets the bytes held for the file: its copy when the stream cannot seek, else nothing.</summary>
    public long HeldBytes => _rented is null ? 0 : Length;

    /// <summary>Opens the input for reading at offsets.</summary>
    public static TiffSource Open(EgamiInput input)
    {
        ArraySegment<byte> whole = input.WholeBuffer;
        if (whole.Array is not null)
        {
            return new TiffSource(null, whole, whole.Count, null);
        }

        if (input.Length is long length)
        {
            return new TiffSource(input, default, length, null);
        }

        // Not seekable: read it all, within MaxTotalBytes.
        long max = Math.Min(input.Limits.MaxTotalBytes, Array.MaxLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int count = 0;
        try
        {
            while (true)
            {
                if (count == buffer.Length)
                {
                    if (count >= max)
                    {
                        throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), input.Limits.MaxTotalBytes, count + 1L);
                    }

                    byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(max, (long)buffer.Length * 2));
                    buffer.AsSpan(0, count).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int n = input.Read(buffer.AsSpan(count, (int)Math.Min(buffer.Length, max) - count));
                if (n == 0)
                {
                    break;
                }

                count += n;
            }

            return new TiffSource(null, new ArraySegment<byte>(buffer, 0, count), count, buffer);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>Reads up to <paramref name="destination"/>.Length bytes at <paramref name="offset"/>; returns how many there were.</summary>
    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Length)
        {
            return 0;
        }

        if (_input is null)
        {
            int n = (int)Math.Min(destination.Length, Length - offset);
            _memory.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }

        return _input.ReadAt(offset, destination);
    }

    /// <summary>
    /// Returns the bytes at <paramref name="offset"/> without copying when the file is in memory, cut at the end of the file;
    /// otherwise reads them into <paramref name="scratch"/>.
    /// </summary>
    public ReadOnlySpan<byte> Slice(long offset, int length, Span<byte> scratch)
    {
        if (offset < 0 || offset >= Length)
        {
            return [];
        }

        int n = (int)Math.Min(length, Length - offset);
        if (_input is null)
        {
            return _memory.AsSpan((int)offset, n);
        }

        return scratch[..Read(offset, scratch[..n])];
    }

    /// <summary>Gets whether <see cref="Slice"/> needs no scratch memory.</summary>
    public bool InMemory => _input is null;

    public void Dispose()
    {
        byte[]? rented = Interlocked.Exchange(ref _rented, null);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>One IFD entry: its tag, type, value count, and where its values lie (inside the entry or at an offset).</summary>
internal readonly record struct TiffEntry(int Tag, int Type, long Count, long ValueAt)
{
    /// <summary>Gets the bytes one value of the entry's type takes, or 0 for a type TIFF does not define.</summary>
    public int ValueSize => Size(Type);

    /// <summary>Gets the bytes all the values take.</summary>
    public long ByteCount => Count * ValueSize;

    public static int Size(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 0,
    };
}

/// <summary>One image file directory, its entries by tag and the offset of the next one.</summary>
internal sealed class TiffIfd(long offset, Dictionary<int, TiffEntry> entries, long next)
{
    public long Offset { get; } = offset;

    public Dictionary<int, TiffEntry> Entries { get; } = entries;

    public long Next { get; } = next;

    public bool TryGet(int tag, out TiffEntry entry) => Entries.TryGetValue(tag, out entry);

    public bool Has(int tag) => Entries.ContainsKey(tag);
}

/// <summary>
/// The structure of a TIFF file: its header and IFDs, read from the TIFF 6.0 specification and the BigTIFF design note. Every
/// offset and count is checked against the file length before it is used.
/// </summary>
internal sealed class TiffFile
{
    private const int MaxEntries = 4096;

    private TiffFile(TiffSource source, bool bigEndian, bool big, long firstIfd, EgamiInput input)
    {
        Source = source;
        BigEndian = bigEndian;
        Big = big;
        FirstIfd = firstIfd;
        Input = input;
    }

    public TiffSource Source { get; }

    public EgamiInput Input { get; }

    public bool BigEndian { get; }

    /// <summary>Gets whether the file is BigTIFF.</summary>
    public bool Big { get; }

    public long FirstIfd { get; }

    /// <summary>Gets the bytes an offset takes: 4 in classic TIFF, 8 in BigTIFF.</summary>
    public int OffsetSize => Big ? 8 : 4;

    /// <summary>Reads the header.</summary>
    public static TiffFile Open(TiffSource source, EgamiInput input)
    {
        Span<byte> header = stackalloc byte[16];
        int n = source.Read(0, header);
        if (n < 8 || !TiffReader.Matches(header[..n]))
        {
            throw input.Corrupt(0, "The TIFF header is wrong.");
        }

        bool bigEndian = header[0] == 'M';
        int version = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(header[2..]) : BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        bool big = version == 43;
        long first;
        if (big)
        {
            if (n < 16)
            {
                throw new CorruptDataException(input.FormatName, 0, "The BigTIFF header is cut short.") { IsTruncation = true };
            }

            ulong offset = bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(header[8..]) : BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
            if (offset > long.MaxValue)
            {
                throw input.Corrupt(8, "The first IFD offset is beyond any file.");
            }

            first = (long)offset;
        }
        else
        {
            first = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(header[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        }

        TiffFile file = new(source, bigEndian, big, first, input);
        if (first < (big ? 16 : 8) || first > source.Length - (big ? 8 : 2))
        {
            throw file.OutOfFile(big ? 8 : 4, $"The first IFD at {first} lies outside the {source.Length}-byte file.");
        }

        return file;
    }

    /// <summary>Reads the IFD at <paramref name="offset"/>.</summary>
    public TiffIfd ReadIfd(long offset)
    {
        int countSize = Big ? 8 : 2;
        int entrySize = Big ? 20 : 12;
        Span<byte> head = stackalloc byte[8];
        if (Source.Read(offset, head[..countSize]) < countSize)
        {
            throw OutOfFile(offset, $"The IFD at {offset} lies outside the file.");
        }

        ulong count = Big ? U64(head) : U16(head);
        if (count == 0 || count > MaxEntries || (long)count * entrySize > Source.Length - offset - countSize)
        {
            throw count == 0 || count > MaxEntries
                ? Input.Corrupt(offset, $"The IFD at {offset} has {count} entries.")
                : OutOfFile(offset, $"The IFD at {offset} has {count} entries, more than the file holds.");
        }

        int bytes = ((int)count * entrySize) + OffsetSize;
        byte[] rented = ArrayPool<byte>.Shared.Rent(bytes);
        try
        {
            Span<byte> data = rented.AsSpan(0, bytes);
            int got = Source.Read(offset + countSize, data);
            if (got < (int)count * entrySize)
            {
                throw OutOfFile(offset, $"The IFD at {offset} is cut short.");
            }

            Dictionary<int, TiffEntry> entries = new((int)count);
            for (int i = 0; i < (int)count; i++)
            {
                Span<byte> e = data.Slice(i * entrySize, entrySize);
                int tag = U16(e);
                int type = U16(e[2..]);
                ulong n = Big ? U64(e[4..]) : U32(e[4..]);
                int size = TiffEntry.Size(type);
                if (size == 0 || n == 0 || n > (ulong)Source.Length)
                {
                    // Unknown types and empty entries are skipped, as the specification says readers should.
                    continue;
                }

                long valueBytes = (long)n * size;
                long at = offset + countSize + (i * entrySize) + (Big ? 12 : 8);
                if (valueBytes > OffsetSize)
                {
                    ulong pointer = Big ? U64(e[12..]) : U32(e[8..]);
                    if (pointer > (ulong)Source.Length || (long)pointer > Source.Length - valueBytes)
                    {
                        // A value outside the file: kept so the tag that needs it can report it; others never read it.
                        at = -1;
                    }
                    else
                    {
                        at = (long)pointer;
                    }
                }

                entries.TryAdd(tag, new TiffEntry(tag, type, (long)n, at));
            }

            long next = 0;
            if (got >= bytes)
            {
                ReadOnlySpan<byte> link = data.Slice((int)count * entrySize, OffsetSize);
                ulong value = Big ? U64(link) : U32(link);
                next = value > long.MaxValue ? -1 : (long)value;
            }

            return new TiffIfd(offset, entries, next);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Returns an entry's first value as an unsigned integer, or <paramref name="fallback"/> when it is missing.</summary>
    public ulong Integer(TiffIfd ifd, int tag, ulong fallback)
    {
        return ifd.TryGet(tag, out TiffEntry entry) && entry.ValueAt >= 0 ? Integer(entry, 0) : fallback;
    }

    /// <summary>Returns value <paramref name="index"/> of an integer entry (BYTE, SHORT, LONG, LONG8 and their signed kinds).</summary>
    public ulong Integer(TiffEntry entry, long index)
    {
        Span<byte> bytes = stackalloc byte[8];
        int size = entry.ValueSize;
        if (entry.ValueAt < 0 || index >= entry.Count || Source.Read(entry.ValueAt + (index * size), bytes[..size]) < size)
        {
            throw OutOfFile(entry.ValueAt, $"Tag {entry.Tag}'s values lie outside the file.");
        }

        return Decode(bytes[..size], entry.Type);
    }

    /// <summary>Reads every value of an integer entry, at most <paramref name="max"/> of them.</summary>
    public ulong[] Integers(TiffEntry entry, long max)
    {
        if (entry.Count > max)
        {
            throw Input.Corrupt(entry.ValueAt, $"Tag {entry.Tag} has {entry.Count} values; at most {max} make sense.");
        }

        byte[] raw = Bytes(entry);
        int size = entry.ValueSize;
        ulong[] values = new ulong[entry.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Decode(raw.AsSpan(i * size, size), entry.Type);
        }

        return values;
    }

    /// <summary>Reads the values of a RATIONAL, SRATIONAL, FLOAT or DOUBLE entry (integers too) as numbers.</summary>
    public double[] Numbers(TiffEntry entry, long max)
    {
        if (entry.Count > max)
        {
            throw Input.Corrupt(entry.ValueAt, $"Tag {entry.Tag} has {entry.Count} values; at most {max} make sense.");
        }

        byte[] raw = Bytes(entry);
        double[] values = new double[entry.Count];
        int size = entry.ValueSize;
        for (int i = 0; i < values.Length; i++)
        {
            ReadOnlySpan<byte> v = raw.AsSpan(i * size, size);
            values[i] = entry.Type switch
            {
                5 => Ratio(U32(v), U32(v[4..])),
                10 => Ratio((int)U32(v), (int)U32(v[4..])),
                11 => BitConverter.Int32BitsToSingle((int)U32(v)),
                12 => BitConverter.Int64BitsToDouble((long)U64(v)),
                6 or 8 or 9 or 17 => (long)Decode(v, entry.Type),
                _ => Decode(v, entry.Type),
            };
        }

        return values;
    }

    /// <summary>Reads an entry's values as stored, in the file's byte order.</summary>
    public byte[] Bytes(TiffEntry entry)
    {
        if (entry.ValueAt < 0)
        {
            throw OutOfFile(-1, $"Tag {entry.Tag}'s values lie outside the file.");
        }

        if (entry.ByteCount > int.MaxValue)
        {
            throw Input.Corrupt(entry.ValueAt, $"Tag {entry.Tag} has {entry.Count} values.");
        }

        byte[] bytes = new byte[entry.ByteCount];
        if (Source.Read(entry.ValueAt, bytes) < bytes.Length)
        {
            throw OutOfFile(entry.ValueAt, $"Tag {entry.Tag}'s values lie outside the file.");
        }

        return bytes;
    }

    /// <summary>Reads an ASCII entry, cut at its first NUL.</summary>
    public string Ascii(TiffEntry entry)
    {
        byte[] bytes = Bytes(entry);
        int end = Array.IndexOf(bytes, (byte)0);
        return System.Text.Encoding.Latin1.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    /// <summary>Reads an entry's values and swaps them to little-endian, for <see cref="TiffTag"/>.</summary>
    public byte[] LittleEndianBytes(TiffEntry entry)
    {
        byte[] bytes = Bytes(entry);
        if (BigEndian)
        {
            Swap(bytes, entry.Type is 5 or 10 ? 4 : entry.ValueSize);
        }

        return bytes;
    }

    /// <summary>Reverses each <paramref name="size"/>-byte value in place.</summary>
    public static void Swap(Span<byte> bytes, int size)
    {
        switch (size)
        {
            case 2:
                Span<ushort> shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(bytes[..(bytes.Length & ~1)]);
                BinaryPrimitives.ReverseEndianness(shorts, shorts);
                break;
            case 4:
                Span<uint> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes[..(bytes.Length & ~3)]);
                BinaryPrimitives.ReverseEndianness(words, words);
                break;
            case 8:
                Span<ulong> longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(bytes[..(bytes.Length & ~7)]);
                BinaryPrimitives.ReverseEndianness(longs, longs);
                break;
        }
    }

    public CorruptDataException OutOfFile(long offset, string problem)
    {
        return new CorruptDataException(Input.FormatName, offset, problem) { IsTruncation = !Source.Buffered && Input.Incremental };
    }

    public ushort U16(ReadOnlySpan<byte> b) => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);

    public uint U32(ReadOnlySpan<byte> b) => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);

    public ulong U64(ReadOnlySpan<byte> b) => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b);

    private ulong Decode(ReadOnlySpan<byte> v, int type) => type switch
    {
        1 or 2 or 7 => v[0],
        6 => (ulong)(long)(sbyte)v[0],
        3 => U16(v),
        8 => (ulong)(long)(short)U16(v),
        4 or 13 => U32(v),
        9 => (ulong)(long)(int)U32(v),
        16 or 18 => U64(v),
        17 => U64(v),
        5 => U32(v[4..]) == 0 ? 0 : U32(v) / U32(v[4..]),
        11 => (ulong)Math.Max(0, BitConverter.Int32BitsToSingle((int)U32(v))),
        12 => (ulong)Math.Max(0, BitConverter.Int64BitsToDouble((long)U64(v))),
        _ => 0,
    };

    private static double Ratio(double numerator, double denominator) => denominator == 0 ? 0 : numerator / denominator;
}

/// <summary>
/// The values of a strip or tile offset or byte count tag, read in windows of 1,024 as they are needed, so a large tiled file's
/// millions of entries are never read at once.
/// </summary>
internal sealed class TiffArray
{
    private const int Window = 1024;
    private readonly TiffFile _file;
    private readonly TiffEntry _entry;
    private readonly ulong[] _cache;
    private long _start = -1;

    public TiffArray(TiffFile file, TiffEntry entry)
    {
        _file = file;
        _entry = entry;
        _cache = new ulong[(int)Math.Min(Window, entry.Count)];
    }

    /// <summary>Creates an array of values computed rather than stored.</summary>
    public TiffArray(ulong[] values)
    {
        _file = null!;
        _cache = values;
        _start = 0;
        _entry = new TiffEntry(0, 16, values.Length, 0);
    }

    public long Count => _entry.Count;

    public ulong this[long index]
    {
        get
        {
            if ((ulong)index >= (ulong)Count)
            {
                return 0;
            }

            if (_file is null)
            {
                return _cache[index];
            }

            if (_start < 0 || index < _start || index >= _start + _cache.Length)
            {
                Fill(index - (index % Window));
            }

            return _cache[index - _start];
        }
    }

    private void Fill(long start)
    {
        int n = (int)Math.Min(_cache.Length, Count - start);
        int size = _entry.ValueSize;
        byte[] bytes = ArrayPool<byte>.Shared.Rent(n * size);
        try
        {
            if (_entry.ValueAt < 0 || _file.Source.Read(_entry.ValueAt + (start * size), bytes.AsSpan(0, n * size)) < n * size)
            {
                throw _file.OutOfFile(_entry.ValueAt, $"Tag {_entry.Tag}'s values lie outside the file.");
            }

            for (int i = 0; i < n; i++)
            {
                ReadOnlySpan<byte> v = bytes.AsSpan(i * size, size);
                _cache[i] = size switch
                {
                    1 => v[0],
                    2 => _file.U16(v),
                    4 => _file.U32(v),
                    _ => _file.U64(v),
                };
            }

            _start = start;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}
