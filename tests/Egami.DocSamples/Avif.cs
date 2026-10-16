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
}
