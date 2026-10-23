using Egami.Formats.Avif;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Animated AVIF writing: a raster with several frames is written as an image sequence (avis) of one AV1 stream, key frame first,
/// and reads back with every frame, its delay, the loop count and the alpha.
/// </summary>
public class AvifAnimationWriteTests
{
    private static readonly string Photo = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-q100.jpg");

    /// <summary>Gets an animation panning across the photo, each frame <paramref name="delayMs"/> long, transparent in a corner when asked.</summary>
    internal static Raster<Rgba8> Animation(int width, int height, int frames, bool alpha, int delayMs = 40)
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Photo);
        Raster<Rgba8> anim = Raster.Create<Rgba8>(width, height);
        for (int i = 0; i < frames; i++)
        {
            Frame<Rgba8> frame = i == 0 ? anim.Frames[0] : anim.Frames.Add(anim.Frames[0]);
            frame.Metadata.Delay = TimeSpan.FromMilliseconds(delayMs * (i % 3 == 2 ? 2 : 1));
            for (int y = 0; y < height; y++)
            {
                Span<Rgba8> row = frame.Row(y);
                photo.Row(y + 20 + i)[(30 + (2 * i))..][..width].CopyTo(row);
                if (alpha)
                {
                    for (int x = 0; x < width / 3; x++)
                    {
                        row[x].A = (byte)Math.Clamp((x * 12) + (y * 3) + (i * 20), 0, 255);
                    }
                }
            }
        }

        return anim;
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public void FramesDelaysAndLoopsReadBack(bool alpha, int loops)
    {
        using Raster<Rgba8> anim = Animation(96, 64, 5, alpha);
        anim.Metadata.LoopCount = loops;
        byte[] file = AvifWriterTests.Save(anim, new AvifWriterOptions { Quality = 70, Effort = 3 });
        using Raster<Rgba8> back = Raster.Load<Rgba8>(file);
        Assert.Equal(5, back.FrameCount);
        Assert.Equal(loops, back.Metadata.LoopCount);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(anim.Frames[i].Metadata.Delay, back.Frames[i].Metadata.Delay);
            double psnr = Psnr(anim.Frames[i], back.Frames[i], 96, 64);
            Assert.True(psnr > 30, $"Frame {i} at {psnr:F1} dB.");
            if (alpha)
            {
                // Alpha is lossless by default.
                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 96; x++)
                    {
                        Assert.Equal(anim.Frames[i].Row(y)[x].A, back.Frames[i].Row(y)[x].A);
                    }
                }
            }
        }

        Assert.Equal("avis", ((AvifDetails)back.Metadata.FormatDetails!).MajorBrand);
    }

    [Fact]
    public void LosslessFramesReadBackExactly()
    {
        using Raster<Rgba8> anim = Animation(40, 30, 4, alpha: true);
        using Raster<Rgba8> back = Raster.Load<Rgba8>(AvifWriterTests.Save(anim, new AvifWriterOptions { Lossless = true }));
        Assert.Equal(4, back.FrameCount);
        for (int i = 0; i < 4; i++)
        {
            for (int y = 0; y < 30; y++)
            {
                Assert.True(anim.Frames[i].Row(y).SequenceEqual(back.Frames[i].Row(y)), $"Frame {i} differs on row {y}.");
            }
        }
    }

    [Fact]
    public void InterFramesAreSmallerThanStills()
    {
        using Raster<Rgba8> anim = Animation(96, 64, 6, alpha: false);
        byte[] sequence = AvifWriterTests.Save(anim, new AvifWriterOptions { Quality = 60 });
        using Raster<Rgba8> first = Raster.Create<Rgba8>(96, 64);
        for (int y = 0; y < 64; y++)
        {
            anim.Frames[0].Row(y).CopyTo(first.Row(y));
        }

        byte[] still = AvifWriterTests.Save(first, new AvifWriterOptions { Quality = 60 });
        Assert.True(sequence.Length < still.Length * 3, $"Six frames took {sequence.Length} bytes, one still {still.Length}.");
    }

    private static double Psnr(Frame<Rgba8> a, Frame<Rgba8> b, int width, int height)
    {
        double se = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Rgba8 p = a.Row(y)[x], q = b.Row(y)[x];
                se += ((p.R - q.R) * (p.R - q.R)) + ((p.G - q.G) * (p.G - q.G)) + ((p.B - q.B) * (p.B - q.B));
            }
        }

        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 * 3 * width * height / se);
    }
}
