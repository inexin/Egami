using System.Globalization;
using System.Numerics;

namespace Egami.Operations;

/// <summary>
/// A 3 × 3 projective matrix in double precision, for perspective transforms. It follows <see cref="Matrix3x2"/>'s conventions:
/// points are row vectors, so <c>[x, y, 1] × H = [X, Y, W]</c> and the point lands at <c>(X / W, Y / W)</c>; <c>A * B</c> applies
/// A first, then B. Coordinates are in pixels with the origin at the top-left corner of the top-left pixel, so pixel centers sit
/// at +0.5.
/// </summary>
/// <remarks>
/// <c>default(Homography)</c> is all zeros, which maps nothing; start from <see cref="Identity"/> or a factory.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Transforms.cs" region="homography"/>
/// </example>
public readonly struct Homography : IEquatable<Homography>
{
    /// <summary>Initializes a new instance of the <see cref="Homography"/> struct from its nine elements, row by row.</summary>
    /// <param name="m11">Row 1, column 1: x's weight in X.</param>
    /// <param name="m12">Row 1, column 2: x's weight in Y.</param>
    /// <param name="m13">Row 1, column 3: x's weight in W.</param>
    /// <param name="m21">Row 2, column 1: y's weight in X.</param>
    /// <param name="m22">Row 2, column 2: y's weight in Y.</param>
    /// <param name="m23">Row 2, column 3: y's weight in W.</param>
    /// <param name="m31">Row 3, column 1: the X translation.</param>
    /// <param name="m32">Row 3, column 2: the Y translation.</param>
    /// <param name="m33">Row 3, column 3: the W constant, 1 for an affine matrix.</param>
    public Homography(double m11, double m12, double m13, double m21, double m22, double m23, double m31, double m32, double m33)
    {
        M11 = m11;
        M12 = m12;
        M13 = m13;
        M21 = m21;
        M22 = m22;
        M23 = m23;
        M31 = m31;
        M32 = m32;
        M33 = m33;
    }

    /// <summary>Gets the matrix that maps every point to itself.</summary>
    public static Homography Identity { get; } = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    /// <summary>Gets row 1, column 1.</summary>
    public double M11 { get; }

    /// <summary>Gets row 1, column 2.</summary>
    public double M12 { get; }

    /// <summary>Gets row 1, column 3.</summary>
    public double M13 { get; }

    /// <summary>Gets row 2, column 1.</summary>
    public double M21 { get; }

    /// <summary>Gets row 2, column 2.</summary>
    public double M22 { get; }

    /// <summary>Gets row 2, column 3.</summary>
    public double M23 { get; }

    /// <summary>Gets row 3, column 1.</summary>
    public double M31 { get; }

    /// <summary>Gets row 3, column 2.</summary>
    public double M32 { get; }

    /// <summary>Gets row 3, column 3.</summary>
    public double M33 { get; }

    /// <summary>Gets a value indicating whether the matrix has no perspective: its third column is (0, 0, c) with c ≠ 0.</summary>
    public bool IsAffine => M13 == 0 && M23 == 0 && M33 != 0;

    /// <summary>Gets a value indicating whether the matrix maps every point to itself.</summary>
    public bool IsIdentity => IsAffine && M11 / M33 == 1 && M12 == 0 && M21 == 0 && M22 / M33 == 1 && M31 == 0 && M32 == 0;

    /// <summary>Gets the determinant. A matrix whose determinant is 0 cannot be inverted and collapses the image.</summary>
    public double Determinant =>
        (M11 * ((M22 * M33) - (M23 * M32))) - (M12 * ((M21 * M33) - (M23 * M31))) + (M13 * ((M21 * M32) - (M22 * M31)));

    /// <summary>Returns the matrix of an affine <see cref="Matrix3x2"/>.</summary>
    /// <param name="matrix">The affine matrix.</param>
    /// <returns>The same mapping as a homography.</returns>
    public static Homography FromAffine(Matrix3x2 matrix)
    {
        return new(matrix.M11, matrix.M12, 0, matrix.M21, matrix.M22, 0, matrix.M31, matrix.M32, 1);
    }

    /// <summary>
    /// Returns the projection of a 3D matrix applied to the image's plane at z = 0, as CSS <c>matrix3d</c> and Skia's <c>SkM44</c>
    /// draw a flat image: <c>[x, y, 0, 1] × M = [X, Y, Z, W]</c> lands at <c>(X / W, Y / W)</c>.
    /// </summary>
    /// <param name="matrix">The 3D matrix, in <see cref="Matrix4x4"/>'s row-vector convention.</param>
    /// <returns>The homography that maps the plane the same way.</returns>
    public static Homography FromMatrix4x4(Matrix4x4 matrix)
    {
        return new(matrix.M11, matrix.M12, matrix.M14, matrix.M21, matrix.M22, matrix.M24, matrix.M41, matrix.M42, matrix.M44);
    }

    /// <summary>
    /// Returns the matrix that moves four points onto four others: corner <c>i</c> of <paramref name="source"/> lands on corner
    /// <c>i</c> of <paramref name="destination"/>. Give the corners in the same order on both sides, for example top-left,
    /// top-right, bottom-right, bottom-left.
    /// </summary>
    /// <param name="source">Four points, no three on one line.</param>
    /// <param name="destination">The four points they go to, no three on one line.</param>
    /// <returns>The homography.</returns>
    /// <exception cref="ArgumentException">A span does not hold exactly four finite points, or three of them lie on one line.</exception>
    public static Homography FromQuad(ReadOnlySpan<Vector2> source, ReadOnlySpan<Vector2> destination)
    {
        Homography fromSquare = SquareToQuad(source, nameof(source));
        Homography toQuad = SquareToQuad(destination, nameof(destination));
        if (!fromSquare.TryInvert(out Homography toSquare))
        {
            throw new ArgumentException("Three of the four source points lie on one line.", nameof(source));
        }

        return toSquare * toQuad;
    }

    /// <summary>
    /// Returns the matrix that moves four points onto the corners of a rectangle, in the order top-left, top-right, bottom-right,
    /// bottom-left: what straightening a photographed page needs.
    /// </summary>
    /// <param name="source">The four points, in that order.</param>
    /// <param name="destination">The rectangle, in pixels; its corners are its pixel edges.</param>
    /// <returns>The homography.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> does not hold exactly four finite points, three of them lie on one line, or the rectangle is empty.
    /// </exception>
    public static Homography FromQuad(ReadOnlySpan<Vector2> source, Rect destination)
    {
        return FromQuad(source, Corners(destination, nameof(destination)));
    }

    /// <summary>
    /// Returns the matrix that moves the corners of a rectangle onto four points, in the order top-left, top-right, bottom-right,
    /// bottom-left.
    /// </summary>
    /// <param name="source">The rectangle, in pixels; its corners are its pixel edges.</param>
    /// <param name="destination">The four points, in that order.</param>
    /// <returns>The homography.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> does not hold exactly four finite points, three of them lie on one line, or the rectangle is
    /// empty.
    /// </exception>
    public static Homography FromQuad(Rect source, ReadOnlySpan<Vector2> destination)
    {
        return FromQuad(Corners(source, nameof(source)), destination);
    }

    /// <summary>Returns a rotation about the origin, clockwise on screen for a positive angle, as <see cref="Matrix3x2.CreateRotation(float)"/>.</summary>
    /// <param name="radians">The angle in radians.</param>
    /// <returns>The rotation.</returns>
    public static Homography CreateRotation(double radians)
    {
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return new(cos, sin, 0, -sin, cos, 0, 0, 0, 1);
    }

    /// <summary>Returns a rotation about <paramref name="center"/>, clockwise on screen for a positive angle.</summary>
    /// <param name="radians">The angle in radians.</param>
    /// <param name="center">The point that stays put.</param>
    /// <returns>The rotation.</returns>
    public static Homography CreateRotation(double radians, Vector2 center)
    {
        return About(CreateRotation(radians), center);
    }

    /// <summary>Returns a scale about the origin.</summary>
    /// <param name="scaleX">The horizontal factor; negative mirrors.</param>
    /// <param name="scaleY">The vertical factor; negative mirrors.</param>
    /// <returns>The scale.</returns>
    public static Homography CreateScale(double scaleX, double scaleY)
    {
        return new(scaleX, 0, 0, 0, scaleY, 0, 0, 0, 1);
    }

    /// <summary>Returns a scale about <paramref name="center"/>.</summary>
    /// <param name="scaleX">The horizontal factor; negative mirrors.</param>
    /// <param name="scaleY">The vertical factor; negative mirrors.</param>
    /// <param name="center">The point that stays put.</param>
    /// <returns>The scale.</returns>
    public static Homography CreateScale(double scaleX, double scaleY, Vector2 center)
    {
        return About(CreateScale(scaleX, scaleY), center);
    }

    /// <summary>
    /// Returns a shear about the origin, as <see cref="Matrix3x2.CreateSkew(float, float)"/>: x moves by y × tan(<paramref name="radiansX"/>)
    /// and y by x × tan(<paramref name="radiansY"/>).
    /// </summary>
    /// <param name="radiansX">The angle the vertical lines lean by, in radians.</param>
    /// <param name="radiansY">The angle the horizontal lines lean by, in radians.</param>
    /// <returns>The shear.</returns>
    public static Homography CreateSkew(double radiansX, double radiansY)
    {
        return new(1, Math.Tan(radiansY), 0, Math.Tan(radiansX), 1, 0, 0, 0, 1);
    }

    /// <summary>Returns a shear about <paramref name="center"/>.</summary>
    /// <param name="radiansX">The angle the vertical lines lean by, in radians.</param>
    /// <param name="radiansY">The angle the horizontal lines lean by, in radians.</param>
    /// <param name="center">The point that stays put.</param>
    /// <returns>The shear.</returns>
    public static Homography CreateSkew(double radiansX, double radiansY, Vector2 center)
    {
        return About(CreateSkew(radiansX, radiansY), center);
    }

    /// <summary>Returns a translation.</summary>
    /// <param name="x">The horizontal move, in pixels.</param>
    /// <param name="y">The vertical move, in pixels.</param>
    /// <returns>The translation.</returns>
    public static Homography CreateTranslation(double x, double y)
    {
        return new(1, 0, 0, 0, 1, 0, x, y, 1);
    }

    /// <summary>Returns the matrix that applies <paramref name="first"/>, then <paramref name="second"/>.</summary>
    /// <param name="first">The matrix applied first.</param>
    /// <param name="second">The matrix applied second.</param>
    /// <returns>The product.</returns>
    public static Homography operator *(Homography first, Homography second)
    {
        return Multiply(first, second);
    }

    /// <summary>Returns the matrix that applies <paramref name="first"/>, then <paramref name="second"/>.</summary>
    /// <param name="first">The matrix applied first.</param>
    /// <param name="second">The matrix applied second.</param>
    /// <returns>The product.</returns>
    public static Homography Multiply(Homography first, Homography second)
    {
        Homography a = first;
        Homography b = second;
        return new(
            (a.M11 * b.M11) + (a.M12 * b.M21) + (a.M13 * b.M31),
            (a.M11 * b.M12) + (a.M12 * b.M22) + (a.M13 * b.M32),
            (a.M11 * b.M13) + (a.M12 * b.M23) + (a.M13 * b.M33),
            (a.M21 * b.M11) + (a.M22 * b.M21) + (a.M23 * b.M31),
            (a.M21 * b.M12) + (a.M22 * b.M22) + (a.M23 * b.M32),
            (a.M21 * b.M13) + (a.M22 * b.M23) + (a.M23 * b.M33),
            (a.M31 * b.M11) + (a.M32 * b.M21) + (a.M33 * b.M31),
            (a.M31 * b.M12) + (a.M32 * b.M22) + (a.M33 * b.M32),
            (a.M31 * b.M13) + (a.M32 * b.M23) + (a.M33 * b.M33));
    }

    /// <summary>Returns whether two matrices have the same elements.</summary>
    /// <param name="left">The first matrix.</param>
    /// <param name="right">The second matrix.</param>
    /// <returns><see langword="true"/> if every element is equal.</returns>
    public static bool operator ==(Homography left, Homography right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns whether two matrices differ in any element.</summary>
    /// <param name="left">The first matrix.</param>
    /// <param name="right">The second matrix.</param>
    /// <returns><see langword="true"/> if any element differs.</returns>
    public static bool operator !=(Homography left, Homography right)
    {
        return !left.Equals(right);
    }

    /// <summary>Returns the inverse, which maps destination points back to source points.</summary>
    /// <param name="inverse">The inverse, or the zero matrix when there is none.</param>
    /// <returns><see langword="false"/> if the matrix is singular (or not finite) and has no inverse.</returns>
    public bool TryInvert(out Homography inverse)
    {
        double c11 = (M22 * M33) - (M23 * M32);
        double c12 = (M23 * M31) - (M21 * M33);
        double c13 = (M21 * M32) - (M22 * M31);
        double det = (M11 * c11) + (M12 * c12) + (M13 * c13);
        // Singular, or so nearly that the inverse is noise: the determinant is tiny next to the product of the row lengths, which
        // bounds it (Hadamard), so the test does not depend on the matrix's scale or on a large translation.
        double rows = Length(M11, M12, M13) * Length(M21, M22, M23) * Length(M31, M32, M33);
        if (det == 0 || !double.IsFinite(det) || Math.Abs(det) <= 1e-12 * rows)
        {
            inverse = default;
            return false;
        }

        double r = 1 / det;
        inverse = new(
            c11 * r,
            ((M13 * M32) - (M12 * M33)) * r,
            ((M12 * M23) - (M13 * M22)) * r,
            c12 * r,
            ((M11 * M33) - (M13 * M31)) * r,
            ((M13 * M21) - (M11 * M23)) * r,
            c13 * r,
            ((M12 * M31) - (M11 * M32)) * r,
            ((M11 * M22) - (M12 * M21)) * r);
        if (!inverse.IsFinite())
        {
            inverse = default;
            return false;
        }

        return true;
    }

    /// <summary>Maps a point.</summary>
    /// <param name="point">The point, in pixels.</param>
    /// <returns>Where it lands; both coordinates are NaN when the point goes to infinity (W = 0).</returns>
    public Vector2 Map(Vector2 point)
    {
        (double x, double y) = Map(point.X, point.Y);
        return new((float)x, (float)y);
    }

    /// <summary>Returns whether two matrices have the same elements.</summary>
    /// <param name="other">The other matrix.</param>
    /// <returns><see langword="true"/> if every element is equal.</returns>
    public bool Equals(Homography other)
    {
        return M11.Equals(other.M11) && M12.Equals(other.M12) && M13.Equals(other.M13)
            && M21.Equals(other.M21) && M22.Equals(other.M22) && M23.Equals(other.M23)
            && M31.Equals(other.M31) && M32.Equals(other.M32) && M33.Equals(other.M33);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is Homography other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(M11);
        hash.Add(M12);
        hash.Add(M13);
        hash.Add(M21);
        hash.Add(M22);
        hash.Add(M23);
        hash.Add(M31);
        hash.Add(M32);
        hash.Add(M33);
        return hash.ToHashCode();
    }

    /// <summary>Returns the elements row by row, as <c>{ {M11 M12 M13} {M21 M22 M23} {M31 M32 M33} }</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{{ {{{M11} {M12} {M13}}} {{{M21} {M22} {M23}}} {{{M31} {M32} {M33}}} }}");
    }

    /// <summary>Maps a point in double precision; NaN for both when W is 0.</summary>
    internal (double X, double Y) Map(double x, double y)
    {
        double w = (x * M13) + (y * M23) + M33;
        if (w == 0)
        {
            return (double.NaN, double.NaN);
        }

        return (((x * M11) + (y * M21) + M31) / w, ((x * M12) + (y * M22) + M32) / w);
    }

    /// <summary>Returns W for a point: positive in front of the viewer, 0 at infinity, negative behind.</summary>
    internal double W(double x, double y)
    {
        return (x * M13) + (y * M23) + M33;
    }

    /// <summary>Returns the matrix scaled so M33 is 1, when it can be; affine matrices compare and decompose more easily so.</summary>
    internal Homography Normalized()
    {
        if (M33 == 0 || M33 == 1)
        {
            return this;
        }

        double r = 1 / M33;
        return new(M11 * r, M12 * r, M13 * r, M21 * r, M22 * r, M23 * r, M31 * r, M32 * r, 1);
    }

    internal bool IsFinite()
    {
        return double.IsFinite(M11) && double.IsFinite(M12) && double.IsFinite(M13) && double.IsFinite(M21) && double.IsFinite(M22)
            && double.IsFinite(M23) && double.IsFinite(M31) && double.IsFinite(M32) && double.IsFinite(M33);
    }

    private static Homography About(Homography matrix, Vector2 center)
    {
        return CreateTranslation(-center.X, -center.Y) * matrix * CreateTranslation(center.X, center.Y);
    }

    private static Vector2[] Corners(Rect rect, string parameter)
    {
        if (rect.IsEmpty)
        {
            throw new ArgumentException("The rectangle is empty.", parameter);
        }

        return [new(rect.X, rect.Y), new(rect.Right, rect.Y), new(rect.Right, rect.Bottom), new(rect.X, rect.Bottom)];
    }

    /// <summary>
    /// Returns the matrix that maps the unit square's corners (0, 0), (1, 0), (1, 1), (0, 1) onto the quad (Heckbert 1989, section
    /// 2.2.3).
    /// </summary>
    private static Homography SquareToQuad(ReadOnlySpan<Vector2> quad, string parameter)
    {
        if (quad.Length != 4)
        {
            throw new ArgumentException($"A quad has 4 points, not {quad.Length}.", parameter);
        }

        foreach (Vector2 p in quad)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
            {
                throw new ArgumentException("Every point must be finite.", parameter);
            }
        }

        double x0 = quad[0].X, y0 = quad[0].Y, x1 = quad[1].X, y1 = quad[1].Y;
        double x2 = quad[2].X, y2 = quad[2].Y, x3 = quad[3].X, y3 = quad[3].Y;
        double sx = x0 - x1 + x2 - x3;
        double sy = y0 - y1 + y2 - y3;
        double g = 0, h = 0;
        if (sx != 0 || sy != 0)
        {
            double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
            double den = (dx1 * dy2) - (dx2 * dy1);
            if (den == 0)
            {
                throw new ArgumentException("Three of the four points lie on one line.", parameter);
            }

            g = ((sx * dy2) - (dx2 * sy)) / den;
            h = ((dx1 * sy) - (sx * dy1)) / den;
        }

        Homography result = new(x1 - x0 + (g * x1), y1 - y0 + (g * y1), g, x3 - x0 + (h * x3), y3 - y0 + (h * y3), h, x0, y0, 1);
        if (!result.TryInvert(out _))
        {
            throw new ArgumentException("Three of the four points lie on one line.", parameter);
        }

        return result;
    }

    private static double Length(double a, double b, double c)
    {
        return Math.Sqrt((a * a) + (b * b) + (c * c));
    }
}
