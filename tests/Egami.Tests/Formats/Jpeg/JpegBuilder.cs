using System.Buffers.Binary;
using System.Text;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>Builds JPEG segment streams for header tests: SOI, any segments, a scan header, a few entropy bytes and EOI.</summary>
internal sealed class JpegBuilder : IDisposable
{
    private readonly MemoryStream _bytes = new();

    internal JpegBuilder()
    {
        _bytes.Write([0xFF, 0xD8]);
    }

    /// <summary>A baseline (SOF0) frame. Components are (id, h, v, quant table).</summary>
    internal static JpegBuilder Baseline(int width, int height, params (int Id, int H, int V)[] components)
    {
        return new JpegBuilder().Quant(0, 75).Frame(0xC0, 8, width, height, components);
    }

    internal JpegBuilder Frame(byte marker, int precision, int width, int height, params (int Id, int H, int V)[] components)
    {
        List<byte> data = [(byte)precision, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components.Length];
        foreach ((int id, int h, int v) in components)
        {
            data.AddRange([(byte)id, (byte)((h << 4) | v), 0]);
        }

        return Segment(marker, [.. data]);
    }

    /// <summary>A DQT with the T.81 luma table scaled to an IJG <paramref name="quality"/>, in zigzag order.</summary>
    internal JpegBuilder Quant(int slot, int quality)
    {
        ReadOnlySpan<byte> luma = Egami.Formats.Jpeg.JpegTables.LumaQuant;
        ReadOnlySpan<byte> zigzag = Egami.Formats.Jpeg.JpegTables.ZigZag;
        int factor = quality < 50 ? 5000 / quality : 200 - (2 * quality);
        byte[] data = new byte[65];
        data[0] = (byte)slot;
        for (int k = 0; k < 64; k++)
        {
            data[1 + k] = (byte)Math.Clamp(((luma[zigzag[k]] * factor) + 50) / 100, 1, 255);
        }

        return Segment(0xDB, data);
    }

    internal JpegBuilder Huffman(int tableClass, int slot)
    {
        byte[] data = new byte[17 + 1];
        data[0] = (byte)((tableClass << 4) | slot);
        data[1] = 1;
        data[17] = 0;
        return Segment(0xC4, data);
    }

    internal JpegBuilder Jfif(int units, int x, int y)
    {
        byte[] data = [.. "JFIF\0"u8, 1, 2, (byte)units, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, 0, 0];
        return Segment(0xE0, data);
    }

    /// <summary>An EXIF APP1 whose IFD0 holds only the Orientation tag.</summary>
    internal JpegBuilder ExifOrientation(int orientation, bool littleEndian)
    {
        byte[] tiff = new byte[8 + 2 + 12 + 4];
        tiff[0] = tiff[1] = littleEndian ? (byte)'I' : (byte)'M';
        Put16(tiff.AsSpan(2), 42, littleEndian);
        Put32(tiff.AsSpan(4), 8, littleEndian);
        Put16(tiff.AsSpan(8), 1, littleEndian);
        Put16(tiff.AsSpan(10), 0x0112, littleEndian);
        Put16(tiff.AsSpan(12), 3, littleEndian);
        Put32(tiff.AsSpan(14), 1, littleEndian);
        Put16(tiff.AsSpan(18), (ushort)orientation, littleEndian);
        return Segment(0xE1, [.. "Exif\0\0"u8, .. tiff]);
    }

    internal JpegBuilder Xmp(string packet)
    {
        return Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Encoding.UTF8.GetBytes(packet)]);
    }

    internal JpegBuilder ExtendedXmp(string guid, int total, int offset, byte[] part)
    {
        byte[] head = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)offset);
        return Segment(0xE1, [.. "http://ns.adobe.com/xmp/extension/\0"u8, .. Encoding.ASCII.GetBytes(guid), .. head, .. part]);
    }

    internal JpegBuilder Icc(int sequence, int count, byte[] chunk)
    {
        return Segment(0xE2, [.. "ICC_PROFILE\0"u8, (byte)sequence, (byte)count, .. chunk]);
    }

    internal JpegBuilder Adobe(int transform)
    {
        return Segment(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, (byte)transform]);
    }

    internal JpegBuilder Comment(byte[] text)
    {
        return Segment(0xFE, text);
    }

    internal JpegBuilder Segment(byte marker, byte[] data)
    {
        _bytes.Write([0xFF, marker, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2)]);
        _bytes.Write(data);
        return this;
    }

    internal JpegBuilder Raw(params byte[] bytes)
    {
        _bytes.Write(bytes);
        return this;
    }

    /// <summary>A scan header over the given component ids, a few entropy bytes and EOI.</summary>
    internal byte[] Scan(params int[] ids)
    {
        List<byte> data = [(byte)ids.Length];
        foreach (int id in ids)
        {
            data.AddRange([(byte)id, 0x00]);
        }

        data.AddRange([0, 63, 0]);
        Segment(0xDA, [.. data]);
        _bytes.Write([0x12, 0x34, 0xFF, 0x00, 0x56, 0xFF, 0xD9]);
        return _bytes.ToArray();
    }

    public void Dispose()
    {
        _bytes.Dispose();
    }

    private static void Put16(Span<byte> at, ushort value, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(at, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(at, value);
        }
    }

    private static void Put32(Span<byte> at, uint value, bool little)
    {
        if (little)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(at, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(at, value);
        }
    }
}
