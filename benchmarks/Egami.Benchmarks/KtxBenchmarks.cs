using System.Buffers.Binary;

using BenchmarkDotNet.Attributes;

using Egami.Compression;
using Egami.Formats.Extended;

namespace Egami.Benchmarks;

/// <summary>
/// KTX 2.0 decoding, one thread: a 2048 × 2048 texture in BC1, BC7 and RGBA8, and BC7 supercompressed with Zstandard, loaded in
/// its natural pixel type, so a change shows up in time and allocations. The blocks are pseudo-random bytes from a fixed seed, as
/// in <see cref="DdsBenchmarks"/>, so the block decoders the two formats share cost the same; the gap is the container.
/// </summary>
[MemoryDiagnoser]
public class KtxBenchmarks
{
    private readonly EgamiContext _context = EgamiContext.Default.WithKtx2() with { MaxParallelism = 1 };
    private byte[] _file = [];

    [Params("BC1", "BC7", "RGBA8", "BC7-Zstd")]
    public string Format { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        const int Size = 2048;
        (int vk, int bytes) = Format switch
        {
            "BC1" => (133, Size * Size / 2),
            "BC7" or "BC7-Zstd" => (145, Size * Size),
            _ => (37, Size * Size * 4),
        };
        byte[] level = new byte[bytes];
        new Random(42).NextBytes(level);
        int scheme = 0;
        long uncompressed = bytes;
        if (Format == "BC7-Zstd")
        {
            byte[] packed = new byte[Zstandard.MaxCompressedLength(bytes)];
            level = packed[..Zstandard.Compress(level, packed)];
            scheme = 2;
        }

        // Header, one level index entry, a 28-byte basic data format descriptor (sRGB primaries, linear transfer), the level.
        const int DfdOffset = 80 + 24;
        const int DataOffset = 144;
        _file = new byte[DataOffset + level.Length];
        Span<byte> h = _file;
        new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(h);
        uint[] fields = [(uint)vk, 1, Size, Size, 0, 0, 1, 1, (uint)scheme, DfdOffset, 28, 0, 0];
        for (int i = 0; i < fields.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h[(12 + (i * 4))..], fields[i]);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(h[80..], DataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(h[88..], (ulong)level.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[96..], (ulong)uncompressed);
        BinaryPrimitives.WriteUInt32LittleEndian(h[DfdOffset..], 28);
        BinaryPrimitives.WriteUInt32LittleEndian(h[(DfdOffset + 8)..], 2 | (24u << 16));
        h[DfdOffset + 12] = 1;
        h[DfdOffset + 13] = 1;
        h[DfdOffset + 14] = 1;
        level.CopyTo(h[DataOffset..]);
    }

    [Benchmark]
    public int Load()
    {
        using Raster raster = Raster.Load(_file, _context);
        return raster.Width;
    }
}
