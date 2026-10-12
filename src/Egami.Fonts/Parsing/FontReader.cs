using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Fonts.Parsing;

/// <summary>
/// A bounds-checked big-endian reader over font bytes. Every read past the end throws <see cref="CorruptDataException"/> with the
/// absolute offset, so a damaged table never reads outside its slice.
/// </summary>
internal ref struct FontReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly long _base;
    private int _position;

    /// <summary>Starts a reader at the beginning of <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to read.</param>
    /// <param name="baseOffset">The offset of <paramref name="data"/> in the file, for error messages.</param>
    public FontReader(ReadOnlySpan<byte> data, long baseOffset = 0)
    {
        _data = data;
        _base = baseOffset;
        _position = 0;
    }

    public readonly int Position => _position;

    public readonly int Length => _data.Length;

    public readonly int Remaining => _data.Length - _position;

    public readonly ReadOnlySpan<byte> Data => _data;

    public void Seek(int position)
    {
        if ((uint)position > (uint)_data.Length)
        {
            throw Truncated(position);
        }

        _position = position;
    }

    public void Skip(int count)
    {
        Seek(_position + count);
    }

    public byte ReadByte()
    {
        if (_position >= _data.Length)
        {
            throw Truncated(_position);
        }

        return _data[_position++];
    }

    public sbyte ReadSByte() => (sbyte)ReadByte();

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));

    public uint ReadUInt24()
    {
        ReadOnlySpan<byte> b = Take(3);
        return ((uint)b[0] << 16) | ((uint)b[1] << 8) | b[2];
    }

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));

    /// <summary>Reads a 16.16 fixed-point number.</summary>
    public float ReadFixed() => ReadInt32() / 65536f;

    /// <summary>Reads a 2.14 fixed-point number.</summary>
    public float ReadF2Dot14() => ReadInt16() / 16384f;

    public FontTag ReadTag() => new(ReadUInt32());

    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > _data.Length - _position)
        {
            throw Truncated(_position);
        }

        ReadOnlySpan<byte> span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    private readonly CorruptDataException Truncated(int position) => FontErrors.Truncated(_base + position);
}

/// <summary>Big-endian reads at fixed offsets, bounds-checked, for random access into tables.</summary>
internal static class BigEndian
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort UInt16(ReadOnlySpan<byte> data, int offset)
    {
        if ((uint)offset > (uint)(data.Length - 2))
        {
            throw FontErrors.Truncated(offset);
        }

        return BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short Int16(ReadOnlySpan<byte> data, int offset) => (short)UInt16(data, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint UInt32(ReadOnlySpan<byte> data, int offset)
    {
        if ((uint)offset > (uint)(data.Length - 4))
        {
            throw FontErrors.Truncated(offset);
        }

        return BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint UInt24(ReadOnlySpan<byte> data, int offset)
    {
        if ((uint)offset > (uint)(data.Length - 3))
        {
            throw FontErrors.Truncated(offset);
        }

        return ((uint)data[offset] << 16) | ((uint)data[offset + 1] << 8) | data[offset + 2];
    }

    /// <summary>Returns <paramref name="data"/>[offset..offset+length], or throws when it does not fit.</summary>
    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > data.Length || length > data.Length - offset)
        {
            throw FontErrors.Truncated(offset);
        }

        return data.Slice((int)offset, (int)length);
    }
}

/// <summary>The exceptions and diagnostic ids Egami.Fonts uses.</summary>
internal static class FontErrors
{
    public const string FormatName = "font";

    public static CorruptDataException Truncated(long offset) => new(FormatName, offset, "The data ends early or an offset points outside it.");

    public static CorruptDataException Corrupt(long offset, string problem) => new(FormatName, offset, problem);
}
