using System.Numerics;

using Egami.Colors;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Inks;

/// <summary>
/// Phase 2.6.5 task 8: what is not ink converts into the process inks through its own profile, CMYK in the canvas's profile passes
/// through bit for bit, ink images map by name, and ink gradients and meshes blend their tints.
/// </summary>
public class InkConversionTests
{
    private static readonly InkSet Cmyk = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", InkAppearance.Cmyk(0, 1, 0.81f, 0.04f));
    private static readonly string[] Process = ["Cyan", "Magenta", "Yellow", "Black"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CmykInTheCanvasProfilePassesThroughBitForBit(bool tagged)
    {
        using Raster<Cmyk8> image = Raster.Create<Cmyk8>(11, 7);
        Random random = new(8);
        for (int y = 0; y < 7; y++)
        {
            for (int x = 0; x < 11; x++)
            {
                image.Row(y)[x] = new Cmyk8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        }

        if (tagged)
        {
            image.Metadata.IccProfile = ColorProfile.Fogra39.ToIcc();
        }

        using InkRaster plates = new(Cmyk, 16, 12);
        using (InkCanvas canvas = new(plates))
        {
            canvas.DrawImage(image, new Vector2(3, 2));
        }

        for (int y = 0; y < 7; y++)
        {
            for (int x = 0; x < 11; x++)
            {
                Cmyk8 p = image.Row(y)[x];
                Assert.Equal((p.C, p.M, p.Y, p.K, (byte)255), (Plate(plates, "Cyan", x + 3, y + 2), Plate(plates, "Magenta", x + 3, y + 2), Plate(plates, "Yellow", x + 3, y + 2), Plate(plates, "Black", x + 3, y + 2), plates.Alpha.Row8(y + 2)[x + 3]));
            }
        }

        Assert.Equal(0, plates.Alpha.Row8(0)[0]);
    }

    [Fact]
    public void OtherProfilesConvertThroughTheirOwnTransform()
    {
        ColorTransformOptions options = new() { Intent = RenderingIntent.RelativeColorimetric, BlackPointCompensation = true };

        // CMYK in another profile, RGB tagged Display P3, untagged RGB (the canvas's RGB encoding) and gray.
        ColorProfile otherCmyk = ColorProfile.FromIcc(File.ReadAllBytes(RepoFile("tests", "images", "icc", "synthetic", "cmyk-lut16-v2.icc")));
        using Raster<Cmyk8> cmyk = Raster.Create(2, 2, new Cmyk8(30, 200, 90, 10));
        cmyk.Metadata.IccProfile = otherCmyk.ToIcc();
        Check(cmyk, otherCmyk, [30 / 255f, 200 / 255f, 90 / 255f, 10 / 255f], InkCanvasOptions.Default);

        using Raster<Rgb8> p3 = Raster.Create(2, 2, new Rgb8(200, 60, 40));
        p3.Metadata.IccProfile = ColorProfile.DisplayP3.ToIcc();
        Check(p3, ColorProfile.DisplayP3, [200 / 255f, 60 / 255f, 40 / 255f], InkCanvasOptions.Default);

        using Raster<Rgb8> plain = Raster.Create(2, 2, new Rgb8(200, 60, 40));
        Check(plain, ColorProfile.AdobeRgb, [200 / 255f, 60 / 255f, 40 / 255f], new InkCanvasOptions { RgbSource = ColorProfile.AdobeRgb });

        using Raster<Gray8> gray = Raster.Create(2, 2, new Gray8(90));
        Check(gray, ColorProfile.GraySrgb, [90 / 255f], InkCanvasOptions.Default);

        // The intent is the canvas's.
        Check(plain, ColorProfile.Srgb, [200 / 255f, 60 / 255f, 40 / 255f], new InkCanvasOptions { Intent = RenderingIntent.Perceptual, BlackPointCompensation = false });

        static void Check(Raster image, ColorProfile profile, float[] color, InkCanvasOptions options)
        {
            float[] expected = new float[4];
            ColorTransform.Create(profile, ColorProfile.Fogra39, new ColorTransformOptions { Intent = options.Intent, BlackPointCompensation = options.BlackPointCompensation }).Apply(color, expected);
            using InkRaster plates = new(Cmyk, 2, 2);
            using (InkCanvas canvas = new(plates, options))
            {
                canvas.DrawImage(image, Vector2.Zero);
            }

            for (int c = 0; c < 4; c++)
            {
                Assert.True(Math.Abs(Plate(plates, Process[c], 1, 1) - (expected[c] * 255)) <= 1, $"{profile.Name} {Process[c]}: {Plate(plates, Process[c], 1, 1)} against {expected[c] * 255:F1}");
            }
        }
    }

    [Fact]
    public void InkImagesMapByName()
    {
        InkSet source = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", null).WithSpot("Violet", InkAppearance.Cmyk(0.6f, 0.9f, 0, 0));
        using InkRaster image = new(source, 4, 4);
        for (int y = 0; y < 4; y++)
        {
            image.Plate("Cyan")!.Row8(y).Fill(40);
            image.Plate("PANTONE 186 C")!.Row8(y).Fill(200);
            image.Plate("Violet")!.Row8(y).Fill(255);
            image.Alpha.Row8(y).Fill(255);
        }

        using InkRaster plates = new(Cmyk, 6, 6);
        using (InkCanvas canvas = new(plates))
        {
            canvas.DrawImage(image, new RectF(1, 1, 4, 4));
        }

        // The spot lands on its plate; Violet, which the canvas lacks, prints through its appearance over the cyan.
        Assert.Equal(200, Plate(plates, "PANTONE 186 C", 2, 2));
        Assert.Equal(InkByte(1 - ((1 - (40 / 255f)) * (1 - 0.6f))), Plate(plates, "Cyan", 2, 2));
        Assert.Equal(InkByte(0.9f), Plate(plates, "Magenta", 2, 2));
        Assert.Equal(0, Plate(plates, "Yellow", 2, 2));
        Assert.Equal(0, Plate(plates, "PANTONE 186 C", 0, 0));
    }

    [Fact]
    public void InkGradientsMeshesAndPatchesBlendTints()
    {
        InkColor none = InkColor.Spot(Cmyk[4], 0), full = InkColor.Spot(Cmyk[4], 1);
        using InkRaster plates = new(Cmyk, 64, 30);
        using (InkCanvas canvas = new(plates))
        {
            canvas.FillRect(new RectF(0, 0, 64, 10), new LinearGradient(Vector2.Zero, new Vector2(64, 0), [new GradientStop(0, none), new GradientStop(1, full)]));

            // A mixed gradient: an RGB stop converts, the ink stop prints its spot.
            canvas.FillRect(new RectF(0, 10, 64, 10), new LinearGradient(Vector2.Zero, new Vector2(64, 0), [new GradientStop(0, new RgbaF32(1, 1, 1, 1)), new GradientStop(1, InkColor.Cmyk(0, 0, 0, 1))]));
            Mesh mesh = new Mesh(MeshMode.Triangles, [new Vector2(0, 20), new Vector2(64, 20), new Vector2(0, 30)]).WithInks([none, full, InkColor.Spot(Cmyk[4], 0.5f)]);
            canvas.DrawMesh(mesh);
        }

        for (int x = 0; x < 64; x++)
        {
            Assert.True(Math.Abs(Plate(plates, "PANTONE 186 C", x, 5) - ((x + 0.5f) / 64 * 255)) <= 1, $"gradient at {x}: {Plate(plates, "PANTONE 186 C", x, 5)}");
            Assert.Equal(0, Plate(plates, "Cyan", x, 5));
            Assert.True(Math.Abs(Plate(plates, "Black", x, 15) - ((x + 0.5f) / 64 * 255)) <= 2, $"black at {x}: {Plate(plates, "Black", x, 15)}");
        }

        // The mesh's spot plate: barycentric tints at the pixel center, here at (10.5, 22.5): w1 = 10.5 / 64, w2 = 2.5 / 10.
        float expected = ((10.5f / 64) * 1) + ((2.5f / 10) * 0.5f);
        Assert.True(Math.Abs(Plate(plates, "PANTONE 186 C", 10, 22) - (expected * 255)) <= 1, $"mesh: {Plate(plates, "PANTONE 186 C", 10, 22)} against {expected * 255:F1}");

        // A patch with corner inks prints the corners' mean at its center.
        using InkRaster patchPlates = new(Cmyk, 40, 40);
        using (InkCanvas canvas = new(patchPlates))
        {
            Vector2[] net = [new(0, 0), new(13, 0), new(27, 0), new(40, 0), new(40, 13), new(40, 27), new(40, 40), new(27, 40), new(13, 40), new(0, 40), new(0, 27), new(0, 13)];
            canvas.DrawPatch(Patch.Coons(net).WithInks([none, full, full, none]));
        }

        Assert.True(Math.Abs(Plate(patchPlates, "PANTONE 186 C", 20, 20) - 128) <= 4, $"patch center: {Plate(patchPlates, "PANTONE 186 C", 20, 20)}");
        Assert.True(Plate(patchPlates, "PANTONE 186 C", 2, 20) < 20 && Plate(patchPlates, "PANTONE 186 C", 37, 20) > 235);
    }

    private static string RepoFile(params string[] parts)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return System.IO.Path.Combine([dir!.FullName, .. parts]);
    }

    private static byte InkByte(float v) => (byte)((v * 255) + 0.5f);

    private static byte Plate(InkRaster plates, string name, int x, int y) => plates.Plate(name)!.Row8(y)[x];
}
