using Egami.Colors;
using Egami.Formats;
using Egami.Metadata;
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
    public void OmitSrgb()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4);
        raster.Metadata.IccProfile = ColorProfile.Srgb.ToIcc();
        using MemoryStream output = new();

        #region omit-srgb
        // Leave out an embedded profile that only says sRGB, which is what a file without one means anyway.
        raster.Save(output, "png", new WriterOptions { OmitSrgbProfile = true });
        #endregion

        Assert.True(Raster.Load(output.ToArray()).Metadata.IccProfile.IsEmpty);
    }

    [Fact]
    public void ToneMap()
    {
        using Raster<RgbaF32> hdr = Raster.Create<RgbaF32>(2, 2, new RgbaF32(0.5f, 0.5f, 0.5f, 1));
        hdr.Metadata.CodePoints = ColorCodePoints.Rec2100Pq;

        #region tone-map
        // HDR to SDR tone maps by default (BT.2390, source peak from the file's mDCV/cLLI, else 1,000 cd/m²).
        using Raster<Rgb8> web = hdr.ConvertColor<Rgb8>(ColorProfile.Srgb);

        // Or choose the curve and peak; Clip turns tone mapping off.
        using Raster<Rgb8> soft = hdr.ConvertColor<Rgb8>(ColorProfile.Srgb, new ColorTransformOptions { ToneMapping = ToneMapping.Reinhard, SourcePeakNits = 4000 });

        // Linear HDR pixels (1.0 = reference white, 203 cd/m²) can be tone mapped in place.
        using Raster<RgbaF32> linear = hdr.ConvertColor<RgbaF32>(ColorProfile.LinearSrgb, new ColorTransformOptions { ToneMapping = ToneMapping.Clip });
        linear.Apply(ops => ops.ToneMap(ToneMapping.Bt2390, sourcePeakNits: 1000));
        #endregion

        Assert.Equal(web.Width, soft.Width);
    }

    [Fact]
    public void ColorValues()
    {
        #region color-values
        // Color values in the models apps ask for, from and to pixels, and between each other.
        Rgba8 orange = new(240, 128, 32, 255);
        HslColor hsl = HslColor.From(orange);                          // hue 28°, saturation 0.87, lightness 0.53
        LabColor lab = ColorConvert.Convert<HslColor, LabColor>(hsl);  // through XYZ
        OklchColor oklch = OklchColor.From(orange);
        YCbCrColor jpeg = YCbCrColor.From(orange);                     // BT.601 full range, as JPEG
        YCbCrColor video = YCbCrColor.From(orange, YuvMatrix.Bt709, YuvRange.Limited);

        // Color difference: CIEDE2000 on Lab, distance on Oklab.
        double deltaE = lab.DeltaE2000(LabColor.From(new Rgba8(235, 130, 40, 255)));

        // Whole rows at once, from any pixel type and encoding.
        Rgba8[] pixels = [orange, new(10, 20, 30, 255)];
        OklabColor[] row = new OklabColor[pixels.Length];
        ColorConvert.FromPixels<Rgba8, OklabColor>(pixels, row);
        ColorConvert.FromPixels<Rgba8, OklabColor>(pixels, row, ColorProfile.DisplayP3);   // the same pixels read as Display P3
        #endregion

        Assert.True(deltaE < 3 && oklch.H > 0 && video.Y < jpeg.Y + 1);
    }

    [Fact]
    public void CmykInk()
    {
        #region cmyk-ink
        // The inks a press would use for a color, and the color it would print, through the press's CMYK profile: here the
        // built-in FOGRA39 (coated offset paper); ColorProfile.FromIcc reads any other.
        ColorProfile print = ColorProfile.Fogra39;
        CmykColor inks = CmykColor.From(new Rgba8(200, 60, 30, 255), print, new ColorTransformOptions { Intent = RenderingIntent.RelativeColorimetric });
        Rgba8 proof = inks.ToRgba8(print);
        #endregion

        Assert.True(inks.M > 0.5f && proof.R > 150);
    }

    [Fact]
    public void Inks()
    {
        #region inks
        // The inks of a page: FOGRA39's process inks, a spot colour with how it looks, and a varnish that never shows.
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39)
            .WithSpot("PANTONE 186 C", InkAppearance.Cmyk(0, 1, 0.81f, 0.04f))
            .WithSpot("Varnish", null, ColorantKind.Technical);

        // Colours as inks: process CMYK, the spot at 60%, and the spot over 30% black (a DeviceN colour).
        InkColor text = InkColor.Cmyk(0, 0, 0, 1);
        InkColor logo = InkColor.Spot(inks[4], 0.6f);
        InkColor shadow = InkColor.Mix(new InkTint(Colorant.Black, 0.3f), new InkTint(inks[4], 1));
        InkAppearance? onScreen = shadow.Appearance;   // CMYK 0, 1, 0.81, 0.328: the spot's look printed over the black
        #endregion

        Assert.True(text.IsCmyk && logo.TintOf("PANTONE 186 C") == 0.6f && onScreen?.Values.W > 0.3f && inks.Count == 6);
    }

    [Fact]
    public void InkPlates()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", InkAppearance.Cmyk(0, 1, 0.81f, 0.04f));

        #region ink-raster
        // A page as plates: CMYK and the spot, 8 bits each, plus alpha. Rows are written in place; a plate is also a gray raster.
        using InkRaster page = new(inks, 200, 100, InkDepth.Unorm8);
        page.Plate("PANTONE 186 C")!.Row8(10).Fill(255);              // a full-tint line of the spot
        page.Alpha.Row8(10).Fill(255);
        using Raster<Gray8> spot = page.Plate("PANTONE 186 C")!.AsRaster<Gray8>();   // no copy: 0 = no ink, 255 = full ink
        #endregion

        Assert.Equal(255, spot.Frames[0].Plane.Row(10)[0].L);
    }

    [Fact]
    public void InkTiff()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", InkAppearance.Cmyk(0, 1, 0.81f, 0.04f));
        using InkRaster page = new(inks, 200, 100);
        using MemoryStream file = new();

        #region ink-tiff
        // One separated TIFF with every plate and the ink names, Deflate by default; it loads back plate for plate.
        page.Save(file, new InkTiffOptions { Compression = Egami.Formats.Tiff.TiffCompression.Lzw });
        file.Position = 0;
        using InkRaster loaded = InkRaster.Load(file);
        #endregion

        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black", "PANTONE 186 C"], loaded.Plates.Select(p => p.Name));
    }

    [Fact]
    public void InkProof()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", InkAppearance.Cmyk(0, 1, 0.81f, 0.04f));
        using InkRaster page = new(inks, 200, 100);
        page.Plate("PANTONE 186 C")!.Row8(10).Fill(255);

        #region ink-proof
        // How the plates look on screen, and whether they print within the press's total ink limit.
        using Raster<Rgba8> proof = page.Proof<Rgba8>(new ProofOptions { SimulatePaper = true });
        InkCoverage coverage = page.MeasureCoverage(limit: 3.0f);
        bool tooMuchInk = coverage.MaxTotal > coverage.Limit;      // where: coverage.MaxAt; how often: coverage.ShareOver
        #endregion

        Assert.False(tooMuchInk);
        Assert.True(proof.Row(10)[0].G < 100);
    }

    [Fact]
    public void HdrInfo()
    {
        using Raster<Rgb16> raster = Raster.Create<Rgb16>(2, 2);
        raster.Metadata.CodePoints = ColorCodePoints.Rec2100Pq;
        raster.Metadata.Hdr = new HdrMetadata { MasteringMaxLuminance = 1000, MaxContentLightLevel = 800 };

        #region hdr-metadata
        // What an HDR file says about its mastering display and light levels, alongside its code points.
        RasterMetadata metadata = raster.Metadata;
        double? peak = metadata.Hdr?.MaxContentLightLevel ?? metadata.Hdr?.MasteringMaxLuminance;
        bool pq = metadata.GetColorProfile().IsHdr;
        #endregion

        Assert.True(pq && peak == 800);
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
