using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Webp;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Webp;

/// <summary>
/// Animated WebP reading: every composed canvas equals libwebp's WebPAnimDecoder's (lossless frames exactly, lossy ones within the
/// one level lossy RGB allows), with its delays, loop count, frame ranges and frame-at-a-time streaming.
/// </summary>
public class WebpAnimationReadTests
{
    public static TheoryData<string> Animations() => [.. WebpCorpus.Animations];

    [Theory]
    [MemberData(nameof(Animations))]
    public void FramesMatchLibwebpsAnimationDecoder(string file)
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(WebpCorpus.Read(file));
        byte[][] expected = WebpCorpus.Frames(file);
        int[] timestamps = WebpCorpus.Timestamps(file);
        Assert.Equal(expected.Length, raster.FrameCount);
        int previous = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            Frame<Rgba8> frame = raster.Frames[i];
            Compare(file, i, frame, expected[i], Lossless(frame));
            Assert.Equal(TimeSpan.FromMilliseconds(timestamps[i] - previous), frame.Metadata.Delay);
            previous = timestamps[i];
        }

        Assert.Equal(WebpCorpus.Info(file).GetProperty("loop").GetInt32(), raster.Metadata.LoopCount);
        Assert.Empty(raster.Metadata.Warnings);
    }

    [Theory]
    [MemberData(nameof(Animations))]
    public void ProbeCountsTheFrames(string file)
    {
        RasterInfo info = Raster.Probe(WebpCorpus.Read(file));
        Assert.Equal(WebpCorpus.Frames(file).Length, info.FrameCount);
        Assert.Equal(WebpCorpus.Info(file).GetProperty("loop").GetInt32(), info.LoopCount);
    }

    [Theory]
    [InlineData("anim-pan-lossless.webp", 0, 2)]
    [InlineData("anim-pan-lossless.webp", 3, 8)]
    [InlineData("anim-mixed.webp", 5, 6)]
    public void FrameRangesKeepThoseFrames(string file, int start, int end)
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(WebpCorpus.Read(file), options: new ReaderOptions { Frames = start..end });
        byte[][] expected = WebpCorpus.Frames(file);
        Assert.Equal(end - start, raster.FrameCount);
        for (int i = 0; i < raster.FrameCount; i++)
        {
            Compare(file, start + i, raster.Frames[i], expected[start + i], Lossless(raster.Frames[i]));
        }
    }

    [Fact]
    public void RangesCountedFromTheEndWork()
    {
        const string File = "anim-lossless.webp";
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(WebpCorpus.Read(File), options: new ReaderOptions { Frames = ^2.. });
        byte[][] expected = WebpCorpus.Frames(File);
        Assert.Equal(2, raster.FrameCount);
        Compare(File, expected.Length - 1, raster.Frames[1], expected[^1], lossless: true);
    }

    [Fact]
    public void MetadataAfterTheFramesIsReadWhenTheRangeEndsEarly()
    {
        using Raster raster = Raster.Load(WebpCorpus.Read("meta-anim-xmp.webp"), options: new ReaderOptions { Frames = ..1 });
        Assert.Equal(1, raster.FrameCount);
        Assert.False(raster.Metadata.Xmp.IsEmpty);
    }

    [Theory]
    [MemberData(nameof(Animations))]
    public void StreamingGivesTheSameFrames(string file)
    {
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(WebpCorpus.Read(file));
        using RasterDecoder decoder = RasterDecoder.Open(new MemoryStream(WebpCorpus.Read(file)));
        using Raster<Rgba8> target = Raster.Create<Rgba8>(loaded.Width, loaded.Height);
        int frames = 0;
        while (decoder.ReadFrame(target) is FrameMetadata metadata)
        {
            Assert.Equal(loaded.Frames[frames].Metadata.Delay, metadata.Delay);
            for (int y = 0; y < loaded.Height; y++)
            {
                Assert.True(loaded.Frames[frames].Plane.Row(y).SequenceEqual(target.Row(y)), $"{file}: frame {frames}, row {y}");
            }

            frames++;
        }

        Assert.Equal(loaded.FrameCount, frames);
    }

    [Fact]
    public void BackgroundColorIsUsedWhenAsked()
    {
        const string File = "anim-gif2webp.webp";
        using Raster<Rgba8> plain = Raster.Load<Rgba8>(WebpCorpus.Read(File));
        using Raster<Rgba8> filled = Raster.Load<Rgba8>(WebpCorpus.Read(File), options: new WebpReaderOptions { UseBackgroundColor = true });
        Rgba8 background = Assert.IsType<WebpDetails>(filled.Metadata.FormatDetails).BackgroundColor;
        Assert.Equal(plain.FrameCount, filled.FrameCount);

        // Where the plain decode is transparent, the filled one shows the background color.
        bool any = false;
        for (int y = 0; y < plain.Height; y++)
        {
            ReadOnlySpan<Rgba8> a = plain.Frames[0].Plane.Row(y), b = filled.Frames[0].Plane.Row(y);
            for (int x = 0; x < a.Length; x++)
            {
                if (a[x].A == 0)
                {
                    Assert.Equal(background, b[x]);
                    any = true;
                }
            }
        }

        Assert.True(any || background.A == 0 || plain.Frames[0].Plane.Row(0)[0].A != 0, "the first frame covers the canvas");
    }

    [Fact]
    public void BlendMatchesLibwebpsIntegerFormula()
    {
        // Spot values from the formula: an opaque destination, a half-transparent source.
        Rgba8 blended = WebpAnimation.BlendNonPremultiplied(new Rgba8(200, 100, 0, 128), new Rgba8(0, 50, 250, 255));
        int dstFactor = (255 * (256 - 128)) >> 8;
        int alpha = 128 + dstFactor;
        uint scale = (1u << 24) / (uint)alpha;
        Assert.Equal((byte)alpha, blended.A);
        Assert.Equal((byte)((((200 * 128) + (0 * dstFactor)) * scale) >> 24), blended.R);
        Assert.Equal(new Rgba8(1, 2, 3, 0), WebpAnimation.BlendNonPremultiplied(new Rgba8(9, 9, 9, 0), new Rgba8(1, 2, 3, 0)));
    }

    private static bool Lossless(Frame<Rgba8> frame) => frame.Metadata.FormatDetails is WebpFrameDetails { Lossless: true };

    private static void Compare(string file, int index, Frame<Rgba8> frame, byte[] expected, bool lossless)
    {
        int width = frame.Plane.Width;
        int worst = 0;
        for (int y = 0; y < frame.Plane.Height; y++)
        {
            ReadOnlySpan<byte> row = MemoryMarshal.AsBytes(frame.Plane.Row(y));
            ReadOnlySpan<byte> reference = expected.AsSpan(y * width * 4, width * 4);
            for (int i = 0; i < row.Length; i++)
            {
                int d = Math.Abs(row[i] - reference[i]);
                if (i % 4 == 3 && d != 0 && reference[i] == 255)
                {
                    Assert.Fail($"{file} frame {index}: alpha differs at ({i / 4}, {y})");
                }

                worst = Math.Max(worst, d);
            }
        }

        // Earlier lossy frames show through later blended ones, so a lossless frame can carry their one-level error.
        Assert.True(worst <= (lossless && !file.Contains("lossy", StringComparison.Ordinal) && !file.Contains("mixed", StringComparison.Ordinal) ? 0 : 1), $"{file} frame {index}: off by {worst}");
    }
}
