using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Containers.Matroska;

/// <summary>EBML primitives from RFC 8794: variable-size integers, element headers and typed values.</summary>
internal static class Ebml
{
    /// <summary>The size value that means "unknown size" (all VINT data bits set), reported for any length.</summary>
    public const long UnknownSize = -1;

    /// <summary>Reads a VINT. Returns its length in bytes, or 0 when the first byte has no marker in the first 8 bits.</summary>
    public static int ReadVint(ReadOnlySpan<byte> data, out long value, bool keepMarker = false)
    {
        value = 0;
        if (data.IsEmpty || data[0] == 0)
        {
            return 0;
        }

        int length = System.Numerics.BitOperations.LeadingZeroCount((uint)data[0]) - 23;
        if (data.Length < length)
        {
            return 0;
        }

        long v = keepMarker ? data[0] : data[0] & (0xFF >> length);
        bool allOnes = v == (0xFF >> length);
        for (int i = 1; i < length; i++)
        {
            v = (v << 8) | data[i];
            allOnes &= data[i] == 0xFF;
        }

        value = !keepMarker && allOnes ? UnknownSize : v;
        return length;
    }

    /// <summary>Length in bytes of the VINT starting with <paramref name="first"/>, or 0 when invalid.</summary>
    public static int VintLength(byte first) => first == 0 ? 0 : System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;

    public static ulong ReadUnsigned(ReadOnlySpan<byte> body)
    {
        ulong v = 0;
        foreach (byte b in body[..Math.Min(8, body.Length)])
        {
            v = (v << 8) | b;
        }

        return v;
    }

    public static long ReadSigned(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return 0;
        }

        long v = (sbyte)body[0];
        foreach (byte b in body[1..Math.Min(8, body.Length)])
        {
            v = (v << 8) | b;
        }

        return v;
    }

    public static double ReadFloat(ReadOnlySpan<byte> body) => body.Length switch
    {
        4 => BinaryPrimitives.ReadSingleBigEndian(body),
        8 => BinaryPrimitives.ReadDoubleBigEndian(body),
        _ => 0,
    };

    public static string ReadString(ReadOnlySpan<byte> body)
    {
        int end = body.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? body : body[..end]);
    }

    /// <summary>Iterates the children of a master element held in memory.</summary>
    public static ChildEnumerator Children(ReadOnlyMemory<byte> body) => new(body);

    public static int WriteId(Span<byte> destination, uint id)
    {
        int length = id >= 0x1000000 ? 4 : id >= 0x10000 ? 3 : id >= 0x100 ? 2 : 1;
        for (int i = 0; i < length; i++)
        {
            destination[i] = (byte)(id >> (8 * (length - 1 - i)));
        }

        return length;
    }

    /// <summary>Writes a size as a VINT of <paramref name="length"/> bytes (the smallest that fits when 0).</summary>
    public static int WriteSize(Span<byte> destination, long size, int length = 0)
    {
        if (length == 0)
        {
            length = 1;
            while (length < 8 && size >= (1L << (7 * length)) - 1)
            {
                length++;
            }
        }

        if (size == UnknownSize)
        {
            destination[0] = (byte)(0xFF >> (length - 1));
            destination[1..length].Fill(0xFF);
            return length;
        }

        if (length < 8 && size >= (1L << (7 * length)) - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The size does not fit the VINT length.");
        }

        for (int i = length - 1; i >= 0; i--)
        {
            destination[i] = (byte)size;
            size >>= 8;
        }

        destination[0] |= (byte)(0x80 >> (length - 1));
        return length;
    }

    /// <summary>Children of an in-memory master element: id and body, stopping at the first malformed header.</summary>
    internal struct ChildEnumerator(ReadOnlyMemory<byte> body)
    {
        private int _position;

        public (uint Id, ReadOnlyMemory<byte> Body) Current { get; private set; }

        public readonly ChildEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            ReadOnlySpan<byte> span = body.Span;
            if (_position >= span.Length)
            {
                return false;
            }

            int idLength = ReadVint(span[_position..], out long id, keepMarker: true);
            if (idLength is 0 or > 4)
            {
                return false;
            }

            int sizeLength = ReadVint(span[(_position + idLength)..], out long size);
            if (sizeLength == 0)
            {
                return false;
            }

            int start = _position + idLength + sizeLength;
            long available = span.Length - start;
            long length = size == UnknownSize || size > available ? available : size;
            Current = ((uint)id, body.Slice(start, (int)length));
            _position = start + (int)length;
            return true;
        }
    }
}
