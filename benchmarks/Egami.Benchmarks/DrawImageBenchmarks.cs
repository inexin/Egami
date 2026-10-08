using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>The compositing scenario from the operations design: a 512 × 512 logo with alpha onto a 4000 × 3000 Rgba8 photo.</summary>
[MemoryDiagnoser]
public class DrawImageBenchmarks
{
    private Raster<Rgba8> _photo = null!;
    private Raster<Rgba8> _logo = null!;

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads };
        _photo = Raster.Create<Rgba8>(4000, 3000, context);
        Random random = new(1);
        for (int y = 0; y < _photo.Height; y++)
        {
            Span<Rgba8> row = _photo.Row(y);
            random.NextBytes(MemoryMarshal.AsBytes(row));
            foreach (ref Rgba8 p in row)
            {
                p.A = 255;
            }
        }

        // A disc with a soft edge: transparent corners, an opaque middle and a ring of partial alpha.
        _logo = Raster.Create<Rgba8>(512, 512, context);
        for (int y = 0; y < 512; y++)
        {
            Span<Rgba8> row = _logo.Row(y);
            for (int x = 0; x < 512; x++)
            {
                double r = Math.Sqrt(((x - 255.5) * (x - 255.5)) + ((y - 255.5) * (y - 255.5)));
                byte a = (byte)Math.Clamp((250 - r) * 255 / 20, 0, 255);
                row[x] = new Rgba8((byte)x, (byte)y, 128, a);
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _photo.Dispose();
        _logo.Dispose();
    }

    [Benchmark]
    public void DrawLogo()
    {
        _photo.Apply(ops => ops.DrawImage(_logo, new Point(1700, 1200)));
    }

    [Benchmark]
    public void DrawLogoHalfOpacity()
    {
        _photo.Apply(ops => ops.DrawImage(_logo, new Point(1700, 1200), 0.5f));
    }
}
