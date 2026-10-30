using Egami.Colors;
using Egami.Formats;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.1.2 task 6: ColorTransform on pixel spans, ConvertColor, ConvertColor&lt;TTo&gt;, ConvertColorTo and ToIcc.</summary>
public class ColorConversionApiTests
{
    private static readonly string CmykJpeg = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-cmyk.jpg");
    private static readonly string Duck = Path.Combine(AppContext.BaseDirectory, "images", "ops", "duck.png");

    public static TheoryData<string> BuiltIns() => ["sRGB", "Linear sRGB", "Display P3", "Adobe RGB (1998)", "ProPhoto RGB", "Rec. 2020", "Rec. 2100 PQ", "Rec. 2100 HLG", "Gray sRGB", "Gray linear"];

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void WrittenProfilesReadBackAsTheBuiltIns(string name)
    {
        ColorProfile builtIn = CompiledTransformTests.Profile(name);
        byte[] icc = builtIn.ToIcc();
        IccData parsed = IccData.Parse(icc);
        Assert.False(parsed.IsDamaged, string.Join(" ", parsed.Warnings));
        Assert.Equal(builtIn.IsHdr ? new Version(4, 4, 0) : new Version(4, 3, 0), parsed.Version);
        Assert.Equal(name, parsed.Description);
        ColorProfile back = ColorProfile.FromIcc(parsed);
        Assert.True(back.IsEquivalentTo(builtIn), $"{back.Primaries} {back.White} {back.Transfer}");
        Assert.Equal(icc, builtIn.ToIcc());   // the same bytes every time
        Assert.Equal(icc, back.ToIcc());      // a profile read from ICC gives its bytes back
        if (!builtIn.IsHdr)
        {
            // Through the ICC form, colors come out as through the built-in.
            ColorPipeline a = ColorPipeline.Create(builtIn, ColorProfile.Srgb, RenderingIntent.RelativeColorimetric, false);
            ColorPipeline b = ColorPipeline.Create(back, ColorProfile.Srgb, RenderingIntent.RelativeColorimetric, false);
            double[] x = new double[3], y = new double[3];
            for (int i = 0; i < 125; i++)
            {
                double[] input = builtIn.Model == ColorModel.Gray ? [i / 124.0] : [i / 25 / 4.0, i / 5 % 5 / 4.0, i % 5 / 4.0];
                a.Evaluate(input, x);
                b.Evaluate(input, y);
                for (int c = 0; c < 3; c++)
                {
                    Assert.True(Math.Abs(x[c] - y[c]) < 2e-3, $"[{string.Join(", ", input)}] {c}: {x[c]} against {y[c]}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void Version2ProfilesReadBackAsTheBuiltIns(string name)
    {
        ColorProfile builtIn = CompiledTransformTests.Profile(name);
        byte[] icc = builtIn.ToIccV2();
        IccData parsed = IccData.Parse(icc);
        Assert.False(parsed.IsDamaged, string.Join(" ", parsed.Warnings));
        Assert.Equal(new Version(2, 4, 0), parsed.Version);
        Assert.Equal(name, parsed.Description);
        Assert.Equal("No copyright, use freely", parsed.Copyright);
        Assert.False(parsed.TryGetTag("chad", out _));
        Assert.All(parsed.Tags.Where(t => t.Signature.EndsWith("TRC", StringComparison.Ordinal)), t => Assert.Equal("curv", t.Type));
        ColorProfile back = ColorProfile.FromIcc(parsed);
        Assert.Equal(icc, builtIn.ToIccV2());
        Assert.Equal(icc, back.ToIccV2());    // a version 2 profile read back gives its bytes back
        if (!builtIn.IsHdr)
        {
            Assert.True(back.IsEquivalentTo(builtIn), $"{back.Primaries} {back.White} {back.Transfer}");
            ColorPipeline a = ColorPipeline.Create(builtIn, ColorProfile.Srgb, RenderingIntent.RelativeColorimetric, false);
            ColorPipeline b = ColorPipeline.Create(back, ColorProfile.Srgb, RenderingIntent.RelativeColorimetric, false);
            double[] x = new double[3], y = new double[3];
            for (int i = 0; i < 125; i++)
            {
                double[] input = builtIn.Model == ColorModel.Gray ? [i / 124.0] : [i / 25 / 4.0, i / 5 % 5 / 4.0, i % 5 / 4.0];
                a.Evaluate(input, x);
                b.Evaluate(input, y);
                for (int c = 0; c < 3; c++)
                {
                    Assert.True(Math.Abs(x[c] - y[c]) < 2e-3, $"[{string.Join(", ", input)}] {c}: {x[c]} against {y[c]}");
                }
            }
        }
    }

    [Fact]
    public void Version2NeedsAMatrixShaper()
    {
        // FOGRA39L is a version 2 profile already; a version 4 lookup-table profile has nothing a version 2 matrix-shaper holds.
        Assert.Equal(ColorProfile.Fogra39.ToIcc(), ColorProfile.Fogra39.ToIccV2());
        Assert.Throws<InvalidOperationException>(() => ColorProfile.FromIcc(IccReferences.Bytes("synthetic/rgb-mab-xyz-v4.icc")).ToIccV2());
        byte[] v4 = ColorProfile.DisplayP3.ToIcc();
        Assert.Equal(2, IccData.Parse(ColorProfile.FromIcc(v4).ToIccV2()).Version.Major);
    }

    [Fact]
    public void SpansOfEveryLayoutAgree()
    {
        ColorTransform transform = ColorTransform.Create(ColorProfile.DisplayP3, ColorProfile.Srgb);
        Random random = new(6);
        Rgba8[] rgba = new Rgba8[700];
        random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgba.AsSpan()));
        Rgba8[] expected = new Rgba8[rgba.Length];
        transform.Apply<Rgba8, Rgba8>(rgba, expected);

        // B, G, R, A in; R, G, B out; alpha kept, and opaque from a type without it.
        Bgra8[] bgra = [.. rgba.Select(p => new Bgra8(p.B, p.G, p.R, p.A))];
        Rgb8[] rgb = new Rgb8[rgba.Length];
        transform.Apply<Bgra8, Rgb8>(bgra, rgb);
        Assert.Equal(expected.Select(p => (p.R, p.G, p.B)), rgb.Select(p => (p.R, p.G, p.B)));
        transform.Apply<Bgra8>(bgra);
        Assert.Equal(expected, bgra.Select(p => new Rgba8(p.R, p.G, p.B, p.A)));
        Rgba8[] fromRgb = new Rgba8[rgb.Length];
        transform.Apply<Rgb8, Rgba8>([.. rgba.Select(p => new Rgb8(p.R, p.G, p.B))], fromRgb);
        Assert.All(fromRgb, p => Assert.Equal(255, p.A));

        // The float path agrees with the byte path within a level, and keeps values outside 0..1.
        RgbaF32[] floats = [.. rgba.Select(p => RgbaF32.FromRgba8(p))];
        transform.Apply<RgbaF32>(floats);
        for (int i = 0; i < floats.Length; i++)
        {
            Rgba8 back = floats[i].ToRgba8();
            Assert.True(Math.Abs(back.R - expected[i].R) <= 1 && Math.Abs(back.G - expected[i].G) <= 1 && Math.Abs(back.B - expected[i].B) <= 1, $"{i}: {back} against {expected[i]}");
            Assert.Equal(rgba[i].A / 255f, floats[i].A, 5);
        }

        RgbaF32[] red = [new(1, 0, 0, 1)];
        transform.Apply<RgbaF32>(red);
        Assert.True(red[0].R > 1 && red[0].G < 0);
        Assert.True(ColorTransform.Create(ColorProfile.Srgb, ColorProfile.FromIcc(IccReferences.Bytes("compact/sRGB-v4.icc"))).IsIdentity);
    }

    [Fact]
    public void ConvertColorRecordsTheNewEncoding()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(Duck);
        using Raster<Rgba8> original = raster.Copy();
        raster.Apply(ops => ops.ConvertColor(ColorProfile.DisplayP3), TestContext.Current.CancellationToken);
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, raster.Metadata.ColorSpace);
        Assert.True(raster.Metadata.GetColorProfile().IsEquivalentTo(ColorProfile.DisplayP3));
        Assert.NotEqual(original.GetPixel(10, 10), raster.GetPixel(10, 10));

        // Back to sRGB: no profile, the sRGB hint, and the pixels within a level of where they started.
        raster.Apply(ops => ops.ConvertColor(ColorProfile.Srgb), TestContext.Current.CancellationToken);
        Assert.True(raster.Metadata.IccProfile.IsEmpty);
        Assert.Equal(ColorSpaceHint.Srgb, raster.Metadata.ColorSpace);
        for (int y = 0; y < raster.Height; y += 7)
        {
            for (int x = 0; x < raster.Width; x += 7)
            {
                Rgba8 a = original.GetPixel(x, y), b = raster.GetPixel(x, y);
                Assert.True(Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1, $"({x}, {y}): {b} against {a}");
            }
        }

        // A chain converts from where the previous step left it.
        using Raster<Rgba8> chained = original.Derive(ops => ops.ConvertColor(ColorProfile.Rec2020).ConvertColor(ColorProfile.Srgb), TestContext.Current.CancellationToken);
        Assert.True(chained.Metadata.IccProfile.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => raster.Apply(ops => ops.ConvertColor(ColorProfile.GraySrgb), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GrayUsesTheCurveOfItsEncoding()
    {
        using Raster<Gray8> gray = Raster.Create<Gray8>(256, 1);
        for (int x = 0; x < 256; x++)
        {
            gray.SetPixel(x, 0, new Gray8((byte)x));
        }

        gray.Metadata.ColorSpace = ColorSpaceHint.Linear;
        gray.Apply(ops => ops.ConvertColor(ColorProfile.Srgb), TestContext.Current.CancellationToken);
        Assert.Equal(ColorSpaceHint.Srgb, gray.Metadata.ColorSpace);
        for (int x = 0; x < 256; x++)
        {
            Assert.Equal((byte)Math.Round(TransferFunction.Srgb.FromLinear(x / 255.0) * 255), gray.GetPixel(x, 0).L);
        }
    }

    [Fact]
    public void CmykConvertsThroughItsProfile()
    {
        ColorProfile cmyk = ColorProfile.FromIcc(IccReferences.Bytes("synthetic/cmyk-mab-v4.icc"));
        using Raster<Cmyk8> inks = Raster.Load<Cmyk8>(CmykJpeg);
        inks.Metadata.IccProfile = cmyk.ToIcc();
        using Raster<Rgb8> rgb = inks.ConvertColor<Rgb8>(ColorProfile.Srgb);
        Assert.Equal(ColorSpaceHint.Srgb, rgb.Metadata.ColorSpace);
        Assert.True(rgb.Metadata.IccProfile.IsEmpty);

        // Every pixel as the reference pipeline gives it, within the grid's interpolation.
        ColorPipeline pipeline = ColorPipeline.Create(cmyk, ColorProfile.Srgb, RenderingIntent.Perceptual, false);
        double[] reference = new double[3];
        double total = 0;
        int count = 0;
        for (int y = 0; y < inks.Height; y += 3)
        {
            for (int x = 0; x < inks.Width; x += 3)
            {
                Cmyk8 p = inks.GetPixel(x, y);
                pipeline.Evaluate([p.C / 255.0, p.M / 255.0, p.Y / 255.0, p.K / 255.0], reference);
                Rgb8 q = rgb.GetPixel(x, y);
                total += Math.Abs(q.R - (Math.Clamp(reference[0], 0, 1) * 255)) + Math.Abs(q.G - (Math.Clamp(reference[1], 0, 1) * 255)) + Math.Abs(q.B - (Math.Clamp(reference[2], 0, 1) * 255));
                count += 3;
            }
        }

        Assert.True(total / count < 0.6, $"mean {total / count} levels");

        // RGB to CMYK and back: the round trip is the profile's own (its B2A only approximately inverts its A2B), so the compiled
        // round trip is checked against the reference pipeline's.
        RenderingIntent relative = RenderingIntent.RelativeColorimetric;
        using Raster<Cmyk8> again = rgb.ConvertColor<Cmyk8>(cmyk, new ColorTransformOptions { Intent = relative });
        Assert.True(again.Metadata.GetColorProfile().IsEquivalentTo(cmyk));
        using Raster<Rgb8> back = again.ConvertColor<Rgb8>(ColorProfile.Srgb, new ColorTransformOptions { Intent = relative });
        ColorPipeline there = ColorPipeline.Create(ColorProfile.Srgb, cmyk, relative, false), home = ColorPipeline.Create(cmyk, ColorProfile.Srgb, relative, false);
        double[] mid = new double[4], end = new double[3];
        double sum = 0;
        int n = 0;
        for (int y = 0; y < rgb.Height; y += 3)
        {
            for (int x = 0; x < rgb.Width; x += 3)
            {
                Rgb8 p = rgb.GetPixel(x, y);
                there.Evaluate([p.R / 255.0, p.G / 255.0, p.B / 255.0], mid);
                home.Evaluate([.. mid.Select(v => Math.Round(Math.Clamp(v, 0, 1) * 255) / 255)], end);
                Rgb8 q = back.GetPixel(x, y);
                sum += Math.Abs(q.R - (Math.Clamp(end[0], 0, 1) * 255)) + Math.Abs(q.G - (Math.Clamp(end[1], 0, 1) * 255)) + Math.Abs(q.B - (Math.Clamp(end[2], 0, 1) * 255));
                n += 3;
            }
        }

        Assert.True(sum / n < 1.5, $"round trip mean {sum / n} levels from the pipeline's");
    }

    [Fact]
    public async Task ConvertColorToEqualsLoadThenConvert()
    {
        // A Display P3 PNG, and a CMYK JPEG with its profile.
        using MemoryStream png = new();
        using (Raster<Rgba8> duck = Raster.Load<Rgba8>(Duck))
        {
            duck.Apply(ops => ops.ConvertColor(ColorProfile.DisplayP3), TestContext.Current.CancellationToken);
            duck.Save(png, "png");
        }

        using MemoryStream jpeg = new();
        using (Raster<Cmyk8> inks = Raster.Load<Cmyk8>(CmykJpeg))
        {
            inks.Metadata.IccProfile = IccReferences.Bytes("synthetic/cmyk-lut16-v2.icc");
            inks.Save(jpeg, "jpeg");
        }

        ReaderOptions convert = new() { ConvertColorTo = ColorProfile.Srgb };
        foreach (byte[] file in new[] { png.ToArray(), jpeg.ToArray() })
        {
            using Raster stored = Raster.Load(file);
            using Raster natural = Raster.Load(file, options: convert);
            Assert.Equal(stored.Layout.ChannelOrder == PixelChannelOrder.Cmyk ? PixelChannelOrder.Rgb : stored.Layout.ChannelOrder, natural.Layout.ChannelOrder);
            Assert.Equal(ColorSpaceHint.Srgb, natural.Metadata.ColorSpace);
            using Raster<Rgba8> typed = Raster.Load<Rgba8>(file, options: convert);
            using Raster<Rgba8> expected = stored.ConvertColor<Rgba8>(ColorProfile.Srgb);
            Assert.Equal(Pixels(expected), Pixels(typed));
            using Raster<Rgba8> naturalAsRgba = natural.As<Rgba8>();
            Assert.Equal(Pixels(expected), Pixels(naturalAsRgba));
            using Raster<Rgba8> async = await Raster.LoadAsync<Rgba8>(new MemoryStream(file), options: convert, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Pixels(expected), Pixels(async));
        }
    }

    private static Rgba8[] Pixels(Raster<Rgba8> raster)
    {
        Rgba8[] pixels = new Rgba8[raster.Width * raster.Height];
        raster.CopyPixelsTo(pixels);
        return pixels;
    }
}
