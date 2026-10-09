using Egami.Formats;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class GettingStarted
{
    [Fact]
    public void LoadEditSave()
    {
        using SampleFiles files = new();

        #region load-edit-save
        // Load in the pixel type that best fits the file (here Rgba8: this PNG stores alpha).
        using Raster image = Raster.Load("duck.png");
        Console.WriteLine($"{image.Width} × {image.Height}, {image.Layout.ChannelOrder}");

        // Make a thumbnail that fits 200 × 200, keeping the aspect ratio, and save it as JPEG.
        image.Apply(ops => ops.Fit(200, 200));
        image.Save("duck-small.jpg");
        #endregion

        Assert.True(Raster.Probe("duck-small.jpg").Width <= 200);
    }

    [Fact]
    public void PixelTypes()
    {
        using SampleFiles files = new();

        #region pixel-types
        // Decode straight into the type you want to work in.
        using Raster<Rgba8> rgba = Raster.Load<Rgba8>("duck.png");

        // Pixels are a row at a time: each row is a Span<Rgba8>.
        for (int y = 0; y < rgba.Height; y++)
        {
            Span<Rgba8> row = rgba.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x].A = 128;
            }
        }

        // Convert a whole raster; the result is a new raster you dispose too.
        using Raster<Gray8> gray = rgba.As<Gray8>();
        gray.Save("duck-gray.png");
        #endregion

        Assert.Equal(PixelChannelOrder.Gray, Raster.Probe("duck-gray.png").Layout.ChannelOrder);
    }

    [Fact]
    public void Disposal()
    {
        using SampleFiles files = new();

        #region disposal
        // Every raster owns its pixel memory: dispose it when done, best with a using declaration.
        using Raster photo = Raster.Load("duck.png");

        // Copy, As and Derive return new rasters, which you dispose as well. Everything else works in place.
        using Raster copy = photo.Derive(ops => ops.Resize(100, 0));
        photo.Apply(ops => ops.Grayscale());
        #endregion

        Assert.Equal(100, copy.Width);
    }

    [Fact]
    public async Task SyncAndAsync()
    {
        using SampleFiles files = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        #region async
        // Every Load, Probe and Save has an async twin. Async reads the stream into a pooled buffer first,
        // then decodes (decoding is CPU work, so it is synchronous either way).
        await using FileStream stream = File.OpenRead("duck.png");
        using Raster image = await Raster.LoadAsync(stream, cancellationToken: cancellationToken);
        await image.SaveAsync("duck-copy.png", cancellationToken: cancellationToken);
        #endregion

        Assert.True(File.Exists("duck-copy.png"));
    }

    [Fact]
    public void ChoosingAFormat()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region choosing-a-format
        // Save(path) picks the writer from the file extension...
        image.Save("duck-copy.qoi");

        // ...or name the format, with its options.
        using FileStream output = File.Create("duck-q90.jpg");
        image.Save(output, Egami.Formats.Jpeg.JpegFormat.Info, new Egami.Formats.Jpeg.JpegWriterOptions { Quality = 90 });

        // Loading never looks at the extension: the format is detected from the first bytes.
        RasterInfo info = Raster.Probe("duck.png");
        Console.WriteLine(info.Format.Name);   // PNG
        #endregion

        Assert.Equal("PNG", info.Format.Name);
    }
}
