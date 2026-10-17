using Egami.Colors;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.7 task 2: the lossy encoder's linear input and forward XYB. Each raster goes to linear sRGB through the 2.1.2
/// pipeline and to XYB, and 2.9.3's XYB to linear sRGB gives the same light back within float rounding: sRGB, wide gamut
/// (colors outside sRGB stay outside 0 to 1), gray, CMYK and an HDR intensity target.
/// </summary>
public class JxlXybTests
{
    private const float Tolerance = 2e-5f;

    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    [Fact]
    public void XybThenTheDecodersInverseGivesTheLightBack()
    {
        Random random = new(29);
        int count = 4099;
        float[][] linear = [new float[count], new float[count], new float[count]];
        for (int i = 0; i < count; i++)
        {
            // In gamut and a little brighter than white. Values below 0 come from wide-gamut colors, whose cone responses stay
            // positive (WideGamutColorsStayOutsideSrgb); arbitrary negative values are not colors and clamp, as in libjxl.
            for (int c = 0; c < 3; c++)
            {
                linear[c][i] = (float)(random.NextDouble() * 1.2);
            }
        }

        RoundTrip(linear, count, 255);
    }

    [Fact]
    public void HdrIntensityTargetRoundTrips()
    {
        Random random = new(31);
        int count = 1000;
        float[][] linear = [new float[count], new float[count], new float[count]];
        for (int i = 0; i < count; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                linear[c][i] = (float)(random.NextDouble() * 4);
            }
        }

        RoundTrip(linear, count, 1000);
    }

    [Fact]
    public void SrgbRasterRoundTrips()
    {
        using Raster raster = Raster.Load(Path.Combine(Images, "ops", "duck.png"));
        JxlLinearImage linear = JxlXyb.ToLinear(raster);
        Assert.False(linear.IsGray);
        Assert.Null(linear.Black);
        using Raster<RgbF32> expected = raster.ConvertColor<RgbF32>(ColorProfile.LinearSrgb);
        Assert.Equal(expected.Row(7)[11].G, linear.Color[1][(7 * raster.Width) + 11]);
        RoundTrip(linear.Color, raster.Width * raster.Height, 255);
    }

    [Fact]
    public void WideGamutColorsStayOutsideSrgb()
    {
        using Raster<RgbF32> p3 = Raster.Create<RgbF32>(4, 1);
        p3.Row(0)[0] = new RgbF32(0, 1, 0);
        p3.Row(0)[1] = new RgbF32(1, 0, 0);
        p3.Row(0)[2] = new RgbF32(0.5f, 0.5f, 0.5f);
        p3.Row(0)[3] = new RgbF32(0, 0, 1);
        p3.Metadata.IccProfile = ColorProfile.DisplayP3.ToIcc();
        JxlLinearImage linear = JxlXyb.ToLinear(p3);
        Assert.True(linear.Color[0][0] < -0.1f, $"P3 green's red in linear sRGB: {linear.Color[0][0]}");
        Assert.True(linear.Color[1][1] < -0.01f, $"P3 red's green in linear sRGB: {linear.Color[1][1]}");
        RoundTrip(linear.Color, 4, 255);
    }

    [Fact]
    public void GrayGoesInAllThreePlanesWithNoX()
    {
        using Raster<Gray8> gray = Raster.Create<Gray8>(256, 1);
        for (int i = 0; i < 256; i++)
        {
            gray.Row(0)[i] = new Gray8((byte)i);
        }

        JxlLinearImage linear = JxlXyb.ToLinear(gray);
        Assert.True(linear.IsGray);
        Assert.Equal(linear.Color[0], linear.Color[2]);
        Assert.Equal(TransferFunction.Srgb.ToLinear(200 / 255.0), linear.Color[1][200], 1e-5);
        float[][] xyb = Copy(linear.Color, 256);
        JxlXyb.LinearToXyb(xyb, 0, 256, 255);
        Assert.All(xyb[0], x => Assert.InRange(x, -1e-6f, 1e-6f));
        RoundTrip(linear.Color, 256, 255);
    }

    [Fact]
    public void CmykKeepsItsBlackChannel()
    {
        using Raster<Cmyk8> inks = Raster.Create<Cmyk8>(3, 1);
        inks.Row(0)[0] = new Cmyk8(0, 0, 0, 0);
        inks.Row(0)[1] = new Cmyk8(0, 0, 0, 255);
        inks.Row(0)[2] = new Cmyk8(200, 30, 10, 51);
        inks.Metadata.IccProfile = ColorProfile.Fogra39.ToIcc();
        JxlLinearImage linear = JxlXyb.ToLinear(inks);
        Assert.Equal([1f, 0f, 0.8f], linear.Black!);
        Assert.True(linear.Color[1][0] > 0.7f, "paper white is light");
        Assert.True(linear.Color[1][1] < 0.05f, "full black is dark");
        RoundTrip(linear.Color, 3, 255);
    }

    [Fact]
    public void EightBitSrgbTableGivesTheTransformsValues()
    {
        // The lossy writer's table for 8-bit sRGB rows against the 2.1.2 transform on the same pixels: the same but for the
        // transform's matrix, whose cross terms leave values near 1e-17 where the table has 0.
        Random random = new(9);
        Rgb8[] row = [.. Enumerable.Range(0, 333).Select(_ => new Rgb8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)))];
        JxlLinearRows rows = new(row.Length, 1, ColorProfile.Srgb, Rgb8.Layout);
        rows.Add<Rgb8>(row, 0);
        RgbF32[] expected = new RgbF32[row.Length];
        ColorTransform.Create(ColorProfile.Srgb, ColorProfile.LinearSrgb).Apply<Rgb8, RgbF32>(row, expected);
        for (int x = 0; x < row.Length; x++)
        {
            Assert.Equal(expected[x].R, rows.Image.Color[0][x], 1e-6f);
            Assert.Equal(expected[x].G, rows.Image.Color[1][x], 1e-6f);
            Assert.Equal(expected[x].B, rows.Image.Color[2][x], 1e-6f);
        }
    }

    [Fact]
    public void VectorAndScalarCubeRootsAgree()
    {
        int count = 64;
        float[][] a = [new float[count], new float[count], new float[count]];
        Random random = new(5);
        for (int i = 0; i < count; i++)
        {
            a[0][i] = (float)random.NextDouble();
            a[1][i] = (float)random.NextDouble();
            a[2][i] = (float)random.NextDouble();
        }

        float[][] whole = Copy(a, count);
        JxlXyb.LinearToXyb(whole, 0, count, 255);
        for (int i = 0; i < count; i++)
        {
            // One sample at a time takes the scalar path.
            float[][] one = Copy(a, count);
            JxlXyb.LinearToXyb(one, i, i + 1, 255);
            Assert.Equal(whole[0][i], one[0][i]);
            Assert.Equal(whole[1][i], one[1][i]);
            Assert.Equal(whole[2][i], one[2][i]);
        }

        foreach (float x in new[] { 1e-30f, 1e-6f, 0.0037930732f, 0.5f, 1f, 7f, 1e4f })
        {
            Assert.Equal(MathF.Cbrt(x), JxlXyb.CbrtScalar(x), MathF.Cbrt(x) * 1e-6f);
        }
    }

    private static void RoundTrip(float[][] linear, int count, float intensityTarget)
    {
        float[][] planes = Copy(linear, count);
        JxlXyb.LinearToXyb(planes, 0, count, intensityTarget);
        JxlColor.XybToLinear(planes, count, new JxlImageHeader { ToneMapping = new JxlToneMapping(intensityTarget) });
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < count; i++)
            {
                float want = linear[c][i];
                float tolerance = Tolerance * Math.Max(1, Math.Abs(want));
                Assert.True(Math.Abs(planes[c][i] - want) <= tolerance, $"plane {c} sample {i}: {planes[c][i]} for {want}");
            }
        }
    }

    private static float[][] Copy(float[][] planes, int count) => [planes[0][..count], planes[1][..count], planes[2][..count]];
}
