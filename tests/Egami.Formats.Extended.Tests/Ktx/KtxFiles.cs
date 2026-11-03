using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Formats.Extended.Tests.Dds;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Ktx;

/// <summary>
/// Builds KTX 1.1 and KTX 2.0 files byte by byte, from the two specifications, and reads the committed corpus that
/// tools/make-ktx-references.py makes with KTX-Software.
/// </summary>
internal static class KtxFiles
{
    public static string Corpus(string name) => Path.Combine(AppContext.BaseDirectory, "images", "ktx", name);

    public static string Reference(string name) => Path.Combine(AppContext.BaseDirectory, "reference", "ktx", name);

    /// <summary>The raw bytes a raw-*.ktx2 file of the corpus was made from.</summary>
    public static byte[] RawBytes(string name) => Gunzip(name + ".bin.gz");

    /// <summary>A gzipped reference file, unzipped.</summary>
    public static byte[] Gunzip(string name)
    {
        using FileStream file = File.OpenRead(Reference(name));
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        return bytes.ToArray();
    }

    /// <summary>
    /// A KTX 1.1 file. <paramref name="levels"/> holds each level's images back to back as the file stores them (rows padded
    /// to 4 bytes for uncompressed data); each gets its imageSize word, given or the level's length.
    /// </summary>
    public static byte[] Ktx1(int glType, int glTypeSize, int glFormat, int glInternal, int width, int height, IReadOnlyList<byte[]> levels,
        int depth = 0, int arrayElements = 0, int faces = 1, bool bigEndian = false, IReadOnlyList<(string Key, string Value)>? keyValues = null, int? imageSize = null)
    {
        using MemoryStream file = new();
        file.Write([0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] kvd = KeyValueData(keyValues ?? [], bigEndian);
        uint[] fields = [0x04030201, (uint)glType, (uint)glTypeSize, (uint)glFormat, (uint)glInternal, (uint)glFormat, (uint)width, (uint)height,
            (uint)depth, (uint)arrayElements, (uint)faces, (uint)levels.Count, (uint)kvd.Length];
        foreach (uint field in fields)
        {
            Word(file, field, bigEndian);
        }

        file.Write(kvd);
        foreach (byte[] level in levels)
        {
            Word(file, (uint)(imageSize ?? level.Length), bigEndian);
            file.Write(level);
        }

        return file.ToArray();
    }

    /// <summary>
    /// A KTX 2.0 file whose levels are given mip 0 first and stored smallest first, with a basic data format descriptor
    /// (colour model, primaries, transfer function, flags, and a sample of each channel type in <paramref name="channels"/>),
    /// optional key/value pairs and optional supercompression global data.
    /// </summary>
    public static byte[] Ktx2(int vk, int width, int height, IReadOnlyList<byte[]> levels, int depth = 0, int layers = 0, int faces = 1, int scheme = 0,
        IReadOnlyList<long>? uncompressed = null, byte model = 1, byte primaries = 1, byte transfer = 1, byte flags = 0,
        IReadOnlyList<(string Key, string Value)>? keyValues = null, byte[]? globalData = null, byte[]? channels = null)
    {
        channels ??= [];
        byte[] dfd = new byte[4 + 24 + (16 * channels.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(dfd, (uint)dfd.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(dfd.AsSpan(8), 2 | ((24u + (16u * (uint)channels.Length)) << 16));
        if (channels.Length > 0)
        {
            // A Basis Universal descriptor: 4 × 4 blocks of 8 bytes a slice (ETC1S) or 16 (UASTC), a sample per slice.
            bool uastc = model == 166;
            dfd[4 + 12] = 3;
            dfd[4 + 13] = 3;
            dfd[4 + 16] = (byte)(uastc ? 16 : 8 * channels.Length);
            for (int c = 0; c < channels.Length; c++)
            {
                Span<byte> sample = dfd.AsSpan(4 + 24 + (16 * c));
                BinaryPrimitives.WriteUInt16LittleEndian(sample, (ushort)(c * 64));
                sample[2] = (byte)(uastc ? 127 : 63);
                sample[3] = channels[c];
                BinaryPrimitives.WriteUInt32LittleEndian(sample[12..], uint.MaxValue);
            }
        }

        dfd[12] = model;
        dfd[13] = primaries;
        dfd[14] = transfer;
        dfd[15] = flags;
        byte[] kvd = KeyValueData(keyValues ?? [], false);
        int indexEnd = 80 + (levels.Count * 24);
        int dfdOffset = indexEnd;
        int kvdOffset = dfdOffset + dfd.Length;
        globalData ??= [];
        long sgdOffset = Align(kvdOffset + kvd.Length, 8);
        int alignment = scheme == 1 ? 1 : 16; // BasisLZ levels are not padded
        long data = Align(sgdOffset + globalData.Length, alignment);
        long[] offsets = new long[levels.Count];
        for (int m = levels.Count - 1; m >= 0; m--)
        {
            offsets[m] = data;
            data = Align(data + levels[m].Length, alignment);
        }

        byte[] file = new byte[data];
        Span<byte> h = file;
        new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(h);
        uint[] fields = [(uint)vk, 1, (uint)width, (uint)height, (uint)depth, (uint)layers, (uint)faces, (uint)levels.Count, (uint)scheme,
            (uint)dfdOffset, (uint)dfd.Length, kvd.Length > 0 ? (uint)kvdOffset : 0, (uint)kvd.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[(12 + (i * 4))..], fields[i]);
        }

        for (int m = 0; m < levels.Count; m++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(h[(80 + (m * 24))..], (ulong)offsets[m]);
            BinaryPrimitives.WriteUInt64LittleEndian(h[(88 + (m * 24))..], (ulong)levels[m].Length);
            BinaryPrimitives.WriteUInt64LittleEndian(h[(96 + (m * 24))..], (ulong)(uncompressed?[m] ?? levels[m].Length));
            levels[m].CopyTo(h[(int)offsets[m]..]);
        }

        if (globalData.Length > 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(h[64..], (ulong)sgdOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(h[72..], (ulong)globalData.Length);
            globalData.CopyTo(h[(int)sgdOffset..]);
        }

        dfd.CopyTo(h[dfdOffset..]);
        kvd.CopyTo(h[kvdOffset..]);
        return file;
    }

    /// <summary>A DDS file with the DX10 header holding <paramref name="data"/> in DXGI format <paramref name="dxgi"/>.</summary>
    public static byte[] Dds(int width, int height, int dxgi, byte[] data) => DdsFiles.Dx10(width, height, dxgi, data);

    /// <summary>Returns every row of a raster converted to RgbaF32, as bytes, so rasters of any pixel type compare bit for bit.</summary>
    public static byte[] Floats(Raster raster)
    {
        using Raster<RgbaF32> floats = raster.As<RgbaF32>();
        byte[] bytes = new byte[floats.Width * floats.Height * 16];
        for (int y = 0; y < floats.Height; y++)
        {
            MemoryMarshal.AsBytes(floats.Row(y)).CopyTo(bytes.AsSpan(y * floats.Width * 16));
        }

        return bytes;
    }

    private static byte[] KeyValueData(IReadOnlyList<(string Key, string Value)> pairs, bool bigEndian)
    {
        using MemoryStream data = new();
        foreach ((string key, string value) in pairs)
        {
            byte[] pair = [.. Encoding.UTF8.GetBytes(key), 0, .. Encoding.UTF8.GetBytes(value), 0];
            Word(data, (uint)pair.Length, bigEndian);
            data.Write(pair);
            data.Write(new byte[(4 - (pair.Length % 4)) % 4]);
        }

        return data.ToArray();
    }

    private static void Word(Stream stream, uint value, bool bigEndian)
    {
        Span<byte> word = stackalloc byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(word, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, value);
        }

        stream.Write(word);
    }

    private static long Align(long value, int to) => (value + to - 1) / to * to;
}
