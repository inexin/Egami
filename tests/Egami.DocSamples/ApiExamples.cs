using System.Buffers;
using System.Numerics;

using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Formats.Gif;
using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Formats.Pnm;
using Egami.Formats.Qoi;
using Egami.Formats.Tga;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

/// <summary>Short examples the API reference shows on types the guides only mention in passing.</summary>
[Collection("Samples")]
public class ApiExamples
{
    [Fact]
    public void FormatDetails()
    {
        using SampleFiles files = new();
        using (Raster source = Raster.Load("duck.png"))
        {
            source.Save("duck.bmp");
            source.Save("duck.tga");
            source.Save("duck.qoi");
            source.Save("duck.pgm");
        }

        #region format-details
        // Each reader fills in what the file's header says, in the format's own terms.
        using Raster png = Raster.Load("duck.png");
        if (png.Metadata.FormatDetails is PngDetails pngDetails)
        {
            Console.WriteLine($"PNG {pngDetails.ColorType}, {pngDetails.BitDepth}-bit, interlaced: {pngDetails.Interlaced}");
            foreach (PngTextSource source in pngDetails.TextSources)
            {
                Console.WriteLine($"Text from a {source.ChunkType} chunk, compressed: {source.Compressed}");
            }
        }

        using Raster bmp = Raster.Load("duck.bmp");
        BmpDetails bmpDetails = (BmpDetails)bmp.Metadata.FormatDetails!;
        Console.WriteLine($"BMP {bmpDetails.BitsPerPixel}-bit, {bmpDetails.Compression}, {bmpDetails.HeaderSize}-byte header");

        using Raster tga = Raster.Load("duck.tga");
        TgaDetails tgaDetails = (TgaDetails)tga.Metadata.FormatDetails!;
        Console.WriteLine($"TGA type {tgaDetails.ImageType}, origin {tgaDetails.Origin}, alpha {tgaDetails.AlphaType}");

        using Raster qoi = Raster.Load("duck.qoi");
        QoiDetails qoiDetails = (QoiDetails)qoi.Metadata.FormatDetails!;
        Console.WriteLine($"QOI {qoiDetails.Channels} channels, linear: {qoiDetails.Linear}");

        using Raster pgm = Raster.Load("duck.pgm");
        PnmDetails pnmDetails = (PnmDetails)pgm.Metadata.FormatDetails!;
        Console.WriteLine($"PNM {pnmDetails.Kind}, maxval {pnmDetails.MaxValue}, plain text: {pnmDetails.PlainText}");
        #endregion

        Assert.Equal(BmpCompression.None, bmpDetails.Compression);
        Assert.Equal(PnmKind.Graymap, pnmDetails.Kind);
    }

    [Fact]
    public void JpegDetailsExample()
    {
        using SampleFiles files = new();

        #region jpeg-details
        using Raster photo = Raster.Load("duck.jpg", options: new JpegReaderOptions { FastUpsampling = true });
        JpegDetails details = (JpegDetails)photo.Metadata.FormatDetails!;
        Console.WriteLine($"{details.ColorSpace}, progressive: {details.Progressive}, quality about {details.EstimatedQuality}");
        foreach (JpegComponent component in details.Components)
        {
            Console.WriteLine($"Component {component.Id}: sampled {component.HorizontalSampling} × {component.VerticalSampling}");
        }

        // Gray output, and 4:4:4 (no chroma subsampling) for sharp colored edges.
        photo.Save("gray.jpg", new JpegWriterOptions { ColorType = JpegColorType.Gray });
        photo.Save("sharp.jpg", new JpegWriterOptions { Quality = 90, Subsampling = ChromaSubsampling.Subsample444 });
        #endregion

        Assert.Equal(JpegColorSpace.YCbCr, details.ColorSpace);
    }

    [Fact]
    public void GifDetailsExample()
    {
        using SampleFiles files = new();

        #region gif-details
        using Raster<Rgba8> clip = Raster.Load<Rgba8>("pan.gif");
        GifDetails details = (GifDetails)clip.Metadata.FormatDetails!;
        Console.WriteLine($"GIF{details.Version}, {details.GlobalPalette.Length} global colors, repeats {details.RepetitionCount}");
        foreach (GifApplicationExtension extension in details.OtherExtensions)
        {
            Console.WriteLine($"Kept extension {extension.Identifier}: {extension.Data.Length} bytes");
        }

        foreach (Frame<Rgba8> frame in clip.Frames)
        {
            if (frame.Metadata.FormatDetails is GifFrameDetails frameDetails)
            {
                Console.WriteLine($"Frame: {frameDetails.LocalPalette.Length} local colors, transparent index {frameDetails.TransparentIndex}");
            }
        }
        #endregion

        Assert.NotNull(details.Version);
    }

    [Fact]
    public void PngWriting()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region png-writer
        // Force a palette, choose a filter and a zlib strategy, and keep a private chunk.
        ((PngDetails)image.Metadata.FormatDetails!).OtherChunks.Add(
            new PngChunk("prVt", "hello"u8.ToArray(), PngChunkPosition.AfterImageData));
        image.Save("duck-palette.png", new PngWriterOptions
        {
            ColorType = PngColorType.Palette,
            Filter = PngFilterMode.Paeth,
            CompressionStrategy = PngCompressionStrategy.Filtered,
        });
        #endregion

        Assert.Equal(PngColorType.Palette, ((PngDetails)Raster.Load("duck-palette.png").Metadata.FormatDetails!).ColorType);
    }

    [Fact]
    public void TgaWriting()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region tga-writer
        image.Save("duck-gray.tga", new TgaWriterOptions { PixelFormat = TgaPixelFormat.Gray8, Rle = false });
        using Raster back = Raster.Load("duck-gray.tga");
        TgaDetails details = (TgaDetails)back.Metadata.FormatDetails!;
        bool upsideDown = details.Origin is TgaOrigin.BottomLeft or TgaOrigin.BottomRight;
        bool hasAlpha = details.AlphaType is TgaAlphaType.Straight or TgaAlphaType.Premultiplied;
        #endregion

        Assert.True(upsideDown);
        Assert.False(hasAlpha);
    }

    [Fact]
    public void FormatInfos()
    {
        using SampleFiles files = new();

        #region format-info
        // Every built-in format, with its name, extensions and MIME types; Raster.Save picks one by extension.
        foreach (FormatInfo format in new[] { PngFormat.Info, JpegFormat.Info, GifFormat.Info, BmpFormat.Info, TgaFormat.Info, QoiFormat.Info, PnmFormat.Info })
        {
            Console.WriteLine($"{format.Name}: {string.Join(", ", format.Extensions)} ({string.Join(", ", format.MimeTypes)})");
        }

        // A SignatureMatcher decides from the first SignatureLength bytes whether a file is in the format.
        SignatureMatcher isPng = PngFormat.Info.Matches;
        bool png = isPng(File.ReadAllBytes("duck.png").AsSpan(0, PngFormat.Info.SignatureLength));
        #endregion

        Assert.True(png);
    }

    [Fact]
    public void ExifValues()
    {
        using SampleFiles files = new();

        #region exif-values
        using Raster photo = Raster.Load("sideways.jpg");
        ExifData exif = photo.Metadata.Exif ?? new ExifData();

        // Typed values go in with their EXIF type; fractions are exact numerator / denominator pairs.
        exif.Set(ExifKey.ExposureTime, new ExifFraction(1, 250));
        exif.Set(ExifKey.Artist, "A. Photographer");
        if (exif.TryGet(ExifKey.ExposureTime, out ExifEntry? entry) && entry.Type == ExifType.Rational)
        {
            Console.WriteLine($"Exposure {entry.AsFraction()} s = {entry.AsFraction().ToDouble()} s, in the {entry.Key.Ifd} IFD");
        }

        if (exif.Location is GeoPosition where)
        {
            Console.WriteLine($"Taken at {where.Latitude:F5}, {where.Longitude:F5}");
        }
        #endregion

        Assert.Equal(ExifIfd.Exif, ExifKey.ExposureTime.Ifd);
    }

    [Fact]
    public void ExifParse()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("sideways.jpg");
        ReadOnlyMemory<byte> tiff = photo.Metadata.Exif!.ToBytes();

        #region exif-parse
        // EXIF from somewhere other than an image file (a database column, a sidecar), with a cap on the bytes of values read.
        ExifData exif = ExifData.Parse(tiff.Span, SafetyLimits.Default with { MaxMetadataBytes = 64 * 1024 });
        foreach (string warning in exif.Warnings)
        {
            Console.WriteLine($"EXIF: {warning}");
        }
        #endregion

        Assert.Equal(ImageOrientation.Rotate90, exif.Orientation);
    }

    [Fact]
    public async Task ProbeAsync()
    {
        using SampleFiles files = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        #region probe-async
        // The header only: size, format, frames and metadata, without decoding the pixels.
        RasterInfo info = await Raster.ProbeAsync("pan.gif", cancellationToken: cancellationToken);
        Console.WriteLine($"{info.Format.Name} {info.Width} × {info.Height}, {info.FrameCount} frames");
        #endregion

        Assert.Equal(8, info.FrameCount);
    }

    [Fact]
    public void ColorSpaceHints()
    {
        using SampleFiles files = new();

        #region color-space-hint
        using Raster image = Raster.Load("duck.png");
        string note = image.Metadata.ColorSpace switch
        {
            ColorSpaceHint.Srgb or ColorSpaceHint.Unknown => "sRGB: shown as is",
            ColorSpaceHint.Linear => "linear light: encode before showing",
            ColorSpaceHint.Gamma => "a file gamma other than sRGB",
            ColorSpaceHint.EmbeddedIcc => "an ICC profile describes the colors",
            _ => "unknown",
        };
        #endregion

        Assert.NotNull(note);
    }

    [Fact]
    public void Frames()
    {
        using SampleFiles files = new();

        #region frame-list
        using Raster<Rgba8> clip = Raster.Load<Rgba8>("pan.gif");
        FrameList<Rgba8> frames = clip.Frames;

        // Play the clip forward and then backward: append the middle frames in reverse.
        for (int i = frames.Count - 2; i > 0; i--)
        {
            Frame<Rgba8> copy = frames.Add(frames[i]);
            copy.Metadata.Delay = TimeSpan.FromMilliseconds(80);
            copy.Metadata.Disposal = FrameDisposal.None;
            copy.Metadata.Blend = FrameBlend.Source;
        }

        frames.RemoveAt(0);
        clip.Save("pan-bounce.gif");
        #endregion

        Assert.Equal((8 * 2) - 3, clip.FrameCount);
    }

    [Fact]
    public void FrameMetadataExample()
    {
        using SampleFiles files = new();

        #region frame-metadata
        using Raster<Rgba8> clip = Raster.Load<Rgba8>("pan.gif");
        TimeSpan length = TimeSpan.Zero;
        foreach (Frame<Rgba8> frame in clip.Frames)
        {
            FrameMetadata timing = frame.Metadata;
            length += timing.Delay;
            Console.WriteLine($"{timing.Delay.TotalMilliseconds} ms, {timing.Disposal}, {timing.Blend}, changed area {timing.Bounds}");
        }
        #endregion

        Assert.True(length > TimeSpan.Zero);
    }

    [Fact]
    public void RowsAndPlanes()
    {
        #region row-action
        using Raster<Rgba8> image = Raster.Create<Rgba8>(256, 64);

        // A horizontal gradient; rows run in parallel, each row is a Span over the pixels.
        image.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgba8((byte)x, (byte)(y * 4), 128, 255);
            }
        });
        #endregion

        #region pixel-plane
        PixelPlane<Rgba8> plane = image.Frames[0].Plane;
        if (plane.TryGetContiguous(out Span<Rgba8> all))
        {
            all.Fill(Rgba8.White);  // one Span over every pixel when the rows are stored back to back
        }
        else
        {
            for (int y = 0; y < plane.Height; y++)
            {
                plane.Row(y).Fill(Rgba8.White);
            }
        }
        #endregion

        Assert.Equal(Rgba8.White, image.Row(63)[255]);
    }

    [Fact]
    public void RasterOpsChain()
    {
        using SampleFiles files = new();

        #region raster-ops
        // RasterOps records steps; Apply runs them in one pass where it can, in place when the size stays.
        using Raster photo = Raster.Load("egret.png");
        photo.Apply(ops => ops
            .AutoOrient()
            .Cover(400, 300, Anchor.Center)
            .Saturation(1.1f)
            .Sharpen(1, 0.6f));
        #endregion

        Assert.Equal(400, photo.Width);
    }

    [Fact]
    public void BandAction()
    {
        using SampleFiles files = new();

        #region row-band-action
        using Raster photo = Raster.Load("egret.png");
        photo.Apply(ops => ops.Add(new Threshold(0.5f)));
        #endregion

        Assert.Equal(PixelChannelOrder.Rgba, photo.Layout.ChannelOrder);
    }

    #region row-band-operation
    /// <summary>Black or white per pixel: ForEachBand gives each worker a band of rows from start to end.</summary>
    private sealed class Threshold(float level) : IRasterOperation
    {
        public Size OutputSize(Size input) => input;

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            RowBandAction band = (start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    Span<TPixel> source = input.Source.Row(y), destination = input.Destination.Row(y);
                    for (int x = 0; x < source.Length; x++)
                    {
                        Vector4 v = source[x].ToScaled();
                        float on = ((0.2126f * v.X) + (0.7152f * v.Y) + (0.0722f * v.Z)) >= level ? 1 : 0;
                        destination[x] = TPixel.FromScaled(new Vector4(on, on, on, v.W));
                    }
                }
            };
            input.ForEachBand(band);
        }
    }
    #endregion

    [Fact]
    public void PixelTypes()
    {
        #region pixel-types
        // Eight-bit, sixteen-bit and float pixels; every type converts to every other through PixelConvert.
        Rgba8 orange = new(255, 128, 0, 255);
        Bgr8 bgr = Bgr8.FromRgba8(orange);
        Gray8 gray = Gray8.FromRgba8(orange);
        GrayAlpha8 grayAlpha = new(gray.L, 128);
        Gray16 deep = Gray16.FromRgba8(orange);
        Rgba16 wide = new(65535, 32896, 0, 65535);
        Rgb16 wideOpaque = Rgb16.FromRgba16(wide);           // exact: 16-bit to 16-bit never rounds
        GrayAlpha16 deepMask = new(deep.L, 32768);
        Argb8 alphaFirst = Argb8.FromRgba8(orange);             // bytes A, R, G, B
        Alpha8 mask = Alpha8.FromRgba8(orange);                 // alpha only; reads back as black
        RgbaF16 hdr = RgbaF16.FromScaled(new Vector4(4f, 2f, 0.5f, 1f)); // half floats keep values above 1
        GrayF32 depth = new(12.5f);                              // floats hold any value until written to an integer type
        RgbaF32 linear = RgbaF32.FromScaled(orange.ToScaled());

        Rgba8[] row = [orange, Rgba8.Transparent];
        Bgra8[] converted = new Bgra8[row.Length];
        PixelConvert.Convert<Rgba8, Bgra8>(row, converted);
        #endregion

        Assert.Equal(orange, wide.ToRgba8());
        Assert.Equal(255, bgr.R);
        Assert.True(deep.L > 0 && linear.A == 1 && grayAlpha.A == 128 && wideOpaque.G == 32896 && deepMask.A == 32768 && alphaFirst.A == 255 && mask.A == 255 && (float)hdr.R == 4f && depth.L == 12.5f);
    }

    [Fact]
    public void Layouts()
    {
        #region pixel-layout
        // What a pixel type stores, for code that handles any TPixel.
        PixelLayoutInfo layout = Rgba16.Layout;
        Console.WriteLine($"{layout.ChannelOrder} {layout.ChannelType}: {layout.ChannelCount} channels, {layout.BitsPerChannel} bits each, {layout.BytesPerPixel} bytes");
        bool needsAlpha = layout.Alpha != AlphaKind.None;
        bool eightBit = layout.ChannelType == PixelChannelType.Unorm8;
        bool bgrOrder = Bgr8.Layout.ChannelOrder is PixelChannelOrder.Bgr or PixelChannelOrder.Bgra;
        #endregion

        Assert.True(needsAlpha && !eightBit && bgrOrder);
    }

    [Fact]
    public void MemorySource()
    {
        #region memory-source
        // Every pixel buffer comes from the context's IPixelMemorySource; wrap the pooled one to count what it hands out.
        CountingSource counting = new(PooledPixelMemorySource.Shared);
        EgamiContext context = EgamiContext.Default with { Memory = counting };
        using (Raster<Rgba8> image = Raster.Create<Rgba8>(512, 512, context))
        {
            image.Apply(ops => ops.Blur(2));
        }

        Console.WriteLine($"{counting.Blocks} blocks, {counting.Bytes:N0} bytes");
        #endregion

        Assert.True(counting.Bytes >= 512 * 512 * 4);
    }

    #region counting-source
    private sealed class CountingSource(IPixelMemorySource inner) : IPixelMemorySource
    {
        public int Blocks;
        public long Bytes;

        public int MaxBlockBytes => inner.MaxBlockBytes;

        public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            Interlocked.Increment(ref Blocks);
            Interlocked.Add(ref Bytes, byteLength);
            return inner.Allocate(byteLength, clear);   // ClearMode.Zero when the caller needs zeroed memory
        }
    }
    #endregion

    [Fact]
    public void LeakEvents()
    {
        #region leak-detected
        // In tests or debug builds: report a raster that was never disposed, and where it was allocated.
        PooledPixelMemorySource memory = new();
        memory.Usage.TrackLeaks = true;
        memory.Usage.CaptureStackTraces = true;
        memory.Usage.LeakDetected += (sender, e) =>
            Console.Error.WriteLine($"Leaked {e.Bytes:N0} bytes, allocated at:\n{e.StackTrace}");
        #endregion

        Assert.True(memory.Usage.TrackLeaks);
    }
}
