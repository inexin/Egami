using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Formats.Jpeg;
using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Cookbook
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Thumbnail()
    {
        using SampleFiles files = new();

        #region thumbnail
        // Decode at the smallest JPEG scale that still covers 400 × 400, turn it upright, fit it, and save at quality 80.
        using Raster photo = Raster.Load("sideways.jpg", options: new ReaderOptions { MinimumSize = new Size(400, 400) });
        photo.Apply(ops => ops.AutoOrient().Fit(400, 400, enlarge: false));
        photo.Save("thumb.jpg", new JpegWriterOptions { Quality = 80, StripMetadata = true });
        #endregion

        Assert.True(Raster.Probe("thumb.jpg").Width <= 400);
    }

    [Fact]
    public void WebpForTheWeb()
    {
        using SampleFiles files = new();

        #region webp-web
        // Photos as lossy WebP, graphics with few colors as lossless: Probe reads only the header to tell them apart.
        foreach (string path in new[] { "duck.png", "egret.png" })
        {
            using Raster image = Raster.Load(path);
            bool photo = image.Width * image.Height > 100_000;
            image.Save(Path.ChangeExtension(path, ".webp"), photo
                ? new WebpWriterOptions { Quality = 80, Effort = 4 }
                : new WebpWriterOptions { Lossless = true, Effort = 6 });
        }
        #endregion

        Assert.True(File.Exists("duck.webp"));
    }

    [Fact]
    public void BatchConvert()
    {
        using SampleFiles files = new();
        Directory.CreateDirectory("in");
        File.Copy("duck.png", Path.Combine("in", "duck.png"));
        File.Copy("egret.png", Path.Combine("in", "egret.png"));

        #region batch-convert
        // Convert a folder of images to QOI, a few at a time. Each image runs on one thread, so the files run side by side.
        EgamiContext oneThread = EgamiContext.Default with { MaxParallelism = 1 };
        Directory.CreateDirectory("out");
        Parallel.ForEach(Directory.GetFiles("in"), new ParallelOptions { MaxDegreeOfParallelism = 4 }, path =>
        {
            try
            {
                using Raster image = Raster.Load(path, oneThread);
                image.Save(Path.Combine("out", Path.GetFileNameWithoutExtension(path) + ".qoi"));
            }
            catch (EgamiException e)
            {
                Console.WriteLine($"{path}: {e.Message}");
            }
        });
        #endregion

        Assert.Equal(2, Directory.GetFiles("out").Length);
    }

    [Fact]
    public void GifFromFrames()
    {
        using SampleFiles files = new();

        #region gif-from-frames
        // Start from one picture and add frames: each Add copies the frame you pass.
        using Raster<Rgba8> animation = Raster.Load<Rgba8>("duck.png");
        animation.Apply(ops => ops.Fit(120, 120));
        using Raster<Rgba8> next = animation.Derive(ops => ops.HueRotate(120));
        using Raster<Rgba8> last = animation.Derive(ops => ops.HueRotate(240));
        animation.Frames.Add(next.Frames[0]);
        animation.Frames.Add(last.Frames[0]);
        foreach (Frame<Rgba8> frame in animation.Frames)
        {
            frame.Metadata.Delay = TimeSpan.FromMilliseconds(400);
        }

        animation.Metadata.LoopCount = 0;   // forever
        animation.Save("hues.gif");
        #endregion

        Assert.Equal(3, Raster.Probe("hues.gif").FrameCount);
    }

    [Fact]
    public void ReadSize()
    {
        using SampleFiles files = new();

        #region read-size
        RasterInfo info = Raster.Probe("sideways.jpg");
        bool sideways = info.Orientation is >= Egami.Metadata.ImageOrientation.Transpose;
        (int width, int height) = sideways ? (info.Height, info.Width) : (info.Width, info.Height);
        Console.WriteLine($"{info.Format.Name}, shown as {width} × {height}, about {info.EstimatedBytes:N0} bytes to load");
        #endregion

        Assert.True(width > 0);
    }

    [Fact]
    public void RawBuffers()
    {
        using SampleFiles files = new();

        #region raw-buffers
        // Pixels from somewhere else, such as a camera SDK: 640 × 480 BGRA, rows 2,560 bytes apart.
        byte[] buffer = new byte[2560 * 480];

        // Wrap them without copying, work on them, and save.
        using (Raster<Bgra8> frame = Raster.Wrap<Bgra8>(buffer, 640, 480, byteStride: 2560))
        {
            frame.Apply(ops => ops.FillColor(Rgba8.Black).Opacity(1));
            frame.Save("frame.png");
        }

        // Or copy the pixels out of a raster into your own buffer, row by row.
        using Raster<Rgba8> image = Raster.Load<Rgba8>("duck.png");
        Rgba8[] pixels = new Rgba8[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            image.Row(y).CopyTo(pixels.AsSpan(y * image.Width, image.Width));
        }
        #endregion

        Assert.Equal(255, buffer[3]);
    }

    [Fact]
    public async Task Upload()
    {
        using SampleFiles files = new();
        await using Stream upload = File.OpenRead("duck.png");
        await using Stream response = File.Create("avatar.jpg");

        #region upload
        // In an ASP.NET Core endpoint: `upload` is IFormFile.OpenReadStream(), `response` is HttpResponse.Body.
        EgamiContext limits = EgamiContext.Default with
        {
            Limits = SafetyLimits.Default with { MaxPixels = 40_000_000, MaxTotalBytes = 512L * 1024 * 1024 },
        };

        using Raster image = await Raster.LoadAsync(upload, limits, new ReaderOptions { MinimumSize = new Size(256, 256) }, Ct);
        image.Apply(ops => ops.AutoOrient().Cover(256, 256), Ct);
        await image.SaveAsync(response, JpegFormat.Info, new JpegWriterOptions { Quality = 85, StripMetadata = true }, Ct);
        #endregion

        Assert.True(response.Length > 0);
    }
}
