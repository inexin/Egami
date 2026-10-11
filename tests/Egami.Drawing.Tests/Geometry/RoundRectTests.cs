using System.Numerics;

namespace Egami.Drawing.Tests.Geometry;

public class RoundRectTests
{
    /// <summary>CSS Backgrounds 3: one factor, the smallest side over the sum of its radii, scales every radius.</summary>
    [Theory]
    [InlineData(100, 50, 20, 20, 20, 20, 1.0)]
    [InlineData(100, 50, 30, 30, 30, 30, 50.0 / 60)]
    [InlineData(100, 50, 40, 40, 40, 40, 50.0 / 80)]
    [InlineData(100, 50, 80, 40, 10, 10, 50.0 / 90)]
    [InlineData(10, 100, 0, 30, 90, 0, 10.0 / 90)]
    public void OverlappingRadiiScaleAsCssDoes(float width, float height, float tl, float tr, float br, float bl, double f)
    {
        RoundRect r = new(new RectF(0, 0, width, height), new CornerRadii(tl, tr, br, bl));

        // Each radius is the requested one times the same factor (a circular corner on a narrow side stays circular).
        Assert.Equal(tl * f, r.Radii.TopLeft.X, 3);
        Assert.Equal(tr * f, r.Radii.TopRight.Y, 3);
        Assert.Equal(br * f, r.Radii.BottomRight.X, 3);
        Assert.Equal(bl * f, r.Radii.BottomLeft.Y, 3);
    }

    [Fact]
    public void GeneratedCasesNeverOverlapAfterScaling()
    {
        Random random = new(3);
        for (int i = 0; i < 2000; i++)
        {
            float w = (float)(random.NextDouble() * 200), h = (float)(random.NextDouble() * 200);
            CornerRadii radii = new(Next(random), Next(random), Next(random), Next(random));
            RoundRect r = new(new RectF(0, 0, w, h), radii);
            if (r.IsEmpty)
            {
                continue;
            }

            CornerRadii s = r.Radii;
            float slack = 1e-3f;
            Assert.True(s.TopLeft.X + s.TopRight.X <= w + slack);
            Assert.True(s.BottomLeft.X + s.BottomRight.X <= w + slack);
            Assert.True(s.TopLeft.Y + s.BottomLeft.Y <= h + slack);
            Assert.True(s.TopRight.Y + s.BottomRight.Y <= h + slack);
        }

        static Vector2 Next(Random random) => new((float)(random.NextDouble() * 300), (float)(random.NextDouble() * 300));
    }

    [Fact]
    public void KindsAreRecognized()
    {
        Assert.Equal(RoundRectKind.Empty, new RoundRect(new RectF(0, 0, 0, 10), 3).Kind);
        Assert.Equal(RoundRectKind.Rect, new RoundRect(new RectF(0, 0, 10, 10), 0).Kind);
        Assert.Equal(RoundRectKind.Rect, new RoundRect(new RectF(0, 0, 10, 10), 5, 0).Kind);
        Assert.Equal(RoundRectKind.Oval, RoundRect.Oval(new RectF(0, 0, 30, 10)).Kind);
        Assert.Equal(RoundRectKind.Oval, new RoundRect(new RectF(0, 0, 10, 10), 50).Kind);
        Assert.Equal(RoundRectKind.Simple, new RoundRect(new RectF(0, 0, 30, 10), 2).Kind);
        Assert.Equal(RoundRectKind.Complex, new RoundRect(new RectF(0, 0, 30, 10), new CornerRadii(1, 2, 3, 4)).Kind);
        Assert.Equal(RoundRectKind.Rect, new RoundRect(new RectF(0, 0, 30, 10), -4).Kind);
    }

    [Fact]
    public void ContainsFollowsTheCorners()
    {
        RoundRect r = new(new RectF(0, 0, 20, 20), 10);
        Assert.True(r.Contains(new Vector2(10, 10)));
        Assert.False(r.Contains(new Vector2(1, 1)));
        Assert.True(r.Contains(new Vector2(3, 3)));
        Assert.True(r.Contains(new Vector2(0, 10)));
        Assert.False(r.Contains(new Vector2(21, 10)));
        Assert.Throws<ArgumentException>(() => new RoundRect(new RectF(0, 0, float.NaN, 1), 1));
    }

    [Fact]
    public void RoundRectPathsRunBothWaysAndMatchContains()
    {
        RoundRect r = new(new RectF(2, 3, 40, 24), new CornerRadii(new Vector2(8, 5), Vector2.Zero, new Vector2(12, 12), new Vector2(3, 9)));
        Path clockwise = Path.RoundRect(r);
        Path counter = new PathBuilder().AddRoundRect(r, PathDirection.CounterClockwise).ToPath();
        Random random = new(9);
        for (int i = 0; i < 5000; i++)
        {
            Vector2 p = new((float)(random.NextDouble() * 46), (float)(random.NextDouble() * 30));
            bool inside = r.Contains(p);
            Assert.Equal(inside, clockwise.Contains(p) || Near(r, p));
            Assert.Equal(clockwise.Contains(p), counter.Contains(p));
        }

        Path both = new PathBuilder().AddRoundRect(r).AddRoundRect(r, PathDirection.CounterClockwise).ToPath();
        Assert.False(both.Contains(r.Bounds.Center));
    }

    [Fact]
    public void RectFOperations()
    {
        RectF a = new(0, 0, 10, 10), b = new(5, 5, 10, 10);
        Assert.Equal(new RectF(5, 5, 5, 5), a.Intersect(b));
        Assert.Equal(new RectF(0, 0, 15, 15), a.Union(b));
        Assert.Equal(RectF.Empty, a.Intersect(new RectF(20, 20, 1, 1)));
        Assert.True(a.Contains(new Vector2(0, 0)));
        Assert.False(a.Contains(new Vector2(10, 5)));
        Assert.Equal(new Rect(-1, 2, 13, 4), new RectF(-0.5f, 2, 12, 3.2f).RoundOut());
        RectF fromInt = new Rect(1, 2, 3, 4);
        Assert.Equal(new RectF(1, 2, 3, 4), fromInt);
        Assert.Equal(new RectF(-1, -2, 12, 14), a.Inflate(1, 2));
        Assert.True(new RectF(0, 0, float.NaN, 1).IsEmpty);
    }

    // Points within a hair of the curve may land either side of a cubic approximating the ellipse.
    private static bool Near(RoundRect r, Vector2 p)
    {
        return r.Contains(p) && !r.Inflate(-0.01f).Contains(p);
    }
}
