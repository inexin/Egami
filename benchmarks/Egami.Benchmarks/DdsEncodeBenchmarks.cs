using BenchmarkDotNet.Attributes;

using Egami.Formats.Extended;
using Egami.Formats.Extended.Dds;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// DDS encoding, one thread: a 1024 × 1024 picture saved in each writable block format and in RGBA8 at the default effort,
/// without mips, so a change shows up in time and allocations. The comparisons with texconv live in egami-compare. The picture
/// is smooth waves with hard edges and noise from a fixed seed.
/// </summary>
[MemoryDiagnoser]
public class DdsEncodeBenchmarks
{
    private readonly EgamiContext _context = EgamiContext.Default.WithDds() with { MaxParallelism = 1 };
    private Raster<Rgba8> _image = null!;
    private DdsWriterOptions _options = null!;

    [Params("BC1", "BC3", "BC4", "BC5", "BC6H", "BC7", "RGBA8")]
    public string Format { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _image = Raster.Create<Rgba8>(1024, 1024, _context);
        Random random = new(3);
        for (int y = 0; y < _image.Height; y++)
        {
            Span<Rgba8> row = _image.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                double wave = Math.Sin(x * 0.03) * Math.Cos(y * 0.02);
                int edge = ((x / 64) + (y / 64)) % 2 == 0 ? 40 : 0;
                row[x] = new Rgba8(
                    (byte)Math.Clamp(120 + (90 * wave) + edge + random.Next(-6, 7), 0, 255),
                    (byte)Math.Clamp(100 + (60 * Math.Sin((x + y) * 0.01)) + random.Next(-6, 7), 0, 255),
                    (byte)Math.Clamp(80 + (y / 8) - edge + random.Next(-6, 7), 0, 255),
                    (byte)Math.Clamp(160 + (80 * wave), 0, 255));
            }
        }

        DdsTextureFormat format = Format switch
        {
            "BC1" => DdsTextureFormat.Bc1,
            "BC3" => DdsTextureFormat.Bc3,
            "BC4" => DdsTextureFormat.Bc4,
            "BC5" => DdsTextureFormat.Bc5,
            "BC6H" => DdsTextureFormat.Bc6h,
            "BC7" => DdsTextureFormat.Bc7,
            _ => DdsTextureFormat.Rgba8,
        };
        _options = new DdsWriterOptions { Format = format, MipLevels = 1 };
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public long Save()
    {
        using MemoryStream output = new(4 << 20);
        _image.Save(output, DdsFormat.Info, _options);
        return output.Length;
    }
}
