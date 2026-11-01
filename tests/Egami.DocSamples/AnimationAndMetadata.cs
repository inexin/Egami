using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.DocSamples;

[Collection("Samples")]
public class AnimationAndMetadata
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Frames()
    {
        using SampleFiles files = new();

        #region frames
        using Raster<Rgba8> anim = Raster.Load<Rgba8>("pan.gif");
        Console.WriteLine($"{anim.FrameCount} frames, plays {anim.Metadata.LoopCount} times (0 = forever)");

        anim.Frames.RemoveAt(0);                                            // frames are removed,
        anim.Frames.Add(anim.Frames[0]).Metadata.Delay = TimeSpan.FromMilliseconds(500); // copied in,
        anim.Frames.Move(anim.FrameCount - 1, 0);                           // and moved
        anim.Apply(ops => ops.Fit(64, 64), Ct);                            // operations run on every frame

        anim.Save("pan-small.gif");                                         // or .png for an APNG
        using Raster<Rgba8> still = anim.CopyFrame(0);                      // one frame as its own raster
        #endregion

        Assert.True(anim.FrameCount > 1);
    }

    [Fact]
    public void FirstFrame()
    {
        using SampleFiles files = new();

        #region first-frame
        // Only the first frame, decoding nothing after it: a fast preview of an animation.
        using Raster first = Raster.Load("pan.gif", options: new ReaderOptions { Frames = ..1 });
        #endregion

        Assert.Equal(1, first.FrameCount);
    }

    [Fact]
    public void WritingGif()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region gif
        using FileStream output = File.Create("duck.gif");
        photo.Save(output, GifFormat.Info, new GifWriterOptions
        {
            Palette = new PaletteOptions { MaxColors = 128, Dither = DitherMode.Ordered }, // FloydSteinberg (default), Ordered or None
            PaletteScope = GifPaletteScope.PerFrame,   // Auto (default): one table when all frames fit, else one per frame
        });
        #endregion

        Assert.True(output.Length > 0);
    }

    [Fact]
    public void Exif()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("sideways.jpg");

        #region exif
        if (image.Metadata.Exif is ExifData exif)
        {
            Console.WriteLine($"{exif.CameraMake} {exif.CameraModel}, {exif.DateTaken}, {exif.Location}");
            if (exif.TryGet(ExifKey.ExposureTime, out ExifEntry? exposure))
            {
                Console.WriteLine($"1/{1 / exposure.AsDouble():0} s");
            }

            exif.Set(ExifKey.Copyright, "© 2026 Example");
            exif.Remove(ExifKey.GpsLatitude);
        }
        #endregion

        Assert.NotNull(image.Metadata.Exif);
    }

    [Fact]
    public void OtherMetadata()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region metadata
        RasterMetadata metadata = image.Metadata;
        metadata.Text.Add(new TextEntry("Comment", "Made with Egami"));
        metadata.Resolution = new Resolution(300, 300, ResolutionUnit.PerInch);
        Console.WriteLine($"{metadata.ColorSpace}, ICC {metadata.IccProfile.Length} bytes, XMP {metadata.Xmp.Length} bytes");
        foreach (EgamiDiagnostic warning in metadata.Warnings)
        {
            Console.WriteLine($"Recovered from: {warning.Message} ({warning.Id})");
        }

        image.Save("duck-300dpi.png");
        #endregion

        using Raster back = Raster.Load("duck-300dpi.png");
        Assert.Contains(back.Metadata.Text, t => t.Value == "Made with Egami");
    }

    [Fact]
    public void Strip()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("sideways.jpg");

        #region strip
        // Every writer's options have StripMetadata: the pixels are written, EXIF, XMP, ICC and text are not.
        image.Save("clean.jpg", new JpegWriterOptions { StripMetadata = true });
        #endregion

        using Raster clean = Raster.Load("clean.jpg");
        Assert.Null(clean.Metadata.Exif);
    }

    [Fact]
    public void ScaledDecode()
    {
        using SampleFiles files = new();

        #region scaled-decode
        // At least 100 × 75 from the stored JPEG, decoded at 1/2, 1/4 or 1/8 scale straight from the DCT.
        using Raster preview = Raster.Load("duck.jpg", options: new ReaderOptions { MinimumSize = new Size(100, 75) });
        #endregion

        Assert.True(preview.Width >= 100);
    }

    [Fact]
    public void JpegWrite()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region jpeg-write
        using FileStream output = File.Create("duck-q85.jpg");
        image.Save(output, JpegFormat.Info, new JpegWriterOptions
        {
            Quality = 85,                                  // 75 by default, on libjpeg's scale
            Subsampling = ChromaSubsampling.Subsample444,  // 4:2:0 by default
            OptimizeHuffman = true,                        // a few percent smaller, a little slower
        });
        #endregion

        Assert.True(output.Length > 0);
    }

    [Fact]
    public void JpegWriteModes()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> image = Raster.Load<Rgb8>("duck.png");

        #region jpeg-progressive
        // Shows coarse first and sharpens as it loads; usually a few percent smaller, and decodes to the same pixels.
        image.Save("duck-progressive.jpg", new JpegWriterOptions { Progressive = true });
        #endregion

        #region jpeg-cmyk
        // CMYK pixels save as CMYK, inverted behind an Adobe marker as Photoshop writes; YCCK is usually smaller.
        using Raster<Cmyk8> inks = image.As<Cmyk8>();
        inks.Save("duck-cmyk-out.jpg", JpegWriterOptions.Default);
        inks.Save("duck-ycck.jpg", new JpegWriterOptions { ColorType = JpegColorType.Ycck });
        #endregion

        #region jpeg-12bit
        // 16-bit and float pixels keep more than 8 bits in a 12-bit file; it loads back as Rgb16. No browser shows it.
        using Raster<Rgb16> deep = image.As<Rgb16>();
        deep.Save("duck-12bit.jpg", new JpegWriterOptions { Precision = 12, Quality = 95 });
        #endregion

        #region jpeg-arithmetic
        // Arithmetic coding: the same pixels in a smaller file, for archives; browsers do not read it.
        image.Save("duck-arithmetic.jpg", new JpegWriterOptions { Quality = 90, Arithmetic = true });

        // An existing Huffman-coded file repacked with arithmetic coding, with no change to its pixels.
        JpegTransform.Apply("duck-progressive.jpg", "duck-repacked.jpg", new JpegTransformOptions { Arithmetic = true });
        #endregion

        #region jpeg-lossless
        // Lossless JPEG: the exact samples back, here 16-bit; the predictor is picked for the image unless you name one.
        image.As<Rgb16>().Save("duck-lossless.jpg", new JpegWriterOptions { Lossless = new JpegLosslessOptions() });

        // Predictor 1 (the sample to the left), arithmetic-coded (SOF11, which only Egami and Richter's libjpeg read).
        image.Save("duck-lossless-arithmetic.jpg", new JpegWriterOptions
        {
            Lossless = new JpegLosslessOptions { Predictor = 1 },
            Arithmetic = true,
        });
        #endregion

        using (Raster<Rgb16> exact = Raster.Load<Rgb16>("duck-lossless.jpg"))
        using (Raster<Rgb16> wide = image.As<Rgb16>())
        {
            Assert.True(((JpegDetails)exact.Metadata.FormatDetails!).Lossless);
            for (int y = 0; y < wide.Height; y++)
            {
                Assert.True(wide.Row(y).SequenceEqual(exact.Row(y)));
            }
        }

        #region jpeg-lossless-samples
        // Samples that are not pixels, such as a camera's 14-bit sensor data, as a lossless JPEG stream (a DNG or TIFF tile).
        ushort[] samples = new ushort[64 * 32];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (ushort)((i * 37) % 16384);
        }

        using (MemoryStream tile = new())
        {
            JpegLosslessWriter.Write(tile, samples, components: 1, width: 64, precision: 14, predictor: null);

            // A colour filter array tile as DNG lays it out: each row's photosites coded in pairs, half as wide.
            using MemoryStream cfa = new();
            JpegLosslessWriter.WriteCfa(cfa, samples, width: 64, precision: 14);
            Assert.True(tile.Length > 0 && cfa.Length > 0);
        }
        #endregion

        Assert.True(((JpegDetails)Raster.Load("duck-lossless-arithmetic.jpg").Metadata.FormatDetails!).Arithmetic);
        Assert.True(((JpegDetails)Raster.Load("duck-arithmetic.jpg").Metadata.FormatDetails!).Arithmetic);
        Assert.True(((JpegDetails)Raster.Load("duck-repacked.jpg").Metadata.FormatDetails!).Arithmetic);
        Assert.True(((JpegDetails)Raster.Load("duck-progressive.jpg").Metadata.FormatDetails!).Progressive);
        Assert.Equal(JpegColorSpace.Ycck, ((JpegDetails)Raster.Load("duck-ycck.jpg").Metadata.FormatDetails!).ColorSpace);
        Assert.Equal(12, ((JpegDetails)Raster.Load("duck-12bit.jpg").Metadata.FormatDetails!).Precision);
        Assert.Equal(JpegColorSpace.Cmyk, ((JpegDetails)Raster.Load("duck-cmyk-out.jpg").Metadata.FormatDetails!).ColorSpace);
    }

    [Fact]
    public void JpegTransformSample()
    {
        using SampleFiles files = new();

        #region jpeg-transform
        // Turns the photo upright by its EXIF orientation without re-encoding: no quality is lost, and the tag is reset.
        JpegTransform.Apply("sideways.jpg", "upright.jpg", new JpegTransformOptions { Operation = JpegTransformOperation.AutoOrient });

        // Rotates, crops and drops the color in one pass; a file can be transformed in place.
        JpegTransformResult result = JpegTransform.Apply("upright.jpg", "upright.jpg", new JpegTransformOptions
        {
            Operation = JpegTransformOperation.Rotate90,
            Crop = new Rect(0, 0, 200, 120),
            Grayscale = true,
        });
        Console.WriteLine($"{result.Width} × {result.Height}");
        #endregion

        RasterInfo info = Raster.Probe("upright.jpg");
        Assert.Equal((result.Width, result.Height), (info.Width, info.Height));
        Assert.Equal(ImageOrientation.Normal, info.Orientation);
        Assert.Equal(Gray8.Layout, info.Layout);
    }

    [Fact]
    public void JpegDetailsSample()
    {
        using SampleFiles files = new();

        #region jpeg-details
        using Raster image = Raster.Load("duck.jpg");
        if (image.Metadata.FormatDetails is JpegDetails jpeg)
        {
            Console.WriteLine($"Progressive: {jpeg.Progressive}, saved at about quality {jpeg.EstimatedQuality}");
        }
        #endregion

        Assert.IsType<JpegDetails>(image.Metadata.FormatDetails);
    }
}
