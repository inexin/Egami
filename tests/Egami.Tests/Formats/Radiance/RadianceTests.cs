using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Radiance;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Radiance;

/// <summary>
/// Radiance pictures (Phase 2.9.1 task 2): every file of tests/images/radiance decodes to the floats of
/// tests/reference/radiance bit for bit (stb_image's, or tools/hdrref/hdrcorpus.py's decode where stb_image does not read the
/// file), and the writer round-trips them.
/// </summary>
public class RadianceTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "radiance");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "radiance");

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Images, "*.hdr").Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Files))]
    public void DecodesToTheReferenceFloats(string name)
    {
        using Raster raster = Raster.Load(Path.Combine(Images, name));
        Raster<RgbF32> rgb = Assert.IsType<Raster<RgbF32>>(raster);
        (int width, int height, float[] expected) = ReadReference(name);
        Assert.Equal((width, height), (rgb.Width, rgb.Height));
        RadianceDetails details = (RadianceDetails)rgb.Metadata.FormatDetails!;
        if (details.PixelFormat == RadiancePixelFormat.Xyze)
        {
            // The reference holds X, Y and Z; Egami gives RGB in Radiance's standard primaries, which the metadata names.
            Assert.Equal(RadianceReader.StandardPrimaries, rgb.Metadata.Primaries);
            double[,] toXyz = ToXyz(RadianceReader.StandardPrimaries);
            for (int y = 0; y < height; y++)
            {
                Span<RgbF32> row = rgb.Row(y);
                for (int x = 0; x < width; x++)
                {
                    int i = ((y * width) + x) * 3;
                    RgbF32 p = row[x];
                    for (int c = 0; c < 3; c++)
                    {
                        double back = (toXyz[c, 0] * p.R) + (toXyz[c, 1] * p.G) + (toXyz[c, 2] * p.B);
                        Assert.True(Math.Abs(back - expected[i + c]) <= 1e-5 * Math.Max(1, Math.Abs(expected[i + c])), $"({x}, {y}) channel {c}: {back} for {expected[i + c]}");
                    }
                }
            }

            return;
        }

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<float> row = MemoryMarshal.Cast<RgbF32, float>(rgb.Row(y));
            ReadOnlySpan<float> want = expected.AsSpan(y * width * 3, width * 3);
            if (!row.SequenceEqual(want))
            {
                int x = 0;
                while (BitConverter.SingleToInt32Bits(row[x]) == BitConverter.SingleToInt32Bits(want[x]))
                {
                    x++;
                }

                Assert.Fail($"Row {y}, sample {x}: {row[x]} for {want[x]}.");
            }
        }
    }

    [Fact]
    public void TheReferencesSayWhereTheyCameFrom()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        Dictionary<string, string> files = manifest.RootElement.GetProperty("files").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Assert.Equal(Files().Count, files.Count);
        Assert.Equal("stb_image", files["desk-rle.hdr"]);
        Assert.Equal("stb_image", files["desk-magick.hdr"]);
        Assert.True(files.Values.Count(v => v == "stb_image") >= 10);
    }

    [Fact]
    public void ReadsTheHeaderLines()
    {
        using Raster raster = Raster.Load(Path.Combine(Images, "desk-header.hdr"));
        RadianceDetails details = (RadianceDetails)raster.Metadata.FormatDetails!;
        Assert.Equal(RadiancePixelFormat.Rgbe, details.PixelFormat);
        Assert.Equal(2.0, details.Exposure);
        Assert.Equal((1.0, 0.9, 1.1), details.ColorCorrection);
        Assert.Equal(1.0, details.PixelAspectRatio);
        Assert.Equal("hdrcorpus", details.Software);
        Assert.Equal("-vtv -vp 0 0 0 -vd 0 1 0", details.View);
        Assert.Equal("-Y 64 +X 96", details.Orientation);
        Assert.Equal(new ColorPrimaries(new(0.680, 0.320), new(0.265, 0.690), new(0.150, 0.060), Chromaticity.D65), details.Primaries);
        Assert.Equal(details.Primaries, raster.Metadata.Primaries);
        Assert.Equal("# made by hdrcorpus.py", details.HeaderLines[0]);
        Assert.Equal(ColorSpaceHint.Linear, raster.Metadata.ColorSpace);
        Assert.Equal(TransferFunction.Linear, raster.Metadata.GetColorProfile().Transfer);
    }

    [Fact]
    public void MeasuresThePeakAsTheLightLevel()
    {
        using Raster<RgbF32> raster = Raster.Load<RgbF32>(Path.Combine(Images, "desk-rle.hdr"));
        float peak = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (RgbF32 p in raster.Row(y))
            {
                peak = Math.Max(peak, Math.Max(p.R, Math.Max(p.G, p.B)));
            }
        }

        Assert.True(peak > 1);
        Assert.Equal(peak * 203.0, raster.Metadata.Hdr!.MaxContentLightLevel);
    }

    [Fact]
    public void ProbesWithoutDecoding()
    {
        RasterInfo info = Raster.Probe(Path.Combine(Images, "orient-px-ny.hdr"));
        Assert.Equal((RadianceFormat.Info, 37, 23, RgbF32.Layout), (info.Format, info.Width, info.Height, info.Layout));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void RoundTripsBitForBit(string name)
    {
        if (name == "desk-xyze.hdr")
        {
            // XYZE pixels load converted to RGB, which RGBE holds only to 8-bit mantissas.
            return;
        }

        using Raster<RgbF32> loaded = Raster.Load<RgbF32>(Path.Combine(Images, name));
        foreach (bool compress in new[] { true, false })
        {
            using MemoryStream file = new();
            loaded.Save(file, RadianceFormat.Info, new RadianceWriterOptions { Compress = compress });
            file.Position = 0;
            using Raster<RgbF32> again = Raster.Load<RgbF32>(file);
            Assert.Equal(RadianceFormat.Info.Name, again.Metadata.FormatName);
            for (int y = 0; y < loaded.Height; y++)
            {
                Assert.True(MemoryMarshal.Cast<RgbF32, float>(loaded.Row(y)).SequenceEqual(MemoryMarshal.Cast<RgbF32, float>(again.Row(y))), $"row {y}");
            }

            // Writing what was read gives the same bytes again.
            file.Position = 0;
            byte[] first = file.ToArray();
            using MemoryStream second = new();
            again.Save(second, RadianceFormat.Info, new RadianceWriterOptions { Compress = compress });
            Assert.Equal(first, second.ToArray());
        }
    }

    [Fact]
    public void WritesTheExposureLineOnlyWhenAsked()
    {
        using Raster<RgbF32> raster = Raster.Create<RgbF32>(10, 2);
        using MemoryStream plain = new(), exposed = new();
        raster.Save(plain, RadianceFormat.Info, RadianceWriterOptions.Default);
        raster.Save(exposed, RadianceFormat.Info, new RadianceWriterOptions { Exposure = 0.25 });
        string head = Encoding.ASCII.GetString(plain.ToArray(), 0, 46);
        Assert.StartsWith("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 2 +X 10\n", head, StringComparison.Ordinal);
        Assert.Contains("EXPOSURE=0.25\n", Encoding.ASCII.GetString(exposed.ToArray()), StringComparison.Ordinal);
        exposed.Position = 0;
        using Raster again = Raster.Load(exposed);
        Assert.Equal(0.25, ((RadianceDetails)again.Metadata.FormatDetails!).Exposure);
    }

    [Fact]
    public void SavesByExtension()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-{Guid.NewGuid():N}.hdr");
        try
        {
            using Raster<RgbF32> raster = Raster.Create<RgbF32>(9, 3);
            raster.Row(1)[4] = new RgbF32(1000, 0.001f, 3);
            raster.Save(path);
            using Raster<RgbF32> again = Raster.Load<RgbF32>(path);
            RgbF32 p = again.Row(1)[4];
            Assert.Equal(1000, p.R, 1000 / 128.0);
            Assert.Equal(0, p.G);
            Assert.Equal(3, p.B, 1000 / 128.0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WritesNegativeNanAndInfinityAsSafeValues()
    {
        using Raster<RgbF32> raster = Raster.Create<RgbF32>(8, 1);
        raster.Row(0)[0] = new RgbF32(-1, 2, float.NaN);
        raster.Row(0)[1] = new RgbF32(float.PositiveInfinity, 1, 0);
        raster.Row(0)[2] = new RgbF32(1e-40f, 0, 0);
        using MemoryStream file = new();
        raster.Save(file, RadianceFormat.Info, RadianceWriterOptions.Default);
        file.Position = 0;
        using Raster<RgbF32> again = Raster.Load<RgbF32>(file);
        Assert.Equal(new RgbF32(0, 2, 0), again.Row(0)[0]);
        Assert.True(float.IsFinite(again.Row(0)[1].R) && again.Row(0)[1].R > 1e38f);
        Assert.Equal(new RgbF32((float)Math.ScaleB(4, -135), 0, 0), again.Row(0)[2]);
    }

    [Fact]
    public void LenientLoadKeepsTheScanlinesBeforeTheEnd()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "desk-flat.hdr"));
        byte[] cut = file[..(file.Length - (96 * 4 * 10) - 7)];
        using Raster<RgbF32> raster = Raster.Load<RgbF32>(cut);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Radiance.RowsMissing");
        Assert.True(raster.Row(63).ToArray().All(p => p.Equals(default(RgbF32))));
        Assert.Throws<CorruptDataException>(() => Raster.Load(cut, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Theory]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 2 +Z 3\n")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 2 -Y 3\n")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 0 +X 3\n")]
    [InlineData("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n")]
    public void RejectsBrokenHeaders(string header)
    {
        Assert.Throws<CorruptDataException>(() => Raster.Load(Encoding.ASCII.GetBytes(header)));
    }

    [Fact]
    public void RejectsUnknownPixelFormats()
    {
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=24-bit_rgb\n\n-Y 1 +X 1\n\0\0\0\0")));
    }

    [Fact]
    public void RejectsRunsPastTheScanline()
    {
        // Width 8, run-length encoded, first channel a run of 9.
        byte[] head = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 8\n");
        byte[] file = [.. head, 2, 2, 0, 8, 128 + 9, 1];
        Assert.Throws<CorruptDataException>(() => Raster.Load(file));
    }

    private static (int Width, int Height, float[] Samples) ReadReference(string name)
    {
        using FileStream file = File.OpenRead(Path.Combine(References, name + ".raw.gz"));
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using MemoryStream bytes = new();
        gzip.CopyTo(bytes);
        byte[] data = bytes.ToArray();
        int width = BitConverter.ToInt32(data, 0), height = BitConverter.ToInt32(data, 4);
        Assert.Equal(3, BitConverter.ToInt32(data, 8));
        return (width, height, MemoryMarshal.Cast<byte, float>(data.AsSpan(12)).ToArray());
    }

    private static double[,] ToXyz(ColorPrimaries p)
    {
        // Columns: each primary's XYZ, scaled so the white maps to its own XYZ.
        double[][] c = [Xyz(p.Red), Xyz(p.Green), Xyz(p.Blue)];
        double[] w = Xyz(p.White);
        double[,] m = { { c[0][0], c[1][0], c[2][0] }, { c[0][1], c[1][1], c[2][1] }, { c[0][2], c[1][2], c[2][2] } };
        double[] s = Solve(m, w);
        double[,] r = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                r[i, j] = m[i, j] * s[j];
            }
        }

        return r;

        static double[] Xyz(Chromaticity c) => [c.X / c.Y, 1, (1 - c.X - c.Y) / c.Y];
    }

    private static double[] Solve(double[,] m, double[] v)
    {
        double det = Det(m);
        double[] x = new double[3];
        for (int k = 0; k < 3; k++)
        {
            double[,] mk = (double[,])m.Clone();
            for (int i = 0; i < 3; i++)
            {
                mk[i, k] = v[i];
            }

            x[k] = Det(mk) / det;
        }

        return x;

        static double Det(double[,] a) =>
            (a[0, 0] * ((a[1, 1] * a[2, 2]) - (a[1, 2] * a[2, 1]))) - (a[0, 1] * ((a[1, 0] * a[2, 2]) - (a[1, 2] * a[2, 0]))) + (a[0, 2] * ((a[1, 0] * a[2, 1]) - (a[1, 1] * a[2, 0])));
    }

    [Fact]
    public void EightPixelsAtATimeEncodeAsOneAtATime()
    {
        // The vector path takes groups of eight in the usual range; any group with a value outside it, and the tail, go the
        // scalar way. Both must give the same bytes for every kind of value.
        Random random = new(7);
        float[] specials = [0, -0f, -1, float.NaN, float.PositiveInfinity, float.Epsilon, 1e-38f, 1e-30f, 3e38f, 1, 255.5f, 1e-3f];
        RgbF32[] pixels = new RgbF32[1001];
        for (int i = 0; i < pixels.Length; i++)
        {
            float Next() => random.Next(8) == 0 ? specials[random.Next(specials.Length)] : (float)(random.NextDouble() * Math.Pow(2, random.Next(-40, 40)));
            pixels[i] = i < 600 ? new RgbF32(Next(), Next(), Next()) : new RgbF32((float)random.NextDouble() * 4, (float)random.NextDouble(), (float)random.NextDouble());
        }

        byte[] all = new byte[pixels.Length * 4];
        RadianceWriter.ToRgbe(pixels, all);
        byte[] one = new byte[4];
        for (int i = 0; i < pixels.Length; i++)
        {
            RadianceWriter.ToRgbe(pixels.AsSpan(i, 1), one);
            Assert.Equal(one, new[] { all[i], all[pixels.Length + i], all[(2 * pixels.Length) + i], all[(3 * pixels.Length) + i] });
        }
    }
}
