using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tests.Advanced;

/// <summary>Helpers the 2.6.4 tests share: test images, drawing into a fresh raster, and comparing pixels.</summary>
internal static class Pixels
{
    internal static Raster<Rgba8> Photo(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 255 / Math.Max(1, height - 1)), (byte)((((x / 3) + (y / 3)) % 2) * 200), 255);
            }
        }

        return raster;
    }

    internal static Raster<Rgba8> Draw(int width, int height, Action<Egami.Drawing.Canvas> draw, Rgba8? background = null, CanvasOptions? options = null)
    {
        Raster<Rgba8> raster = Raster.Create(width, height, background ?? Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas(options);
        draw(canvas);
        return raster;
    }

    /// <summary>The largest channel difference between two rasters over a region, or the whole of them.</summary>
    internal static int MaxDifference(Raster<Rgba8> a, Raster<Rgba8> b, Rect? region = null)
    {
        Rect r = region ?? new Rect(0, 0, a.Width, a.Height);
        int max = 0;
        for (int y = r.Y; y < r.Bottom; y++)
        {
            for (int x = r.X; x < r.Right; x++)
            {
                Rgba8 p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                max = Math.Max(max, Math.Max(Math.Max(Math.Abs(p.R - q.R), Math.Abs(p.G - q.G)), Math.Max(Math.Abs(p.B - q.B), Math.Abs(p.A - q.A))));
            }
        }

        return max;
    }

    internal static double MeanDifference(Raster<Rgba8> a, Raster<Rgba8> b, Rect? region = null)
    {
        Rect r = region ?? new Rect(0, 0, a.Width, a.Height);
        double sum = 0;
        for (int y = r.Y; y < r.Bottom; y++)
        {
            for (int x = r.X; x < r.Right; x++)
            {
                Rgba8 p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) + Math.Abs(p.A - q.A);
            }
        }

        return sum / (4.0 * r.Width * r.Height);
    }

    internal static void Equal(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Assert.True(a.GetPixel(x, y) == b.GetPixel(x, y), $"({x}, {y}): {a.GetPixel(x, y)} against {b.GetPixel(x, y)}");
            }
        }
    }

    internal static Vector2[] Grid(int columns, int rows, float cell, float jitter, int seed)
    {
        Random random = new(seed);
        Vector2[] points = new Vector2[(columns + 1) * (rows + 1)];
        for (int j = 0; j <= rows; j++)
        {
            for (int i = 0; i <= columns; i++)
            {
                bool inside = i > 0 && j > 0 && i < columns && j < rows;
                float dx = inside ? (float)((random.NextDouble() - 0.5) * jitter) : 0, dy = inside ? (float)((random.NextDouble() - 0.5) * jitter) : 0;
                points[(j * (columns + 1)) + i] = new Vector2((i * cell) + dx, (j * cell) + dy);
            }
        }

        return points;
    }

    internal static int[] GridIndices(int columns, int rows)
    {
        List<int> indices = [];
        for (int j = 0; j < rows; j++)
        {
            for (int i = 0; i < columns; i++)
            {
                int a = (j * (columns + 1)) + i, b = a + 1, d = a + columns + 1, c = d + 1;
                indices.AddRange([a, b, c, a, c, d]);
            }
        }

        return [.. indices];
    }
}
