using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>Phase 2.5.4 task 6: MotionBlur, RotationalBlur and ZoomBlur properties.</summary>
public class DirectionalBlurTests
{
    [Fact]
    public void FlatImagesStayFlat()
    {
        using Raster<Rgba8> flat = Raster.Create(64, 48, new Rgba8(90, 140, 30, 255));
        foreach (Action<RasterOps> chain in new Action<RasterOps>[] { o => o.MotionBlur(9, 3, 37), o => o.RotationalBlur(25), o => o.ZoomBlur(0.5f), o => o.ZoomBlur(0.3f, new Vector2(5, 40)) })
        {
            using Raster<Rgba8> result = flat.Derive(chain, Ct);
            Same(flat, result);
        }
    }

    [Fact]
    public void CenterPixelDoesNotMove()
    {
        // With the center on a pixel's center, that pixel samples only itself.
        using Raster<Rgba8> source = Load("duck");
        Vector2 center = new(120.5f, 90.5f);
        using Raster<Rgba8> zoom = source.Derive(o => o.ZoomBlur(0.8f, center), Ct);
        using Raster<Rgba8> spin = source.Derive(o => o.RotationalBlur(40, center), Ct);
        Assert.Equal(source.Row(90)[120], zoom.Row(90)[120]);
        Assert.Equal(source.Row(90)[120], spin.Row(90)[120]);
    }

    [Fact]
    public void ZoomKeepsTheImagesEnergy()
    {
        using Raster<Rgba8> source = Load("egret");
        // Sampling at scale s reads an area 1/s² as large, so no per-pixel average keeps the energy exactly; with the scales spaced
        // evenly in their logarithm the drift is at most about amount² / 6, and on a photo much less.
        using Raster<Rgba8> zoom = source.Derive(o => o.ZoomBlur(0.2f), Ct);
        Assert.True(Math.Abs(Mean(source) - Mean(zoom)) <= Mean(source) * 0.005, $"{Mean(source)} against {Mean(zoom)}");
    }

    [Fact]
    public void MotionStreaksOneWayOrBoth()
    {
        // A single white pixel: the one-sided streak runs from it toward the angle, the centered one both ways.
        using Raster<RgbaF32> dot = Raster.Create(41, 9, new RgbaF32(0, 0, 0, 1));
        dot.Row(4)[20] = new RgbaF32(1, 1, 1, 1);
        using Raster<RgbaF32> one = dot.Derive(o => o.MotionBlur(5, 100, 180), Ct);
        using Raster<RgbaF32> both = dot.Derive(o => o.MotionBlur(5, 100, 0, centered: true), Ct);

        // Angle 180: each pixel reads toward the left, so the dot lights the pixels to its right.
        Assert.True(one.Row(4)[25].R > 0.05f && one.Row(4)[15].R == 0);
        Assert.True(both.Row(4)[25].R > 0.05f && both.Row(4)[15].R > 0.05f);
        Assert.Equal(both.Row(4)[24].R, both.Row(4)[16].R, 1e-6);
    }

    [Fact]
    public void SameOnOneThreadAndInsideAnArea()
    {
        SameOnOneThread("egret", o => o.MotionBlur(12, 4, 75).RotationalBlur(6).ZoomBlur(0.2f));
        SameInsideArea("duck", new Rect(40, 50, 120, 90), o => o.MotionBlur(7, 3, 200));
        SameInsideArea("duck", new Rect(40, 50, 120, 90), o => o.RotationalBlur(12));
        SameInsideArea("duck", new Rect(40, 50, 120, 90), o => o.ZoomBlur(0.4f, new Vector2(10, 10)));
    }

    private static double Mean(Raster<Rgba8> raster)
    {
        double sum = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                sum += p.R + p.G + p.B;
            }
        }

        return sum / (raster.Width * raster.Height * 3);
    }
}
