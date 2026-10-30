using Egami.Formats;
using Egami.Formats.Avif;
using Egami.Formats.Bmp;
using Egami.Formats.Gif;
using Egami.Formats.Jpeg;
using Egami.Formats.Jxl;
using Egami.Formats.Png;
using Egami.Formats.Tiff;
using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Tests.Formats;

/// <summary>
/// Save, SaveAsync, SaveAll and Transcode report <see cref="SaveProgress"/> as Egami.Video's jobs report MediaProgress: a first
/// report, reports that never go backwards, and a final one with a fraction of 1; they write the same bytes as without a handler,
/// and a canceled token stops the encode.
/// </summary>
public class SaveProgressTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Formats => ["png", "png-interlace", "jpeg", "jpeg-progressive", "bmp", "tiff", "gif-animated", "avif", "avif-alpha", "jxl-lossy", "jxl-lossless", "webp-lossy", "webp-lossless"];

    [Theory]
    [MemberData(nameof(Formats))]
    public void ReportsStartThenRisingFractionsThenFinal(string name)
    {
        (FormatInfo format, WriterOptions options, Raster raster) = Case(name);
        using (raster)
        {
            List<SaveProgress> reports = [];
            SaveReporter reporter = new(new Collect(reports), CancellationToken.None, new EveryCall());
            using MemoryStream stream = new();
            using (reporter.Enter(stream))
            {
                raster.Save(stream, format, options);
            }

            reporter.Finish();

            Assert.True(reports.Count >= 2);
            Assert.False(reports[0].IsFinal);
            Assert.All(reports[..^1], r => Assert.False(r.IsFinal));
            SaveProgress last = reports[^1];
            Assert.True(last.IsFinal);
            Assert.Equal(1, last.Fraction);
            Assert.Equal(stream.Length, last.BytesWritten);
            double previous = 0;
            foreach (SaveProgress r in reports)
            {
                if (r.Fraction is double f)
                {
                    Assert.InRange(f, previous, 1);
                    previous = f;
                }
            }

            using MemoryStream plain = new();
            raster.Save(plain, format, options);
            Assert.Equal(plain.ToArray(), stream.ToArray());
        }
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("bmp")]
    [InlineData("gif-animated")]
    [InlineData("avif")]
    [InlineData("avif-alpha")]
    [InlineData("jxl-lossy")]
    [InlineData("webp-lossy")]
    public void CountsWorkBetweenStartAndEnd(string name)
    {
        (FormatInfo format, WriterOptions options, Raster raster) = Case(name);
        using (raster)
        {
            List<SaveProgress> reports = [];
            SaveReporter reporter = new(new Collect(reports), CancellationToken.None, new EveryCall());
            using MemoryStream stream = new();
            using (reporter.Enter(stream))
            {
                raster.Save(stream, format, options);
            }

            Assert.Contains(reports, r => r.Fraction is > 0 and < 1);
        }
    }

    [Theory]
    [InlineData("jxl-lossless")]
    [InlineData("webp-lossless")]
    public void GatheringCodecsWithoutCountsLeaveTheFractionUnknown(string name)
    {
        (FormatInfo format, WriterOptions options, Raster raster) = Case(name);
        using (raster)
        {
            List<SaveProgress> reports = [];
            SaveReporter reporter = new(new Collect(reports), CancellationToken.None, new EveryCall());
            using MemoryStream stream = new();
            using (reporter.Enter(stream))
            {
                raster.Save(stream, format, options);
            }

            Assert.All(reports, r => Assert.Null(r.Fraction));
        }
    }

    [Fact]
    public void AnimationCountsFrames()
    {
        (FormatInfo format, WriterOptions options, Raster raster) = Case("gif-animated");
        using (raster)
        {
            List<SaveProgress> reports = [];
            using MemoryStream stream = new();
            raster.Save(stream, format, options, new Collect(reports), Ct);
            Assert.Equal(raster.FrameCount, reports[^1].Frames);
        }
    }

    [Fact]
    public void PublicSaveReportsStartAndFinal()
    {
        using Raster<Rgba8> raster = Gradient(64, 64, alpha: false);
        List<SaveProgress> reports = [];
        using MemoryStream stream = new();
        raster.Save(stream, PngFormat.Info, null, new Collect(reports), Ct);
        Assert.False(reports[0].IsFinal);
        Assert.Equal(0, reports[0].BytesWritten);
        Assert.True(reports[^1].IsFinal);
        Assert.Equal(stream.Length, reports[^1].BytesWritten);
    }

    [Fact]
    public void ReporterIsCurrentOnlyDuringTheSave()
    {
        using Raster<Rgba8> raster = Gradient(16, 16, alpha: false);
        SaveReporter? during = null;
        using MemoryStream stream = new();
        raster.Save(stream, PngFormat.Info, null, new Inline(_ => during ??= SaveReporter.Current), Ct);
        Assert.NotNull(during);
        Assert.Null(SaveReporter.Current);
    }

    [Fact]
    public void NoHandlerAndNoTokenMakesNoReporter()
    {
        Assert.Null(SaveReporter.For(null, CancellationToken.None));
        using CancellationTokenSource cts = new();
        Assert.NotNull(SaveReporter.For(null, cts.Token));
    }

    [Fact]
    public async Task SaveAsyncReportsAndWritesTheSameBytes()
    {
        using Raster<Rgba8> raster = Gradient(300, 200, alpha: true);
        List<SaveProgress> reports = [];
        using MemoryStream stream = new();
        await raster.SaveAsync(stream, PngFormat.Info, null, new Collect(reports), Ct);
        Assert.True(reports[^1].IsFinal);
        Assert.Equal(1, reports[^1].Fraction);
        using MemoryStream plain = new();
        raster.Save(plain, PngFormat.Info);
        Assert.Equal(plain.ToArray(), stream.ToArray());
    }

    [Theory]
    [InlineData("avif")]
    [InlineData("webp-lossy")]
    [InlineData("jxl-lossy")]
    [InlineData("png")]
    public void CancelingInsideTheEncodeStopsItAndDeletesTheFile(string name)
    {
        (FormatInfo format, WriterOptions options, Raster raster) = Case(name);
        using (raster)
        {
            string path = Path.Combine(Path.GetTempPath(), $"egami-save-progress-{Guid.NewGuid():N}{format.Extensions[0]}");
            using CancellationTokenSource cts = new();
            Inline cancel = new(r =>
            {
                if (!r.IsFinal && r.Fraction is > 0)
                {
                    cts.Cancel();
                }
            });
            SaveReporter reporter = new(cancel, cts.Token, new EveryCall());
            Assert.ThrowsAny<OperationCanceledException>(() => Raster.WriteFile(path, stream =>
            {
                using (reporter.Enter(stream))
                {
                    raster.Save(stream, format, options);
                }
            }));
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void ACanceledTokenStopsBeforeAnyByte()
    {
        using Raster<Rgba8> raster = Gradient(16, 16, alpha: false);
        using CancellationTokenSource cts = new();
        cts.Cancel();
        using MemoryStream stream = new();
        Assert.ThrowsAny<OperationCanceledException>(() => raster.Save(stream, PngFormat.Info, null, null, cts.Token));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void MultiPageTiffCountsPagesInOrder()
    {
        using Raster<Rgba8> a = Gradient(64, 200, alpha: false);
        using Raster<Rgba8> b = Gradient(64, 200, alpha: false);
        using Raster<Rgba8> c = Gradient(64, 200, alpha: false);
        List<SaveProgress> reports = [];
        SaveReporter reporter = new(new Collect(reports), CancellationToken.None, new EveryCall());
        using MemoryStream stream = new();
        using (reporter.Enter(stream))
        {
            Raster.SaveAll([a, b, c], stream, TiffFormat.Info);
        }

        double previous = 0;
        foreach (SaveProgress r in reports)
        {
            Assert.InRange(r.Fraction ?? previous, previous, 1);
            previous = r.Fraction ?? previous;
        }

        Assert.Contains(reports, r => r.Fraction is > 0.34 and < 0.66);
        Assert.Equal(3, reports[^1].Frames);
    }

    [Fact]
    public void SaveAllReportsFinal()
    {
        using Raster<Rgba8> a = Gradient(32, 32, alpha: false);
        using Raster<Rgba8> b = Gradient(32, 32, alpha: true);
        List<SaveProgress> reports = [];
        using MemoryStream stream = new();
        Raster.SaveAll([a, b], stream, TiffFormat.Info, null, new Collect(reports), Ct);
        Assert.True(reports[^1].IsFinal);
        using MemoryStream plain = new();
        Raster.SaveAll([a, b], plain, TiffFormat.Info);
        Assert.Equal(plain.ToArray(), stream.ToArray());
    }

    [Fact]
    public void TranscodeReportsRowsAndWritesTheSameBytes()
    {
        using Raster<Rgba8> raster = Gradient(256, 300, alpha: false);
        using MemoryStream png = new();
        raster.Save(png, PngFormat.Info);

        List<SaveProgress> reports = [];
        SaveReporter reporter = new(new Collect(reports), CancellationToken.None, new EveryCall());
        using MemoryStream output = new();
        png.Position = 0;
        using (reporter.Enter(output))
        {
            Raster.Transcode(png, output, JpegFormat.Info);
        }

        reporter.Finish();
        Assert.Contains(reports, r => r.Fraction is > 0 and < 1);
        Assert.Equal(output.Length, reports[^1].BytesWritten);

        using MemoryStream plain = new();
        png.Position = 0;
        Raster.Transcode(png, plain, JpegFormat.Info);
        Assert.Equal(plain.ToArray(), output.ToArray());

        List<SaveProgress> published = [];
        using MemoryStream again = new();
        png.Position = 0;
        Raster.Transcode(png, again, JpegFormat.Info, null, null, null, null, new Collect(published), Ct);
        Assert.True(published[^1].IsFinal);
    }

    private static (FormatInfo Format, WriterOptions Options, Raster Raster) Case(string name) => name switch
    {
        "png" => (PngFormat.Info, PngWriterOptions.Default, Gradient(200, 300, alpha: true)),
        "png-interlace" => (PngFormat.Info, new PngWriterOptions { Interlace = true }, Gradient(200, 300, alpha: true)),
        "jpeg" => (JpegFormat.Info, JpegWriterOptions.Default, Gradient(200, 300, alpha: false)),
        "jpeg-progressive" => (JpegFormat.Info, new JpegWriterOptions { Progressive = true }, Gradient(200, 300, alpha: false)),
        "bmp" => (BmpFormat.Info, WriterOptions.Default, Gradient(200, 300, alpha: false)),
        "tiff" => (TiffFormat.Info, WriterOptions.Default, Gradient(200, 300, alpha: false)),
        "gif-animated" => (GifFormat.Info, WriterOptions.Default, Animation(64, 48, 4)),
        "avif" => (AvifFormat.Info, new AvifWriterOptions { Effort = 0 }, Gradient(200, 300, alpha: false)),
        "avif-alpha" => (AvifFormat.Info, new AvifWriterOptions { Effort = 0 }, Gradient(200, 300, alpha: true)),
        "jxl-lossy" => (JxlFormat.Info, new JxlWriterOptions { Lossless = false }, Gradient(200, 300, alpha: false)),
        "jxl-lossless" => (JxlFormat.Info, new JxlWriterOptions { Lossless = true }, Gradient(200, 300, alpha: false)),
        "webp-lossy" => (WebpFormat.Info, new WebpWriterOptions { Lossless = false }, Gradient(200, 300, alpha: true)),
        "webp-lossless" => (WebpFormat.Info, new WebpWriterOptions { Lossless = true }, Gradient(200, 300, alpha: false)),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static Raster<Rgba8> Gradient(int width, int height, bool alpha)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)((x ^ y) & 255), alpha ? (byte)((x + y) & 255) : (byte)255);
            }
        }

        return raster;
    }

    private static Raster<Rgba8> Animation(int width, int height, int frames)
    {
        Raster<Rgba8> raster = Gradient(width, height, alpha: false);
        for (int i = 1; i < frames; i++)
        {
            Frame<Rgba8> frame = raster.Frames.Add(raster.Frames[0]);
            frame.Row(i).Fill(new Rgba8((byte)(i * 60), 40, 200, 255));
        }

        return raster;
    }

    /// <summary>Collects reports on the thread that sends them, unlike <see cref="Progress{T}"/>, which posts them.</summary>
    private sealed class Collect(List<SaveProgress> reports) : IProgress<SaveProgress>
    {
        public void Report(SaveProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }

    private sealed class Inline(Action<SaveProgress> report) : IProgress<SaveProgress>
    {
        public void Report(SaveProgress value) => report(value);
    }

    /// <summary>A clock that moves a second on every read, so every update is reported.</summary>
    private sealed class EveryCall : TimeProvider
    {
        private long _now;

        public override long GetTimestamp() => Interlocked.Add(ref _now, TimestampFrequency);
    }
}
