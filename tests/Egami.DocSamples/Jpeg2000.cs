using Egami.Formats;
using Egami.Formats.Jpeg2000;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Jpeg2000
{
    [Fact]
    public void Load()
    {
        using SampleFiles files = new();

        #region jpeg2000-load
        // JP2, JPX and raw codestreams are detected like any other format, so is a PDF's JPXDecode stream.
        using Raster photo = Raster.Load("photo.jp2");

        // A thumbnail drops whole wavelet levels instead of decoding at full size, and a region decodes only the code-blocks
        // it needs.
        using Raster thumbnail = Raster.Load("photo.jp2", options: new ReaderOptions { MinimumSize = new Size(24, 18) });
        using Raster corner = Raster.Load("photo.jp2", options: new ReaderOptions { Region = new Rect(0, 0, 32, 32) });
        #endregion

        Assert.Equal(Rgb8.Layout, photo.Layout);
        Assert.True(thumbnail.Width < photo.Width);
        Assert.Equal(32, corner.Width);
    }

    [Fact]
    public void Options()
    {
        using SampleFiles files = new();
        byte[] jpxStream = File.ReadAllBytes("photo.j2k");

        #region jpeg2000-options
        // A PDF that names its own color space wants the codestream's components as stored.
        using Raster components = Raster.Load(jpxStream, options: new Jpeg2000ReaderOptions { Components = Jpeg2000Components.Raw });

        // A quick preview of a layered file: only its first quality layer, chroma repeated as OpenJPEG does.
        using Raster preview = Raster.Load("photo.jp2", options: new Jpeg2000ReaderOptions { MaxQualityLayers = 1, FastUpsampling = true });
        #endregion

        Assert.Equal(3, components.Layout.ChannelCount);
        Assert.Equal(Rgb8.Layout, preview.Layout);
    }

    [Fact]
    public void Details()
    {
        using SampleFiles files = new();

        #region jpeg2000-details
        using Raster image = Raster.Load("photo.jp2");
        var details = (Jpeg2000Details)image.Metadata.FormatDetails!;
        Console.WriteLine($"{details.Container}, {(details.Reversible ? "lossless 5/3" : "lossy 9/7")}, {details.DecompositionLevels} levels");
        Console.WriteLine($"{details.QualityLayers} layers in {details.ProgressionOrder} order, {details.TileCount} tiles, {details.ColorSpace}");
        foreach (Jpeg2000Component component in details.Components)
        {
            Console.WriteLine($"{component.Precision} bits{(component.IsSigned ? " signed" : "")}, sampled every {component.SubsamplingX}×{component.SubsamplingY}");
        }

        foreach (Jpeg2000Channel channel in details.Channels)
        {
            Console.WriteLine($"Channel {channel.Channel}: {channel.Type} for color {channel.Association}");
        }
        #endregion

        Assert.Equal(Jpeg2000Container.Jp2, details.Container);
        Assert.Equal(Jpeg2000ProgressionOrder.Rlcp, details.ProgressionOrder);
        Assert.Equal(Jpeg2000ColorSpace.Srgb, details.ColorSpace);
        Assert.Empty(details.Channels);
    }
}
