using Egami.Formats.Ccitt;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Ccitt
{
    [Fact]
    public void Filter()
    {
        using SampleFiles files = new();
        byte[] stream = File.ReadAllBytes("fax-g4.bin");

        #region ccitt-filter
        // A PDF image with /Filter /CCITTFaxDecode: its DecodeParms become the parameters, and the result is the filter's
        // output, packed rows with black 0 and white 1, ready for the image's /Decode array and color space.
        CcittFaxParameters parameters = new() { K = -1, EncodedByteAlign = true, Columns = 301, Rows = 48 };
        byte[] rows = CcittFax.Decode(stream, parameters, out int rowCount);
        #endregion

        Assert.Equal(48, rowCount);
        Assert.Equal(48 * 38, rows.Length);
    }

    [Fact]
    public void Load()
    {
        using SampleFiles files = new();
        byte[] stream = File.ReadAllBytes("fax-g4.bin");

        #region ccitt-load
        // The same data as an image: Gray8, black 0 and white 255. The data has no header, so name the format.
        CcittReaderOptions options = new() { Parameters = new CcittFaxParameters { K = -1, EncodedByteAlign = true, Columns = 301, Rows = 48 } };
        using Raster image = Raster.Load(stream, CcittFormat.Info, options: options);
        #endregion

        Assert.Equal(Gray8.Layout, image.Layout);
        Assert.Equal((301, 48), (image.Width, image.Height));
    }
}
