using Egami.Colors;

namespace Egami.DocSamples;

public class ColorManagement
{
    [Fact]
    public void Profiles()
    {
        #region profiles
        // A ColorProfile says what a raster's numbers mean. The built-ins cover the common encodings...
        ColorProfile web = ColorProfile.Srgb;
        ColorProfile wide = ColorProfile.DisplayP3;
        ColorProfile hdr = ColorProfile.Rec2100Pq;

        // ...and Create makes any other RGB encoding from primaries and a transfer function.
        ColorProfile camera = ColorProfile.Create(ColorPrimaries.AdobeRgb, TransferFunction.Gamma(2.2), "Camera RGB");
        Console.WriteLine($"{camera.Name}: {camera.Model}, HDR {camera.IsHdr}, same as Adobe RGB: {camera.IsEquivalentTo(ColorProfile.AdobeRgb)}");
        #endregion

        Assert.True(hdr.IsHdr && !web.IsHdr && wide.Model == ColorModel.Rgb);
    }

    [Fact]
    public void Primaries()
    {
        #region primaries
        // Primaries are the chromaticities of red, green, blue and white.
        ColorPrimaries p3 = ColorPrimaries.DisplayP3;
        Chromaticity red = p3.Red;
        Console.WriteLine($"P3 red is x {red.X}, y {red.Y}; white is D65: {p3.White == Chromaticity.D65}");
        ColorPrimaries custom = new(new(0.68, 0.32), new(0.265, 0.69), new(0.15, 0.06), Chromaticity.D65);
        #endregion

        Assert.True(custom.IsCloseTo(p3));
    }

    [Fact]
    public void Transfer()
    {
        #region transfer
        // A transfer function decodes stored values to linear light and back; HDR curves put reference white at 1.0.
        double linear = TransferFunction.Srgb.ToLinear(0.5);           // 0.214
        double nits = TransferFunction.Pq.ToLinear(0.75) * 203;         // a 75% PQ signal is about 983 cd/m²
        float[] row = [0f, 0.25f, 0.5f, 1f];
        TransferFunction.Srgb.ToLinear(row, row);                        // a whole row at once, in place
        #endregion

        Assert.True(linear > 0.21 && nits > 900 && row[3] == 1f);
    }

    [Fact]
    public void CodePoints()
    {
        #region code-points
        // H.273 code points, as PNG cICP, AVIF and JPEG XL store them, name an encoding in four bytes.
        ColorCodePoints pq = ColorCodePoints.Rec2100Pq;
        ColorProfile? profile = ColorProfile.FromCodePoints(pq);              // Rec. 2100 PQ
        ColorCodePoints? back = ColorProfile.DisplayP3.ToCodePoints();         // 12, 13, 0, full range
        #endregion

        Assert.Same(ColorProfile.Rec2100Pq, profile);
        Assert.Equal((byte)12, back!.Value.Primaries);
    }
}
