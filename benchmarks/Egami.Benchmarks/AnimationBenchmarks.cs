using BenchmarkDotNet.Attributes;

using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Formats.Png;
using Egami.Operations;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Benchmarks;

/// <summary>
/// The animation design's scenarios: GIF decode (animated, still, first frame), GIF encode (photo with and without dithering,
/// exact, animated with global and per-frame palettes), APNG decode and encode, the animated thumbnail, and Probe of 50 GIFs and
/// 50 APNGs. Inputs are made in memory from fixed formulas.
/// </summary>
[MemoryDiagnoser]
public class AnimationBenchmarks
{
    private static readonly GifWriterOptions Dithered = new();
    private static readonly GifWriterOptions Undithered = new() { Palette = new PaletteOptions { Dither = DitherMode.None } };
    private static readonly GifWriterOptions GlobalPalette = new() { PaletteScope = GifPaletteScope.Global };
    private static readonly GifWriterOptions PerFramePalette = new() { PaletteScope = GifPaletteScope.PerFrame };
    private static readonly ReaderOptions FirstFrame = new() { Frames = ..1 };

    private EgamiContext _context = EgamiContext.Default;
    private Raster<Rgba8> _video = null!;
    private Raster<Rgb8> _photo = null!;
    private Raster<Rgba8> _still = null!;
    private byte[] _animatedGif = [];
    private byte[] _stillGif = [];
    private byte[] _apng = [];
    private byte[][] _probeFiles = [];

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _context = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads };
        _video = AnimationFiles.Video(_context);
        _photo = AnimationFiles.Photo(_context);
        _animatedGif = AnimationFiles.AnimatedGif();
        _stillGif = AnimationFiles.StillGif();
        _apng = AnimationFiles.Apng();
        _still = Raster.Load<Rgba8>(_stillGif, _context);
        _probeFiles = AnimationFiles.ProbeFiles();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _video.Dispose();
        _photo.Dispose();
        _still.Dispose();
    }

    [Benchmark]
    public int GifDecodeAnimated()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_animatedGif, _context);
        return raster.FrameCount;
    }

    [Benchmark]
    public int GifDecodeStill()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_stillGif, _context);
        return raster.Width;
    }

    [Benchmark]
    public int GifDecodeFirstFrame()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_animatedGif, _context, FirstFrame);
        return raster.FrameCount;
    }

    [Benchmark]
    public void GifEncodePhotoDithered() => _photo.Save(Stream.Null, GifFormat.Info, Dithered);

    [Benchmark]
    public void GifEncodePhotoUndithered() => _photo.Save(Stream.Null, GifFormat.Info, Undithered);

    [Benchmark]
    public void GifEncodeExact() => _still.Save(Stream.Null, GifFormat.Info, Dithered);

    [Benchmark]
    public void GifEncodeAnimatedGlobal() => _video.Save(Stream.Null, GifFormat.Info, GlobalPalette);

    [Benchmark]
    public void GifEncodeAnimatedPerFrame() => _video.Save(Stream.Null, GifFormat.Info, PerFramePalette);

    [Benchmark]
    public int ApngDecode()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_apng, _context);
        return raster.FrameCount;
    }

    [Benchmark]
    public void ApngEncode() => _video.Save(Stream.Null, PngFormat.Info, PngWriterOptions.Default);

    [Benchmark]
    public void AnimatedThumbnail()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_animatedGif, _context);
        raster.Apply(ops => ops.Fit(160, 160));
        raster.Save(Stream.Null, GifFormat.Info, Dithered);
    }

    [Benchmark]
    public int Probe100()
    {
        int frames = 0;
        foreach (byte[] file in _probeFiles)
        {
            frames += Raster.Probe(file, _context).FrameCount ?? 0;
        }

        return frames;
    }
}
