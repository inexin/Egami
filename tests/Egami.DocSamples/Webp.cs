using Egami.Formats.Webp;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Webp
{
    [Fact]
    public void Save()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region webp
        // Lossless: every pixel exact, colors under full transparency included. Effort 0 (fastest) to 6 (smallest).
        photo.Save("duck.webp", new WebpWriterOptions { Lossless = true, Effort = 6 });

        // A raster read from a WebP file keeps its kind when saved with the defaults.
        using Raster again = Raster.Load("duck.webp");
        again.Save("copy.webp");
        #endregion

        Assert.True(((WebpDetails)Raster.Load("copy.webp").Metadata.FormatDetails!).Lossless);
    }

    [Fact]
    public void Details()
    {
        using SampleFiles files = new();
        using (Raster photo = Raster.Load("duck.png"))
        {
            photo.Save("duck.webp", new WebpWriterOptions { Lossless = true });
        }

        #region webp-details
        using Raster<Rgba8> image = Raster.Load<Rgba8>("duck.webp");
        WebpDetails webp = (WebpDetails)image.Metadata.FormatDetails!;
        Console.WriteLine($"{(webp.Lossless ? "lossless" : "lossy")}, extended: {webp.Extended}, alpha: {webp.HasAlpha}");

        // Chunks Egami does not read are kept and written back where they were.
        foreach (WebpChunk chunk in webp.OtherChunks)
        {
            Console.WriteLine($"{chunk.Type}: {chunk.Data.Length} bytes, {chunk.Position}");
        }

        // An animation's frames say whether each was stored lossless.
        foreach (Frame<Rgba8> frame in image.Frames)
        {
            Console.WriteLine(frame.Metadata.FormatDetails is WebpFrameDetails { Lossless: true } ? "lossless frame" : "lossy frame");
        }
        #endregion

        Assert.True(webp.Lossless);
    }

    [Fact]
    public void Read()
    {
        using SampleFiles files = new();
        using (Raster photo = Raster.Load("duck.png"))
        {
            photo.Save("duck.webp", new WebpWriterOptions { Lossless = true });
        }

        #region webp-read
        using Raster image = Raster.Load("duck.webp", options: new WebpReaderOptions
        {
            UseBackgroundColor = true, // animations: start from the ANIM background color instead of transparent
            FastUpsampling = true,     // lossy files: repeat chroma samples instead of interpolating
        });
        #endregion

        Assert.Equal(WebpFormat.Info.Name, image.Metadata.FormatName);
    }
}
