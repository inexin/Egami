using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Transforms
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AffineTransform()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region transform
        // Turn by 20° and shrink to 40% in one pass. Matrices map source positions to destination positions and compose with *,
        // left first; shrinking averages each pixel's footprint, so fine detail fades instead of aliasing.
        Matrix3x2 matrix = Matrix3x2.CreateRotation(MathF.PI / 9) * Matrix3x2.CreateScale(0.4f);
        using Raster turned = photo.Derive(ops => ops.Transform(matrix), Ct);

        // A 3D tilt, as CSS draws a flat element: rotate about the vertical axis and look at it from 800 pixels away.
        Matrix4x4 tilt = Matrix4x4.CreateTranslation(-320, -240, 0) * Matrix4x4.CreateRotationY(0.5f) * new Matrix4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, -1f / 800, 0, 0, 0, 1);
        using Raster tilted = photo.Derive(ops => ops.Transform(tilt), Ct);
        #endregion

        Assert.True(turned.Width < photo.Width && tilted.Height > 0);
    }

    [Fact]
    public void PerspectiveFromFourPoints()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region homography
        // The four corners of a page photographed at an angle, top-left first and clockwise...
        Vector2[] corners = [new(120, 60), new(560, 40), new(600, 430), new(80, 420)];

        // ...go to the corners of an upright 420 × 594 (A4-shaped) canvas.
        Homography straighten = Homography.FromQuad(corners, new Rect(0, 0, 420, 594));
        using Raster page = photo.Derive(ops => ops.Transform(straighten, new TransformOptions { Viewport = new Rect(0, 0, 420, 594) }), Ct);

        // Homography maps points too, and inverts.
        Vector2 middle = straighten.Map(new Vector2(340, 240));
        bool invertible = straighten.TryInvert(out Homography back);
        #endregion

        Assert.Equal((420, 594), (page.Width, page.Height));
        Assert.True(invertible && back.Map(middle).X > 0);
    }

    [Fact]
    public void Options()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> photo = Raster.Load<Rgba8>("duck.png");

        #region transform-options
        // Keep the input's size (and the matrix's translation), fill the uncovered corners with white, blend in linear light.
        TransformOptions keep = new()
        {
            Viewport = new Rect(0, 0, photo.Width, photo.Height),
            Background = Rgba8.White,
            ColorSpace = ColorSpace.Linear,
        };
        photo.Apply(ops => ops.Transform(Matrix3x2.CreateRotation(0.2f, new Vector2(photo.Width / 2f, photo.Height / 2f)), keep), Ct);

        // Wrap extends the image as a tiling, so a turned copy fills the whole canvas.
        using Raster<Rgba8> pattern = photo.Derive(ops => ops.Transform(Matrix3x2.CreateRotation(0.5f), new TransformOptions
        {
            Viewport = new Rect(0, 0, 800, 600),
            Edges = EdgeMode.Wrap,
            Filter = ResampleFilter.Lanczos3,
        }), Ct);
        #endregion

        Assert.Equal((800, 600), (pattern.Width, pattern.Height));
    }

    [Fact]
    public void Skew()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region skew
        // Lean a banner 12° to the right, filling the uncovered corners with white; the canvas grows to fit.
        using Raster banner = photo.Derive(ops => ops.Skew(12, 0, new TransformOptions { Background = Rgba8.White }), Ct);

        // Skew a region in place: a viewport the size of the area keeps it the same size inside Within.
        using Raster region = photo.Derive(ops => ops.Within(new Rect(100, 100, 200, 120), s => s.Skew(0, 8, new TransformOptions { Viewport = new Rect(0, 0, 200, 120) })), Ct);
        #endregion

        Assert.True(banner.Width > photo.Width);
        Assert.Equal(photo.Width, region.Width);
    }

    [Fact]
    public void QuadDistort()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region quad-distort
        // Pin the image onto a trapezoid, as if it lay on a table: its corners go to these four points (pixel edges, so the
        // image's own corners are (0, 0) and (640, 480)).
        using Raster onTable = photo.Derive(ops => ops.QuadDistort(new(200, 0), new(440, 0), new(640, 300), new(0, 300)), Ct);
        #endregion

        Assert.Equal(640, onTable.Width);
    }
}
