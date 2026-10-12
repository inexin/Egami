using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Advanced;

public class MeshTests
{
    private static readonly RgbaF32 Red = new(1, 0, 0, 1);
    private static readonly RgbaF32 Blue = new(0, 0, 1, 1);

    [Fact]
    public void TwoTrianglesFormingARectAreItsBilinearColor()
    {
        // Colors that change along x only are bilinear and linear at once, so the triangles must give exactly the ramp.
        Vector2[] corners = [new(4, 4), new(60, 4), new(60, 36), new(4, 36)];
        Mesh mesh = new(MeshMode.Triangles, corners, [Red, Blue, Blue, Red], indices: [0, 1, 2, 0, 2, 3]);
        using Raster<Rgba8> drawn = Pixels.Draw(64, 40, c => c.DrawMesh(mesh, Rgba8.White));
        for (int y = 4; y < 36; y++)
        {
            for (int x = 4; x < 60; x++)
            {
                float t = (x + 0.5f - 4) / 56;
                Rgba8 p = drawn.GetPixel(x, y);
                Assert.True(Math.Abs(p.R - ((1 - t) * 255)) <= 1 && p.G == 0 && Math.Abs(p.B - (t * 255)) <= 1 && p.A == 255, $"({x}, {y}): {p}");
            }
        }

        // Outside the rectangle nothing is drawn, and its edges are antialiased as a FillRect's.
        using Raster<Rgba8> half = Pixels.Draw(64, 40, c => c.DrawMesh(new Mesh(MeshMode.Triangles, [new(4.5f, 4), new(60, 4), new(60, 36), new(4.5f, 36)], [Red, Red, Red, Red], indices: [0, 1, 2, 0, 2, 3]), Rgba8.White));
        using Raster<Rgba8> rect = Pixels.Draw(64, 40, c => c.FillRect(RectF.FromEdges(4.5f, 4, 60, 36), Red));
        Pixels.Equal(half, rect);
    }

    [Fact]
    public void LargeMeshesHaveNoCracks()
    {
        // 100 × 50 cells, 10,000 triangles, inner vertices jittered: every pixel inside the outline is fully covered.
        Vector2[] grid = Pixels.Grid(100, 50, 3.7f, 2.5f, seed: 5);
        Mesh mesh = new(MeshMode.Triangles, grid, indices: Pixels.GridIndices(100, 50));
        Assert.Equal(10_000, mesh.TriangleCount);
        using Raster<Rgba8> drawn = Pixels.Draw(380, 190, c =>
        {
            c.Translate(2.3f, 1.6f);
            c.DrawMesh(mesh, Rgba8.White);
        }, Rgba8.Black);
        for (int y = 3; y < 185; y++)
        {
            for (int x = 3; x < 371; x++)
            {
                Assert.Equal(Rgba8.White, drawn.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void StripsAndFansMakeTheSameTrianglesAsLists()
    {
        Vector2[] strip = [new(5, 5), new(5, 35), new(25, 5), new(25, 35), new(45, 5), new(45, 35)];
        RgbaF32[] colors = [Red, Blue, Blue, Red, Red, Blue];
        using Raster<Rgba8> a = Pixels.Draw(50, 40, c => c.DrawMesh(new Mesh(MeshMode.Strip, strip, colors), Rgba8.White));
        using Raster<Rgba8> b = Pixels.Draw(50, 40, c => c.DrawMesh(new Mesh(MeshMode.Triangles, strip, colors, indices: [0, 1, 2, 1, 2, 3, 2, 3, 4, 3, 4, 5]), Rgba8.White));
        Pixels.Equal(a, b);

        Vector2[] fan = [new(25, 20), new(5, 5), new(45, 5), new(45, 35), new(5, 35), new(5, 5)];
        using Raster<Rgba8> c1 = Pixels.Draw(50, 40, c => c.DrawMesh(new Mesh(MeshMode.Fan, fan), Rgba8.Green));
        using Raster<Rgba8> c2 = Pixels.Draw(50, 40, c => c.FillRect(new RectF(5, 5, 40, 30), Rgba8.Green));
        Pixels.Equal(c1, c2);
    }

    [Fact]
    public void ValuesMapThroughTheStops()
    {
        Vector2[] corners = [new(0, 0), new(64, 0), new(64, 8), new(0, 8)];
        Mesh mesh = new(MeshMode.Triangles, corners, values: [0, 1, 1, 0], indices: [0, 1, 2, 0, 2, 3]);
        GradientStop[] stops = [new(0, Red), new(0.5f, new RgbaF32(0, 1, 0, 1)), new(1, Blue)];
        using Raster<Rgba8> drawn = Pixels.Draw(64, 8, c => c.DrawMesh(mesh, Rgba8.White, BlendMode.Modulate, stops));
        using Raster<Rgba8> gradient = Pixels.Draw(64, 8, c => c.FillRect(new RectF(0, 0, 64, 8), new LinearGradient(Vector2.Zero, new Vector2(64, 0), stops)));
        Assert.True(Pixels.MaxDifference(drawn, gradient) <= 1);

        using RasterCanvas<Rgba8> canvas = drawn.CreateCanvas();
        Assert.Throws<ArgumentException>(() => canvas.DrawMesh(mesh, Rgba8.White));
    }

    [Fact]
    public void TextureCoordinatesMapThePaintOntoTheTriangles()
    {
        using Raster<Rgba8> image = Pixels.Photo(20, 16);
        SamplingOptions nearest = new() { Filter = ResampleFilter.Nearest };
        Vector2[] corners = [new(10, 6), new(50, 6), new(50, 38), new(10, 38)];
        Vector2[] tex = [new(0, 0), new(20, 0), new(20, 16), new(0, 16)];
        Mesh mesh = new(MeshMode.Triangles, corners, texCoords: tex, indices: [0, 1, 2, 0, 2, 3]);
        using Raster<Rgba8> meshed = Pixels.Draw(60, 44, c => c.DrawMesh(mesh, new ImagePaint(image, sampling: nearest)));
        using Raster<Rgba8> drawn = Pixels.Draw(60, 44, c => c.DrawImage(image, new RectF(10, 6, 40, 32), nearest));
        Pixels.Equal(meshed, drawn);

        // With colors, the image is modulated by them.
        Mesh tinted = new(MeshMode.Triangles, corners, [Red, Red, Red, Red], tex, indices: [0, 1, 2, 0, 2, 3]);
        using Raster<Rgba8> red = Pixels.Draw(60, 44, c => c.DrawMesh(tinted, new ImagePaint(image, sampling: nearest)));
        Rgba8 p = red.GetPixel(30, 20), q = drawn.GetPixel(30, 20);
        Assert.Equal(q.R, p.R);
        Assert.Equal(0, p.G);
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void TheLaterOfOverlappingTrianglesShows()
    {
        Mesh mesh = new(MeshMode.Triangles, [new(0, 0), new(40, 0), new(0, 40), new(0, 0), new(40, 0), new(40, 40)], [Red, Red, Red, Blue, Blue, Blue]);
        using Raster<Rgba8> drawn = Pixels.Draw(40, 40, c => c.DrawMesh(mesh, Rgba8.White));
        Assert.Equal(new Rgba8(0, 0, 255, 255), drawn.GetPixel(25, 5));
        Assert.Equal(new Rgba8(255, 0, 0, 255), drawn.GetPixel(5, 25));
    }

    [Fact]
    public void ASolidPaintsAlphaScalesTheVertexColors()
    {
        Mesh mesh = new(MeshMode.Triangles, [new(0, 0), new(20, 0), new(20, 20), new(0, 20)], [Red, Red, Red, Red], indices: [0, 1, 2, 0, 2, 3]);
        using Raster<Rgba8> drawn = Pixels.Draw(20, 20, c => c.DrawMesh(mesh, new RgbaF32(0, 1, 0, 0.5f)), Rgba8.Black);
        Rgba8 p = drawn.GetPixel(10, 10);
        Assert.InRange(p.R, 127, 128);
        Assert.Equal(0, p.G);
    }

    [Fact]
    public void LinearColorSpaceInterpolatesInLinearLight()
    {
        Mesh mesh = new(MeshMode.Triangles, [new(0, 0), new(64, 0), new(64, 4), new(0, 4)], [Red, Blue, Blue, Red], indices: [0, 1, 2, 0, 2, 3]);
        using Raster<Rgba8> encoded = Pixels.Draw(64, 4, c => c.DrawMesh(mesh, Rgba8.White));
        using Raster<Rgba8> linear = Pixels.Draw(64, 4, c => c.DrawMesh(mesh, Rgba8.White), options: new CanvasOptions { ColorSpace = ColorSpace.Linear });
        Assert.InRange(encoded.GetPixel(32, 2).R, 120, 130);
        Assert.InRange(linear.GetPixel(32, 2).R, 180, 195);
    }

    [Fact]
    public void MeshesAreChecked()
    {
        Assert.Throws<ArgumentException>(() => new Mesh(MeshMode.Triangles, [Vector2.Zero, Vector2.One, Vector2.UnitX], [Red]));
        Assert.Throws<ArgumentException>(() => new Mesh(MeshMode.Triangles, [Vector2.Zero, Vector2.One, Vector2.UnitX], [Red, Red, Red], values: [0, 0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Mesh(MeshMode.Triangles, [Vector2.Zero, Vector2.One, Vector2.UnitX], indices: [0, 1, 3]));
        Assert.Throws<ArgumentException>(() => new Mesh(MeshMode.Triangles, [new Vector2(float.NaN, 0)]));
        Assert.Equal(0, new Mesh(MeshMode.Strip, [Vector2.Zero, Vector2.One]).TriangleCount);
        Assert.Equal(2, new Mesh(MeshMode.Fan, [Vector2.Zero, Vector2.One, Vector2.UnitX, Vector2.UnitY]).TriangleCount);
    }

    [Fact]
    public void ABackendWithoutMeshesDrawsThemThroughItsFallback()
    {
        Mesh mesh = new(MeshMode.Triangles, [new(2, 2), new(30, 2), new(16, 30)], [Red, Blue, Red]);
        using Canvas.TraceCanvas trace = new(64, 48);
        trace.DrawMesh(mesh, Rgba8.White);
        Assert.Contains(trace.Calls, c => c.StartsWith("DrawImage 30x30", StringComparison.Ordinal));
    }
}
