using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

namespace Egami.GdiPlus.Tests;

/// <summary>GDI+ brush and region semantics: wrap modes, hatches, blend stops, path gradients and combine modes.</summary>
public class BrushAndRegionTests
{
    private static readonly Rgba8 Red = new(255, 0, 0, 255);

    private static readonly Rgba8 Blue = new(0, 0, 255, 255);

    [Theory]
    [InlineData(GdiPlusBrushes.WrapTile, TileMode.Repeat, TileMode.Repeat)]
    [InlineData(GdiPlusBrushes.WrapTileFlipX, TileMode.Mirror, TileMode.Repeat)]
    [InlineData(GdiPlusBrushes.WrapTileFlipY, TileMode.Repeat, TileMode.Mirror)]
    [InlineData(GdiPlusBrushes.WrapTileFlipXY, TileMode.Mirror, TileMode.Mirror)]
    [InlineData(GdiPlusBrushes.WrapClamp, TileMode.Decal, TileMode.Decal)]
    public void WrapModesMapToTileModes(int mode, TileMode x, TileMode y)
    {
        Assert.Equal((x, y), GdiPlusBrushes.Wrap(mode));
    }

    [Fact]
    public void EveryHatchStyleDrawsItsTileBitForBit()
    {
        Assert.Equal(53, GdiPlusHatches.Tiles.Length);
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(8, 8);
        for (int style = 0; style < 53; style++)
        {
            using (RasterCanvas<Rgba8> canvas = new(raster))
            {
                canvas.DrawPicture(GdiPlusBrushes.HatchTile(style, Red, Blue));
            }

            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    bool fore = (GdiPlusHatches.Tiles[style][y] & (0x80 >> x)) != 0;
                    Assert.Equal(fore ? Red : Blue, raster.Row(y)[x]);
                }
            }
        }
    }

    [Fact]
    public void HatchTilesScaleByWholeDevicePixels()
    {
        Assert.Equal(1, GdiPlusBrushes.HatchScale(Matrix3x2.Identity));
        Assert.Equal(1, GdiPlusBrushes.HatchScale(Matrix3x2.CreateScale(0.3f)));
        Assert.Equal(3, GdiPlusBrushes.HatchScale(Matrix3x2.CreateScale(2.6f)));
    }

    [Fact]
    public void BlendStopsClampAndMixTheTwoColors()
    {
        GradientStop half = GdiPlusBrushes.BlendStop(0.25f, 0.5f, Red, Blue, gamma: false);
        Assert.Equal(0.25f, half.Offset);
        Assert.Equal(new Rgba8(128, 0, 128, 255), half.Color.ToRgba8());
        Assert.Equal(1f, GdiPlusBrushes.BlendStop(2, 3, Red, Blue, gamma: false).Offset);
        Assert.Equal(Blue, GdiPlusBrushes.BlendStop(2, 3, Red, Blue, gamma: false).Color.ToRgba8());

        // Gamma-corrected blending mixes in linear light, so the middle is brighter than the encoded average.
        Assert.Equal(new Rgba8(186, 0, 186, 255), GdiPlusBrushes.BlendStop(0.5f, 0.5f, Red, Blue, gamma: true).Color.ToRgba8());
        Assert.Equal(0f, GdiPlusBrushes.PresetStop(-1, Red).Offset);
    }

    [Fact]
    public void APathGradientIsAMeshOfRingsAroundTheCenter()
    {
        Vector2[] square = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        Mesh mesh = GdiPlusBrushes.PathGradient(square, new Vector2(5, 5), Red, [Blue], null, Matrix3x2.Identity)!;

        // Two rings of four points and the center; four quads between the rings and four triangles to the center.
        Assert.Equal(9, mesh.Positions.Length);
        Assert.Equal((4 * 6) + (4 * 3), mesh.Indices.Length);
        Assert.Equal(Blue, mesh.Colors.Span[0].ToRgba8());
        Assert.Equal(Red, mesh.Colors.Span[^1].ToRgba8());
        Assert.Null(GdiPlusBrushes.PathGradient([new Vector2(1, 1)], Vector2.Zero, Red, [], null, Matrix3x2.Identity));
    }

    [Fact]
    public void FlatteningKeepsLinesAndSplitsCurves()
    {
        Path path = new PathBuilder().MoveTo(new Vector2(0, 0)).LineTo(new Vector2(10, 0)).CubicTo(new Vector2(10, 5), new Vector2(5, 10), new Vector2(0, 10)).ToPath();
        Vector2[] points = GdiPlusBrushes.Flatten(path);
        Assert.Equal(2 + 12, points.Length);
        Assert.Equal(new Vector2(0, 10), points[^1]);
        Assert.Empty(GdiPlusBrushes.Flatten(null));
    }

    [Fact]
    public void RegionsCombineWithNullAsTheInfiniteRegion()
    {
        Path a = Path.Rect(new RectF(0, 0, 10, 10)), b = Path.Rect(new RectF(5, 0, 10, 10));
        Assert.Same(b, GdiPlusRegions.Combine(a, b, GdiPlusRegions.Replace));
        Assert.Same(a, GdiPlusRegions.Combine(a, null, GdiPlusRegions.Intersect));
        Assert.Null(GdiPlusRegions.Combine(a, null, GdiPlusRegions.Union));
        Assert.Equal(new RectF(5, 0, 5, 10), GdiPlusRegions.Combine(a, b, GdiPlusRegions.Intersect)!.Bounds);
        Assert.Equal(new RectF(0, 0, 15, 10), GdiPlusRegions.Combine(a, b, GdiPlusRegions.Union)!.Bounds);
        Assert.Equal(new RectF(0, 0, 5, 10), GdiPlusRegions.Combine(a, b, GdiPlusRegions.Exclude)!.Bounds);
        Assert.Equal(new RectF(10, 0, 5, 10), GdiPlusRegions.Combine(a, b, GdiPlusRegions.Complement)!.Bounds);
        Path xor = GdiPlusRegions.Combine(a, b, GdiPlusRegions.Xor)!;
        Assert.True(xor.Contains(new Vector2(2, 5)) && xor.Contains(new Vector2(12, 5)) && !xor.Contains(new Vector2(7, 5)));

        // Excluding from the infinite region leaves a hole in a two-million-unit square.
        Path outside = GdiPlusRegions.Combine(null, a, GdiPlusRegions.Exclude)!;
        Assert.False(outside.Contains(new Vector2(5, 5)));
        Assert.True(outside.Contains(new Vector2(-1000, 5)));
    }
}
