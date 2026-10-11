using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Formats.Gif;
using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Formats.Pnm;
using Egami.Formats.Qoi;
using Egami.Formats.Tga;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Tests.Memory;

namespace Egami.Tests.Streaming;

/// <summary>
/// Phase 2.2 task 10: RasterEncoder writes the bytes Save writes for every option streaming allows, refuses at Create the options
/// that need every pixel, and SaveAsync goes through the same encoder.
/// </summary>
public class EncodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The streamed cases: a name, the format, its options, and how a stream that cannot seek fares: the same bytes, the same
    /// pixels written another way (top-down BMP and TGA), or refused.
    /// </summary>
    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["png"] = new(PngFormat.Info, () => PngWriterOptions.Default, Pixel.Rgba8),
        ["png-rgb16"] = new(PngFormat.Info, () => new PngWriterOptions { BitDepth = 16 }, Pixel.Rgb8),
        ["png-gray"] = new(PngFormat.Info, () => new PngWriterOptions { ColorType = PngColorType.Gray }, Pixel.Rgba8),
        ["png-none-9"] = new(PngFormat.Info, () => new PngWriterOptions { Filter = PngFilterMode.None, CompressionLevel = 9 }, Pixel.Rgba8),
        ["png-interlace"] = new(PngFormat.Info, () => new PngWriterOptions { Interlace = true }, Pixel.Rgba8),
        ["png-parallel"] = new(PngFormat.Info, () => new PngWriterOptions { ParallelCompression = true, ParallelThreshold = 0, ParallelBandSize = 8192 }, Pixel.Rgb8),
        ["jpeg"] = new(JpegFormat.Info, () => JpegWriterOptions.Default, Pixel.Rgb8),
        ["jpeg-optimized"] = new(JpegFormat.Info, () => new JpegWriterOptions { OptimizeHuffman = true }, Pixel.Rgb8),
        ["jpeg-restart"] = new(JpegFormat.Info, () => new JpegWriterOptions { RestartInterval = 3, Subsampling = ChromaSubsampling.Subsample422 }, Pixel.Rgba8),
        ["jpeg-gray"] = new(JpegFormat.Info, () => new JpegWriterOptions { ColorType = JpegColorType.Gray, OptimizeHuffman = true }, Pixel.Rgba8),
        ["jpeg-444-background"] = new(JpegFormat.Info, () => new JpegWriterOptions { Subsampling = ChromaSubsampling.Subsample444, Background = new Rgba8(200, 30, 40, 255) }, Pixel.Rgba8),
        ["gif-per-frame"] = new(GifFormat.Info, () => new GifWriterOptions { PaletteScope = GifPaletteScope.PerFrame }, Pixel.Rgba8),
        ["gif-unoptimized"] = new(GifFormat.Info, () => new GifWriterOptions { PaletteScope = GifPaletteScope.PerFrame, OptimizeFrames = false, Palette = new() { MaxColors = 16, Dither = Egami.Quantization.DitherMode.Ordered } }, Pixel.Rgba8),
        ["bmp-24"] = new(BmpFormat.Info, () => BmpWriterOptions.Default, Pixel.Rgb8, Unseekable.SamePixels),
        ["bmp-32"] = new(BmpFormat.Info, () => new BmpWriterOptions { PixelFormat = BmpPixelFormat.Bgra32 }, Pixel.Rgba8, Unseekable.SamePixels),
        ["tga-raw"] = new(TgaFormat.Info, () => new TgaWriterOptions { Rle = false, PixelFormat = TgaPixelFormat.Bgra32 }, Pixel.Rgba8, Unseekable.SamePixels),
        ["tga-rle"] = new(TgaFormat.Info, () => TgaWriterOptions.Default, Pixel.Rgb8, Unseekable.SamePixels, Seekable: Unseekable.SamePixels),
        ["qoi"] = new(QoiFormat.Info, () => new QoiWriterOptions { IncludeAlpha = true }, Pixel.Rgba8),
        ["qoi-rgb"] = new(QoiFormat.Info, () => QoiWriterOptions.Default, Pixel.Rgb8),
        ["pam"] = new(PnmFormat.Info, () => PnmWriterOptions.Default, Pixel.Rgba8),
        ["ppm-plain"] = new(PnmFormat.Info, () => new PnmWriterOptions { PlainText = true }, Pixel.Rgb8),
        ["pfm"] = new(PnmFormat.Info, () => PnmWriterOptions.Default, Pixel.RgbF32, Unseekable.Refused),
    };

    public static TheoryData<string, string> FilesAndCases()
    {
        TheoryData<string, string> data = [];
        foreach (string file in StreamingCorpus.Files)
        {
            foreach (string name in Cases.Keys)
            {
                data.Add(file, name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FilesAndCases))]
    public void StreamingWritesWhatSaveWrites(string file, string name)
    {
        Case c = Cases[name];
        byte[] data = StreamingCorpus.Read(file);
        switch (c.Pixel)
        {
            case Pixel.Rgba8:
                Check<Rgba8>(data, c, $"{file} {name}");
                break;
            case Pixel.Rgb8:
                Check<Rgb8>(data, c, $"{file} {name}");
                break;
            default:
                Check<RgbF32>(data, c, $"{file} {name}");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task SaveAsyncWritesWhatSaveWrites(string file)
    {
        using Raster raster = Raster.Load(StreamingCorpus.Read(file));
        foreach (FormatInfo format in new[] { PngFormat.Info, JpegFormat.Info, GifFormat.Info, BmpFormat.Info, TgaFormat.Info, QoiFormat.Info, PnmFormat.Info })
        {
            using MemoryStream sync = new();
            raster.Save(sync, format);
            using MemoryStream async = new();
            await raster.SaveAsync(new NonSeekableWriteStream(async), format, cancellationToken: Ct);
            Assert.True(sync.ToArray().AsSpan().SequenceEqual(async.ToArray()), $"{file} as {format.Name}");
        }
    }

    public static TheoryData<string> Files() => [.. StreamingCorpus.Files];

    [Fact]
    public void AnApngToAStreamThatCannotSeekNeedsItsFrameCount()
    {
        using Raster<Rgba8> frame = Raster.Create(8, 8, new Rgba8(1, 2, 3, 255));
        using MemoryStream memory = new();
        using RasterEncoder<Rgba8> encoder = RasterEncoder.Create<Rgba8>(new NonSeekableWriteStream(memory), PngFormat.Info, 8, 8);
        Assert.Throws<InvalidOperationException>(() => encoder.WriteFrame(frame));
    }

    [Fact]
    public void ADeclaredFrameCountMustMatch()
    {
        using Raster<Rgba8> frame = Raster.Create(8, 8, new Rgba8(1, 2, 3, 255));
        using MemoryStream memory = new();
        using RasterEncoder<Rgba8> encoder = RasterEncoder.Create<Rgba8>(memory, PngFormat.Info, 8, 8, new PngWriterOptions { FrameCount = 3 });
        encoder.WriteFrame(frame);
        encoder.WriteFrame(frame);
        Assert.Throws<InvalidOperationException>(encoder.Complete);
    }

    [Fact]
    public void OneDeclaredFrameIsAStillImage()
    {
        using Raster<Rgba8> frame = Raster.Create(8, 8, new Rgba8(1, 2, 3, 255));
        using MemoryStream saved = new();
        frame.Save(saved, PngFormat.Info);
        using MemoryStream memory = new();
        using (RasterEncoder<Rgba8> encoder = RasterEncoder.Create<Rgba8>(new NonSeekableWriteStream(memory), PngFormat.Info, 8, 8, new PngWriterOptions { FrameCount = 1 }))
        {
            encoder.WriteFrame(frame);
            Assert.Throws<InvalidOperationException>(() => encoder.WriteFrame(frame));
            encoder.Complete();
        }

        Assert.Equal(saved.ToArray(), memory.ToArray());
    }

    [Fact]
    public void OptionsThatNeedEveryPixelAreRefusedAtCreate()
    {
        using MemoryStream memory = new();
        void Refused<TPixel>(FormatInfo format, WriterOptions? options, Stream? stream = null)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            NotSupportedException e = Assert.Throws<NotSupportedException>(() => RasterEncoder.Create<TPixel>(stream ?? memory, format, 4, 4, options));
            Assert.Contains(format == PnmFormat.Info && typeof(TPixel) == typeof(RgbF32) ? "PFM" : format.Name, e.Message, StringComparison.Ordinal);
        }

        Refused<Rgba8>(QoiFormat.Info, null);
        Refused<Rgba8>(BmpFormat.Info, null);
        Refused<Rgb8>(BmpFormat.Info, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed8 });
        Refused<Rgba8>(TgaFormat.Info, null);
        Refused<Gray8>(TgaFormat.Info, new TgaWriterOptions { PixelFormat = TgaPixelFormat.Indexed8 });
        Refused<Rgb8>(PngFormat.Info, new PngWriterOptions { ColorType = PngColorType.Palette });
        Refused<Rgb8>(PngFormat.Info, new PngWriterOptions { Reduce = true });
        Refused<Rgba8>(GifFormat.Info, new GifWriterOptions { PaletteScope = GifPaletteScope.Global });
        Refused<RgbF32>(PnmFormat.Info, null, new NonSeekableWriteStream(memory));

        // The same options are fine with a pixel type that settles the choice.
        using RasterEncoder<Rgb8> qoi = RasterEncoder.Create<Rgb8>(memory, QoiFormat.Info, 4, 4);
        using RasterEncoder<Gray8> tga = RasterEncoder.Create<Gray8>(memory, TgaFormat.Info, 4, 4);
    }

    [Fact]
    public void RowsAreCountedAndCannotMixWithFrames()
    {
        using Raster<Rgb8> band = Raster.Create(4, 3, new Rgb8(9, 9, 9));
        using Raster<Rgb8> frame = Raster.Create(4, 4, new Rgb8(9, 9, 9));
        using MemoryStream memory = new();
        using RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(memory, PngFormat.Info, 4, 4);
        encoder.WriteRows(band);
        Assert.Equal(3, encoder.RowsWritten);
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.WriteRows(band));
        Assert.Throws<InvalidOperationException>(() => encoder.WriteFrame(frame));
        Assert.Throws<InvalidOperationException>(encoder.Complete);
        encoder.WriteRows(band, 1);
        encoder.Complete();
        Assert.Throws<InvalidOperationException>(() => encoder.WriteRows(band, 0));
        using Raster<Rgb8> loaded = Raster.Load<Rgb8>(memory.ToArray());
        Assert.Equal((4, 4), (loaded.Width, loaded.Height));
    }

    [Fact]
    public void ABandOfTheWrongWidthIsRefused()
    {
        using Raster<Rgb8> band = Raster.Create(5, 3, new Rgb8(9, 9, 9));
        using MemoryStream memory = new();
        using RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(memory, PngFormat.Info, 4, 4);
        Assert.Throws<ArgumentException>(() => encoder.WriteRows(band));
    }

    [Fact]
    public void AFileIsDeletedWhenTheEncoderIsDisposedUnfinished()
    {
        string directory = Directory.CreateTempSubdirectory("egami-encode-").FullName;
        try
        {
            using Raster<Rgb8> image = Raster.Create(16, 16, new Rgb8(10, 20, 30));
            string unfinished = Path.Combine(directory, "unfinished.png");
            using (RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(unfinished, 16, 16))
            {
                encoder.WriteRows(image, 8);
            }

            Assert.False(File.Exists(unfinished));

            string finished = Path.Combine(directory, "finished.ppm");
            using (RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(finished, 16, 16))
            {
                encoder.WriteRows(image);
                encoder.Complete();
            }

            string saved = Path.Combine(directory, "saved.ppm");
            image.Save(saved);
            Assert.Equal(File.ReadAllBytes(saved), File.ReadAllBytes(finished));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AsyncCallsWriteTheSameBytes()
    {
        using Raster<Rgba8> image = Raster.Load<Rgba8>(StreamingCorpus.Read(StreamingCorpus.Files.First(f => f.EndsWith(".jpg", StringComparison.Ordinal))));
        using MemoryStream saved = new();
        image.Save(saved, PngFormat.Info);
        using MemoryStream memory = new();
        using (RasterEncoder<Rgba8> encoder = RasterEncoder.Create<Rgba8>(new NonSeekableWriteStream(memory), PngFormat.Info, image.Width, image.Height, metadata: image.Metadata))
        {
            using Raster<Rgba8> band = Raster.Create<Rgba8>(image.Width, 5);
            for (int y = 0; y < image.Height; y += 5)
            {
                int rows = Math.Min(5, image.Height - y);
                for (int r = 0; r < rows; r++)
                {
                    image.Row(y + r).CopyTo(band.Row(r));
                }

                await encoder.WriteRowsAsync(band, rows, Ct);
            }

            await encoder.CompleteAsync(Ct);
        }

        Assert.Equal(saved.ToArray(), memory.ToArray());
    }

    [Fact]
    public void AFormatWithoutRowsGetsTheGatheredImage()
    {
        FormatInfo raw = new() { Name = "RAW-TEST", SignatureLength = 1, Matches = _ => false, Writer = new RawWriter() };
        using Raster<Rgb8> image = Raster.Create(6, 5, new Rgb8(1, 2, 3));
        image.Row(2)[3] = new Rgb8(200, 100, 50);
        using MemoryStream saved = new();
        image.Save(saved, raw);
        using MemoryStream memory = new();
        using (RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(memory, raw, 6, 5))
        {
            encoder.WriteRows(image, 2);
            using Raster<Rgb8> rest = Raster.Create<Rgb8>(6, 3);
            for (int y = 0; y < 3; y++)
            {
                image.Row(2 + y).CopyTo(rest.Row(y));
            }

            encoder.WriteRows(rest);
            encoder.Complete();
        }

        Assert.Equal(saved.ToArray(), memory.ToArray());
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("bmp")]
    public void AStreamedEncodeHoldsABandNotTheImage(string extension)
    {
        const int Width = 3000;
        const int Height = 2000;
        FormatInfo format = EgamiContext.Default.FindFormat(extension)!;
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using Raster<Rgb8> band = Raster.Create<Rgb8>(Width, 16, context);
            using RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(new NonSeekableWriteStream(Stream.Null), format, Width, Height, context: context);
            for (int y = 0; y < Height; y += 16)
            {
                for (int r = 0; r < 16; r++)
                {
                    Span<Rgb8> row = band.Row(r);
                    for (int x = 0; x < Width; x++)
                    {
                        row[x] = new Rgb8((byte)x, (byte)(y + r), (byte)(x ^ y));
                    }
                }

                encoder.WriteRows(band);
            }

            encoder.Complete();
        });

        // The band is 144 KB; the whole image would be 18 MB.
        Assert.True(peak.PeakPixelBytes <= Width * 16 * 3 * 2, peak.ToString());
        Assert.Equal(0, peak.LeftBytes);
    }

    private static void Check<TPixel>(byte[] data, Case c, string because)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> raster = Raster.Load<TPixel>(data);
        bool frames = raster.FrameCount > 1 && (c.Format == PngFormat.Info || c.Format == GifFormat.Info);
        using MemoryStream saved = new();
        raster.Save(saved, c.Format, c.Options());
        byte[] expected = frames ? saved.ToArray() : SaveFirst(raster, c);

        Verify(Streamed(raster, c, seekable: true, frames), c.Seekable);
        WriterOptions options = c.Options();
        if (frames && c.Format == PngFormat.Info)
        {
            PngWriterOptions png = (PngWriterOptions)options;
            options = new PngWriterOptions
            {
                ColorType = png.ColorType,
                BitDepth = png.BitDepth,
                Filter = png.Filter,
                CompressionLevel = png.CompressionLevel,
                CompressionStrategy = png.CompressionStrategy,
                Interlace = png.Interlace,
                ParallelCompression = png.ParallelCompression,
                ParallelThreshold = png.ParallelThreshold,
                ParallelBandSize = png.ParallelBandSize,
                FrameCount = raster.FrameCount,
            };
        }

        if (c.Unseekable == Unseekable.Refused)
        {
            Assert.Throws<NotSupportedException>(() => Streamed(raster, c with { Options = () => options }, seekable: false, frames));
            return;
        }

        Verify(Streamed(raster, c with { Options = () => options }, seekable: false, frames), c.Unseekable);

        void Verify(byte[] actual, Unseekable how)
        {
            if (how == Unseekable.SameBytes)
            {
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"{because}: the bytes differ from Save's.");
                return;
            }

            using Raster<Rgba8> want = Raster.Load<Rgba8>(expected);
            using Raster<Rgba8> got = Raster.Load<Rgba8>(actual);
            StreamingCorpus.Equal(want, 0, got, because);
        }
    }

    /// <summary>What Save writes for the first frame alone, which is what a still image streamed by rows is.</summary>
    private static byte[] SaveFirst<TPixel>(Raster<TPixel> raster, Case c)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> first = raster.CopyFrame(0);
        using MemoryStream saved = new();
        first.Save(saved, c.Format, c.Options());
        return saved.ToArray();
    }

    private static byte[] Streamed<TPixel>(Raster<TPixel> raster, Case c, bool seekable, bool frames)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream memory = new();
        Stream target = seekable ? memory : new NonSeekableWriteStream(memory);
        using (RasterEncoder<TPixel> encoder = RasterEncoder.Create<TPixel>(target, c.Format, raster.Width, raster.Height, c.Options(), raster.Metadata))
        {
            if (frames)
            {
                for (int f = 0; f < raster.FrameCount; f++)
                {
                    using Raster<TPixel> frame = raster.CopyFrame(f);
                    encoder.WriteFrame(frame, raster.Frames[f].Metadata);
                }
            }
            else
            {
                using Raster<TPixel> band = Raster.Create<TPixel>(raster.Width, 7);
                for (int y = 0; y < raster.Height; y += 7)
                {
                    int rows = Math.Min(7, raster.Height - y);
                    for (int r = 0; r < rows; r++)
                    {
                        raster.Row(y + r).CopyTo(band.Row(r));
                    }

                    encoder.WriteRows(band, rows);
                }
            }

            encoder.Complete();
        }

        return memory.ToArray();
    }

    private enum Pixel
    {
        Rgba8,
        Rgb8,
        RgbF32,
    }

    private enum Unseekable
    {
        SameBytes,
        SamePixels,
        Refused,
    }

    private sealed record Case(FormatInfo Format, Func<WriterOptions> Options, Pixel Pixel, Unseekable Unseekable = Unseekable.SameBytes, Unseekable Seekable = Unseekable.SameBytes);

    /// <summary>A format from outside with only the frozen writer interface: every row of every frame, raw.</summary>
    private sealed class RawWriter : IFormatWriter
    {
        public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            foreach (Frame<TPixel> frame in raster.Frames)
            {
                for (int y = 0; y < raster.Height; y++)
                {
                    output.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.Row(y)));
                }
            }
        }
    }

    /// <summary>A stream that takes writes but cannot seek, like a network upload.</summary>
    internal sealed class NonSeekableWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
