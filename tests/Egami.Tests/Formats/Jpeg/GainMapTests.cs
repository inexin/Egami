using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Colors;
using Egami.Formats;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Gain map JPEGs (Phase 2.9.1 tasks 10 and 11): every file of tests/images/gainmap finds its map with the metadata
/// libultrahdr 2.0.2 reads, and its HDR rendition at three display headrooms lies within tolerance of libultrahdr's linear
/// half-float decode (tests/reference/gainmap).
/// </summary>
public class GainMapTests(ITestOutputHelper output)
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "gainmap");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "gainmap");

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Images, "*.jpg").Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    public static TheoryData<string, double> Renditions()
    {
        TheoryData<string, double> data = [];
        foreach (JsonProperty file in Manifest().EnumerateObject())
        {
            foreach (JsonElement boost in file.Value.GetProperty("boosts").EnumerateArray())
            {
                data.Add(file.Name, boost.GetDouble());
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FindsTheMapWithLibultrahdrsMetadata(string name)
    {
        JsonElement want = Manifest().GetProperty(name);
        using Raster raster = Raster.Load(Path.Combine(Images, name));
        GainMap map = Assert.IsType<GainMap>(raster.Metadata.GainMap);
        Assert.Equal((want.GetProperty("gainmapWidth").GetInt32(), want.GetProperty("gainmapHeight").GetInt32()), (map.Width, map.Height));
        GainMapInfo info = map.Info;
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(want.GetProperty("maxContentBoost")[c].GetDouble(), Math.Pow(2, info.GainMapMax[c]), 1e-4 * Math.Pow(2, info.GainMapMax[c]));
            Assert.Equal(want.GetProperty("minContentBoost")[c].GetDouble(), Math.Pow(2, info.GainMapMin[c]), 1e-4);
            Assert.Equal(want.GetProperty("gamma")[c].GetDouble(), info.Gamma[c], 1e-6);
            Assert.Equal(want.GetProperty("offsetSdr")[c].GetDouble(), info.OffsetSdr[c], 1e-6);
            Assert.Equal(want.GetProperty("offsetHdr")[c].GetDouble(), info.OffsetHdr[c], 1e-6);
        }

        Assert.Equal(want.GetProperty("hdrCapacityMin").GetDouble(), Math.Pow(2, info.HdrCapacityMin), 1e-4);
        Assert.Equal(want.GetProperty("hdrCapacityMax").GetDouble(), Math.Pow(2, info.HdrCapacityMax), 1e-4 * Math.Pow(2, info.HdrCapacityMax));
        GainMapFormat format = name.StartsWith("apple", StringComparison.Ordinal) ? GainMapFormat.Apple : name.StartsWith("adobe", StringComparison.Ordinal) ? GainMapFormat.AdobeXmp : GainMapFormat.Iso21496;
        Assert.Equal(format, info.Format);
        using Raster image = map.ToRaster();
        Assert.Equal((map.Width, map.Height), (image.Width, image.Height));
    }

    [Theory]
    [MemberData(nameof(Renditions))]
    public void HdrIsCloseToLibultrahdr(string name, double boost)
    {
        using Raster raster = Raster.Load(Path.Combine(Images, name), options: new ReaderOptions { Hdr = new HdrDecode { Headroom = boost } });
        Raster<RgbaF16> hdr = Assert.IsType<Raster<RgbaF16>>(raster);
        (int width, int height, ushort[] want) = Reference(name, boost);
        Assert.Equal((width, height), (hdr.Width, hdr.Height));

        // libultrahdr renders in the HDR intent's gamut, which the encoder may set apart from the base image's; Egami keeps the
        // base image's primaries, so compare in libultrahdr's.
        ColorPrimaries target = Manifest().GetProperty(name).GetProperty("outputGamut").GetInt32() switch
        {
            0 => ColorPrimaries.Rec709,
            1 => ColorPrimaries.DisplayP3,
            _ => ColorPrimaries.Rec2020,
        };
        Matrix3d m = Convert(hdr.Metadata.Primaries ?? ColorPrimaries.Rec709, target);
        double sum = 0, worst = 0;
        int count = 0, far = 0;
        for (int y = 0; y < height; y++)
        {
            Span<RgbaF16> row = hdr.Row(y);
            for (int x = 0; x < width; x++)
            {
                RgbaF16 p = row[x];
                float[] mine = new float[3];
                for (int c = 0; c < 3; c++)
                {
                    mine[c] = (float)((m[c, 0] * (float)p.R) + (m[c, 1] * (float)p.G) + (m[c, 2] * (float)p.B));
                }

                for (int c = 0; c < 3; c++)
                {
                    float w = (float)BitConverter.UInt16BitsToHalf(want[(((y * width) + x) * 4) + c]);

                    // Relative to the brighter of the two, with an SDR floor so near-black noise does not dominate.
                    double error = Math.Abs(mine[c] - w) / Math.Max(Math.Max(mine[c], w), 0.05);
                    sum += error;
                    worst = Math.Max(worst, error);
                    far += error > 0.1 ? 1 : 0;
                    count++;
                }
            }
        }

        double mean = sum / count;
        output.WriteLine($"{name} at {boost}: mean relative error {mean:P2}, worst {worst:P1}, {far} of {count} samples beyond 10%");
        // A map smaller than the image is interpolated: libultrahdr weighs the four nearest map pixels by inverse squared
        // distance, Egami bilinearly, and at a large boost the two differ by more than rounding.
        bool scaled = hdr.Width != Manifest().GetProperty(name).GetProperty("gainmapWidth").GetInt32();
        Assert.True(mean < (scaled ? 0.06 : 0.015), $"mean relative error {mean:P2}");
        Assert.True(far < count / (scaled ? 5 : 40), $"{far} samples beyond 10%");
    }

    [Fact]
    public void ApplyOnARasterGivesTheSameAsTheHdrLoad()
    {
        string path = Path.Combine(Images, "uhdr-default.jpg");
        using Raster sdr = Raster.Load(path);
        using Raster<RgbaF16> applied = sdr.Metadata.GainMap!.Apply(sdr, 4);
        using Raster<RgbaF16> loaded = Raster.Load<RgbaF16>(path, options: new ReaderOptions { Hdr = new HdrDecode { Headroom = 4 } });
        for (int y = 0; y < sdr.Height; y++)
        {
            Assert.True(MemoryMarshal.AsBytes(applied.Row(y)).SequenceEqual(MemoryMarshal.AsBytes(loaded.Row(y))));
        }

        Assert.Equal(applied.Metadata.Hdr, loaded.Metadata.Hdr);
        Assert.True(loaded.Metadata.Hdr!.MaxContentLightLevel > 203);
    }

    [Fact]
    public void HeadroomOneGivesTheSdrImageInLinearLightInItsOwnPrimaries()
    {
        string path = Path.Combine(Images, "uhdr-default.jpg");
        using Raster<Rgb8> sdr = Raster.Load<Rgb8>(path);
        using Raster<RgbaF32> hdr = Raster.Load<RgbaF32>(path, options: new ReaderOptions { Hdr = new HdrDecode { Headroom = 1 } });
        for (int y = 0; y < sdr.Height; y += 7)
        {
            for (int x = 0; x < sdr.Width; x += 5)
            {
                Assert.Equal(TransferFunction.Srgb.ToLinear(sdr.Row(y)[x].G / 255.0), hdr.Row(y)[x].G, 0.002);
            }
        }
    }

    [Fact]
    public void PlainJpegsHaveNoMap()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-420.jpg");
        using Raster raster = Raster.Load(path);
        Assert.Null(raster.Metadata.GainMap);
        using Raster hdr = Raster.Load(path, options: new ReaderOptions { Hdr = new HdrDecode() });
        Assert.Equal(PixelChannelType.Unorm8, hdr.Layout.ChannelType);
    }

    [Fact]
    public void ADamagedMapIsDroppedWithAWarning()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "uhdr-default.jpg"));
        int iso = file.AsSpan().LastIndexOf("urn:iso:std:iso:ts:21496:-1\0"u8);
        file[iso + 28] = 9;
        using Raster raster = Raster.Load(file);
        Assert.Null(raster.Metadata.GainMap);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Jpeg.GainMapDropped");
    }

    [Fact]
    public void RegionAndHdrDoNotMix()
    {
        Assert.Throws<NotSupportedException>(() => Raster.Load(Path.Combine(Images, "uhdr-default.jpg"), options: new ReaderOptions { Hdr = new HdrDecode(), Region = new Rect(0, 0, 8, 8) }).Dispose());
    }

    /// <summary>The linear RGB to RGB matrix between two sets of primaries, adapting the white with Bradford.</summary>
    private static Matrix3d Convert(ColorPrimaries from, ColorPrimaries to) => to.ToXyz().Inverse() * Matrix3d.Bradford(from.White.ToXyz(), to.White.ToXyz()) * from.ToXyz();

    private static JsonElement Manifest()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return document.RootElement.GetProperty("files").Clone();
    }

    private static (int Width, int Height, ushort[] Pixels) Reference(string name, double boost)
    {
        string path = Path.Combine(References, $"{name}.{boost.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}.raw.gz");
        using FileStream file = File.OpenRead(path);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        byte[] data = bytes.ToArray();
        return (BitConverter.ToInt32(data, 0), BitConverter.ToInt32(data, 4), MemoryMarshal.Cast<byte, ushort>(data.AsSpan(8)).ToArray());
    }
}
