using System.Numerics;

using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

using Point = System.Drawing.Point;

/// <summary>
/// A 3 by 2 affine transform, as System.Drawing's Matrix: row vectors times the matrix, so <see cref="MatrixOrder.Prepend"/> (the
/// default) applies a new transform before the existing one.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="transforms"/>
/// </example>
public sealed class Matrix : MarshalByRefObject, IDisposable
{
    private Matrix3x2 _m;

    /// <summary>Initializes a new instance of the <see cref="Matrix"/> class: the identity.</summary>
    public Matrix()
    {
        _m = Matrix3x2.Identity;
    }

    /// <summary>Initializes a new instance of the <see cref="Matrix"/> class from its six elements.</summary>
    /// <param name="m11">The first row's first element.</param>
    /// <param name="m12">The first row's second element.</param>
    /// <param name="m21">The second row's first element.</param>
    /// <param name="m22">The second row's second element.</param>
    /// <param name="dx">The horizontal translation.</param>
    /// <param name="dy">The vertical translation.</param>
    public Matrix(float m11, float m12, float m21, float m22, float dx, float dy)
    {
        _m = new Matrix3x2(m11, m12, m21, m22, dx, dy);
    }

    /// <summary>Initializes a new instance of the <see cref="Matrix"/> class from a .NET matrix.</summary>
    /// <param name="matrix">The matrix.</param>
    public Matrix(Matrix3x2 matrix)
    {
        _m = matrix;
    }

    /// <summary>Initializes a new instance of the <see cref="Matrix"/> class that maps a rectangle to a parallelogram.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="plgpts">Where its top-left, top-right and bottom-left corners go.</param>
    public Matrix(RectangleF rect, PointF[] plgpts)
    {
        _m = Map(rect, plgpts);
    }

    /// <summary>Initializes a new instance of the <see cref="Matrix"/> class that maps a rectangle to a parallelogram.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="plgpts">Where its top-left, top-right and bottom-left corners go.</param>
    public Matrix(Rectangle rect, Point[] plgpts)
    {
        ArgumentNullException.ThrowIfNull(plgpts);
        _m = Map(rect, [.. plgpts.Select(p => (PointF)p)]);
    }

    /// <summary>Gets the six elements: m11, m12, m21, m22, dx and dy.</summary>
    public float[] Elements => [_m.M11, _m.M12, _m.M21, _m.M22, _m.M31, _m.M32];

    /// <summary>Gets or sets the matrix as a .NET matrix.</summary>
    public Matrix3x2 MatrixElements
    {
        get => _m;
        set => _m = value;
    }

    /// <summary>Gets the horizontal translation.</summary>
    public float OffsetX => _m.M31;

    /// <summary>Gets the vertical translation.</summary>
    public float OffsetY => _m.M32;

    /// <summary>Gets a value indicating whether the matrix is the identity.</summary>
    public bool IsIdentity => _m.IsIdentity;

    /// <summary>Gets a value indicating whether the matrix has an inverse.</summary>
    public bool IsInvertible => Matrix3x2.Invert(_m, out _);

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public Matrix Clone() => new(_m);

    /// <summary>Does nothing; the matrix holds no unmanaged memory.</summary>
    public void Dispose()
    {
    }

    /// <summary>Makes the matrix the identity.</summary>
    public void Reset() => _m = Matrix3x2.Identity;

    /// <summary>Multiplies by another matrix, applied before this one.</summary>
    /// <param name="matrix">The other matrix.</param>
    public void Multiply(Matrix matrix) => Multiply(matrix, MatrixOrder.Prepend);

    /// <summary>Multiplies by another matrix, applied before or after this one.</summary>
    /// <param name="matrix">The other matrix.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void Multiply(Matrix matrix, MatrixOrder order)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        Apply(matrix._m, order);
    }

    /// <summary>Translates, before this transform.</summary>
    /// <param name="offsetX">The horizontal distance.</param>
    /// <param name="offsetY">The vertical distance.</param>
    public void Translate(float offsetX, float offsetY) => Translate(offsetX, offsetY, MatrixOrder.Prepend);

    /// <summary>Translates, before or after this transform.</summary>
    /// <param name="offsetX">The horizontal distance.</param>
    /// <param name="offsetY">The vertical distance.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void Translate(float offsetX, float offsetY, MatrixOrder order) => Apply(Matrix3x2.CreateTranslation(offsetX, offsetY), order);

    /// <summary>Scales, before this transform.</summary>
    /// <param name="scaleX">The horizontal factor.</param>
    /// <param name="scaleY">The vertical factor.</param>
    public void Scale(float scaleX, float scaleY) => Scale(scaleX, scaleY, MatrixOrder.Prepend);

    /// <summary>Scales, before or after this transform.</summary>
    /// <param name="scaleX">The horizontal factor.</param>
    /// <param name="scaleY">The vertical factor.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void Scale(float scaleX, float scaleY, MatrixOrder order) => Apply(Matrix3x2.CreateScale(scaleX, scaleY), order);

    /// <summary>Rotates clockwise about the origin, before this transform.</summary>
    /// <param name="angle">The angle in degrees.</param>
    public void Rotate(float angle) => Rotate(angle, MatrixOrder.Prepend);

    /// <summary>Rotates clockwise about the origin, before or after this transform.</summary>
    /// <param name="angle">The angle in degrees.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void Rotate(float angle, MatrixOrder order) => Apply(Rotation(angle), order);

    /// <summary>Rotates clockwise about a point, before this transform.</summary>
    /// <param name="angle">The angle in degrees.</param>
    /// <param name="point">The point.</param>
    public void RotateAt(float angle, PointF point) => RotateAt(angle, point, MatrixOrder.Prepend);

    /// <summary>Rotates clockwise about a point, before or after this transform.</summary>
    /// <param name="angle">The angle in degrees.</param>
    /// <param name="point">The point.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void RotateAt(float angle, PointF point, MatrixOrder order) =>
        Apply(Matrix3x2.CreateTranslation(-point.X, -point.Y) * Rotation(angle) * Matrix3x2.CreateTranslation(point.X, point.Y), order);

    /// <summary>Shears, before this transform: x gains <paramref name="shearX"/> × y and y gains <paramref name="shearY"/> × x.</summary>
    /// <param name="shearX">The horizontal shear.</param>
    /// <param name="shearY">The vertical shear.</param>
    public void Shear(float shearX, float shearY) => Shear(shearX, shearY, MatrixOrder.Prepend);

    /// <summary>Shears, before or after this transform.</summary>
    /// <param name="shearX">The horizontal shear.</param>
    /// <param name="shearY">The vertical shear.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void Shear(float shearX, float shearY, MatrixOrder order) => Apply(new Matrix3x2(1, shearY, shearX, 1, 0, 0), order);

    /// <summary>Inverts the matrix.</summary>
    /// <exception cref="ArgumentException">The matrix has no inverse.</exception>
    public void Invert()
    {
        if (!Matrix3x2.Invert(_m, out Matrix3x2 inverse))
        {
            throw GdiErrors.InvalidParameter();
        }

        _m = inverse;
    }

    /// <summary>Transforms points in place.</summary>
    /// <param name="pts">The points.</param>
    public void TransformPoints(PointF[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformPoints(pts.AsSpan());
    }

    /// <summary>Transforms points in place.</summary>
    /// <param name="pts">The points.</param>
    public void TransformPoints(ReadOnlySpan<PointF> pts) => Points(pts, _m);

    /// <summary>Transforms points in place, rounding to whole coordinates.</summary>
    /// <param name="pts">The points.</param>
    public void TransformPoints(Point[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformPoints(pts.AsSpan());
    }

    /// <summary>Transforms points in place, rounding to whole coordinates.</summary>
    /// <param name="pts">The points.</param>
    public void TransformPoints(ReadOnlySpan<Point> pts) => Points(pts, _m);

    /// <summary>Transforms vectors in place: the matrix without its translation.</summary>
    /// <param name="pts">The vectors.</param>
    public void TransformVectors(PointF[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformVectors(pts.AsSpan());
    }

    /// <summary>Transforms vectors in place: the matrix without its translation.</summary>
    /// <param name="pts">The vectors.</param>
    public void TransformVectors(ReadOnlySpan<PointF> pts) => Points(pts, Linear);

    /// <summary>Transforms vectors in place, rounding to whole coordinates.</summary>
    /// <param name="pts">The vectors.</param>
    public void TransformVectors(Point[] pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        TransformVectors(pts.AsSpan());
    }

    /// <summary>Transforms vectors in place, rounding to whole coordinates.</summary>
    /// <param name="pts">The vectors.</param>
    public void TransformVectors(ReadOnlySpan<Point> pts) => Points(pts, Linear);

    /// <summary>Transforms vectors in place, rounding to whole coordinates; the same as <see cref="TransformVectors(Point[])"/>.</summary>
    /// <param name="pts">The vectors.</param>
    public void VectorTransformPoints(Point[] pts) => TransformVectors(pts);

    /// <summary>Transforms vectors in place, rounding to whole coordinates; the same as <see cref="TransformVectors(ReadOnlySpan{Point})"/>.</summary>
    /// <param name="pts">The vectors.</param>
    public void VectorTransformPoints(ReadOnlySpan<Point> pts) => TransformVectors(pts);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Matrix other && other._m == _m;

    /// <inheritdoc/>
    public override int GetHashCode() => _m.GetHashCode();

    /// <summary>The matrix without its translation.</summary>
    private Matrix3x2 Linear => _m with { M31 = 0, M32 = 0 };

    internal static Matrix3x2 Rotation(float degrees)
    {
        double radians = degrees * Math.PI / 180;
        float cos = (float)Math.Cos(radians), sin = (float)Math.Sin(radians);
        return new Matrix3x2(cos, sin, -sin, cos, 0, 0);
    }

    internal static Matrix3x2 Map(RectangleF rect, PointF[] plgpts)
    {
        ArgumentNullException.ThrowIfNull(plgpts);
        if (plgpts.Length != 3 || rect.Width == 0 || rect.Height == 0)
        {
            throw GdiErrors.InvalidParameter();
        }

        PointF p0 = plgpts[0], p1 = plgpts[1], p2 = plgpts[2];
        Matrix3x2 toUnit = Matrix3x2.CreateTranslation(-rect.X, -rect.Y) * Matrix3x2.CreateScale(1 / rect.Width, 1 / rect.Height);
        return toUnit * new Matrix3x2(p1.X - p0.X, p1.Y - p0.Y, p2.X - p0.X, p2.Y - p0.Y, p0.X, p0.Y);
    }

    internal static void Points(ReadOnlySpan<PointF> pts, Matrix3x2 m)
    {
        Span<PointF> points = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(pts), pts.Length);
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 v = Vector2.Transform(new Vector2(points[i].X, points[i].Y), m);
            points[i] = new PointF(v.X, v.Y);
        }
    }

    internal static void Points(ReadOnlySpan<Point> pts, Matrix3x2 m)
    {
        Span<Point> points = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(pts), pts.Length);
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 v = Vector2.Transform(new Vector2(points[i].X, points[i].Y), m);
            points[i] = new Point(Round(v.X), Round(v.Y));
        }
    }

    /// <summary>GDI+'s rounding of a coordinate to a whole pixel: half rounds up.</summary>
    internal static int Round(float v) => (int)MathF.Floor(v + 0.5f);

    private void Apply(Matrix3x2 m, MatrixOrder order) => _m = order == MatrixOrder.Append ? _m * m : m * _m;
}
