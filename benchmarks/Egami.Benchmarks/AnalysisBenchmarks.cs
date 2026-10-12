using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Analysis;
using Egami.Operations;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Benchmarks;

/// <summary>
/// The Phase 2.5.5 speed scenarios, without the competitors (egami-compare times those): comparison, statistics, colors and hashes
/// on 4000 × 3000 images, quantize and remap, dominant colors, and the animation steps on a 300-frame 640 × 480 recording, at one
/// thread and all cores.
/// </summary>
[MemoryDiagnoser]
public class AnalysisBenchmarks
{
    private Raster<Rgba8> _photo = null!;
    private Raster<Rgba8> _changed = null!;
    private Raster<Rgba16> _photo16 = null!;
    private Raster<Rgba8> _recording = null!;
    private Raster<Rgba8> _sticker = null!;

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
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgba8((byte)(x / 16), (byte)(y / 12), (byte)random.Next(256), 255);
            }
        }

        _changed = _photo.Derive(o => o.Brightness(1.02f));
        _photo16 = _photo.As<Rgba16>();
        _recording = Raster.Create<Rgba8>(640, 480, new Rgba8(240, 240, 240, 255), context);
        _recording.Frames[0].Metadata.Delay = TimeSpan.FromMilliseconds(40);
        for (int i = 1; i < 300; i++)
        {
            using Raster<Rgba8> frame = Raster.Create<Rgba8>(640, 480, new Rgba8((byte)(240 - (i / 10)), 240, 240, 255));
            _recording.Frames.Add(frame.Frames[0]).Metadata.Delay = TimeSpan.FromMilliseconds(40);
        }

        _sticker = Raster.Create<Rgba8>(200, 200, new Rgba8(250, 200, 0, 200), context);
        _sticker.Frames[0].Metadata.Delay = TimeSpan.FromMilliseconds(70);
        for (int i = 1; i < 24; i++)
        {
            using Raster<Rgba8> frame = Raster.Create<Rgba8>(200, 200, new Rgba8(250, (byte)(200 - (i * 5)), 0, 200));
            _sticker.Frames.Add(frame.Frames[0]).Metadata.Delay = TimeSpan.FromMilliseconds(70);
        }

        GC.KeepAlive(MemoryMarshal.AsBytes(_photo.Row(0)).Length);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _photo.Dispose();
        _changed.Dispose();
        _photo16.Dispose();
        _recording.Dispose();
        _sticker.Dispose();
    }

    [Benchmark]
    public CompareResult Compare() => Raster.Compare(_photo, _changed);

    [Benchmark]
    public CompareResult CompareSsim() => Raster.Compare(_photo, _changed, new CompareOptions { Ssim = true });

    [Benchmark]
    public RasterStatistics Statistics8() => _photo.Statistics();

    [Benchmark]
    public RasterStatistics Statistics16() => _photo16.Statistics();

    [Benchmark]
    public Histogram Histogram() => _photo.Histogram(ColorChannel.Luminance);

    [Benchmark]
    public long CountColors() => _photo.CountColors();

    [Benchmark]
    public bool IsOpaque() => _photo.IsOpaque();

    [Benchmark]
    public bool IsGray() => _photo.IsGray();

    [Benchmark]
    public ulong PerceptualHashDct() => PerceptualHash.Dct(_photo);

    [Benchmark]
    public void Quantize64() => _photo.Derive(o => o.Quantize(new PaletteOptions { MaxColors = 64 })).Dispose();

    [Benchmark]
    public void Quantize64Atkinson() => _photo.Derive(o => o.Quantize(new PaletteOptions { MaxColors = 64, Dither = DitherMode.Atkinson })).Dispose();

    [Benchmark]
    public void Remap16() => _photo.Derive(o => o.Remap(Palettes.Uniform(4, 2, 2))).Dispose();

    [Benchmark]
    public IReadOnlyList<WeightedColor> DominantColors() => _photo.DominantColors(5);

    [Benchmark]
    public void MergeDuplicates() => _recording.Derive(o => o.MergeDuplicates()).Dispose();

    [Benchmark]
    public void Retime() => _recording.Derive(o => o.Retime(10)).Dispose();

    [Benchmark]
    public void DrawAnimation() => _recording.Derive(o => o.DrawAnimation(_sticker, new Point(100, 100))).Dispose();
}
