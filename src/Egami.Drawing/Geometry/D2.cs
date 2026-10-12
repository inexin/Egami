using System.Numerics;

namespace Egami.Drawing.Geometry;

/// <summary>A point or vector in double precision, for geometry that float rounding would spoil: lengths, intersections, splits.</summary>
/// <param name="X">The x coordinate.</param>
/// <param name="Y">The y coordinate.</param>
internal readonly record struct D2(double X, double Y)
{
    public static D2 operator +(D2 a, D2 b) => new(a.X + b.X, a.Y + b.Y);

    public static D2 operator -(D2 a, D2 b) => new(a.X - b.X, a.Y - b.Y);

    public static D2 operator -(D2 a) => new(-a.X, -a.Y);

    public static D2 operator *(double s, D2 a) => new(s * a.X, s * a.Y);

    public static D2 operator *(D2 a, double s) => new(s * a.X, s * a.Y);

    public static D2 operator /(D2 a, double s) => new(a.X / s, a.Y / s);

    public static implicit operator D2(Vector2 v) => new(v.X, v.Y);

    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public double LengthSquared => (X * X) + (Y * Y);

    public Vector2 ToVector2() => new((float)X, (float)Y);

    public static double Dot(D2 a, D2 b) => (a.X * b.X) + (a.Y * b.Y);

    public static double Cross(D2 a, D2 b) => (a.X * b.Y) - (a.Y * b.X);

    public static D2 Lerp(D2 a, D2 b, double t) => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    public static double Distance(D2 a, D2 b) => (b - a).Length;

    public D2 Normalized()
    {
        double length = Length;
        return length > 0 ? new D2(X / length, Y / length) : default;
    }
}
