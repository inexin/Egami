using System.Numerics;

using Egami.Colors;
using Egami.Drawing;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.DocSamples;

/// <summary>Phase 2.6.5's cookbook recipes: printing with inks.</summary>
[Collection("Samples")]
public class InkRecipes
{
    [Fact]
    public void Separations()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> photo = Raster.Create(120, 80, new Rgba8(200, 120, 60, 255));

        #region ink-separations
        // A CMYK page with two spot colours: a photo converted into the process inks, a Pantone band and a gold rule.
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39)
            .WithSpot("PANTONE 186 C", InkAppearance.Cmyk(0, 1, 0.81f, 0.04f))
            .WithSpot("Gold", InkAppearance.Lab(new LabColor(70, 5, 60)));
        using InkRaster page = new(inks, 600, 400);
        using (InkCanvas canvas = new(page))
        {
            canvas.Clear(InkColor.None);
            canvas.DrawImage(photo, new RectF(40, 40, 360, 240));
            canvas.FillRect(new RectF(0, 320, 600, 50), new SolidPaint(InkColor.Spot(inks[4], 1)));
            canvas.FillRect(new RectF(40, 300, 520, 4), new SolidPaint(InkColor.Spot(inks[5], 1)));
        }

        // Every plate as its own gray image (ink shows light), and all of them as one separated TIFF for the RIP.
        foreach (InkPlate plate in page.Plates)
        {
            using Raster<Gray8> gray = plate.AsRaster<Gray8>();
            gray.Save($"page-{plate.Name}.png");
        }

        page.Save("page.tif");
        #endregion

        Assert.True(File.Exists("page-Gold.png") && File.Exists("page.tif"));
    }

    [Fact]
    public void SpotLabel()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", InkAppearance.Cmyk(0, 1, 0.81f, 0.04f));
        using InkRaster page = new(inks, 300, 120);

        #region ink-spot-label
        // A label in one spot colour over a CMYK background, overprinted so the press needs no trapping: the background's
        // plates stay under the spot. Black text overprints too, leaving every other plate alone where it prints.
        using (InkCanvas canvas = new(page))
        {
            canvas.FillRect(new RectF(0, 0, 300, 120), new SolidPaint(InkColor.Cmyk(0.1f, 0.05f, 0.4f, 0)));
            canvas.FillPath(Path.RoundRect(new RoundRect(new RectF(20, 20, 260, 80), 12)),
                new SolidPaint(InkColor.Spot(inks[4], 1)) { Overprint = Overprint.On });
            canvas.FillPath(Path.Circle(new Vector2(150, 60), 20), new SolidPaint(InkColor.Cmyk(0, 0, 0, 1)) { Overprint = Overprint.NonZero });
        }
        #endregion

        Assert.Equal(102, page.Plate("Yellow")!.Row8(60)[150]);           // the background is still under the spot and black
        Assert.Equal(255, page.Plate("PANTONE 186 C")!.Row8(60)[150]);
    }

    [Fact]
    public void CoverageCheck()
    {
        InkSet inks = InkSet.FromProfile(ColorProfile.Fogra39);
        using InkRaster page = new(inks, 200, 100);
        using (InkCanvas canvas = new(page))
        {
            canvas.FillRect(new RectF(0, 0, 200, 100), new SolidPaint(InkColor.Cmyk(0.8f, 0.7f, 0.7f, 0.9f)));
        }

        #region ink-coverage
        // Before sending a page to a press with a 300% total ink limit: where is there too much ink, and how much of the page?
        InkCoverage coverage = page.MeasureCoverage(limit: 3.0f);
        if (coverage.MaxTotal > coverage.Limit)
        {
            Console.WriteLine($"{coverage.MaxTotal:P0} at {coverage.MaxAt}; {coverage.ShareOver:P1} of the page is over {coverage.Limit:P0}.");
        }

        // A soft proof shows how it will look on paper, the paper's own tone included.
        using Raster<Rgba8> proof = page.Proof<Rgba8>(new ProofOptions { SimulatePaper = true });
        #endregion

        Assert.Equal(1.0, coverage.ShareOver);
        Assert.True(coverage.MaxTotal > 3.0f);
    }
}
