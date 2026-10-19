using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.Barcodes.Tests;

/// <summary>Photo-like test images: a barcode on a label in perspective, blurred and noisy, on a darker background.</summary>
internal static class Photos
{
    /// <summary>Renders the barcode (with its quiet zone) onto the quadrilateral, clockwise from top left.</summary>
    public static Raster<Gray8> Render(Barcode barcode, int width, int height, ReadOnlySpan<Vector2> quad, float blur = 0.8f, int noise = 8, int seed = 1, byte background = 90)
    {
        Path symbol = barcode.ToPath(1);
        int columns = barcode.Modules.Width + (2 * barcode.QuietZone);
        int rows = barcode.Modules.IsLinear ? barcode.Modules.Height : barcode.Modules.Height + (2 * barcode.QuietZone);
        Homography homography = Homography.FromQuad(new Rect(0, 0, columns, rows), quad);
        Raster<Gray8> raster = Raster.Create(width, height, new Gray8(background));
        using (RasterCanvas<Gray8> canvas = raster.CreateCanvas())
        {
            Path paper = new PathBuilder().AddRect(new RectF(0, 0, columns, rows)).ToPath();
            canvas.FillPath(paper.Transform(homography), new Rgba8(235, 235, 235, 255));
            canvas.FillPath(symbol.Transform(homography), new Rgba8(35, 35, 35, 255));
        }

        if (blur > 0)
        {
            raster.Apply(o => o.Blur(blur));
        }

        if (noise > 0 && raster.TryGetContiguous(out Span<Gray8> pixels))
        {
            var random = new Random(seed);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Gray8((byte)Math.Clamp(pixels[i].L + random.Next(-noise, noise + 1), 0, 255));
            }
        }

        return raster;
    }

    /// <summary>The corners of a rectangle of a size centered at a point, turned by an angle and leaning back by a tilt.</summary>
    public static Vector2[] Quad(Vector2 center, float width, float height, float degrees, float tilt = 0)
    {
        float radians = degrees * MathF.PI / 180;
        var x = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
        var y = new Vector2(-x.Y, x.X);
        float top = width * (1 - tilt), bottom = width;
        return
        [
            center - (x * top / 2) - (y * height / 2),
            center + (x * top / 2) - (y * height / 2),
            center + (x * bottom / 2) + (y * height / 2),
            center - (x * bottom / 2) + (y * height / 2),
        ];
    }
}
