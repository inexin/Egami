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
        foreach (string warning in metadata.Warnings)
        {
            Console.WriteLine($"Recovered from: {warning}");
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

        Assert.True(((JpegDetails)Raster.Load("duck-progressive.jpg").Metadata.FormatDetails!).Progressive);
        Assert.Equal(JpegColorSpace.Ycck, ((JpegDetails)Raster.Load("duck-ycck.jpg").Metadata.FormatDetails!).ColorSpace);
        Assert.Equal(12, ((JpegDetails)Raster.Load("duck-12bit.jpg").Metadata.FormatDetails!).Precision);
        Assert.Equal(Cmyk8.Layout, Raster.Load("duck-cmyk-out.jpg").Layout);
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
