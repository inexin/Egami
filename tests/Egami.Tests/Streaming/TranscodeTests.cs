using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Operations;
using Egami.Pixels;
using Egami.Streaming;
using Egami.Tests.Memory;

namespace Egami.Tests.Streaming;

/// <summary>
/// Phase 2.2 task 11: Transcode gives the pixels of loading with the options it chose and running the rest of the chain, holds a
/// few bands, and refuses operations that cannot stream before writing anything.
/// </summary>
public abstract class TranscodeTests<TPart>
    where TPart : IPart
{
    private const int PartCount = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, Action<RasterOps>> Chains = new()
    {
        ["none"] = _ => { },
        ["fit"] = x => x.Fit(40, 40),
        ["fit-linear-lanczos"] = x => x.Fit(33, 21, ResampleFilter.Lanczos3, ColorSpace.Linear),
        ["cover"] = x => x.Cover(24, 40, Anchor.BottomRight),
        ["resize-up"] = x => x.Resize(150, 0, ResampleFilter.Bilinear),
        ["nearest"] = x => x.Resize(17, 9, ResampleFilter.Nearest),
        ["crop-resize"] = x => x.Crop(3, 2, 20, 15).Resize(11, 7),
        ["crop"] = x => x.Crop(1, 2, 9, 8),
        ["fit-crop"] = x => x.Fit(30, 30).Crop(2, 2, 10, 10),
        ["flip-colors"] = x => x.FlipHorizontal().Brightness(1.2f).Saturation(0.5f),
        ["gray-invert"] = x => x.Grayscale().Invert(0.7f).Fit(25, 25),
        ["orient"] = x => x.AutoOrient().Fit(50, 50),
    };

    public static TheoryData<string, string> FilesAndChains()
    {
        TheoryData<string, string> data = [];
        foreach (string file in StreamingCorpus.Asked)
        {
            foreach (string name in Chains.Keys)
            {
                data.Add(file, name);
            }
        }

        return Parts.Take(data, TPart.Index, PartCount, StreamingCorpus.NotAsked);
    }

    [Theory]
    [MemberData(nameof(FilesAndChains))]
    public void TranscodeEqualsLoadingAndRunningTheChain(string file, string name)
    {
        byte[] data = StreamingCorpus.Read(file);
        Action<RasterOps> chain = Chains[name];
        RasterInfo info;
        using (RasterDecoder probe = RasterDecoder.Open(data))
        {
            info = probe.Info;
        }

        TranscodePlan plan;
        try
        {
            plan = TranscodePlan.Create(chain, info, ReaderOptions.Default);
        }
        catch (NotSupportedException)
        {
            // AutoOrient of a turned image cannot stream; Transcode says so before writing.
            Assert.Throws<NotSupportedException>(() => Raster.Transcode(new MemoryStream(data), new MemoryStream(), PngFormat.Info, chain));
            return;
        }
        catch (ArgumentException)
        {
            // The chain does not fit this image (a crop outside it).
            return;
        }

        using MemoryStream output = new();
        Raster.Transcode(new MemoryStream(data), output, PngFormat.Info, chain);

        using Raster loaded = Raster.Load(data, options: plan.Options);
        using Raster expected = loaded.Derive(x =>
        {
            foreach (IRasterOperation step in plan.Steps)
            {
                x.Add(step);
            }
        }, Ct);
        using Raster<Rgba16> want = expected.As<Rgba16>();
        using Raster<Rgba16> got = Raster.Load<Rgba16>(output.ToArray());
        int frames = expected.FrameCount;
        Assert.Equal(frames, got.FrameCount);
        for (int f = 0; f < frames; f++)
        {
            using Raster<Rgba16> frame = got.CopyFrame(f);
            StreamingCorpus.Equal(want, f, frame, $"{file} {name}");
        }
    }
}

/// <summary>The tests of <see cref="TranscodeTests{TPart}"/> that run once rather than in parts.</summary>
public class TranscodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ALeadingResizeShrinksOnLoadAndALeadingCropIsARegion()
    {
        RasterInfo info = new() { Format = PngFormat.Info, Width = 4000, Height = 3000, Layout = Rgb8.Layout };
        TranscodePlan fit = TranscodePlan.Create(x => x.Fit(400, 400), info, ReaderOptions.Default);
        Assert.Equal(new Size(400, 300), fit.Options.MinimumSize);
        TranscodePlan crop = TranscodePlan.Create(x => x.Crop(100, 200, 1000, 500).Resize(100, 50), info, ReaderOptions.Default);
        Assert.Equal(new Rect(100, 200, 1000, 500), crop.Options.Region);
        Assert.Equal(new Size(100, 50), crop.Options.MinimumSize);
        TranscodePlan chosen = TranscodePlan.Create(x => x.Fit(400, 400), info, new ReaderOptions { MinimumSize = new Size(1000, 1000) });
        Assert.Equal(new Size(1000, 1000), chosen.Options.MinimumSize);
    }

    [Theory]
    [InlineData("blur")]
    [InlineData("rotate")]
    [InlineData("flip-vertical")]
    [InlineData("pad")]
    public void OperationsThatCannotStreamThrowBeforeWriting(string name)
    {
        Action<RasterOps> chain = name switch
        {
            "blur" => x => x.Fit(10, 10).Blur(2),
            "rotate" => x => x.Rotate(30),
            "flip-vertical" => x => x.FlipVertical(),
            _ => x => x.Pad(100, 100),
        };
        string directory = Directory.CreateTempSubdirectory("egami-transcode-").FullName;
        try
        {
            string input = Path.Combine(directory, "in.png");
            using (Raster<Rgb8> image = Raster.Create(20, 20, new Rgb8(1, 2, 3)))
            {
                image.Save(input);
            }

            string output = Path.Combine(directory, "out.jpg");
            NotSupportedException e = Assert.Throws<NotSupportedException>(() => Raster.Transcode(input, output, chain));
            Assert.Contains("Transcode cannot stream", e.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AsyncAndUnseekableInputsWriteTheSameBytes()
    {
        byte[] data = StreamingCorpus.Read(StreamingCorpus.Files.First(f => f.EndsWith(".jpg", StringComparison.Ordinal)));
        using MemoryStream sync = new();
        Raster.Transcode(new MemoryStream(data), sync, JpegFormat.Info, x => x.Fit(64, 64).Contrast(1.1f));
        using MemoryStream unseekable = new();
        Raster.Transcode(new SimpleStreamingTests.NonSeekableStream(data), unseekable, JpegFormat.Info, x => x.Fit(64, 64).Contrast(1.1f));
        using MemoryStream async = new();
        await Raster.TranscodeAsync(new SimpleStreamingTests.NonSeekableStream(data), new EncodeTests.NonSeekableWriteStream(async), JpegFormat.Info, x => x.Fit(64, 64).Contrast(1.1f), cancellationToken: Ct);
        Assert.Equal(sync.ToArray(), unseekable.ToArray());
        Assert.Equal(sync.ToArray(), async.ToArray());
    }

    [Fact]
    public void AThumbnailOfABigImageHoldsAFewBands()
    {
        // A 6,000 × 4,000 PNG, written in bands so the test itself never holds it.
        const int Width = 6000;
        const int Height = 4000;
        using MemoryStream png = new();
        using (Raster<Rgb8> band = Raster.Create<Rgb8>(Width, 50))
        using (RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(png, PngFormat.Info, Width, Height, new PngWriterOptions { CompressionLevel = 1 }))
        {
            for (int y = 0; y < Height; y += 50)
            {
                for (int r = 0; r < 50; r++)
                {
                    Span<Rgb8> row = band.Row(r);
                    for (int x = 0; x < Width; x++)
                    {
                        row[x] = new Rgb8((byte)(x >> 4), (byte)((y + r) >> 4), (byte)(x ^ (y + r)));
                    }
                }

                encoder.WriteRows(band);
            }

            encoder.Complete();
        }

        byte[] file = png.ToArray();
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            Raster.Transcode(new MemoryStream(file), Stream.Null, JpegFormat.Info, x => x.Fit(1024, 1024), context: context);
        });

        // Load alone would hold 72 MB of Rgb8.
        Assert.True(peak.PeakPixelBytes < 4 * 1024 * 1024, peak.ToString());
        Assert.Equal(0, peak.LeftBytes);
    }
}

/// <summary>Part 1 of 5 of <see cref="TranscodeTests{TPart}"/>.</summary>
public sealed class TranscodeTestsPart0 : TranscodeTests<Part0>;

/// <summary>Part 2 of 5 of <see cref="TranscodeTests{TPart}"/>.</summary>
public sealed class TranscodeTestsPart1 : TranscodeTests<Part1>;

/// <summary>Part 3 of 5 of <see cref="TranscodeTests{TPart}"/>.</summary>
public sealed class TranscodeTestsPart2 : TranscodeTests<Part2>;

/// <summary>Part 4 of 5 of <see cref="TranscodeTests{TPart}"/>.</summary>
public sealed class TranscodeTestsPart3 : TranscodeTests<Part3>;

/// <summary>Part 5 of 5 of <see cref="TranscodeTests{TPart}"/>.</summary>
public sealed class TranscodeTestsPart4 : TranscodeTests<Part4>;
