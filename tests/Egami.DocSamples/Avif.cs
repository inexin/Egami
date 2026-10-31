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

        #region write-animation
        // A raster with several frames saves as an animated AVIF: one AV1 stream of a key frame and inter frames, each frame's
        // delay, the loop count (0 plays forever) and, when any frame is transparent, an alpha track.
        using Raster<Rgba8> pan = Raster.Load<Rgba8>("pan.gif");
        pan.Metadata.LoopCount = 0;
        pan.Save("pan.avif", new AvifWriterOptions { Quality = 70 });
        #endregion

        #region write-yuv
        // YUV planes are written as they are: no conversion or resampling, their matrix, range and siting in the file.
        using YuvRaster planes = YuvRaster.Load("duck.avif");
        planes.Save("duck-planes.avif", new AvifWriterOptions { Lossless = true });
        #endregion

        using Raster<Rgba8> panAgain = Raster.Load<Rgba8>("pan.avif");
        Assert.Equal(pan.FrameCount, panAgain.FrameCount);
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

    [Fact]
    public void Extras()
    {
        using SampleFiles files = new();
        using Raster duck = Raster.Load("duck.avif");

        #region layers
        // A progressive file: a small first layer a viewer can show early, then the image (as avifenc --progressive writes it).
        duck.Save("duck-progressive.avif", new AvifWriterOptions { Quality = 60, Progressive = true });

        // Or up to four layers, each with its own quality and size; the last is the image at full size.
        duck.Save("duck-layers.avif", new AvifWriterOptions
        {
            Layers = [new AvifLayer { Quality = 20, Scale = 0.5 }, new AvifLayer { Quality = 45 }, new AvifLayer { Quality = 75 }],
        });

        // Load gives the best layer; AvifReaderOptions.Layer picks another, at full size.
        using Raster best = Raster.Load("duck-layers.avif");
        IReadOnlyList<Size> layers = ((AvifDetails)best.Metadata.FormatDetails!).Layers;   // 160x120, 320x240, 320x240
        using Raster first = Raster.Load("duck-layers.avif", options: new AvifReaderOptions { Layer = 0 });
        #endregion

        #region avif-preview
        // While a progressive file downloads, RenderPreview draws the best layer received so far at full size.
        byte[] file = File.ReadAllBytes("duck-progressive.avif");
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        Raster<Rgb8>? preview = null;
        int previews = 0;
        foreach (byte[] chunk in file.Chunk(1024))
        {
            decoder.Append(chunk);
            if (decoder.HeaderRead)
            {
                preview ??= Raster.Create<Rgb8>(decoder.Info.Width, decoder.Info.Height);
                previews += decoder.RenderPreview(preview) ? 1 : 0;
            }
        }

        decoder.EndOfInput();
        preview?.Dispose();
        #endregion

        #region sato
        // 16 bits a sample, which AV1 cannot code, as a sample transform (AVIF 1.2): a 12-bit image and an 8-bit residual (or,
        // lossless, the high and low bytes), with the first image as the fallback for readers without sample transforms.
        using Raster<Rgb16> wide = duck.As<Rgb16>();
        wide.Save("duck-16bit.avif", new AvifWriterOptions { BitDepth = 16, Quality = 90 });
        using Raster deep = Raster.Load("duck-16bit.avif");
        bool transformed = ((AvifDetails)deep.Metadata.FormatDetails!).HasSampleTransform;   // true, at 16 bits
        using Raster fallback = Raster.Load("duck-16bit.avif", options: new AvifReaderOptions { SampleTransform = false });
        #endregion

        #region grain
        // Grain is costly to code: denoise first and let decoders add it back from AV1 film grain parameters.
        duck.Save("duck-grain.avif", new AvifWriterOptions { Quality = 60, FilmGrainDenoise = 20 });

        // A raster loaded without its grain keeps the parameters, and saving writes them back.
        using Raster plain = Raster.Load("duck-grain.avif", options: new AvifReaderOptions { FilmGrain = false });
        plain.Save("duck-grain-again.avif");
        #endregion

        #region target-size
        // The quality is searched for the largest file not above the size, as avifenc --target-size does.
        duck.Save("duck-8k.avif", new AvifWriterOptions { TargetSize = 8000 });
        #endregion

        Assert.Equal(3, layers.Count);
        Assert.Equal((best.Width, best.Height), (first.Width, first.Height));
        Assert.True(((AvifDetails)Raster.Load("duck-progressive.avif").Metadata.FormatDetails!).IsProgressive);
        Assert.True(previews > 0);
        Assert.True(transformed);
        Assert.Equal(16, ((AvifDetails)deep.Metadata.FormatDetails!).BitDepth);
        Assert.Equal(12, ((AvifDetails)fallback.Metadata.FormatDetails!).BitDepth);
        Assert.True(new FileInfo("duck-8k.avif").Length <= 8000);
        using Raster again = Raster.Load("duck-grain-again.avif", options: new AvifReaderOptions { FilmGrain = false });
        Assert.Equal(((AvifDetails)plain.Metadata.FormatDetails!).FilmGrain is null, ((AvifDetails)again.Metadata.FormatDetails!).FilmGrain is null);
    }
}
