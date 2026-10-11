using System.Numerics;

using Egami.Operations;

namespace Egami.Tests.Operations;

public class HomographyTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void FactoriesMatchMatrix3x2()
    {
        AssertNear(Homography.FromAffine(Matrix3x2.CreateRotation(0.7f)), Homography.CreateRotation(0.7), 1e-6);
        AssertNear(Homography.FromAffine(Matrix3x2.CreateSkew(0.3f, -0.2f)), Homography.CreateSkew(0.3, -0.2), 1e-6);
        AssertNear(Homography.FromAffine(Matrix3x2.CreateScale(2, 3, new Vector2(5, 7))), Homography.CreateScale(2, 3, new Vector2(5, 7)), 1e-6);
        AssertNear(Homography.FromAffine(Matrix3x2.CreateRotation(1.1f, new Vector2(40, 30))), Homography.CreateRotation(1.1, new Vector2(40, 30)), 1e-5);
        AssertNear(Homography.FromAffine(Matrix3x2.CreateTranslation(4, -9)), Homography.CreateTranslation(4, -9), 0);
    }

    [Fact]
    public void ProductsApplyLeftFirst()
    {
        Matrix3x2 a = Matrix3x2.CreateRotation(0.4f) * Matrix3x2.CreateTranslation(10, 20);
        Matrix3x2 b = Matrix3x2.CreateScale(2, 0.5f);
        AssertNear(Homography.FromAffine(a * b), Homography.FromAffine(a) * Homography.FromAffine(b), 1e-5);

        // Hand-computed: scale by 2 then move by (3, 4) sends (1, 1) to (5, 6).
        Homography h = Homography.CreateScale(2, 2) * Homography.CreateTranslation(3, 4);
        Assert.Equal(new Vector2(5, 6), h.Map(new Vector2(1, 1)));
    }

    [Fact]
    public void RotationIsClockwiseOnScreen()
    {
        // y grows downward, so a quarter turn clockwise sends the point to the right of the origin to the point below it.
        Vector2 p = Homography.CreateRotation(Math.PI / 2).Map(new Vector2(1, 0));
        Assert.Equal(0, p.X, 1e-6);
        Assert.Equal(1, p.Y, 1e-6);
    }

    [Fact]
    public void FromQuadMapsItsPoints()
    {
        Vector2[] source = [new(412, 230), new(2950, 180), new(3080, 3900), new(300, 3850)];
        Vector2[] destination = [new(0, 0), new(2480, 0), new(2480, 3508), new(0, 3508)];
        Homography h = Homography.FromQuad(source, destination);
        for (int i = 0; i < 4; i++)
        {
            AssertMaps(h, source[i], destination[i]);
        }

        Assert.False(h.IsAffine);
        Assert.Equal(h, Homography.FromQuad(source, new Rect(0, 0, 2480, 3508)));
        Homography back = Homography.FromQuad(new Rect(0, 0, 2480, 3508), source);
        for (int i = 0; i < 4; i++)
        {
            AssertMaps(back, destination[i], source[i]);
        }
    }

    [Fact]
    public void FromQuadOfAParallelogramIsAffine()
    {
        Homography h = Homography.FromQuad(new Rect(0, 0, 10, 10), [new(5, 0), new(15, 2), new(13, 12), new(3, 10)]);
        Assert.True(h.IsAffine);
        AssertMaps(h, new Vector2(10, 10), new Vector2(13, 12));
    }

    [Fact]
    public void FromQuadRejectsDegenerateQuads()
    {
        Assert.Throws<ArgumentException>(() => Homography.FromQuad(new Rect(0, 0, 4, 4), [new(0, 0), new(1, 1), new(2, 2), new(0, 3)]));
        Assert.Throws<ArgumentException>(() => Homography.FromQuad(new Rect(0, 0, 4, 4), [new(0, 0), new(1, 1), new(2, 2)]));
        Assert.Throws<ArgumentException>(() => Homography.FromQuad(new Rect(0, 0, 0, 4), [new(0, 0), new(1, 0), new(1, 1), new(0, 1)]));
        Assert.Throws<ArgumentException>(() => Homography.FromQuad(new Rect(0, 0, 4, 4), [new(0, 0), new(float.NaN, 0), new(1, 1), new(0, 1)]));
    }

    [Fact]
    public void Matrix4x4ProjectsThePlaneAsCssDoes()
    {
        // CSS: perspective(500px) rotateY(30deg), about the origin. matrix3d's row-vector layout matches Matrix4x4, and a point
        // (x, y, 0, 1) times it lands at (X / W, Y / W).
        Matrix4x4 rotate = Matrix4x4.CreateRotationY(MathF.PI / 6);
        Matrix4x4 perspective = Matrix4x4.Identity;
        perspective.M34 = -1f / 500;
        Matrix4x4 m = rotate * perspective;
        Homography h = Homography.FromMatrix4x4(m);
        foreach (Vector2 p in (Vector2[])[new(0, 0), new(100, 0), new(100, 80), new(-40, 25)])
        {
            Vector4 q = Vector4.Transform(new Vector4(p.X, p.Y, 0, 1), m);
            AssertMaps(h, p, new Vector2(q.X / q.W, q.Y / q.W), 1e-3);
        }

        // By hand: rotateY(30°) shrinks x by cos 30° and tilts it away in z; perspective divides by 1 + x sin 30° / 500.
        double x = 100;
        double w = 1 + (x * Math.Sin(Math.PI / 6) / 500);
        Assert.Equal(x * Math.Cos(Math.PI / 6) / w, h.Map(new Vector2(100, 0)).X, 1e-3);
    }

    [Fact]
    public void RandomMatricesRoundTrip()
    {
        Random random = new(25);
        for (int i = 0; i < 1000; i++)
        {
            Homography h = new(
                Next(random, 3), Next(random, 3), Next(random, 0.002), Next(random, 3), Next(random, 3), Next(random, 0.002),
                Next(random, 500), Next(random, 500), 1 + Next(random, 0.5));
            Assert.True(h.TryInvert(out Homography inverse));
            AssertNear(Homography.Identity, (h * inverse).Normalized(), 1e-9);
            Vector2 p = new((float)Next(random, 1000), (float)Next(random, 1000));
            (double x, double y) = h.Map(p.X, p.Y);
            (double bx, double by) = inverse.Map(x, y);
            Assert.True(Math.Abs(bx - p.X) < 1e-6 * (1 + Math.Abs(p.X)) && Math.Abs(by - p.Y) < 1e-6 * (1 + Math.Abs(p.Y)), $"{h}: {p} came back as ({bx}, {by})");
        }
    }

    [Fact]
    public void SingularMatricesDoNotInvert()
    {
        Assert.False(Homography.CreateScale(0, 2).TryInvert(out Homography inverse));
        Assert.Equal(default, inverse);
        Assert.False(default(Homography).TryInvert(out _));
        Assert.False(new Homography(1, 2, 0, 2, 4, 0, 5, 6, 1).TryInvert(out _));
        Assert.True(Homography.CreateScale(1e-6, 1e-6) * Homography.CreateTranslation(4000, 3000) is var tiny && tiny.TryInvert(out _));
    }

    [Fact]
    public void PointsAtInfinityAreNaN()
    {
        Homography h = new(1, 0, 1, 0, 1, 0, 0, 0, 1);
        Vector2 p = h.Map(new Vector2(-1, 5));
        Assert.True(float.IsNaN(p.X) && float.IsNaN(p.Y));
    }

    [Fact]
    public void EqualityAndText()
    {
        Assert.True(Homography.Identity == Homography.CreateScale(1, 1));
        Assert.True(Homography.Identity != Homography.CreateScale(1, 2));
        Assert.True(Homography.Identity.IsIdentity);
        Assert.False(default(Homography).IsIdentity);
        Assert.Equal("{ {1 0 0} {0 1 0} {0 0 1} }", Homography.Identity.ToString());
        Assert.Equal(Homography.Identity.GetHashCode(), Homography.CreateTranslation(0, 0).GetHashCode());
        Assert.Equal(-2, Homography.CreateScale(1, -2).Determinant, Tolerance);
    }

    private static double Next(Random random, double range) => ((random.NextDouble() * 2) - 1) * range;

    private static void AssertMaps(Homography h, Vector2 from, Vector2 to, double tolerance = Tolerance)
    {
        (double x, double y) = h.Map(from.X, from.Y);
        Assert.True(Math.Abs(x - to.X) <= tolerance * Math.Max(1, Math.Abs(to.X)) && Math.Abs(y - to.Y) <= tolerance * Math.Max(1, Math.Abs(to.Y)), $"{from} → ({x}, {y}), expected {to}");
    }

    private static void AssertNear(Homography expected, Homography actual, double tolerance)
    {
        double[] e = [expected.M11, expected.M12, expected.M13, expected.M21, expected.M22, expected.M23, expected.M31, expected.M32, expected.M33];
        double[] a = [actual.M11, actual.M12, actual.M13, actual.M21, actual.M22, actual.M23, actual.M31, actual.M32, actual.M33];
        for (int i = 0; i < 9; i++)
        {
            Assert.True(Math.Abs(e[i] - a[i]) <= tolerance * Math.Max(1, Math.Abs(e[i])), $"element {i}: {a[i]}, expected {e[i]} ({actual} against {expected})");
        }
    }
}
