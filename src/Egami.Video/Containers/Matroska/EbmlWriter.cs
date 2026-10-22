using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Containers.Matroska;

/// <summary>Writes EBML elements (RFC 8794) to a stream: ids, sizes and typed values.</summary>
internal static class EbmlWriter
{
    public static void Id(Stream output, uint id)
    {
        Span<byte> b = stackalloc byte[4];
        output.Write(b[..Ebml.WriteId(b, id)]);
    }

    public static void Size(Stream output, long size, int length = 0)
    {
        Span<byte> b = stackalloc byte[8];
        output.Write(b[..Ebml.WriteSize(b, size, length)]);
    }

    public static void Header(Stream output, uint id, long size, int sizeLength = 0)
    {
        Id(output, id);
        Size(output, size, sizeLength);
    }

    public static void Element(Stream output, uint id, ReadOnlySpan<byte> body)
    {
        Header(output, id, body.Length);
        output.Write(body);
    }

    public static void Unsigned(Stream output, uint id, ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        int skip = 0;
        while (skip < 7 && b[skip] == 0)
        {
            skip++;
        }

        Element(output, id, b[skip..]);
    }

    public static void Float(Stream output, uint id, double value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, value);
        Element(output, id, b);
    }

    public static void Text(Stream output, uint id, string value) => Element(output, id, Encoding.UTF8.GetBytes(value));

    /// <summary>Writes a Void element filling exactly <paramref name="totalBytes"/> bytes (at least 2).</summary>
    public static void Void(Stream output, int totalBytes)
    {
        // One id byte, then a size VINT; an 8-byte size VINT covers any filler from 9 bytes up.
        int sizeLength = totalBytes - 1 > 8 ? 8 : 1;
        int body = totalBytes - 1 - sizeLength;
        Header(output, MatroskaIds.Void, body, sizeLength);
        output.Write(new byte[body]);
    }

    /// <summary>Writes a master element whose children are built into a buffer first.</summary>
    public static void Master(Stream output, uint id, Action<Stream> children)
    {
        using MemoryStream body = new();
        children(body);
        Header(output, id, body.Length);
        body.Position = 0;
        body.CopyTo(output);
    }
}
