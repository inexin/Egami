using System.Numerics;
using System.Runtime.Intrinsics;

namespace Egami.Barcodes.Reading;

/// <summary>
/// A projective mapping between two quadrilaterals, from the unit-square mappings of Heckbert's "Fundamentals of Texture
/// Mapping and Image Warping" (1989): square to quadrilateral directly, quadrilateral to square by its adjoint.
/// </summary>
internal readonly struct Perspective
{
    private readonly double _a11, _a12, _a13, _a21, _a22, _a23, _a31, _a32, _a33;

    private Perspective(double a11, double a21, double a31, double a12, double a22, double a32, double a13, double a23, double a33)
    {
        (_a11, _a12, _a13, _a21, _a22, _a23, _a31, _a32, _a33) = (a11, a12, a13, a21, a22, a23, a31, a32, a33);
    }

    /// <summary>Maps the quadrilateral <paramref name="from"/> (4 points) onto <paramref name="to"/>, corner to corner.</summary>
    public static Perspective QuadToQuad(ReadOnlySpan<Vector2> from, ReadOnlySpan<Vector2> to) =>
        SquareToQuad(to).Times(SquareToQuad(from).Adjoint());

    /// <summary>Maps (0, 0), (1, 0), (1, 1), (0, 1) onto four points.</summary>
    public static Perspective SquareToQuad(ReadOnlySpan<Vector2> q)
    {
        double x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
        double dx3 = x0 - x1 + x2 - x3, dy3 = y0 - y1 + y2 - y3;
        if (dx3 == 0 && dy3 == 0)
        {
            return new Perspective(x1 - x0, x2 - x1, x0, y1 - y0, y2 - y1, y0, 0, 0, 1);
        }

        double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
        double denominator = (dx1 * dy2) - (dx2 * dy1);
        double a13 = ((dx3 * dy2) - (dx2 * dy3)) / denominator, a23 = ((dx1 * dy3) - (dx3 * dy1)) / denominator;
        return new Perspective(x1 - x0 + (a13 * x1), x3 - x0 + (a23 * x3), x0, y1 - y0 + (a13 * y1), y3 - y0 + (a23 * y3), y0, a13, a23, 1);
    }

    public Vector2 Map(float x, float y)
    {
        double w = (_a13 * x) + (_a23 * y) + _a33;
        return new Vector2((float)(((_a11 * x) + (_a21 * y) + _a31) / w), (float)(((_a12 * x) + (_a22 * y) + _a32) / w));
    }

    /// <summary>
    /// Maps the points (x, <paramref name="y"/>) for each x, as <see cref="Map"/> does one by one, with the same arithmetic in
    /// the same order, four at a time where vectors are fast.
    /// </summary>
    public void MapRow(ReadOnlySpan<float> xs, float y, Span<Vector2> points)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<double> a11 = Vector256.Create(_a11), a12 = Vector256.Create(_a12), a13 = Vector256.Create(_a13);
            Vector256<double> a21y = Vector256.Create(_a21 * y), a22y = Vector256.Create(_a22 * y), a23y = Vector256.Create(_a23 * y);
            Vector256<double> a31 = Vector256.Create(_a31), a32 = Vector256.Create(_a32), a33 = Vector256.Create(_a33);
            for (; i <= xs.Length - 4; i += 4)
            {
                Vector256<double> x = Vector256.Create((double)xs[i], xs[i + 1], xs[i + 2], xs[i + 3]);
                Vector256<double> w = ((a13 * x) + a23y) + a33;
                Vector256<double> px = (((a11 * x) + a21y) + a31) / w, py = (((a12 * x) + a22y) + a32) / w;
                for (int k = 0; k < 4; k++)
                {
                    points[i + k] = new Vector2((float)px.GetElement(k), (float)py.GetElement(k));
                }
            }
        }

        for (; i < xs.Length; i++)
        {
            points[i] = Map(xs[i], y);
        }
    }

    private Perspective Adjoint() => new(
        (_a22 * _a33) - (_a23 * _a32), (_a23 * _a31) - (_a21 * _a33), (_a21 * _a32) - (_a22 * _a31),
        (_a13 * _a32) - (_a12 * _a33), (_a11 * _a33) - (_a13 * _a31), (_a12 * _a31) - (_a11 * _a32),
        (_a12 * _a23) - (_a13 * _a22), (_a13 * _a21) - (_a11 * _a23), (_a11 * _a22) - (_a12 * _a21));

    /// <summary>This mapping after <paramref name="first"/>.</summary>
    private Perspective Times(Perspective first) => new(
        (_a11 * first._a11) + (_a21 * first._a12) + (_a31 * first._a13),
        (_a11 * first._a21) + (_a21 * first._a22) + (_a31 * first._a23),
        (_a11 * first._a31) + (_a21 * first._a32) + (_a31 * first._a33),
        (_a12 * first._a11) + (_a22 * first._a12) + (_a32 * first._a13),
        (_a12 * first._a21) + (_a22 * first._a22) + (_a32 * first._a23),
        (_a12 * first._a31) + (_a22 * first._a32) + (_a32 * first._a33),
        (_a13 * first._a11) + (_a23 * first._a12) + (_a33 * first._a13),
        (_a13 * first._a21) + (_a23 * first._a22) + (_a33 * first._a23),
        (_a13 * first._a31) + (_a23 * first._a32) + (_a33 * first._a33));

    /// <summary>Samples a grid of modules at their centers, (x + 0.5, y + 0.5) in module units mapped to the image.</summary>
    public BarcodeModules? Sample(BitImage bits, int columns, int rows)
    {
        bool[] cells = new bool[columns * rows];
        int outside = 0;
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                Vector2 p = Map(x + 0.5f, y + 0.5f);
                int px = (int)MathF.Floor(p.X), py = (int)MathF.Floor(p.Y);
                if ((uint)px >= (uint)bits.Width || (uint)py >= (uint)bits.Height)
                {
                    if (++outside > (columns * rows) / 50)
                    {
                        return null;
                    }

                    continue;
                }

                cells[(y * columns) + x] = bits[px, py];
            }
        }

        return BarcodeModules.Wrap(columns, rows, cells);
    }
}
