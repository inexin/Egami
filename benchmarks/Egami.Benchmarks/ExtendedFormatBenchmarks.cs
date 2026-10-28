using BenchmarkDotNet.Attributes;

using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// Egami.Formats.Extended's formats, one thread: a 4000 × 3000 picture saved and loaded in each format (bilevel for XBM and WBMP,
/// 256 colors for XPM), so a change shows up in time and allocations. The comparison with ImageMagick lives in egami-compare
/// (<c>-- extended</c>). Inputs are made in memory from fixed formulas.
/// </summary>
[MemoryDiagnoser]
public class ExtendedFormatBenchmarks
{
    private readonly EgamiContext _context = EgamiContext.Default.WithExtendedFormats() with { MaxParallelism = 1 };
    private Raster _image = null!;
    private FormatInfo _format = null!;
    private byte[] _file = [];

    [Params("PCX", "SGI", "Sun raster", "XBM", "XPM", "WBMP", "Farbfeld")]
    public string Format { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _format = _context.Formats.Single(f => f.Name == Format);
        Raster<Rgb8> picture = Raster.Create<Rgb8>(4000, 3000, _context);
        for (int y = 0; y < picture.Height; y++)
        {
            Span<Rgb8> row = picture.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                // Smooth gradients with flat bands, so run-length coders find runs and literals.
                int band = (x / 64) % 4;
                row[x] = Format == "XPM"
                    ? new Rgb8((byte)((x / 250) * 16), (byte)((y / 188) * 16), 0)
                    : band == 0 ? new Rgb8(200, 200, 200) : new Rgb8((byte)x, (byte)(y >> 4), (byte)(x ^ y));
            }
        }

        _image = Format is "XBM" or "WBMP" ? picture.Derive(ops => ops.Threshold(0.5f)) : picture;
        if (!ReferenceEquals(_image, picture))
        {
            picture.Dispose();
        }

        using MemoryStream output = new();
        _image.Save(output, _format);
        _file = output.ToArray();
    }

    [GlobalCleanup]
    public void Cleanup() => _image.Dispose();

    [Benchmark]
    public int Load()
    {
        using Raster raster = Raster.Load(_file, _format, _context);
        return raster.Width;
    }

    [Benchmark]
    public long Save()
    {
        using MemoryStream output = new(_file.Length);
        _image.Save(output, _format);
        return output.Length;
    }
}
