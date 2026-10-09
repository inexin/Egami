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
    private static readonly GifWriterOptions DitheredFast = new() { Palette = new PaletteOptions { FastMapping = true } };
    private static readonly GifWriterOptions PerFrameFast = new() { PaletteScope = GifPaletteScope.PerFrame, Palette = new PaletteOptions { FastMapping = true } };
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
    public void GifEncodePhotoDitheredFast() => _photo.Save(Stream.Null, GifFormat.Info, DitheredFast);

    [Benchmark]
    public void GifEncodeAnimatedPerFrameFast() => _video.Save(Stream.Null, GifFormat.Info, PerFrameFast);

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

/// <summary>The animation benchmark inputs, made from <see cref="BenchImages.Photo"/>.</summary>
internal static class AnimationFiles
{
    private static readonly Lazy<byte[]> AnimatedGifFile = new(() => Save(Video(EgamiContext.Default), GifFormat.Info, GifWriterOptions.Default));
    private static readonly Lazy<byte[]> StillGifFile = new(() => Save(Photo(EgamiContext.Default), GifFormat.Info, GifWriterOptions.Default));
    private static readonly Lazy<byte[]> ApngFile = new(() => Save(Video(EgamiContext.Default), PngFormat.Info, PngWriterOptions.Default));

    internal static byte[] AnimatedGif() => AnimatedGifFile.Value;

    internal static byte[] StillGif() => StillGifFile.Value;

    internal static byte[] Apng() => ApngFile.Value;

    /// <summary>The 1920 × 1080 benchmark photo as Rgb8.</summary>
    internal static Raster<Rgb8> Photo(EgamiContext context)
    {
        using Raster<Rgba8> photo = BenchImages.Photo(1920, 1080);
        Raster<Rgb8> result = Raster.Create<Rgb8>(1920, 1080, context);
        for (int y = 0; y < 1080; y++)
        {
            PixelConvert.Convert<Rgba8, Rgb8>(photo.Row(y), result.Row(y));
        }

        return result;
    }

    /// <summary>
    /// 100 frames of 480 × 270 "video": a still background with a 160 × 90 window that pans across the photo and a square that
    /// moves, so later frames change in rectangles, as most animations do.
    /// </summary>
    internal static Raster<Rgba8> Video(EgamiContext context)
    {
        using Raster<Rgba8> photo = BenchImages.Photo(1920, 1080);
        Raster<Rgba8> video = Raster.Create<Rgba8>(480, 270, context);
        for (int i = 0; i < 100; i++)
        {
            Frame<Rgba8> frame = i == 0 ? video.Frames[0] : video.Frames.Add(video.Frames[0]);
            frame.Metadata.Delay = TimeSpan.FromMilliseconds(40);
            for (int y = 0; y < 270; y++)
            {
                Span<Rgba8> row = frame.Row(y);
                for (int x = 0; x < 480; x++)
                {
                    row[x] = photo.Row(y * 4)[x * 4];
                }

                if (y is >= 40 and < 130)
                {
                    photo.Row(300 + y + i).Slice(400 + (i * 6), 160).CopyTo(row.Slice(280, 160));
                }

                if (y >= 150 + (i % 50) && y < 180 + (i % 50))
                {
                    row.Slice(20 + (i * 3), 30).Fill(new Rgba8(220, 40, 40, 255));
                }
            }
        }

        return video;
    }

    /// <summary>50 small GIFs and 50 small APNGs of a few frames each.</summary>
    internal static byte[][] ProbeFiles()
    {
        List<byte[]> files = [];
        using Raster<Rgba8> video = Video(EgamiContext.Default);
        for (int i = 0; i < 50; i++)
        {
            using Raster<Rgba8> clip = video.Derive(ops => ops.Resize(48 + i, 27 + (i / 2)));
            while (clip.FrameCount > 3 + (i % 5))
            {
                clip.Frames.RemoveAt(clip.FrameCount - 1);
            }

            files.Add(Save(clip, GifFormat.Info, GifWriterOptions.Default));
            files.Add(Save(clip, PngFormat.Info, PngWriterOptions.Default));
        }

        return [.. files];
    }

    private static byte[] Save(Raster raster, FormatInfo format, WriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, format, options);
        return stream.ToArray();
    }
}
