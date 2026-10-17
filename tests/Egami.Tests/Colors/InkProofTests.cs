using Egami.Colors;
using Egami.Pixels;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.6.5 task 12: soft proofs of plates and ink coverage.</summary>
public class InkProofTests
{
    private static readonly ColorTransformOptions Relative = new() { Intent = RenderingIntent.RelativeColorimetric };

    [Fact]
    public void ACmykOnlyProofEqualsTheProfileConversion()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39);
        using InkRaster plates = new(inks, 16, 1);
        for (int x = 0; x < 16; x++)
        {
            for (int p = 0; p < 4; p++)
            {
                plates.Plates[p].Row8(0)[x] = (byte)((x * (37 + (p * 11))) % 256);
            }
        }

        using Raster<RgbaF32> proof = plates.Proof<RgbaF32>();
        ColorTransform expected = ColorTransform.Create(ColorProfile.Fogra39, ColorProfile.Srgb, Relative);
        float[] rgb = new float[3];
        for (int x = 0; x < 16; x++)
        {
            expected.Apply([.. Enumerable.Range(0, 4).Select(p => plates.Plates[p].Row8(0)[x] / 255f)], rgb);
            RgbaF32 got = proof.Row(0)[x];
            Assert.Equal(rgb[0], got.R, 1e-4f);
            Assert.Equal(rgb[1], got.G, 1e-4f);
            Assert.Equal(rgb[2], got.B, 1e-4f);
            Assert.Equal(1f, got.A);
        }
    }

    [Fact]
    public void ASpotShowsAsItsAppearanceOverTheProcessInks()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("Orange", InkAppearance.Cmyk(0, 0.6f, 1, 0));
        using InkRaster plates = new(inks, 3, 1);
        plates.Plate("Orange")!.Row8(0)[0] = 255;
        plates.Plate("Orange")!.Row8(0)[1] = 255;
        plates.Plate("Cyan")!.Row8(0)[1] = 102;
        plates.Plate("Magenta")!.Row8(0)[1] = 76;
        plates.Plate("Yellow")!.Row8(0)[1] = 128;
        plates.Plate("Orange")!.Row8(0)[2] = 128;

        using Raster<RgbaF32> proof = plates.Proof<RgbaF32>();

        // Full tint alone is the appearance; over 40% cyan and 30% magenta the tints add, capped at full ink; half tint is half
        // the appearance.
        Expect(proof.Row(0)[0], [0, 0.6f, 1, 0]);
        Expect(proof.Row(0)[1], [0.4f, (76 / 255f) + 0.6f, 1, 0]);
        Expect(proof.Row(0)[2], [0, 0.6f * 128 / 255, 128 / 255f, 0]);

        static void Expect(RgbaF32 got, float[] cmyk)
        {
            float[] rgb = new float[3];
            ColorTransform.Create(ColorProfile.Fogra39, ColorProfile.Srgb, Relative).Apply(cmyk, rgb);
            Assert.Equal(rgb[0], got.R, 1e-3f);
            Assert.Equal(rgb[1], got.G, 1e-3f);
            Assert.Equal(rgb[2], got.B, 1e-3f);
        }
    }

    [Fact]
    public void TechnicalInksAndBlankPaperDoNotShow()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("Varnish", null, ColorantKind.Technical).WithSpot("Unknown", null);
        using InkRaster plates = new(inks, 2, 1);
        plates.Plate("Varnish")!.Row8(0).Fill(255);
        plates.Plate("Unknown")!.Row8(0).Fill(255);

        using Raster<Rgba8> proof = plates.Proof<Rgba8>();
        Assert.All(proof.Row(0).ToArray(), p => Assert.Equal(new Rgba8(255, 255, 255, 255), p));
    }

    [Fact]
    public void SimulatingPaperShowsThePaperTone()
    {
        using InkRaster plates = new(InkSet.FromProfile(ColorProfile.Fogra39), 1, 1);
        using Raster<RgbaF32> white = plates.Proof<RgbaF32>();
        using Raster<RgbaF32> paper = plates.Proof<RgbaF32>(new ProofOptions { SimulatePaper = true });

        Assert.Equal(1f, white.Row(0)[0].B, 1e-3f);
        Assert.True(paper.Row(0)[0].B < 0.99f, $"paper blue {paper.Row(0)[0].B}");
    }

    [Fact]
    public void AGrayProofIsGray()
    {
        using InkRaster plates = new(InkSet.FromProfile(ColorProfile.Fogra39), 1, 1);
        plates.Plate("Black")!.Row8(0)[0] = 128;
        using Raster<RgbaF32> proof = plates.Proof<RgbaF32>(new ProofOptions { Target = ColorProfile.GraySrgb });
        RgbaF32 p = proof.Row(0)[0];
        Assert.Equal(p.R, p.G);
        Assert.Equal(p.R, p.B);
        Assert.InRange(p.R, 0.3f, 0.8f);
        Assert.Throws<ArgumentException>(() => new ProofOptions { Target = ColorProfile.Fogra39 });
    }

    [Fact]
    public void CoverageTotalsThePrintingInks()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("Gold", null).WithSpot("Varnish", null, ColorantKind.Technical);
        using InkRaster plates = new(inks, 4, 2);
        foreach (string name in new[] { "Cyan", "Magenta", "Yellow" })
        {
            plates.Plate(name)!.Row8(1)[2] = 255;
        }

        plates.Plate("Gold")!.Row8(1)[2] = 51;
        plates.Plate("Black")!.Row8(0)[0] = 255;
        plates.Plate("Varnish")!.Row8(0).Fill(255);
        plates.Plate("Varnish")!.Row8(1).Fill(255);

        InkCoverage coverage = plates.MeasureCoverage();

        Assert.Equal(3.2f, coverage.MaxTotal, 1e-5f);
        Assert.Equal(new Point(2, 1), coverage.MaxAt);
        Assert.Equal(1 / 8.0, coverage.ShareOver, 1e-9);
        Assert.Equal(1 / 8.0, coverage.PerPlate["Black"], 1e-9);
        Assert.Equal(0.2 / 8, coverage.PerPlate["Gold"], 1e-6);
        Assert.Equal(1.0, coverage.PerPlate["Varnish"], 1e-9);
        Assert.Equal(0, plates.MeasureCoverage(3.5f).ShareOver);
        Assert.Throws<ArgumentOutOfRangeException>(() => plates.MeasureCoverage(-1));
    }

    [Fact]
    public void ABlankRasterHasNoMaximum()
    {
        using InkRaster plates = new(InkSet.FromProfile(ColorProfile.Fogra39), 2, 2, InkDepth.Unorm16);
        InkCoverage coverage = plates.MeasureCoverage();
        Assert.Equal(0, coverage.MaxTotal);
        Assert.Null(coverage.MaxAt);
        Assert.Equal(0, coverage.ShareOver);
    }
}
