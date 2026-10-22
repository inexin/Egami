using System.Buffers.Binary;
using System.Text;

using Egami.Formats.Iso;

namespace Egami.Video.Containers.Mp4;

/// <summary>Reads a box's big-endian fields in order; reading past its end is a <see cref="CorruptDataException"/> at the box.</summary>
internal ref struct BoxFields
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly IsoBox _box;
    private int _at;

    public BoxFields(ReadOnlySpan<byte> data, IsoBox box)
    {
        _data = data;
        _box = box;
    }

    public readonly int Position => _at;

    public readonly int Remaining => _data.Length - _at;

    public readonly ReadOnlySpan<byte> Rest => _data[_at..];

    public byte U8() => Take(1)[0];

    public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    public short I16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));

    public uint U24()
    {
        ReadOnlySpan<byte> b = Take(3);
        return (uint)(b[0] << 16 | b[1] << 8 | b[2]);
    }

    public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public int I32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    public ulong U64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

    /// <summary>Reads a 32-bit value, or a 64-bit one when <paramref name="wide"/>, as full boxes of version 1 store times.</summary>
    public long UVar(bool wide) => wide ? (long)Math.Min(U64(), long.MaxValue) : U32();

    /// <summary>Reads a full box's version and flags.</summary>
    public (int Version, uint Flags) FullBox()
    {
        uint word = U32();
        return ((int)(word >> 24), word & 0xFFFFFF);
    }

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > _data.Length - _at)
        {
            throw new CorruptDataException(Mp4Format.Instance.Name, _box.Start, $"A \"{_box.Type}\" box is cut off: it needs {count} more bytes at {_at}, {_data.Length - _at} are left.");
        }

        ReadOnlySpan<byte> part = _data.Slice(_at, count);
        _at += count;
        return part;
    }

    public void Skip(int count) => Take(count);

    /// <summary>Reads a NUL-terminated UTF-8 string, or the rest of the box when it has no NUL.</summary>
    public string CString()
    {
        ReadOnlySpan<byte> rest = Rest;
        int end = rest.IndexOf((byte)0);
        string text = Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);
        _at += end < 0 ? rest.Length : end + 1;
        return text;
    }

    /// <summary>Checks that <paramref name="count"/> entries of <paramref name="size"/> bytes remain.</summary>
    public readonly void Need(long count, int size)
    {
        if (count < 0 || count * size > _data.Length - _at)
        {
            throw new CorruptDataException(Mp4Format.Instance.Name, _box.Start, $"A \"{_box.Type}\" box says {count} entries, more than its {_data.Length - _at} bytes hold.");
        }
    }
}
