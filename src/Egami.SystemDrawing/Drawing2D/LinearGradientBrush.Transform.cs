// Written by tools/sysdrawing-brush-transforms.py.
using System.Numerics;

using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

/// <summary>The transform members.</summary>
public sealed partial class LinearGradientBrush
{
    /// <summary>Gets or sets a copy of the brush's transform, from brush space to world coordinates.</summary>
    /// <exception cref="ArgumentException">The transform cannot be inverted.</exception>
    public Matrix Transform
    {
        get => new(Brushspace);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Brushspace = Matrix3x2.Invert(value.MatrixElements, out _) ? value.MatrixElements : throw GdiErrors.InvalidParameter();
        }
    }

    /// <summary>Gets or sets the brush's transform.</summary>
    internal Matrix3x2 Brushspace { get; set; } = Matrix3x2.Identity;

    /// <summary>Makes the transform the identity.</summary>
    public void ResetTransform() => Brushspace = Matrix3x2.Identity;

    /// <summary>Multiplies the transform by a matrix, applied before it.</summary>
    /// <param name="matrix">The matrix.</param>
    public void MultiplyTransform(Matrix matrix) => MultiplyTransform(matrix, MatrixOrder.Prepend);

    /// <summary>Multiplies the transform by a matrix.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void MultiplyTransform(Matrix matrix, MatrixOrder order)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ApplyTransform(matrix.MatrixElements, order);
    }

    /// <summary>Translates the transform, before it.</summary>
    /// <param name="dx">The horizontal distance.</param>
    /// <param name="dy">The vertical distance.</param>
    public void TranslateTransform(float dx, float dy) => TranslateTransform(dx, dy, MatrixOrder.Prepend);

    /// <summary>Translates the transform.</summary>
    /// <param name="dx">The horizontal distance.</param>
    /// <param name="dy">The vertical distance.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void TranslateTransform(float dx, float dy, MatrixOrder order) => ApplyTransform(Matrix3x2.CreateTranslation(dx, dy), order);

    /// <summary>Scales the transform, before it.</summary>
    /// <param name="sx">The horizontal factor.</param>
    /// <param name="sy">The vertical factor.</param>
    public void ScaleTransform(float sx, float sy) => ScaleTransform(sx, sy, MatrixOrder.Prepend);

    /// <summary>Scales the transform.</summary>
    /// <param name="sx">The horizontal factor.</param>
    /// <param name="sy">The vertical factor.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void ScaleTransform(float sx, float sy, MatrixOrder order) => ApplyTransform(Matrix3x2.CreateScale(sx, sy), order);

    /// <summary>Rotates the transform clockwise, before it.</summary>
    /// <param name="angle">The angle in degrees.</param>
    public void RotateTransform(float angle) => RotateTransform(angle, MatrixOrder.Prepend);

    /// <summary>Rotates the transform clockwise.</summary>
    /// <param name="angle">The angle in degrees.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void RotateTransform(float angle, MatrixOrder order) => ApplyTransform(Matrix.Rotation(angle), order);

    private void ApplyTransform(Matrix3x2 m, MatrixOrder order) => Brushspace = order == MatrixOrder.Append ? Brushspace * m : m * Brushspace;
}
