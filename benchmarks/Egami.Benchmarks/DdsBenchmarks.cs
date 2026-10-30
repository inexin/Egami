using System.Buffers.Binary;

using BenchmarkDotNet.Attributes;

using Egami.Formats.Extended;

namespace Egami.Benchmarks;

/// <summary>
/// DDS decoding, one thread: a 2048 × 2048 texture in each block format and in RGBA8, loaded in its natural pixel type, so a
/// change shows up in time and allocations. The comparisons with texconv and ImageMagick live in egami-compare. The blocks are
/// pseudo-random bytes from a fixed seed, which every block format decodes (BC6H and BC7 meet every mode, reserved ones included).
/// </summary>
[MemoryDiagnoser]
public class DdsBenchmarks
{
    private readonly EgamiContext _context = EgamiContext.Default.WithDds() with { MaxParallelism = 1 };
    private byte[] _file = [];

    [Params("BC1", "BC3", "BC4", "BC5", "BC6H", "BC7", "RGBA8")]
    public string Format { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        const int Size = 2048;
        (int dxgi, int bytes) = Format switch
        {
            "BC1" => (71, Size * Size / 2),
            "BC3" => (77, Size * Size),
            "BC4" => (80, Size * Size / 2),
            "BC5" => (83, Size * Size),
            "BC6H" => (95, Size * Size),
            "BC7" => (98, Size * Size),
            _ => (28, Size * Size * 4),
        };
        _file = new byte[148 + bytes];
        Span<byte> h = _file;
        "DDS "u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 124);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], 0x1007);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], Size);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(h[76..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(h[80..], 4);
        "DX10"u8.CopyTo(h[84..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[128..], (uint)dxgi);
        BinaryPrimitives.WriteUInt32LittleEndian(h[132..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(h[140..], 1);
        new Random(42).NextBytes(_file.AsSpan(148));
    }

    [Benchmark]
    public int Load()
    {
        using Raster raster = Raster.Load(_file, _context);
        return raster.Width;
    }
}
