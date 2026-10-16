using Egami.Formats;
using Egami.Formats.Avif;

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
}
