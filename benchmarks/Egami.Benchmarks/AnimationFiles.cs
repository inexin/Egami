using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Formats.Png;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Benchmarks;

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
