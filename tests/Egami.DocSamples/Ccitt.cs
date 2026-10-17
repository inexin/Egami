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

    [Fact]
    public void Encode()
    {
        using Raster<Gray8> scan = Raster.Create<Gray8>(301, 48, new Gray8(255));
        scan.SetPixel(10, 10, new Gray8(0));

        #region ccitt-encode
        // A bi-level scan as a PDF image with /Filter /CCITTFaxDecode: pack its rows (0 black, 1 white), then write Group 4.
        int rowBytes = (scan.Width + 7) / 8;
        byte[] rows = new byte[rowBytes * scan.Height];
        for (int y = 0; y < scan.Height; y++)
        {
            Span<Gray8> row = scan.Row(y);
            for (int x = 0; x < scan.Width; x++)
            {
                if (row[x].L >= 128)
                {
                    rows[(y * rowBytes) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        // These parameters are the image's DecodeParms: << /K -1 /Columns 301 /Rows 48 >>.
        CcittFaxParameters parameters = new() { K = -1, Columns = scan.Width, Rows = scan.Height };
        byte[] g4 = CcittFax.Encode(rows, parameters);
        #endregion

        Assert.Equal(rows, CcittFax.Decode(g4, parameters, out _));
    }

    [Fact]
    public void PdfImage()
    {
        using SampleFiles files = new();
        byte[] data = File.ReadAllBytes("fax-g4.bin");
        (int k, bool aligned, int columns, int rows, int width, int height) = (-1, true, 301, 0, 301, 48);
        bool imageMask = false;
        double[]? decode = null;

        #region ccitt-pdf-image
        // An image XObject with /Filter /CCITTFaxDecode: DecodeParms keys not given keep PDF's defaults. The image's /Height
        // stands in for a missing Rows, so the decoder stops where the image does.
        CcittFaxParameters parameters = new() { K = k, EncodedByteAlign = aligned, Columns = columns, Rows = rows > 0 ? rows : height };
        byte[] bits = CcittFax.Decode(data, parameters, out int decoded);

        // The filter's samples: 0 black, 1 white. /Decode [1 0] inverts them, and an /ImageMask paints where a sample is 0.
        bool invert = decode is [> 0.5, ..];
        int rowBytes = (columns + 7) / 8;
        using Raster<Gray8> image = Raster.Create<Gray8>(width, decoded);
        for (int y = 0; y < decoded; y++)
        {
            Span<Gray8> row = image.Row(y);
            for (int x = 0; x < Math.Min(width, columns); x++)
            {
                bool sample = ((bits[(y * rowBytes) + (x >> 3)] >> (7 - (x & 7))) & 1) != 0;
                row[x] = new Gray8(sample != invert ? (byte)255 : (byte)0);
            }
        }
        #endregion

        Assert.False(imageMask);
        Assert.Equal(height, decoded);
    }
}
