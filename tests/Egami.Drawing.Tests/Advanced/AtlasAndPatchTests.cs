using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Advanced;

public class AtlasAndPatchTests
{
    private static readonly SamplingOptions Bilinear = new() { Filter = ResampleFilter.Bilinear };

    [Fact]
    public void OneSpriteIsDrawImageUnderItsTransform()
    {
        using Raster<Rgba8> sheet = Pixels.Photo(32, 32);
        RectF source = new(8, 4, 16, 12);
        foreach (SpriteTransform t in new[] { new SpriteTransform(1, 0, 10, 7), SpriteTransform.FromRotationScale(1.5f, 30, new Vector2(30, 30), new Vector2(8, 6)) })
        {
            using Raster<Rgba8> atlas = Pixels.Draw(64, 64, c => c.DrawAtlas(sheet, [t], [source], sampling: Bilinear));
            using Raster<Rgba8> image = Pixels.Draw(64, 64, c =>
            {
                c.Concat(t.ToMatrix());
                c.DrawImage(sheet, source, new RectF(0, 0, source.Width, source.Height), Bilinear);
            });

            // Inside the sprite the two samplers agree within two levels; DrawImage reads only inside the source, the atlas may
            // read past it at the edge, as Skia's does, so the outermost ring is left out.
            Assert.True(Pixels.MeanDifference(atlas, image) < 1.5, $"{t}: mean {Pixels.MeanDifference(atlas, image)}");
        }
    }

    [Fact]
    public void SpritesAreTintedByTheirColorsAndBlendOneByOne()
    {
        using Raster<Rgba8> sheet = Raster.Create(8, 8, Rgba8.White);
        SpriteTransform[] at = [new(1, 0, 10, 10), new(1, 0, 12, 12)];
        RectF[] sources = [new(0, 0, 8, 8), new(0, 0, 8, 8)];
        RgbaF32[] colors = [new(1, 0, 0, 0.5f), new(1, 0, 0, 0.5f)];
        using Raster<Rgba8> drawn = Pixels.Draw(32, 32, c => c.DrawAtlas(sheet, at, sources, colors, BlendMode.Modulate), Rgba8.Black);

        // Modulate multiplies premultiplied colors: white × half red is half red, half transparent; where the sprites overlap,
        // the second blends over the first.
        Assert.Equal(new Rgba8(128, 0, 0, 255), drawn.GetPixel(11, 11));
        Assert.InRange(drawn.GetPixel(14, 14).R, 191, 192);

        // A cull rectangle outside the clip skips the call.
        using Raster<Rgba8> culled = Pixels.Draw(32, 32, c => c.DrawAtlas(sheet, at, sources, cull: new RectF(100, 100, 10, 10)), Rgba8.Black);
        Assert.Equal(Rgba8.Black, culled.GetPixel(14, 14));
    }

    [Fact]
    public void AFlatCoonsPatchIsItsBilinearColors()
    {
        RgbaF32 tl = new(1, 0, 0, 1), tr = new(0, 1, 0, 1), br = new(0, 0, 1, 1), bl = new(1, 1, 0, 1);
        Patch patch = Patch.Coons(Straight(new Vector2(4, 4), new Vector2(60, 4), new Vector2(60, 44), new Vector2(4, 44)), [tl, tr, br, bl]);
        using Raster<Rgba8> drawn = Pixels.Draw(64, 48, c => c.DrawPatch(patch));
        int worst = 0;
        for (int y = 4; y < 44; y++)
        {
            for (int x = 4; x < 60; x++)
            {
                float u = (x + 0.5f - 4) / 56, v = (y + 0.5f - 4) / 40;
                float r = ((1 - u) * (1 - v) * tl.R) + (u * (1 - v) * tr.R) + (u * v * br.R) + ((1 - u) * v * bl.R);
                float g = ((1 - u) * (1 - v) * tl.G) + (u * (1 - v) * tr.G) + (u * v * br.G) + ((1 - u) * v * bl.G);
                float b = ((1 - u) * (1 - v) * tl.B) + (u * (1 - v) * tr.B) + (u * v * br.B) + ((1 - u) * v * bl.B);
                Rgba8 p = drawn.GetPixel(x, y);
                worst = Math.Max(worst, Math.Max(Math.Abs(p.R - (int)MathF.Round(r * 255)), Math.Max(Math.Abs(p.G - (int)MathF.Round(g * 255)), Math.Abs(p.B - (int)MathF.Round(b * 255)))));
            }
        }

        Assert.True(worst <= 1, $"worst {worst}");
    }

    [Fact]
    public void ATensorPatchWithCoonsInnerPointsIsTheCoonsPatch()
    {
        Vector2[] boundary =
        [
            new(10, 10), new(30, 2), new(50, 18), new(70, 8),
            new(78, 30), new(62, 50), new(72, 70),
            new(50, 60), new(30, 78), new(8, 68),
            new(16, 48), new(2, 28),
        ];
        RgbaF32[] colors = [new(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1), new(1, 1, 0, 1)];
        Patch coons = Patch.Coons(boundary, colors);
        Patch tensor = Patch.Tensor(coons.ToTensorNet(), colors);
        for (float u = 0; u <= 1; u += 0.125f)
        {
            for (float v = 0; v <= 1; v += 0.125f)
            {
                Vector2 a = coons.Evaluate(u, v), b = tensor.Evaluate(u, v);
                Assert.True(Vector2.Distance(a, b) < 1e-3f, $"({u}, {v}): {a} against {b}");
            }
        }

        using Raster<Rgba8> c1 = Pixels.Draw(80, 80, c => c.DrawPatch(coons));
        using Raster<Rgba8> c2 = Pixels.Draw(80, 80, c => c.DrawPatch(tensor));
        Assert.True(Pixels.MaxDifference(c1, c2) <= 1);
        Assert.Equal(boundary[0], coons.Evaluate(0, 0));
        Assert.Equal(boundary[6], coons.Evaluate(1, 1));
    }

    [Fact]
    public void WhereAPatchFoldsLargerVCovers()
    {
        // The left and right curves go down and come back up, so v near 0 and v near 1 cover the same rows below y = 14; v = 1
        // is blue and must cover.
        Vector2[] folded =
        [
            new(10, 10), new(30, 10), new(50, 10), new(70, 10),
            new(70, 90), new(70, 90), new(70, 14),
            new(50, 14), new(30, 14), new(10, 14),
            new(10, 90), new(10, 90),
        ];
        RgbaF32 red = new(1, 0, 0, 1), blue = new(0, 0, 1, 1);
        using Raster<Rgba8> drawn = Pixels.Draw(80, 80, c => c.DrawPatch(Patch.Coons(folded, [red, red, blue, blue])));
        Rgba8 p = drawn.GetPixel(40, 20);
        Assert.True(p.B > 200 && p.R < 60, $"{p}");
        Assert.True(drawn.GetPixel(40, 12).R > 200, $"{drawn.GetPixel(40, 12)}");
    }

    [Fact]
    public void PatchValuesMapThroughStops()
    {
        Patch patch = Patch.Coons(Straight(new Vector2(0, 0), new Vector2(64, 0), new Vector2(64, 8), new Vector2(0, 8)), cornerValues: [0, 1, 1, 0]);
        GradientStop[] stops = [new(0, new RgbaF32(0, 0, 0, 1)), new(1, new RgbaF32(1, 1, 1, 1))];
        using Raster<Rgba8> drawn = Pixels.Draw(64, 8, c => c.DrawPatch(patch, valueStops: stops), Rgba8.Black);
        Assert.InRange(drawn.GetPixel(32, 4).R, 126, 130);
        Assert.Throws<ArgumentException>(() => Pixels.Draw(4, 4, c => c.DrawPatch(patch)).Dispose());
        Assert.Throws<ArgumentException>(() => Patch.Coons(new Vector2[11]));
        Assert.Throws<ArgumentException>(() => Patch.Tensor(new Vector2[16], [RgbaF32.FromRgba8(Rgba8.White)]));
    }

    [Fact]
    public void SubdivisionFollowsCurvatureAndTheTransform()
    {
        Patch flat = Patch.Coons(Straight(new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100), new Vector2(0, 100)));
        Assert.Equal((1, 1), flat.Divisions(Matrix3x2.Identity));
        Vector2[] bulge = Straight(new Vector2(0, 0), new Vector2(100, 0), new Vector2(100, 100), new Vector2(0, 100));
        bulge[1] = new Vector2(33, -40);
        bulge[2] = new Vector2(66, -40);
        Patch curved = Patch.Coons(bulge);
        (int u1, _) = curved.Divisions(Matrix3x2.Identity);
        (int u4, _) = curved.Divisions(Matrix3x2.CreateScale(4));
        Assert.True(u1 > 4 && u4 > u1 && u4 <= Patch.MaxDivisions, $"{u1}, {u4}");
    }

    /// <summary>A Coons boundary of straight edges between four corners: top-left, top-right, bottom-right, bottom-left.</summary>
    private static Vector2[] Straight(Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl)
        => [tl, Vector2.Lerp(tl, tr, 1 / 3f), Vector2.Lerp(tl, tr, 2 / 3f), tr, Vector2.Lerp(tr, br, 1 / 3f), Vector2.Lerp(tr, br, 2 / 3f), br, Vector2.Lerp(br, bl, 1 / 3f), Vector2.Lerp(br, bl, 2 / 3f), bl, Vector2.Lerp(bl, tl, 1 / 3f), Vector2.Lerp(bl, tl, 2 / 3f)];
}
