using Egami.Formats;
using Egami.Formats.Avif;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Avif
{
    [Fact]
    public void Probe()
    {
        using SampleFiles files = new();

        #region probe
        // The container alone: size, natural pixel type and orientation, without decoding any AV1.
        RasterInfo info = Raster.Probe("duck.avif");
        bool isAvif = info.Format == AvifFormat.Info;
        long bytes = info.EstimatedBytes;                       // what a full load would allocate
        #endregion

        Assert.True(isAvif);
        Assert.Equal((320, 240), (info.Width, info.Height));
        Assert.Equal(320L * 240 * 3, bytes);
    }

    [Fact]
    public void Options()
    {
        using SampleFiles files = new();

        #region options
        // Load decodes the AV1 image, converts it to RGB the way libavif does, and crops the clean aperture.
        using Raster image = Raster.Load("duck.avif");

        // The image as coded, without the film grain its AV1 stream asks the decoder to add.
        using Raster clean = Raster.Load("duck.avif", options: new AvifReaderOptions { FilmGrain = false });
        AvifFilmGrain? grain = ((AvifDetails)clean.Metadata.FormatDetails!).FilmGrain;   // null when there is none
        #endregion

        Assert.Equal((image.Width, image.Height), (clean.Width, clean.Height));
        Assert.Null(grain);
    }

    [Fact]
    public void Write()
    {
        using SampleFiles files = new();
        using Raster duck = Raster.Load("duck.avif");

        #region write
        // Save picks AVIF from the extension; the defaults are lossy at quality 60 on avifenc's scale, effort 4.
        duck.Save("duck-q60.avif");

        // Smaller and softer, or every sample exact (4:4:4 RGB with the identity matrix).
        duck.Save("duck-q35.avif", new AvifWriterOptions { Quality = 35 });
        duck.Save("duck-lossless.avif", new AvifWriterOptions { Lossless = true });
        #endregion

        #region cookbook-write
        // A transparent image keeps its alpha as a second AV1 image; AlphaQuality below 100 codes it lossy too.
        using Raster<Rgba8> logo = Raster.Create<Rgba8>(64, 64);
        logo.Save("logo.avif", new AvifWriterOptions { Quality = 80, AlphaQuality = 100 });
        #endregion

        #region write-yuv
        // YUV planes are written as they are: no conversion or resampling, their matrix, range and siting in the file.
        using YuvRaster planes = YuvRaster.Load("duck.avif");
        planes.Save("duck-planes.avif", new AvifWriterOptions { Lossless = true });
        #endregion

        using YuvRaster again = YuvRaster.Load("duck-planes.avif");
        Assert.Equal((planes.ChromaFormat, planes.BitDepth, planes.Color), (again.ChromaFormat, again.BitDepth, again.Color));
        Assert.True(new FileInfo("duck-q35.avif").Length < new FileInfo("duck-q60.avif").Length);
        using Raster lossless = Raster.Load("duck-lossless.avif");
        Assert.True(((AvifDetails)lossless.Metadata.FormatDetails!).Lossless);
        Assert.True(File.Exists("logo.avif"));
    }

    [Fact]
    public void Cookbook()
    {
        using SampleFiles files = new();

        #region cookbook-load
        // AVIF loads like any format, in its natural pixel type: Rgb8 or Rgba8 for 8-bit files, Rgb16 or Rgba16 for 10 and 12 bits.
        using Raster duck = Raster.Load("duck.avif");
        duck.Save("duck.png");
        #endregion

        #region cookbook-yuv
        // The AV1 planes themselves, without the conversion to RGB: luma, then chroma at the file's subsampling.
        using YuvRaster planes = YuvRaster.Load("duck.avif");
        ChromaFormat chroma = planes.ChromaFormat;          // Yuv420 for most AVIF files
        #endregion

        #region cookbook-sdr
        // An HDR (PQ or HLG) AVIF, tone mapped into sRGB as it loads; the peak comes from the file's clli box when it has one.
        using Raster<Rgb8> sdr = Raster.Load<Rgb8>("hdr.avif", options: new ReaderOptions { ConvertColorTo = Egami.Colors.ColorProfile.Srgb });
        sdr.Save("hdr-as-sdr.jpg");
        #endregion

        Assert.True(File.Exists("duck.png"));
        Assert.True(File.Exists("hdr-as-sdr.jpg"));
        Assert.Equal(ChromaFormat.Yuv420, chroma);
    }

    [Fact]
    public void Items()
    {
        using SampleFiles files = new();

        #region items
        // Every item of the file, with what it is to the image Load gives: alpha, thumbnails, depth maps, grid cells, metadata.
        using Raster image = Raster.Load("duck.avif");
        IReadOnlyList<AvifItem> items = ((AvifDetails)image.Metadata.FormatDetails!).Items;
        AvifItem primary = items.First(i => i.Role == AvifItemRole.Primary);

        // Any image item decodes on its own, with its own color, such as a depth map or a thumbnail.
        using Raster item = AvifImage.ReadItem("duck.avif", primary.Id);
        #endregion

        Assert.Equal((image.Width, image.Height), (item.Width, item.Height));
    }

    [Fact]
    public void StageD()
    {
        using SampleFiles files = new();

        #region cookbook-gainmap
        // An AVIF with a gain map loads as its base image, the map in Metadata.GainMap; ReaderOptions.Hdr renders the HDR
        // rendition for a display with the given headroom (its peak over SDR white), as linear RgbaF16.
        using Raster sdr = Raster.Load("gainmap.avif");
        bool hasMap = sdr.Metadata.GainMap is not null;
        using Raster hdr = Raster.Load("gainmap.avif", options: new ReaderOptions { Hdr = new HdrDecode { Headroom = 4 } });
        #endregion

        #region cookbook-items
        // The file's other images: thumbnails, depth maps and auxiliary images, each read on its own.
        AvifDetails details = (AvifDetails)sdr.Metadata.FormatDetails!;
        foreach (AvifItem item in details.Items.Where(i => i.IsImage && i.Role is AvifItemRole.Thumbnail or AvifItemRole.Depth or AvifItemRole.GainMap))
        {
            using Raster image = AvifImage.ReadItem("gainmap.avif", item.Id);
            image.Save($"item-{item.Id}-{item.Role}.png");
        }
        #endregion

        #region cookbook-sequence
        // An image sequence whose frames are all key frames loads as an animation; save it as an animated WebP.
        using Raster<Rgba8> frames = Raster.Load<Rgba8>("sequence.avif");
        TimeSpan firstDelay = frames.Frames[0].Metadata.Delay;
        frames.Save("sequence.webp");
        #endregion

        Assert.True(hasMap);
        Assert.Equal(RgbaF16.Layout, hdr.Layout);
        Assert.Equal(4, frames.FrameCount);
        Assert.True(firstDelay > TimeSpan.Zero);
        Assert.True(File.Exists("item-3-GainMap.png") || details.Items.Any(i => i.Role == AvifItemRole.GainMap));
    }
}
