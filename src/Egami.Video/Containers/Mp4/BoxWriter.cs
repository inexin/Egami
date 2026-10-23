using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Containers.Mp4;

/// <summary>Builds ISO BMFF boxes in memory: <see cref="Start(string)"/> opens a box, <see cref="End"/> writes its size.</summary>
internal sealed class BoxWriter : IDisposable
{
    private readonly MemoryStream _buffer = new();
    private readonly Stack<long> _open = new();

    public long Length => _buffer.Length;

    public void Start(string type)
    {
        _open.Push(_buffer.Length);
        U32(0);
        Type(type);
    }

    public void Start(uint type)
    {
        _open.Push(_buffer.Length);
        U32(0);
        U32(type);
    }

    /// <summary>Opens a full box: its header, then version and flags.</summary>
    public void StartFull(string type, int version, uint flags)
    {
        Start(type);
        U32(((uint)version << 24) | (flags & 0xFFFFFF));
    }

    public void End()
    {
        long start = _open.Pop();
        long size = _buffer.Length - start;
        if (size > uint.MaxValue)
        {
            throw new NotSupportedException("A box inside moov is larger than 4 GB.");
        }

        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)size);
        long at = _buffer.Position;
        _buffer.Position = start;
        _buffer.Write(b);
        _buffer.Position = at;
    }

    public void Type(string type) => Bytes(Encoding.Latin1.GetBytes(type));

    public void U8(int value) => _buffer.WriteByte((byte)value);

    public void U16(int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value);
        _buffer.Write(b);
    }

    public void U24(uint value)
    {
        U8((int)(value >> 16));
        U16((int)(value & 0xFFFF));
    }

    public void U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _buffer.Write(b);
    }

    public void I32(int value) => U32((uint)value);

    public void U64(ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        _buffer.Write(b);
    }

    public void Zeros(int count)
    {
        for (int i = 0; i < count; i++)
        {
            _buffer.WriteByte(0);
        }
    }

    public void Bytes(ReadOnlySpan<byte> bytes) => _buffer.Write(bytes);

    /// <summary>Overwrites four bytes written earlier, at <paramref name="at"/> from the start.</summary>
    public void Patch(long at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(_buffer.GetBuffer().AsSpan((int)at, 4), value);

    /// <summary>Empties the writer to build the next boxes in the same buffer.</summary>
    public void Clear()
    {
        _open.Clear();
        _buffer.SetLength(0);
    }

    /// <summary>Writes what was built to <paramref name="stream"/>, without copying it first.</summary>
    public void WriteTo(Stream stream)
    {
        if (_open.Count > 0)
        {
            throw new InvalidOperationException("A box was left open.");
        }

        stream.Write(_buffer.GetBuffer().AsSpan(0, (int)_buffer.Length));
    }

    public void Dispose() => _buffer.Dispose();

    public byte[] ToArray()
    {
        if (_open.Count > 0)
        {
            throw new InvalidOperationException("A box was left open.");
        }

        return _buffer.ToArray();
    }
}
