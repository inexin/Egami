using System.Text.Json;

using Egami.Formats.Tiff;
using Egami.Metadata;

namespace Egami.Tests.Formats.Tiff;

/// <summary>TIFF metadata read as ExifTool reads it: IFD0 tags and sub-IFDs into EXIF, XMP, IPTC, ICC, resolution, raw tags.</summary>
public class TiffMetadataTests
{
    private static JsonElement ExifTool(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(TiffCorpus.Main.References, Path.ChangeExtension(file, ".exif.json")))).RootElement[0];

    [Fact]
    public void ExifComesFromIfd0AndItsSubIfds()
    {
        JsonElement expected = ExifTool("meta-exif.tif");
        using Raster raster = Raster.Load(TiffCorpus.Main.Read("meta-exif.tif"));
        ExifData exif = raster.Metadata.Exif!;
        Assert.Equal(expected.GetProperty("IFD0:Make").GetString(), exif.CameraMake);
        Assert.Equal(expected.GetProperty("IFD0:Model").GetString(), exif.CameraModel);
        string Text(ExifKey key) => exif.TryGet(key, out ExifEntry? e) ? e.AsString() : "(none)";
        Assert.Equal(expected.GetProperty("IFD0:Artist").GetString(), Text(new ExifKey(315, ExifIfd.Primary)));
        Assert.Equal(expected.GetProperty("IFD0:Copyright").GetString(), Text(new ExifKey(33432, ExifIfd.Primary)));
        Assert.Equal(expected.GetProperty("IFD0:ImageDescription").GetString(), Text(new ExifKey(270, ExifIfd.Primary)));
        Assert.Equal(expected.GetProperty("IFD0:Software").GetString(), Text(new ExifKey(305, ExifIfd.Primary)));
        Assert.True(exif.TryGet(new ExifKey(0x829A, ExifIfd.Exif), out ExifEntry? exposure));
        Assert.Equal(expected.GetProperty("ExifIFD:ExposureTime").GetDouble(), exposure.AsDouble(), 6);
        Assert.True(exif.TryGet(new ExifKey(0x8827, ExifIfd.Exif), out ExifEntry? iso));
        Assert.Equal(expected.GetProperty("ExifIFD:ISO").GetInt32(), iso.AsInteger());
        Assert.Equal(new DateTime(2026, 10, 11, 12, 0, 0), exif.DateTaken);
        GeoPosition location = exif.Location!.Value;
        Assert.Equal(expected.GetProperty("GPS:GPSLatitude").GetDouble(), location.Latitude, 6);
        Assert.Equal(expected.GetProperty("GPS:GPSLongitude").GetDouble(), location.Longitude, 6);
    }

    [Fact]
    public void XmpIptcAndResolutionAreRead()
    {
        using Raster raster = Raster.Load(TiffCorpus.Main.Read("meta-exif.tif"));
        RasterMetadata metadata = raster.Metadata;
        Assert.Equal(["Egami"], metadata.XmpData!.Creators);
        Assert.Equal("Duck", metadata.XmpData.Title);
        Assert.Equal(["duck", "bird"], metadata.Iptc!.Keywords);
        Assert.Equal(new Resolution(72, 72, ResolutionUnit.PerInch), metadata.Resolution);
    }

    [Fact]
    public void IccProfileIsRead()
    {
        using Raster raster = Raster.Load(TiffCorpus.Main.Read("meta-icc.tif"));
        byte[] icc = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "icc", "compact", "DisplayP3-v4.icc"));
        Assert.Equal(icc, raster.Metadata.IccProfile.ToArray());
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, raster.Metadata.ColorSpace);
    }

    [Fact]
    public void DetailsDescribeThePage()
    {
        using Raster raster = Raster.Load(TiffCorpus.Main.Read("rgb16-zip-pred-be.tif"));
        TiffDetails details = (TiffDetails)raster.Metadata.FormatDetails!;
        Assert.Equal(TiffByteOrder.BigEndian, details.ByteOrder);
        Assert.Equal(TiffCompression.Deflate, details.Compression);
        Assert.Equal(TiffPredictor.Horizontal, details.Predictor);
        Assert.Equal(TiffPhotometric.Rgb, details.Photometric);
        Assert.Equal(16, details.BitsPerSample);
        Assert.Equal(TiffLayout.Classic, details.Layout);
        Assert.Null(details.TileWidth);

        using Raster tiled = Raster.Load(TiffCorpus.Main.Read("rgb8-bigtiff-tiles-be.tif"));
        TiffDetails big = (TiffDetails)tiled.Metadata.FormatDetails!;
        Assert.Equal((TiffLayout.Big, 16, 16), (big.Layout, big.TileWidth, big.TileHeight));
        Assert.Null(big.RowsPerStrip);

        using Raster alpha = Raster.Load(TiffCorpus.Main.Read("rgba8-assoc-zip.tif"));
        Assert.Equal([TiffExtraSample.Associated], ((TiffDetails)alpha.Metadata.FormatDetails!).ExtraSamples);
    }

    [Fact]
    public void OrientationReachesProbeAndExif()
    {
        for (int orientation = 1; orientation <= 8; orientation++)
        {
            byte[] data = TiffCorpus.Main.Read($"orientation{orientation}.tif");
            Assert.Equal((ImageOrientation)orientation, Raster.Probe(data).Orientation);
            using Raster raster = Raster.Load(data);
            Assert.Equal((ImageOrientation)orientation, raster.Metadata.Exif!.Orientation);
            Assert.Equal(32, raster.Width);
        }
    }
}
