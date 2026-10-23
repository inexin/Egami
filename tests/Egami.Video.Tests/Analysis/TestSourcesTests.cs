using System.Numerics;

using Egami.Pixels;
using Egami.Video.Analysis;
using Egami.Video.Audio;

namespace Egami.Video.Tests.Analysis;

/// <summary>
/// The generated test sources (V1.4 Build Plan task 2): bars carry the code values SMPTE RP 219 gives, frames and audio blocks are
/// timed back to back, and the tones and noise come out at the level asked for.
/// </summary>
public sealed class TestSourcesTests
{
    private static readonly TestVideoFormat Hd10 = new() { Width = 1920, Height = 1080, BitDepth = 10, ChromaFormat = ChromaFormat.Yuv444 };

    [Fact]
    public void HdBarsHaveTheirCodeValues()
    {
        using YuvVideoFrame frame = TestSources.ColorBars(Hd10, MediaTime.FromSeconds(0.04)).First();
        YuvRaster<ushort> raster = Assert.IsType<YuvRaster<ushort>>(frame.Raster);

        // Pattern 1: 40% gray, then 75% yellow as the third bar.
        Assert.Equal((414, 512, 512), Codes(raster, 120, 300));
        Assert.Equal((674, 176, 543), Codes(raster, 240 + 206 + 103, 300));

        // Pattern 2: 100% cyan at the left.
        Assert.Equal((754, 615, 64), Codes(raster, 120, 660));

        // Pattern 4: 15% gray at the sides, the -2% and +4% pluge bars under the red bar.
        Assert.Equal(195, Codes(raster, 120, 1000).Y);
        Assert.Equal(46, Codes(raster, 240 + 308 + 412 + 170 + 34, 1000).Y);
        Assert.Equal(99, Codes(raster, 240 + 308 + 412 + 170 + 68 + 70 + 68 + 70 + 34, 1000).Y);
        Assert.Equal(940, Codes(raster, 240 + 308 + 206, 1000).Y);
    }

    [Fact]
    public void SdBarsAndEightBitCodes()
    {
        TestVideoFormat sd = new() { Width = 720, Height = 480, ChromaFormat = ChromaFormat.Yuv444, Color = YuvColor.Bt601Limited };
        using YuvVideoFrame frame = TestSources.ColorBars(sd, MediaTime.FromSeconds(0.04)).First();
        YuvRaster<byte> raster = Assert.IsType<YuvRaster<byte>>(frame.Raster);

        // The top bars are 75%: white 180, blue at the right.
        Assert.Equal((180, 128, 128), Codes(raster, 50, 100));
        (int y, int cb, int cr) blue = Codes(raster, 700, 100);
        Assert.Equal((35, 212, 114), (blue.y, blue.cb, blue.cr), new Close(1));
    }

    [Fact]
    public void FramesAreTimedBackToBack()
    {
        TestVideoFormat format = new() { Width = 64, Height = 36, FrameRate = new Rational(30000, 1001) };
        int i = 0;
        foreach (YuvVideoFrame frame in TestSources.Pattern(format, MediaTime.FromSeconds(1)))
        {
            using (frame)
            {
                Assert.Equal(i * 1001.0 / 30000, frame.Timestamp.Seconds, 9);
                Assert.Equal(1001.0 / 30000, frame.Duration!.Value.Seconds, 9);
                i++;
            }
        }

        Assert.Equal(30, i);
    }

    [Fact]
    public void SolidAndGradientFillThePicture()
    {
        TestVideoFormat format = new() { Width = 64, Height = 36, ChromaFormat = ChromaFormat.Yuv444 };
        using (YuvVideoFrame black = TestSources.Solid(format, MediaTime.FromSeconds(0.1), new Rgba8(0, 0, 0, 255)).First())
        {
            Assert.Equal((16, 128, 128), Codes((YuvRaster<byte>)black.Raster, 10, 10));
        }

        using YuvVideoFrame ramp = TestSources.Gradient(format, MediaTime.FromSeconds(0.1), new Rgba8(0, 0, 0, 255), new Rgba8(255, 255, 255, 255)).First();
        YuvRaster<byte> planes = (YuvRaster<byte>)ramp.Raster;
        Assert.InRange(Codes(planes, 0, 5).Y, 16, 20);
        Assert.InRange(Codes(planes, 63, 5).Y, 231, 235);
        Assert.True(Codes(planes, 20, 5).Y < Codes(planes, 40, 5).Y);
    }

    [Fact]
    public void SineIsAtItsLevel()
    {
        TestAudioFormat format = new() { SampleRate = 48000, Layout = ChannelLayout.Mono };
        double sum = 0, peak = 0;
        long count = 0;
        foreach (AudioFrame frame in TestSources.Sine(format, MediaTime.FromSeconds(1), 1000, -18))
        {
            using (frame)
            {
                Assert.Equal(count / 48000.0, frame.Timestamp.Seconds, 9);
                foreach (float s in frame.GetSpan<float>(0))
                {
                    sum += s * s;
                    peak = Math.Max(peak, Math.Abs(s));
                }

                count += frame.SampleCount;
            }
        }

        double expected = Math.Pow(10, -18 / 20.0);
        Assert.Equal(48000, count);
        Assert.Equal(expected, peak, 3);
        Assert.Equal(expected / Math.Sqrt(2), Math.Sqrt(sum / count), 3);
    }

    [Theory]
    [InlineData(NoiseColor.White)]
    [InlineData(NoiseColor.Pink)]
    public void NoiseIsAtItsLevelAndRepeats(NoiseColor color)
    {
        TestAudioFormat format = new() { Layout = ChannelLayout.Mono };
        float[] first = Samples(TestSources.Noise(format, MediaTime.FromSeconds(1), color, -20, seed: 7));
        float[] again = Samples(TestSources.Noise(format, MediaTime.FromSeconds(1), color, -20, seed: 7));
        Assert.Equal(first, again);
        // The level is the RMS of a sine with the same peak, so noise at -20 dBFS has an RMS of 0.1 / sqrt(2). Pink noise's slowest
        // rows hardly move in a second, so its level is right only on average.
        double rms = Math.Sqrt(first.Sum(s => (double)s * s) / first.Length);
        Assert.Equal(-20 - (20 * Math.Log10(Math.Sqrt(2))), 20 * Math.Log10(rms), color == NoiseColor.White ? 0.2 : 1.5);
    }

    [Fact]
    public void SilenceAndSweep()
    {
        Assert.All(Samples(TestSources.Silence(null, MediaTime.FromSeconds(0.1))), s => Assert.Equal(0, s));
        float[] sweep = Samples(TestSources.Sweep(new TestAudioFormat { Layout = ChannelLayout.Mono }, MediaTime.FromSeconds(1), 20, 2000));
        Assert.Equal(48000, sweep.Length);
        Assert.Equal(Math.Pow(10, -18 / 20.0), sweep.Max(s => Math.Abs((double)s)), 2);
    }

    private static float[] Samples(IEnumerable<AudioFrame> frames)
    {
        List<float> all = [];
        foreach (AudioFrame frame in frames)
        {
            using (frame)
            {
                all.AddRange(frame.GetSpan<float>(0).ToArray());
            }
        }

        return [.. all];
    }

    private static (int Y, int Cb, int Cr) Codes<T>(YuvRaster<T> raster, int x, int y)
        where T : unmanaged, IBinaryInteger<T>
        => (int.CreateTruncating(raster.Y.Row(y)[x]), int.CreateTruncating(raster.U!.Row(y)[x]), int.CreateTruncating(raster.V!.Row(y)[x]));

    private sealed class Close(int tolerance) : IEqualityComparer<(int, int, int)>
    {
        public bool Equals((int, int, int) a, (int, int, int) b)
            => Math.Abs(a.Item1 - b.Item1) <= tolerance && Math.Abs(a.Item2 - b.Item2) <= tolerance && Math.Abs(a.Item3 - b.Item3) <= tolerance;

        public int GetHashCode((int, int, int) obj) => 0;
    }
}
