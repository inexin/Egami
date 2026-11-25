using System.Buffers.Binary;
using System.Numerics;

namespace Egami.Metafiles.Writing;

/// <summary>A growing little-endian byte buffer: the fields of EMF and EMF+ records, in the order the specifications give them.</summary>
internal sealed class RecordBuffer(int capacity = 256)
{
    private byte[] _data = new byte[Math.Max(16, capacity)];

    /// <summary>Gets the number of bytes written.</summary>
    public int Length { get; private set; }

    /// <summary>Gets the bytes written so far.</summary>
    public ReadOnlySpan<byte> Span => _data.AsSpan(0, Length);

    public RecordBuffer U16(int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), (ushort)value);
        return this;
    }

    public RecordBuffer U32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value);
        return this;
    }

    public RecordBuffer I32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Grow(4), value);
        return this;
    }

    public RecordBuffer I16(int value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(Grow(2), (short)value);
        return this;
    }

    public RecordBuffer F32(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Grow(4), value);
        return this;
    }

    /// <summary>Six floats: M11, M12, M21, M22, Dx, Dy (an EMF XFORM and an EMF+ EmfPlusTransformMatrix alike).</summary>
    public RecordBuffer Matrix(Matrix3x2 m) => F32(m.M11).F32(m.M12).F32(m.M21).F32(m.M22).F32(m.M31).F32(m.M32);

    public RecordBuffer Bytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Grow(bytes.Length));
        return this;
    }

    /// <summary>Pads with zeros to a multiple of four bytes.</summary>
    public RecordBuffer Align()
    {
        int pad = (4 - (Length & 3)) & 3;
        Grow(pad).Clear();
        return this;
    }

    /// <summary>Overwrites four bytes already written, such as a size known only at the end.</summary>
    public void PatchU32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(at, 4), value);

    public void Clear() => Length = 0;

    public byte[] ToArray() => Span.ToArray();

    private Span<byte> Grow(int count)
    {
        if (Length + count > _data.Length)
        {
            Array.Resize(ref _data, Math.Max(Length + count, checked(_data.Length * 2)));
        }

        Span<byte> span = _data.AsSpan(Length, count);
        Length += count;
        return span;
    }
}
