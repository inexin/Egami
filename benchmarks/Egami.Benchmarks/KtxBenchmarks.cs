using System.Buffers.Binary;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Compression;
using Egami.Formats.Extended;
using Egami.Formats.Extended.Textures.Blocks;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// KTX 2.0 decoding, one thread: a 2048 × 2048 texture in BC1, BC7, ETC2 RGBA8, EAC RG11, ASTC 6 × 6 LDR and HDR, RGBA8, BC7
/// supercompressed with Zstandard, and Basis Universal ETC1S (BasisLZ) and UASTC LDR 4 × 4, loaded in its natural pixel type, so a change shows up in time and allocations. The BC and
/// ETC blocks are pseudo-random bytes from a fixed seed, as in <see cref="DdsBenchmarks"/>, so the block decoders the two formats
/// share cost the same; the gap is the container. Random bytes are mostly illegal ASTC blocks, so the LDR blocks are a photo-like
/// picture encoded at effort 2, and the HDR blocks those of tests/images/ktx/astc-6x6-hdr.ktx2 (astcenc -medium) repeated. ETC1S is
/// the same kind of picture encoded at quality 128, effort 1; UASTC repeats the blocks of tests/images/ktx/uastc-rgba.ktx2 (basisu).
/// </summary>
[MemoryDiagnoser]
public class KtxBenchmarks
{
    private readonly EgamiContext _context = EgamiContext.Default.WithKtx2() with { MaxParallelism = 1 };
    private byte[] _file = [];

    [Params("BC1", "BC7", "ETC2-RGBA", "EAC-RG11", "ASTC-6x6", "ASTC-6x6-HDR", "RGBA8", "BC7-Zstd", "ETC1S", "UASTC")]
    public string Format { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        const int Size = 2048;
        const int AstcBytes = 342 * 342 * 16;
        (int vk, int bytes) = Format switch
        {
            "BC1" => (133, Size * Size / 2),
            "BC7" or "BC7-Zstd" => (145, Size * Size),
            "ETC2-RGBA" => (151, Size * Size),
            "EAC-RG11" => (155, Size * Size),
            "ASTC-6x6" => (165, AstcBytes),
            "ASTC-6x6-HDR" => (1000066004, AstcBytes),
            "ETC1S" => (0, 0),
            "UASTC" => (0, Size * Size),
            _ => (37, Size * Size * 4),
        };
        byte[] level = new byte[bytes];
        new Random(42).NextBytes(level);
        if (Format == "ASTC-6x6")
        {
            Tile(EncodedAstc(), level);
        }
        else if (Format == "ASTC-6x6-HDR")
        {
            Tile(CorpusBlocks("astc-6x6-hdr.ktx2"), level);
        }
        else if (Format == "UASTC")
        {
            Tile(CorpusBlocks("uastc-rgba.ktx2"), level);
        }

        byte[] globalData = [];
        if (Format == "ETC1S")
        {
            using Raster<Rgba8> photo = BenchImages.Photo(Size, Size);
            byte[] rgba = new byte[Size * Size * 4];
            for (int y = 0; y < Size; y++)
            {
                MemoryMarshal.AsBytes(photo.Row(y)).CopyTo(rgba.AsSpan(y * Size * 4));
            }

            Etc1sEncoded encoded = Etc1sEncoder.Encode([new Etc1sImage(rgba, Size, Size, 0)], false, 128, 1);
            (level, globalData) = (encoded.Levels[0], encoded.GlobalData);
        }

        int scheme = Format == "ETC1S" ? 1 : 0;
        long uncompressed = bytes;
        if (Format == "BC7-Zstd")
        {
            byte[] packed = new byte[Zstandard.MaxCompressedLength(bytes)];
            level = packed[..Zstandard.Compress(level, packed)];
            scheme = 2;
        }

        // Header, one level index entry, a basic data format descriptor (sRGB primaries, linear transfer; Basis Universal formats
        // add their sample), the global data of ETC1S, the level.
        bool basis = Format is "ETC1S" or "UASTC";
        const int DfdOffset = 80 + 24;
        int dfdLength = basis ? 44 : 28;
        int sgdOffset = DfdOffset + dfdLength;
        int dataOffset = sgdOffset + globalData.Length;
        _file = new byte[dataOffset + level.Length];
        Span<byte> h = _file;
        new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(h);
        uint[] fields = [(uint)vk, 1, Size, Size, 0, 0, 1, 1, (uint)scheme, DfdOffset, (uint)dfdLength, 0, 0];
        for (int i = 0; i < fields.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[(12 + (i * 4))..], fields[i]);
        }

        if (globalData.Length > 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(h[64..], (ulong)sgdOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(h[72..], (ulong)globalData.Length);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(h[80..], (ulong)dataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h[88..], (ulong)level.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[96..], (ulong)uncompressed);
        BinaryPrimitives.WriteUInt32LittleEndian(h[DfdOffset..], (uint)dfdLength);
        BinaryPrimitives.WriteUInt32LittleEndian(h[(DfdOffset + 8)..], 2 | ((uint)(dfdLength - 4) << 16));
        h[DfdOffset + 12] = (byte)(Format == "ETC1S" ? 163 : Format == "UASTC" ? 166 : 1);
        h[DfdOffset + 13] = 1;
        h[DfdOffset + 14] = 1;
        if (basis)
        {
            // 4 × 4 blocks, bytes per block, and one sample: ETC1S RGB (channel 0) or UASTC RGBA (channel 3).
            bool uastc = Format == "UASTC";
            h[DfdOffset + 16] = 3;
            h[DfdOffset + 17] = 3;
            h[DfdOffset + 20] = (byte)(uastc ? 16 : 8);
            h[DfdOffset + 30] = (byte)(uastc ? 127 : 63);
            h[DfdOffset + 31] = (byte)(uastc ? 3 : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(h[(DfdOffset + 40)..], uint.MaxValue);
        }

        globalData.CopyTo(h[sgdOffset..]);
        level.CopyTo(h[dataOffset..]);
    }

    [Benchmark]
    public int Load()
    {
        using Raster raster = Raster.Load(_file, _context);
        return raster.Width;
    }

    private static void Tile(byte[] blocks, byte[] level)
    {
        for (int i = 0; i < level.Length; i += blocks.Length)
        {
            blocks.AsSpan(0, Math.Min(blocks.Length, level.Length - i)).CopyTo(level.AsSpan(i));
        }
    }

    /// <summary>A 384 × 384 photo-like picture (64 × 64 blocks) in ASTC 6 × 6 at effort 2.</summary>
    private static byte[] EncodedAstc()
    {
        Astc astc = Astc.For(6, 6)!;
        using Raster<Rgba8> photo = BenchImages.Photo(384, 384);
        byte[] blocks = new byte[64 * 64 * 16];
        byte[] texels = new byte[36 * 4];
        for (int b = 0; b < 64 * 64; b++)
        {
            for (int y = 0; y < 6; y++)
            {
                MemoryMarshal.AsBytes(photo.Row(((b / 64) * 6) + y).Slice((b % 64) * 6, 6)).CopyTo(texels.AsSpan(y * 24));
            }

            AstcEncoder.Encode(astc, texels, blocks.AsSpan(b * 16, 16), 2, srgb: false);
        }

        return blocks;
    }

    /// <summary>The blocks of a single-level KTX 2 file of the test corpus.</summary>
    private static byte[] CorpusBlocks(string name)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "tests", "images", "ktx")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        byte[] file = File.ReadAllBytes(Path.Combine(root, "tests", "images", "ktx", name));
        int offset = (int)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(80));
        int length = (int)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(88));
        return file[offset..(offset + length)];
    }
}
