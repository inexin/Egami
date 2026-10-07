using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

using Egami.Formats.Png;

namespace Egami.Tests.Formats.Png;

/// <summary>
/// Builds PNG files for tests, straight from the specification and independent of Egami's reader: samples per pixel in,
/// filtered, optionally interlaced, compressed and chunked bytes out. Hooks allow broken files.
/// </summary>
internal sealed class PngBuilder
{
    public PngBuilder(int width, int height, PngColorType colorType, int bitDepth)
    {
        Width = width;
        Height = height;
        ColorType = colorType;
        BitDepth = bitDepth;
        Samples = new int[width * height * Channels];
    }

    public int Width { get; }

    public int Height { get; }

    public PngColorType ColorType { get; }

    public int BitDepth { get; }

    public bool Interlaced { get; set; }

    /// <summary>Gets the samples, pixel by pixel, row by row, <see cref="Channels"/> per pixel.</summary>
    public int[] Samples { get; }

    public byte[]? Palette { get; set; }

    public byte[]? Transparency { get; set; }

    /// <summary>Gets or sets the filter for scanline n (counting across passes); defaults to cycling through all five.</summary>
    public Func<int, byte> Filter { get; set; } = n => (byte)(n % 5);

    /// <summary>Gets chunks inserted before PLTE, as (type, data).</summary>
    public List<(string Type, byte[] Data)> ChunksBeforePalette { get; } = [];

    public List<(string Type, byte[] Data)> ChunksBeforeImageData { get; } = [];

    public List<(string Type, byte[] Data)> ChunksAfterImageData { get; } = [];

    /// <summary>Gets or sets how many IDAT chunks the compressed data is split into.</summary>
    public int IdatChunks { get; set; } = 3;

    public bool OmitIend { get; set; }

    /// <summary>Gets or sets a function that may change the compressed image data before it is chunked.</summary>
    public Func<byte[], byte[]>? EditCompressed { get; set; }

    /// <summary>Gets or sets the type of a chunk whose CRC is written wrong.</summary>
    public string? BadCrcChunk { get; set; }

    public int Channels => ColorType switch
    {
        PngColorType.Gray or PngColorType.Palette => 1,
        PngColorType.GrayAlpha => 2,
        PngColorType.Rgb => 3,
        _ => 4,
    };

    public int MaxSample => (1 << BitDepth) - 1;

    public void Fill(Random random)
    {
        int max = ColorType == PngColorType.Palette ? (Palette!.Length / 3) - 1 : MaxSample;
        for (int i = 0; i < Samples.Length; i++)
        {
            Samples[i] = random.Next(max + 1);
        }
    }

    public int Sample(int x, int y, int channel)
    {
        return Samples[(((y * Width) + x) * Channels) + channel];
    }

    public void SetSample(int x, int y, int channel, int value)
    {
        Samples[(((y * Width) + x) * Channels) + channel] = value;
    }

    public byte[] Build()
    {
        using MemoryStream file = new();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)Height);
        ihdr[8] = (byte)BitDepth;
        ihdr[9] = (byte)ColorType;
        ihdr[12] = Interlaced ? (byte)1 : (byte)0;
        WriteChunk(file, "IHDR", ihdr);

        foreach ((string type, byte[] data) in ChunksBeforePalette)
        {
            WriteChunk(file, type, data);
        }

        if (Palette is not null)
        {
            WriteChunk(file, "PLTE", Palette);
        }

        if (Transparency is not null)
        {
            WriteChunk(file, "tRNS", Transparency);
        }

        foreach ((string type, byte[] data) in ChunksBeforeImageData)
        {
            WriteChunk(file, type, data);
        }

        byte[] compressed = Compress(RawImageData());
        compressed = EditCompressed?.Invoke(compressed) ?? compressed;
        int parts = Math.Max(1, Math.Min(IdatChunks, compressed.Length));
        int size = (compressed.Length + parts - 1) / parts;
        for (int start = 0; start < compressed.Length; start += size)
        {
            WriteChunk(file, "IDAT", compressed.AsSpan(start, Math.Min(size, compressed.Length - start)).ToArray());
        }

        if (compressed.Length == 0)
        {
            WriteChunk(file, "IDAT", []);
        }

        foreach ((string type, byte[] data) in ChunksAfterImageData)
        {
            WriteChunk(file, type, data);
        }

        if (!OmitIend)
        {
            WriteChunk(file, "IEND", []);
        }

        return file.ToArray();
    }

    /// <summary>Returns the filtered scanlines of every pass, each with its filter-type byte, before compression.</summary>
    public byte[] RawImageData()
    {
        using MemoryStream raw = new();
        int line = 0;
        if (!Interlaced)
        {
            WritePass(raw, [.. Enumerable.Range(0, Height)], [.. Enumerable.Range(0, Width)], ref line);
        }
        else
        {
            int[] xs = [0, 4, 0, 2, 0, 1, 0];
            int[] ys = [0, 0, 4, 0, 2, 0, 1];
            int[] dx = [8, 8, 4, 4, 2, 2, 1];
            int[] dy = [8, 8, 8, 4, 4, 2, 2];
            for (int pass = 0; pass < 7; pass++)
            {
                int[] columns = [.. Enumerable.Range(0, Width).Where(x => x >= xs[pass] && (x - xs[pass]) % dx[pass] == 0)];
                int[] rows = [.. Enumerable.Range(0, Height).Where(y => y >= ys[pass] && (y - ys[pass]) % dy[pass] == 0)];
                if (columns.Length > 0 && rows.Length > 0)
                {
                    WritePass(raw, rows, columns, ref line);
                }
            }
        }

        return raw.ToArray();
    }

    public static byte[] Compress(byte[] data)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    public static byte[] Latin1(string keyword, string text)
    {
        return [.. Encoding.Latin1.GetBytes(keyword), 0, .. Encoding.Latin1.GetBytes(text)];
    }

    private void WritePass(MemoryStream raw, int[] rows, int[] columns, ref int line)
    {
        int bitsPerPixel = Channels * BitDepth;
        int rowBytes = ((columns.Length * bitsPerPixel) + 7) / 8;
        int stride = Math.Max(1, bitsPerPixel / 8);
        byte[] previous = new byte[rowBytes];
        foreach (int y in rows)
        {
            byte[] packed = new byte[rowBytes];
            int bit = 0;
            foreach (int x in columns)
            {
                for (int c = 0; c < Channels; c++)
                {
                    int value = Sample(x, y, c);
                    if (BitDepth == 16)
                    {
                        packed[bit / 8] = (byte)(value >> 8);
                        packed[(bit / 8) + 1] = (byte)value;
                    }
                    else
                    {
                        packed[bit / 8] |= (byte)(value << (8 - BitDepth - (bit % 8)));
                    }

                    bit += BitDepth;
                }
            }

            byte filter = Filter(line++);
            raw.WriteByte(filter);
            for (int i = 0; i < rowBytes; i++)
            {
                int left = i >= stride ? packed[i - stride] : 0;
                int above = previous[i];
                int upperLeft = i >= stride ? previous[i - stride] : 0;
                int predicted = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    _ => Paeth(left, above, upperLeft),
                };
                raw.WriteByte((byte)(packed[i] - predicted));
            }

            previous = packed;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private void WriteChunk(Stream file, string type, byte[] data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        file.Write(header);
        file.Write(data);
        uint crc = ReferenceCrc([.. header[4..], .. data]);
        if (type == BadCrcChunk)
        {
            crc ^= 1;
        }

        Span<byte> trailer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
        file.Write(trailer);
    }

    /// <summary>Bitwise CRC-32 straight from the specification's sample code, independent of Egami's implementation.</summary>
    private static uint ReferenceCrc(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
        }

        return ~c;
    }
}
