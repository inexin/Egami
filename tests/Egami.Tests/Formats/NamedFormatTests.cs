using Egami.Formats;
using Egami.Pixels;
using Egami.Tests.Formats.Tiff;

namespace Egami.Tests.Formats;

/// <summary>Loading with a named format: the overloads that take a <see cref="FormatInfo"/> and skip detection.</summary>
public class NamedFormatTests
{
    public static TheoryData<string> Files() =>
    [
        "pngsuite/basn2c08.png", "jpeg/duck-q75.jpg", "gif/colors-16.gif", "webp/lossless-z6.webp", "bmp/duck-24.bmp", "qoi/duck-rgb.qoi",
        "pnm/duck-p6.ppm", "tga/duck-24.tga", "apng/pan-rgb.png", "tiff/pages3.tif",
    ];

    private static byte[] Read(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "images", name);
        if (!File.Exists(path))
        {
            path = Directory.GetFiles(Path.GetDirectoryName(path)!, "*" + Path.GetExtension(path)).Order(StringComparer.Ordinal).First();
        }

        return File.ReadAllBytes(path);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void NamingTheFormatGivesWhatDetectionGives(string name)
    {
        byte[] data = Read(name);
        FormatInfo format = Raster.Probe(data).Format;

        using Raster detected = Raster.Load(data);
        using Raster named = Raster.Load(data, format);
        Assert.Equal(TiffReference.Bytes(detected), TiffReference.Bytes(named));
        Assert.Equal(detected.Layout, named.Layout);
        Assert.Equal(detected.FrameCount, named.FrameCount);
        Assert.Equal(format.Name, named.Metadata.FormatName);

        using MemoryStream stream = new(data);
        using Raster<Rgba8> typed = Raster.Load<Rgba8>(stream, format);
        using Raster<Rgba8> typedDetected = Raster.Load<Rgba8>(data);
        Assert.Equal(TiffReference.Bytes(typedDetected), TiffReference.Bytes(typed));

        Assert.Equal(Raster.Probe(data) with { Format = format }, Raster.Probe(data, format));

        using RasterList all = Raster.LoadAll(data, format);
        using RasterList allDetected = Raster.LoadAll(data);
        Assert.Equal(allDetected.Count, all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            Assert.Equal(TiffReference.Bytes(allDetected[i]), TiffReference.Bytes(all[i]));
        }

        using RasterDecoder decoder = RasterDecoder.Open(data, format);
        Assert.Equal(format, decoder.Info.Format);
        using Raster decoded = decoder.ReadAll();
        Assert.Equal(TiffReference.Bytes(detected), TiffReference.Bytes(decoded));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void NamingTheFormatWorksWithRegionAndReduce(string name)
    {
        byte[] data = Read(name);
        FormatInfo format = Raster.Probe(data).Format;
        ReaderOptions options = new() { Region = new Rect(3, 2, 20, 17), MinimumSize = new Size(5, 4) };
        using Raster detected = Raster.Load(data, options: options);
        using Raster named = Raster.Load(data, format, options: options);
        Assert.Equal(TiffReference.Bytes(detected), TiffReference.Bytes(named));
    }

    [Fact]
    public void ANamedFormatNeedNotBeRegistered()
    {
        byte[] data = Read("qoi/duck-rgb.qoi");
        EgamiContext pngOnly = EgamiContext.Default with { Formats = [.. EgamiContext.Default.Formats.Where(f => f != Egami.Formats.Qoi.QoiFormat.Info)] };
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(data, pngOnly));
        using Raster raster = Raster.Load(data, Egami.Formats.Qoi.QoiFormat.Info, pngOnly);
        Assert.True(raster.Width > 0);
    }

    public static TheoryData<string> Formats() => [.. EgamiContext.Default.Formats.Where(f => f.Reader is not null).Select(f => f.Name)];

    [Theory]
    [MemberData(nameof(Formats))]
    public void NamingTheWrongFormatThrowsAsDamagedData(string formatName)
    {
        FormatInfo format = EgamiContext.Default.Formats.First(f => f.Name == formatName);
        foreach (string file in new[] { "pngsuite/basn2c08.png", "tiff/pages3.tif", "qoi/duck-rgb.qoi" })
        {
            byte[] data = Read(file);
            if (Raster.Probe(data).Format == format)
            {
                continue;
            }

            Assert.ThrowsAny<CorruptDataException>(() =>
            {
                using Raster raster = Raster.Load(data, format, options: new ReaderOptions { Recovery = RecoveryMode.Strict });
            });
        }
    }

    [Fact]
    public void AFormatWithoutAReaderIsRefused()
    {
        FormatInfo writeOnly = new() { Name = "W", SignatureLength = 0, Matches = _ => false };
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(new byte[4], writeOnly));
        Assert.Throws<ArgumentNullException>(() => Raster.Load(new byte[4], (FormatInfo)null!));
    }
}
