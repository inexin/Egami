using System.Text.Json;

using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.3 task 12: XYB decoded into the file's own color space (enumerated or ICC) with the profile on the raster, and
/// linear output. Equality with djxl for each enumerated space is <see cref="JxlDecodeTests.CorpusDecodesLikeDjxl"/>; djxl
/// gives XYB files with an ICC profile in linear sRGB, so those are compared there.
/// </summary>
public class JxlColorTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jxl");

    public static TheoryData<string> IccConformanceCases => ["grayscale", "grayscale_5", "patches", "progressive"];

    [Theory]
    [InlineData("duck-d1.jxl", "sRGB")]
    [InlineData("duck-linear-d1.jxl", "Linear sRGB")]
    public void BuiltInSpacesAreTagged(string file, string name)
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, file));
        Assert.True(raster.Metadata.IccProfile.IsEmpty);
        Assert.Equal(name, raster.Metadata.GetColorProfile().Name);
    }

    [Fact]
    public void EnumeratedSpaceGetsAProfile()
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, "duck-p3-d1.jxl"));
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, raster.Metadata.ColorSpace);
        Assert.True(raster.Metadata.GetColorProfile().IsCloseTo(ColorProfile.DisplayP3, 1e-3));
    }

    [Fact]
    public void FileIccProfileIsKept()
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, "camera-nikon-d300-rec2020-d1.jxl"));
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, raster.Metadata.ColorSpace);
        Assert.False(raster.Metadata.IccProfile.IsEmpty);
        Assert.True(raster.Metadata.GetColorProfile().Primaries!.IsCloseTo(ColorPrimaries.Rec2020, 0.002));
    }

    [Theory]
    [InlineData("duck-d1.jxl")]
    [InlineData("duck-p3-d1.jxl")]
    [InlineData("duck-gray-d1.jxl")]
    [InlineData("duck-lossless-e1.jxl")]
    public void LinearOutputIsTheSameColorsInLinearLight(string file)
    {
        string path = Path.Combine(Corpus, file);
        using Raster encoded = Raster.Load(path);
        using Raster linear = Raster.Load(path, options: new JxlReaderOptions { LinearOutput = true });
        Assert.Equal(PixelChannelType.Float32, linear.Layout.ChannelType);
        ColorProfile own = encoded.Metadata.GetColorProfile();
        ColorProfile profile = linear.Metadata.GetColorProfile();
        Assert.True(profile.Transfer!.IsLinear);
        if (own.Primaries is ColorPrimaries primaries)
        {
            Assert.True(profile.Primaries!.IsCloseTo(primaries, 1e-3));
        }

        // The encoded output is 8-bit, so its linear values are within a rounding step of the linear output.
        using Raster<RgbaF32> expected = encoded.As<RgbaF32>();
        using Raster<RgbaF32> actual = linear.As<RgbaF32>();
        ColorTransform toLinear = ColorTransform.Create(own, profile);
        int y = encoded.Height / 2;
        RgbaF32[] row = expected.Row(y).ToArray();
        toLinear.Apply<RgbaF32>(row);
        for (int x = 0; x < row.Length; x += 3)
        {
            RgbaF32 a = actual.Row(y)[x];
            Assert.Equal(row[x].R, Math.Clamp(a.R, 0, 1), 0.006);
            Assert.Equal(row[x].G, Math.Clamp(a.G, 0, 1), 0.006);
            Assert.Equal(row[x].B, Math.Clamp(a.B, 0, 1), 0.006);
        }
    }

    [Fact]
    public void ProbeReportsFloatForLinearOutput()
    {
        RasterInfo info = Raster.Probe(Path.Combine(Corpus, "duck-d1.jxl"), options: new JxlReaderOptions { LinearOutput = true });
        Assert.Equal(RgbF32.Layout, info.Layout);
    }

    [Theory]
    [MemberData(nameof(IccConformanceCases))]
    public void XybIntoIccProfilesEqualsTheReferenceInLinearSrgb(string name)
    {
        if (JxlReferenceTests.Cache() is not string cache)
        {
            throw LocalCorpus.Skip("jxl");
        }

        string folder = Path.Combine(cache, "conformance", "testcases", name);
        using Raster raster = Raster.Load(Path.Combine(folder, "input.jxl"), options: new JxlReaderOptions { LinearOutput = true });
        Assert.True(((JxlDetails)raster.Metadata.FormatDetails!).IsXybEncoded);
        using Raster encoded = Raster.Load(Path.Combine(folder, "input.jxl"));
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, encoded.Metadata.ColorSpace);

        // The reference is linear sRGB (gray: linear gray); bring the linear output there from the ICC profile's primaries.
        ColorProfile profile = raster.Metadata.GetColorProfile();
        bool gray = profile.Model == ColorModel.Gray;
        using Raster<RgbaF32> floats = raster.As<RgbaF32>();
        ColorTransform toSrgb = ColorTransform.Create(profile, gray ? ColorProfile.GrayLinear : ColorProfile.LinearSrgb);
        JxlReferenceTests.Npy reference = JxlReferenceTests.Npy.Read(Path.Combine(folder, "reference_image.npy"));
        int channels = reference.Shape[3];
        using JsonDocument spec = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "test.json")));
        double limit = spec.RootElement.GetProperty("frames")[0].GetProperty("peak_error").GetDouble();
        double peak = 0;
        for (int y = 0; y < floats.Height; y += 5)
        {
            RgbaF32[] row = floats.Row(y).ToArray();
            if (!gray)
            {
                toSrgb.Apply<RgbaF32>(row);
            }

            for (int x = 0; x < row.Length; x += 5)
            {
                int i = ((y * floats.Width) + x) * channels;
                peak = Math.Max(peak, Math.Abs(row[x].R - reference.Data[i]));
                if (!gray)
                {
                    peak = Math.Max(peak, Math.Abs(row[x].G - reference.Data[i + 1]));
                    peak = Math.Max(peak, Math.Abs(row[x].B - reference.Data[i + 2]));
                }
            }
        }

        Assert.InRange(peak, 0, Math.Max(limit, 0.004));
    }
}
