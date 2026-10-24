using Egami.Pixels;
using Egami.Video.Analysis;
using Egami.Video.Audio;

namespace Egami.Video.Tests.Analysis;

/// <summary>
/// The stage D audio pictures (V1.4 Build Plan tasks 15 and 16): the real FFT against a direct DFT, and the waveform and
/// spectrogram checked by what they must show (where a tone or sweep lands, how far the outline reaches) rather than against
/// stored pictures.
/// </summary>
public sealed class AudioPicturesTests
{
    private const int Rate = 48000;

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(1024)]
    public void FftMatchesADirectDft(int size)
    {
        Random random = new(size);
        float[] input = new float[size];
        for (int i = 0; i < size; i++)
        {
            input[i] = (float)((random.NextDouble() * 2) - 1);
        }

        RealFft fft = new(size);
        double[] re = new double[fft.Bins], im = new double[fft.Bins];
        fft.Spectrum(input, re, im);
        float[] magnitudes = new float[fft.Bins];
        fft.Magnitudes(input, magnitudes);
        for (int k = 0; k < fft.Bins; k++)
        {
            double expectedRe = 0, expectedIm = 0;
            for (int n = 0; n < size; n++)
            {
                double angle = -2 * Math.PI * k * n / size;
                expectedRe += input[n] * Math.Cos(angle);
                expectedIm += input[n] * Math.Sin(angle);
            }

            Assert.Equal(expectedRe, re[k], 1e-9 * size);
            Assert.Equal(expectedIm, im[k], 1e-9 * size);
            Assert.Equal(Math.Sqrt((expectedRe * expectedRe) + (expectedIm * expectedIm)), magnitudes[k], 1e-5 * size);
        }
    }

    [Fact]
    public void ToneLandsOnItsRowAtFullBrightness()
    {
        // A full-scale 1 kHz sine: on the linear axis 1 kHz of 24 kHz sits 1/24 up from the bottom, and it reads 0 dB.
        SpectrogramOptions options = new() { Width = 40, Height = 480, Colors = SpectrogramColors.Grayscale };
        using Raster<Rgba8> picture = AudioPictures.Spectrogram(Frames(1, Rate, i => Math.Sin(2 * Math.PI * 1000 * i / Rate), 2 * Rate), Seconds(2), options, cancellationToken: TestContext.Current.CancellationToken);
        int expected = (int)Math.Round(480 * (1 - (1000.0 / 24000)) - 0.5);
        for (int x = 0; x < picture.Width; x++)
        {
            (int row, byte value) = Brightest(picture, x);
            Assert.InRange(row, expected - 1, expected + 1);
            Assert.True(value >= 250, $"column {x} peaks at {value}");
        }

        // Far from the tone the Hann window's sidelobes are deep below the floor's top.
        Assert.True(picture.Row(10)[20].R < 64);
    }

    [Fact]
    public void LogSweepIsAStraightLineOnTheLogAxis()
    {
        // An exponential sweep from 200 Hz to 12.8 kHz rises by the same number of octaves each second, so on the logarithmic axis
        // its brightest row falls on a straight line. A long window keeps the bins narrower than the rows at the low end.
        const double Seconds = 8, F0 = 200, F1 = 12800;
        double k = Math.Log(F1 / F0);
        SpectrogramOptions options = new() { Width = 120, Height = 300, WindowSize = 8192, Scale = FrequencyScale.Logarithmic, Colors = SpectrogramColors.Grayscale };
        IEnumerable<AudioFrame> sweep = Frames(1, Rate, i =>
        {
            double t = (double)i / Rate;
            return 0.5 * Math.Sin(2 * Math.PI * F0 * Seconds / k * (Math.Exp(k * t / Seconds) - 1));
        }, (long)(Seconds * Rate));
        using Raster<Rgba8> picture = AudioPictures.Spectrogram(sweep, AudioPicturesTests.Seconds(Seconds), options, cancellationToken: TestContext.Current.CancellationToken);

        double[] rows = new double[picture.Width];
        for (int x = 0; x < picture.Width; x++)
        {
            rows[x] = Brightest(picture, x).Row;
        }

        // Least-squares line through the rows, then every column within two rows of it; the first window holds the sweep's onset,
        // whose click spreads over all frequencies, so it only counts towards the line.
        double meanX = (picture.Width - 1) / 2.0, meanY = rows.Average();
        double sxy = 0, sxx = 0;
        for (int x = 0; x < rows.Length; x++)
        {
            sxy += (x - meanX) * (rows[x] - meanY);
            sxx += (x - meanX) * (x - meanX);
        }

        double slope = sxy / sxx;
        for (int x = 1; x < rows.Length; x++)
        {
            Assert.InRange(rows[x] - (meanY + (slope * (x - meanX))), -2, 2);
        }

        // 200 Hz to 12.8 kHz is log(64) / log(1200) of the 20 Hz to 24 kHz axis, crossed while the windows move over all but one window.
        double expected = -300 * Math.Log(64) / Math.Log(1200) / (picture.Width - 1) * (1 - (8192.0 / (Seconds * Rate)));
        Assert.Equal(expected, slope, 0.05);
    }

    [Fact]
    public void WaveformOutlineReachesThePeaks()
    {
        WaveformOptions options = new() { Width = 50, Height = 201 };
        using Raster<Rgba8> picture = AudioPictures.Waveform(Frames(1, Rate, i => 0.5 * Math.Sin(2 * Math.PI * 440 * i / Rate)), Seconds(1), options, cancellationToken: TestContext.Current.CancellationToken);

        // Middle 100.5, half 100.5: +0.5 reaches row 50 and -0.5 row 150.
        for (int x = 0; x < picture.Width; x++)
        {
            (int top, int bottom) = Extent(picture, x, options.Background);
            Assert.InRange(top, 49, 51);
            Assert.InRange(bottom, 149, 151);
            Assert.Equal(options.Foreground, picture.Row(top)[x]);
            Assert.Equal(options.Rms, picture.Row(100)[x]);
        }
    }

    [Fact]
    public void DecibelScaleShowsQuietAudio()
    {
        // A -20 dBFS sine with the floor at -60 reaches two thirds of the way from the middle to the edge.
        WaveformOptions options = new() { Width = 20, Height = 301, Scale = WaveformScale.Decibel };
        using Raster<Rgba8> picture = AudioPictures.Waveform(Frames(1, Rate, i => 0.1 * Math.Sin(2 * Math.PI * 440 * i / Rate)), Seconds(1), options, cancellationToken: TestContext.Current.CancellationToken);
        (int top, int bottom) = Extent(picture, 10, options.Background);
        Assert.InRange(top, 49, 51);
        Assert.InRange(bottom, 249, 251);
    }

    [Fact]
    public void StackedChannelsGetTheirOwnStrips()
    {
        WaveformOptions options = new() { Width = 30, Height = 200, Channels = AudioPictureChannels.Stacked };
        using Raster<Rgba8> picture = AudioPictures.Waveform(Frames(2, Rate, i => 0.9 * Math.Sin(2 * Math.PI * 440 * i / Rate), gain: c => c == 0 ? 1 : 0), Seconds(1), options, cancellationToken: TestContext.Current.CancellationToken);

        // The loud left channel fills most of the top strip; the silent right one is a single line through its middle.
        (int top, int bottom) = Extent(picture, 15, options.Background);
        Assert.InRange(top, 4, 6);
        Assert.InRange(bottom, 149, 151);
        int lines = 0;
        for (int y = 100; y < 200; y++)
        {
            lines += picture.Row(y)[15] != options.Background ? 1 : 0;
        }

        Assert.Equal(1, lines);
    }

    [Fact]
    public void StackedSpectrogramSplitsTheChannels()
    {
        SpectrogramOptions options = new() { Width = 10, Height = 200, Channels = AudioPictureChannels.Stacked, Colors = SpectrogramColors.Grayscale };
        using Raster<Rgba8> picture = AudioPictures.Spectrogram(Frames(2, Rate, i => Math.Sin(2 * Math.PI * 6000 * i / Rate), gain: c => c == 0 ? 1 : 0), Seconds(1), options, cancellationToken: TestContext.Current.CancellationToken);

        // 6 kHz of 24 kHz is a quarter up from the bottom of the top strip; the silent bottom strip stays at the darkest color.
        int expected = (int)Math.Round(100 * 0.75 - 0.5);
        Assert.InRange(Brightest(picture, 5).Row, expected - 1, expected + 1);
        for (int y = 100; y < 200; y++)
        {
            Assert.Equal(0, picture.Row(y)[5].R);
        }
    }

    [Fact]
    public void ShortAudioStillFillsEveryColumn()
    {
        // Less audio than one window: every column sees the same zero-padded window.
        SpectrogramOptions options = new() { Width = 8, Height = 64, Colors = SpectrogramColors.Grayscale };
        using Raster<Rgba8> picture = AudioPictures.Spectrogram(Frames(1, Rate, i => Math.Sin(2 * Math.PI * 3000 * i / Rate), 500), new MediaTime(500, new Rational(1, Rate)), options, cancellationToken: TestContext.Current.CancellationToken);
        int row = Brightest(picture, 0).Row;
        for (int x = 1; x < 8; x++)
        {
            Assert.Equal(row, Brightest(picture, x).Row);
        }
    }

    [Fact]
    public void MagmaRunsFromBlackToPaleYellow()
    {
        SpectrogramOptions options = new() { Width = 4, Height = 64, Floor = -1, Ceiling = 0 };
        using Raster<Rgba8> silent = AudioPictures.Spectrogram(Frames(1, Rate, _ => 0, 4096), Seconds(4096.0 / Rate), options, cancellationToken: TestContext.Current.CancellationToken);
        Rgba8 dark = silent.Row(0)[0];
        Assert.True(dark.R < 4 && dark.G < 4 && dark.B < 8, dark.ToString());

        using Raster<Rgba8> loud = AudioPictures.Spectrogram(Frames(1, Rate, i => Math.Sin(2 * Math.PI * 12000 * i / Rate), 4096), Seconds(4096.0 / Rate), options with { Height = 1 }, cancellationToken: TestContext.Current.CancellationToken);
        Rgba8 bright = loud.Row(0)[0];
        Assert.True(bright.R > 245 && bright.G > 240 && bright.B is > 170 and < 205, bright.ToString());
    }

    [Fact]
    public void OptionsAreChecked()
    {
        AudioFrame[] none = [];
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioPictures.Spectrogram(none, Seconds(1), new SpectrogramOptions { WindowSize = 1000 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioPictures.Spectrogram(none, Seconds(1), new SpectrogramOptions { Floor = 0 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioPictures.Waveform(none, Seconds(1), new WaveformOptions { Width = 0 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioPictures.Waveform(none, Seconds(1), new WaveformOptions { Floor = 3 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioPictures.Waveform(none, MediaTime.Zero, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ThreeHoursKeepMemoryFlat()
    {
        // Three hours of 8 kHz mono, fed as one reused frame: 86 million samples, 345 MB as floats. Both pictures keep only
        // per-column sums or one window, so what they allocate is the picture and their tables.
        const int SlowRate = 8000;
        const long Samples = 3L * 3600 * SlowRate;
        using AudioFrame frame = new(SampleFormat.F32Planar, ChannelLayout.Mono, SlowRate, 4000, MediaTime.Zero);
        Span<float> span = frame.GetSpan<float>(0);
        for (int i = 0; i < span.Length; i++)
        {
            span[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 500 * i / SlowRate));
        }

        MediaTime duration = new(Samples, new Rational(1, SlowRate));
        IEnumerable<AudioFrame> Repeat()
        {
            for (long done = 0; done < Samples; done += frame.SampleCount)
            {
                yield return frame;
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        using (Raster<Rgba8> waveform = AudioPictures.Waveform(Repeat(), duration, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(1800, waveform.Width);
        }

        long waveformBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        using (Raster<Rgba8> spectrogram = AudioPictures.Spectrogram(Repeat(), duration, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(512, spectrogram.Height);
        }

        long spectrogramBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(waveformBytes < 4_000_000, $"waveform allocated {waveformBytes} bytes");
        Assert.True(spectrogramBytes < 6_000_000, $"spectrogram allocated {spectrogramBytes} bytes");
    }

    private static MediaTime Seconds(double seconds) => MediaTime.FromSeconds(seconds);

    /// <summary>Returns the brightest row of a column (by red, which grows with every map) and its value.</summary>
    private static (int Row, byte Value) Brightest(Raster<Rgba8> picture, int x)
    {
        int best = 0;
        byte value = 0;
        for (int y = 0; y < picture.Height; y++)
        {
            byte r = picture.Row(y)[x].R;
            if (r > value)
            {
                (best, value) = (y, r);
            }
        }

        return (best, value);
    }

    /// <summary>Returns the first and last rows of a column that are not the background.</summary>
    private static (int Top, int Bottom) Extent(Raster<Rgba8> picture, int x, Rgba8 background)
    {
        int top = -1, bottom = -1;
        for (int y = 0; y < picture.Height; y++)
        {
            if (picture.Row(y)[x] != background)
            {
                top = top < 0 ? y : top;
                bottom = y;
            }
        }

        return (top, bottom);
    }

    private static IEnumerable<AudioFrame> Frames(int channels, int rate, Func<long, double> wave, long samples = Rate, Func<int, double>? gain = null)
    {
        const int Size = 1024;
        ChannelLayout layout = ChannelLayout.ForChannelCount(channels);
        for (long start = 0; start < samples; start += Size)
        {
            int count = (int)Math.Min(Size, samples - start);
            using AudioFrame frame = new(SampleFormat.F32Planar, layout, rate, count, new MediaTime(start, new Rational(1, rate)));
            for (int c = 0; c < channels; c++)
            {
                Span<float> span = frame.GetSpan<float>(c);
                double g = gain?.Invoke(c) ?? 1;
                for (int i = 0; i < count; i++)
                {
                    span[i] = (float)(g * wave(start + i));
                }
            }

            yield return frame;
        }
    }
}
