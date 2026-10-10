namespace Egami.Colors;

/// <summary>Three doubles: an XYZ, Lab or RGB triple in the color math.</summary>
internal readonly record struct Vector3d(double X, double Y, double Z)
{
    public static Vector3d operator *(Vector3d v, double s) => new(v.X * s, v.Y * s, v.Z * s);

    public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
}

/// <summary>CIE conversions between XYZ and L*a*b* (CIE 15), on <see cref="Vector3d"/> with Lab as (L, a, b).</summary>
internal static class Cie
{
    /// <summary>The ICC connection space white, D50 as ICC headers and Little CMS give it.</summary>
    public static readonly Vector3d D50 = new(0.9642, 1.0, 0.8249);

    private const double Epsilon = 216.0 / 24389;
    private const double Kappa = 24389.0 / 27;

    public static Vector3d XyzToLab(Vector3d xyz, Vector3d white)
    {
        double fx = F(xyz.X / white.X), fy = F(xyz.Y / white.Y), fz = F(xyz.Z / white.Z);
        return new Vector3d((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));

        static double F(double t) => t > Epsilon ? Math.Cbrt(t) : ((Kappa * t) + 16) / 116;
    }

    public static Vector3d LabToXyz(Vector3d lab, Vector3d white)
    {
        double fy = (lab.X + 16) / 116;
        double fx = fy + (lab.Y / 500), fz = fy - (lab.Z / 200);
        return new Vector3d(white.X * G(fx), white.Y * G(fy), white.Z * G(fz));

        static double G(double f) => f > 6.0 / 29 ? f * f * f : ((116 * f) - 16) / Kappa;
    }
}

/// <summary>A 3×3 matrix of doubles, row-major, for color space conversion.</summary>
internal readonly struct Matrix3d
{
    private readonly double _m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22;

    public Matrix3d(double m00, double m01, double m02, double m10, double m11, double m12, double m20, double m21, double m22)
    {
        (_m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22) = (m00, m01, m02, m10, m11, m12, m20, m21, m22);
    }

    public static Matrix3d Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public double this[int row, int column] => (row, column) switch
    {
        (0, 0) => _m00,
        (0, 1) => _m01,
        (0, 2) => _m02,
        (1, 0) => _m10,
        (1, 1) => _m11,
        (1, 2) => _m12,
        (2, 0) => _m20,
        (2, 1) => _m21,
        (2, 2) => _m22,
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    /// <summary>A diagonal matrix.</summary>
    public static Matrix3d Diagonal(Vector3d d) => new(d.X, 0, 0, 0, d.Y, 0, 0, 0, d.Z);

    /// <summary>A matrix whose columns are the three vectors.</summary>
    public static Matrix3d FromColumns(Vector3d a, Vector3d b, Vector3d c) => new(a.X, b.X, c.X, a.Y, b.Y, c.Y, a.Z, b.Z, c.Z);

    public static Matrix3d operator *(Matrix3d a, Matrix3d b)
    {
        return new Matrix3d(
            (a._m00 * b._m00) + (a._m01 * b._m10) + (a._m02 * b._m20),
            (a._m00 * b._m01) + (a._m01 * b._m11) + (a._m02 * b._m21),
            (a._m00 * b._m02) + (a._m01 * b._m12) + (a._m02 * b._m22),
            (a._m10 * b._m00) + (a._m11 * b._m10) + (a._m12 * b._m20),
            (a._m10 * b._m01) + (a._m11 * b._m11) + (a._m12 * b._m21),
            (a._m10 * b._m02) + (a._m11 * b._m12) + (a._m12 * b._m22),
            (a._m20 * b._m00) + (a._m21 * b._m10) + (a._m22 * b._m20),
            (a._m20 * b._m01) + (a._m21 * b._m11) + (a._m22 * b._m21),
            (a._m20 * b._m02) + (a._m21 * b._m12) + (a._m22 * b._m22));
    }

    public static Vector3d operator *(Matrix3d m, Vector3d v)
    {
        return new Vector3d(
            (m._m00 * v.X) + (m._m01 * v.Y) + (m._m02 * v.Z),
            (m._m10 * v.X) + (m._m11 * v.Y) + (m._m12 * v.Z),
            (m._m20 * v.X) + (m._m21 * v.Y) + (m._m22 * v.Z));
    }

    /// <summary>Gets whether the matrix has an inverse: a determinant that is finite and not 0.</summary>
    public bool IsInvertible
    {
        get
        {
            double det = (_m00 * ((_m11 * _m22) - (_m12 * _m21))) + (_m01 * ((_m12 * _m20) - (_m10 * _m22))) + (_m02 * ((_m10 * _m21) - (_m11 * _m20)));
            return det != 0 && double.IsFinite(det);
        }
    }

    public Matrix3d Inverse()
    {
        double c00 = (_m11 * _m22) - (_m12 * _m21);
        double c01 = (_m12 * _m20) - (_m10 * _m22);
        double c02 = (_m10 * _m21) - (_m11 * _m20);
        double det = (_m00 * c00) + (_m01 * c01) + (_m02 * c02);
        if (det == 0 || !double.IsFinite(det))
        {
            throw new InvalidOperationException("The matrix cannot be inverted.");
        }

        double inv = 1.0 / det;
        return new Matrix3d(
            c00 * inv, ((_m02 * _m21) - (_m01 * _m22)) * inv, ((_m01 * _m12) - (_m02 * _m11)) * inv,
            c01 * inv, ((_m00 * _m22) - (_m02 * _m20)) * inv, ((_m02 * _m10) - (_m00 * _m12)) * inv,
            c02 * inv, ((_m01 * _m20) - (_m00 * _m21)) * inv, ((_m00 * _m11) - (_m01 * _m10)) * inv);
    }

    /// <summary>The Bradford chromatic adaptation from one white to another, as ICC and CSS Color 4 use it.</summary>
    public static Matrix3d Bradford(Vector3d fromWhite, Vector3d toWhite)
    {
        Matrix3d b = new(0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296);
        Vector3d from = b * fromWhite, to = b * toWhite;
        return b.Inverse() * Diagonal(new Vector3d(to.X / from.X, to.Y / from.Y, to.Z / from.Z)) * b;
    }
}
