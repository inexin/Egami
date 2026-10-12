using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Exr;
using Egami.Formats.Jpeg;
using Egami.Formats.Radiance;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Hdr
{
    [Fact]
    public void Radiance()
    {
        using SampleFiles files = new();

        #region radiance
        // Linear light, unclamped: 1.0 is reference white. The brightest value is measured as the light level ToneMap fits.
        using Raster<RgbF32> hdr = Raster.Load<RgbF32>("desk.hdr");
        var details = (RadianceDetails)hdr.Metadata.FormatDetails!;
        double? exposure = details.Exposure;                    // the header's EXPOSURE lines; the pixels are kept as stored

        // To SDR: tone map, then save. Saving the HDR raster to PNG or JPEG as it is would clip the highlights.
        using Raster<RgbF32> mapped = hdr.Derive(ops => ops.ToneMap(ToneMapping.Bt2390));
        mapped.Save("desk.jpg");

        // RGBE with per-channel run-length scanlines, as Radiance writes them; EXPOSURE only when asked for.
        hdr.Save("copy.hdr");
        hdr.Save("labeled.hdr", new RadianceWriterOptions { Exposure = 1.5 });
        #endregion

        Assert.True(hdr.Metadata.Hdr!.MaxContentLightLevel > 203);
        Assert.Null(exposure);
    }

    [Fact]
    public void Mpf()
    {
        using SampleFiles files = new();

        #region mpf
        // A JPEG's Multi-Picture index lists the images stored after the primary: previews, stereo views, a gain map.
        using Raster photo = Raster.Load("ultrahdr.jpg");
        MpfData? index = ((JpegDetails)photo.Metadata.FormatDetails!).MultiPicture;
        foreach (MpfImage image in index?.Images ?? [])
        {
            Console.WriteLine($"{image.Type}: {image.Length:N0} bytes at {image.Offset:N0}");
        }

        // LoadAll gives every JPEG image the index lists, the primary first; a plain JPEG gives one.
        using RasterList all = Raster.LoadAll("ultrahdr.jpg");
        #endregion

        Assert.Equal(MpfImageType.BaselinePrimary, index!.Images[0].Type);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Exr()
    {
        using SampleFiles files = new();

        #region exr
        // The first part's RGBA channels as linear light, 1.0 reference white; other layers by name.
        using Raster<RgbaF32> beauty = Raster.Load<RgbaF32>("render.exr");
        var details = (ExrDetails)beauty.Metadata.FormatDetails!;
        IReadOnlyList<string> layers = details.Parts[details.Part].Layers;       // "diffuse", "specular"
        using Raster<RgbaF32> diffuse = Raster.Load<RgbaF32>("render.exr", options: new ExrReaderOptions { Layer = "diffuse" });

        // Any one channel, such as depth or an object id, as 32-bit float.
        using Raster<GrayF32> depth = ExrImage.ReadChannel("aov.exr", "Z");
        #endregion

        Assert.Contains("diffuse", layers);
        Assert.Equal((beauty.Width, beauty.Height), (depth.Width, depth.Height));
    }

    [Fact]
    public void ExrWrite()
    {
        using SampleFiles files = new();
        using Raster<RgbaF32> beauty = Raster.Load<RgbaF32>("render.exr");

        #region exr-write
        // Half-float RGBA, ZIP-compressed, by default; a sRGB or other non-linear raster is linearized first.
        beauty.Save("copy.exr");
        beauty.Save("float.exr", new ExrWriterOptions { Compression = ExrCompression.Piz, PixelType = ExrPixelType.Float });
        #endregion

        Assert.Equal(beauty.Width, Raster.Probe("float.exr").Width);
    }

    [Fact]
    public void GainMapRead()
    {
        using SampleFiles files = new();

        #region gain-map
        // As stored: the SDR base image, with the gain map in the metadata.
        using Raster sdr = Raster.Load("ultrahdr.jpg");
        GainMap? map = sdr.Metadata.GainMap;                                     // null for a plain JPEG

        // The HDR rendition for a display 4x as bright as SDR white: linear half floats, 1.0 = SDR white.
        using Raster<RgbaF16> hdr = Raster.Load<RgbaF16>("ultrahdr.jpg", options: new ReaderOptions { Hdr = new HdrDecode { Headroom = 4 } });

        // The same from a raster in hand, at the map's full capacity.
        using Raster<RgbaF16> full = map!.Apply(sdr);
        #endregion

        Assert.True(full.Metadata.Hdr!.MaxContentLightLevel > hdr.Metadata.Hdr!.MaxContentLightLevel);
    }

    [Fact]
    public void GainMapKeep()
    {
        using SampleFiles files = new();

        #region gain-map-keep
        // Crops, turns, flips and resizes move the gain map with the picture; saving as JPEG writes it back.
        using Raster photo = Raster.Load("ultrahdr.jpg");
        using Raster cropped = photo.Derive(ops => ops.Crop(16, 16, 200, 150).Rotate(90));
        cropped.Save("cropped.jpg");                                             // still Ultra HDR

        // Operations that change pixel values drop it (Operation.GainMapDropped), and so do formats other than JPEG
        // (Save.GainMapDropped); KeepGainMap = false leaves it out on purpose.
        cropped.Save("sdr-only.jpg", new JpegWriterOptions { KeepGainMap = false });
        #endregion

        using Raster kept = Raster.Load("cropped.jpg");
        using Raster dropped = Raster.Load("sdr-only.jpg");
        Assert.NotNull(kept.Metadata.GainMap);
        Assert.Null(dropped.Metadata.GainMap);
    }

    [Fact]
    public void GainMapWrite()
    {
        using SampleFiles files = new();

        #region gain-map-write
        // An HDR raster as Ultra HDR: the SDR base, tone mapped with BT.2390, and a gain map that brings the HDR raster back.
        using Raster<RgbaF32> hdr = Raster.Load<RgbaF32>("render.exr");
        hdr.Save("render.jpg", new JpegWriterOptions { Quality = 90, GainMap = new GainMapOptions() });

        // Or choose the SDR rendition: the map goes in its metadata, and saving as JPEG writes both.
        using Raster<Rgb8> sdr = hdr.Derive(ops => ops.ToneMap(ToneMapping.Reinhard)).As<Rgb8>();
        sdr.Metadata.GainMap = GainMap.Create(hdr, sdr, new GainMapOptions { Scale = 4 });
        sdr.Save("graded.jpg", new JpegWriterOptions { Quality = 90 });
        #endregion

        using Raster written = Raster.Load("render.jpg");
        using Raster graded = Raster.Load("graded.jpg");
        Assert.NotNull(written.Metadata.GainMap);
        Assert.Equal(hdr.Width / 4, graded.Metadata.GainMap!.Width);
    }

    [Fact]
    public void CookbookExrLayerToJpeg()
    {
        using SampleFiles files = new();

        #region cookbook-exr-layer
        // One render pass of a multi-layer OpenEXR file, tone mapped for the web.
        using Raster<RgbaF32> diffuse = Raster.Load<RgbaF32>("render.exr", options: new ExrReaderOptions { Layer = "diffuse" });
        diffuse.Apply(ops => ops.ToneMap(ToneMapping.Bt2390));
        diffuse.Save("diffuse.jpg", new JpegWriterOptions { Quality = 90 });
        #endregion

        using Raster written = Raster.Load("diffuse.jpg");
        Assert.Equal(diffuse.Width, written.Width);
    }

    [Fact]
    public void CookbookUltraHdrView()
    {
        using SampleFiles files = new();
        double displayHeadroom = 4;

        #region cookbook-ultrahdr-view
        // An Ultra HDR photo for an HDR display whose peak is displayHeadroom times SDR white: linear light, 1.0 = SDR white,
        // here written as a half-float OpenEXR file; an SDR display shows the JPEG as it is.
        using Raster<RgbaF16> photo = Raster.Load<RgbaF16>("ultrahdr.jpg", options: new ReaderOptions { Hdr = new HdrDecode { Headroom = displayHeadroom } });
        photo.Save("photo-hdr.exr");
        #endregion

        Assert.True(photo.Metadata.Hdr!.MaxContentLightLevel > 203);
    }

    [Fact]
    public void CookbookExrToUltraHdr()
    {
        using SampleFiles files = new();

        #region cookbook-exr-to-ultrahdr
        // An HDR render as a JPEG every viewer opens, which HDR displays (Android, Chrome, Apple Photos) show brighter.
        using Raster<RgbaF32> render = Raster.Load<RgbaF32>("render.exr");
        render.Save("render-ultrahdr.jpg", new JpegWriterOptions { Quality = 90, GainMap = new GainMapOptions { Scale = 2 } });
        #endregion

        using Raster written = Raster.Load("render-ultrahdr.jpg");
        Assert.NotNull(written.Metadata.GainMap);
    }
}
