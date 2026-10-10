using Egami.Colors;
using Egami.Formats;
using Egami.Pixels;

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
    public void Icc()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "DisplayP3.icc"));

        #region icc
        // IccData reads a profile's header and tags; it never throws, and says what was wrong with a damaged one.
        IccData icc = IccData.Parse(bytes);
        Console.WriteLine($"{icc.Description}: version {icc.Version}, {icc.ProfileClass}, {icc.DataSpace} to {icc.ConnectionSpace}, damaged: {icc.IsDamaged}");
        foreach (IccTag tag in icc.Tags)
        {
            Console.WriteLine($"  {tag.Signature} ({tag.Type}), {tag.Data.Length} bytes");
        }

        // ColorProfile.FromIcc makes an encoding from it: a matrix profile gets its primaries and curve, so an ICC profile written
        // for Display P3 is equivalent to the built-in.
        ColorProfile profile = ColorProfile.FromIcc(icc);
        bool isP3 = profile.IsEquivalentTo(ColorProfile.DisplayP3);
        #endregion

        Assert.True(isP3);
        Assert.Same(icc, profile.Icc);
    }

    [Fact]
    public void Convert()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4, new Rgba8(200, 80, 40, 255));
        raster.Metadata.IccProfile = ColorProfile.DisplayP3.ToIcc();

        #region convert
        // What the pixels mean, from the file's ICC profile, tags and gamma; sRGB when it said nothing.
        ColorProfile stored = raster.Metadata.GetColorProfile();

        // Convert in place to sRGB for the web: the metadata then says sRGB, so no profile is written.
        raster.Apply(ops => ops.ConvertColor(ColorProfile.Srgb));
        #endregion

        Assert.True(stored.IsEquivalentTo(ColorProfile.DisplayP3) && raster.Metadata.IccProfile.IsEmpty);
    }

    [Fact]
    public void ConvertType()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "images", "duck-cmyk.jpg");

        #region convert-type
        // A CMYK image becomes RGB through its profile (or the plain formula if it has none), as a new raster.
        using Raster<Cmyk8> print = Raster.Load<Cmyk8>(path);
        using Raster<Rgb8> screen = print.ConvertColor<Rgb8>(ColorProfile.Srgb, new ColorTransformOptions { Intent = RenderingIntent.RelativeColorimetric, BlackPointCompensation = true });
        #endregion

        Assert.Equal(print.Width, screen.Width);
    }

    [Fact]
    public void ConvertOnLoad()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "images", "duck-cmyk.jpg");

        #region convert-on-load
        // Load straight into sRGB: the same pixels as loading and then calling ConvertColor.
        using Raster web = Raster.Load(path, options: new ReaderOptions { ConvertColorTo = ColorProfile.Srgb });
        #endregion

        Assert.Equal(PixelChannelOrder.Rgb, web.Layout.ChannelOrder);
    }

    [Fact]
    public void Transform()
    {
        #region transform
        // A transform is compiled once (and cached), then applied to spans of any pixel type, in place or into another type.
        ColorTransform transform = ColorTransform.Create(ColorProfile.DisplayP3, ColorProfile.Srgb, new ColorTransformOptions { Intent = RenderingIntent.Perceptual });
        Rgba8[] pixels = [new(255, 0, 0, 255), new(0, 128, 255, 128)];
        transform.Apply<Rgba8>(pixels);
        RgbaF32[] wide = new RgbaF32[pixels.Length];
        transform.Apply<Rgba8, RgbaF32>(pixels, wide);   // float types keep values outside 0..1
        #endregion

        Assert.Equal(128, pixels[1].A);
    }

    [Fact]
    public void ToIcc()
    {
        #region to-icc
        // Any RGB or gray encoding as a small ICC profile, for files and other software.
        byte[] p3 = ColorProfile.DisplayP3.ToIcc();
        bool same = ColorProfile.FromIcc(p3).IsEquivalentTo(ColorProfile.DisplayP3);
        #endregion

        Assert.True(same);
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
