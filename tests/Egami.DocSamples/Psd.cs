using Egami.Formats.Png;
using Egami.Formats.Psd;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Psd
{
    [Fact]
    public void Load()
    {
        using SampleFiles files = new();

        #region psd-load
        // Load gives the flattened image Photoshop stores after the layers, in the type that fits the file: Rgb8 here, Rgba16 for
        // a 16-bit file with transparency, Cmyk8 for CMYK. Save it as anything.
        using Raster flat = Raster.Load("layered.psd");
        flat.Save("layered.png");

        // The ICC profile, resolution, EXIF, XMP and IPTC are in the metadata as for every format.
        using Raster photo = Raster.Load("photo.psd");
        ReadOnlyMemory<byte> icc = photo.Metadata.IccProfile;
        Resolution? dpi = photo.Metadata.Resolution;
        #endregion

        Assert.Equal(PngFormat.Info, Raster.Probe("layered.png").Format);
        Assert.False(icc.IsEmpty);
        Assert.Equal(300, dpi!.Value.X);
    }

    [Fact]
    public void Details()
    {
        using SampleFiles files = new();

        #region psd-details
        using Raster image = Raster.Load("photo.psd");
        var details = (PsdDetails)image.Metadata.FormatDetails!;
        Console.WriteLine($"{(details.Version == 2 ? "PSB" : "PSD")}, {details.ColorMode} at {details.BitsPerChannel} bits, {details.ChannelCount} channels, {details.Compression}");
        if (!details.HasRealComposite)
        {
            // Saved without Maximize Compatibility: what loaded is Photoshop's placeholder, and a warning says so.
            Console.WriteLine("Save the file again with Maximize Compatibility on.");
        }

        foreach (PsdResource resource in details.OtherResources)
        {
            Console.WriteLine($"Resource {resource.Id}, {resource.Data.Length} bytes");
        }
        #endregion

        Assert.Equal(PsdColorMode.Rgb, details.ColorMode);
    }

    [Fact]
    public void Layers()
    {
        using SampleFiles files = new();

        #region psd-layers
        // Layer records come as metadata, bottom to top as the file lists them; their pixels are not loaded.
        using Raster image = Raster.Load("layered.psd");
        foreach (PsdLayer layer in ((PsdDetails)image.Metadata.FormatDetails!).Layers)
        {
            if (layer.Kind == PsdLayerKind.GroupEnd)
            {
                continue;
            }

            string kind = layer.Kind == PsdLayerKind.Layer ? "layer" : "group";
            Console.WriteLine($"{new string(' ', 2 * layer.Level)}{layer.Name} ({kind}, {layer.BlendModeKey}, opacity {layer.Opacity:P0}{(layer.IsVisible ? "" : ", hidden")})");
        }
        #endregion

        Assert.Equal(9, ((PsdDetails)image.Metadata.FormatDetails!).Layers.Count);
    }

    [Fact]
    public void ReadLayers()
    {
        using SampleFiles files = new();

        #region psd-read-layers
        // ReadLayers gives every layer with its pixels, bottom to top; a group comes after its contents.
        using PsdDocument document = PsdImage.ReadLayers("layered.psd");
        foreach (PsdLayerImage layer in document.Layers)
        {
            if (layer.Pixels is Raster pixels)
            {
                // Each layer at its own size, in the natural type with alpha (Rgba8 here); X and Y place it on the canvas.
                pixels.Save($"layer-{layer.Index}.png");
                Console.WriteLine($"{layer.Info.Name}: {pixels.Width}×{pixels.Height} at ({layer.X}, {layer.Y}), in group {layer.ParentIndex}");
            }
        }
        #endregion

        Assert.True(File.Exists("layer-0.png"));
    }
}
