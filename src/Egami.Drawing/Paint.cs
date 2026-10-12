using System.Numerics;

using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// What a drawing call puts on the canvas, and how it combines with what is there: the colors (from the subclass), the
/// <see cref="BlendMode"/>, an <see cref="Opacity"/>, whether edges are antialiased, and for shapes an optional <see cref="Blur"/>.
/// The kinds are <see cref="SolidPaint"/>, the gradients (<see cref="LinearGradient"/>, <see cref="RadialGradient"/>,
/// <see cref="ConicalGradient"/>, <see cref="SweepGradient"/>), <see cref="ImagePaint"/>, <see cref="NoisePaint"/>,
/// <see cref="BlendPaint"/>, <see cref="ColorMatrixPaint"/> and <see cref="CustomPaint"/>. A color converts to a solid paint where a
/// paint is expected.
/// <para>
/// A paint other than a solid one has its own coordinates: <see cref="LocalMatrix"/> maps them into the canvas's local
/// coordinates, so a gradient or an image turns and scales with the canvas transform, and further with its own matrix.
/// </para>
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="paints"/>
/// </example>
public abstract record class Paint
{
    private readonly BlendMode _blendMode;
    private readonly float _opacity = 1;
    private readonly Matrix3x2 _localMatrix = Matrix3x2.Identity;

    /// <summary>Initializes a new instance of the <see cref="Paint"/> class.</summary>
    private protected Paint()
    {
    }

    /// <summary>Gets how the paint combines with the pixels under it. Defaults to <see cref="BlendMode.SourceOver"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public BlendMode BlendMode
    {
        get => _blendMode;
        init => _blendMode = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Gets the paint's opacity, 0 to 1, which multiplies its alpha. Defaults to 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float Opacity
    {
        get => _opacity;
        init => _opacity = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Opacity must be from 0 to 1.");
    }

    /// <summary>
    /// Gets a value indicating whether edges are antialiased: each pixel gets its exact covered area. When <see langword="false"/>,
    /// a pixel is drawn fully when its center is inside and not at all otherwise, for pixel art and masks that must stay binary.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool Antialias { get; init; } = true;

    /// <summary>
    /// Gets the transform from the paint's own coordinates to the canvas's local coordinates, applied before the canvas transform.
    /// A gradient's points, an image's pixels and noise's lattice are in paint coordinates, so this stretches a radial gradient into
    /// an ellipse or turns a pattern. A matrix that cannot be inverted draws nothing. A solid paint ignores it. Defaults to the
    /// identity.
    /// </summary>
    /// <exception cref="ArgumentException">An element is not finite.</exception>
    public Matrix3x2 LocalMatrix
    {
        get => _localMatrix;
        init => _localMatrix = float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M21) && float.IsFinite(value.M22)
            && float.IsFinite(value.M31) && float.IsFinite(value.M32)
            ? value
            : throw new ArgumentException("The local matrix's elements must be finite.", nameof(value));
    }

    /// <summary>
    /// Gets a value indicating whether colors are dithered when they are rounded to 8 bits: a 4×4 ordered pattern of less than half
    /// a level is added before rounding, which hides the banding of large soft gradients and never changes a flat area of an exact
    /// level. Only shaded paints on 8-bit targets dither; solid paints and deeper targets do not. Defaults to <see langword="false"/>,
    /// as in Skia and the reference library.
    /// </summary>
    public bool Dither { get; init; }

    /// <summary>
    /// Gets a blur of the shape's coverage, applied before the paint is: a soft-edged shape, or with <see cref="BlurStyle.Outer"/> a
    /// glow around it. It applies to shapes, paths and text, not to images. <see langword="null"/> (the default) draws sharp edges.
    /// </summary>
    public MaskBlur? Blur { get; init; }

    /// <summary>Converts a color to a <see cref="SolidPaint"/>.</summary>
    /// <param name="color">The color.</param>
    public static implicit operator Paint(Rgba8 color) => new SolidPaint(color);

    /// <summary>Converts a float color to a <see cref="SolidPaint"/>.</summary>
    /// <param name="color">The color.</param>
    public static implicit operator Paint(RgbaF32 color) => new SolidPaint(color);

    /// <summary>
    /// Returns a paint that runs a color matrix on this paint's colors, as Skia's color-filtered shader: each color, with straight
    /// alpha, goes through the matrix and is clamped to 0 to 1. A solid paint gives a solid paint of the transformed color.
    /// </summary>
    /// <param name="matrix">The matrix, as Egami's ColorMatrix operation uses it.</param>
    /// <returns>The new paint, with this paint's blend mode, opacity, antialiasing, dithering and blur.</returns>
    public Paint WithColorMatrix(ColorMatrix matrix)
    {
        if (this is SolidPaint solid)
        {
            return solid with { Color = ColorMatrixPaint.Apply(matrix, solid.Color) };
        }

        return new ColorMatrixPaint(this with { BlendMode = BlendMode.SourceOver, Opacity = 1, Blur = null }, matrix)
        {
            BlendMode = BlendMode,
            Opacity = Opacity,
            Antialias = Antialias,
            Dither = Dither,
            Blur = Blur,
        };
    }

    /// <summary>
    /// Returns the paint's colors in device space, for a paint that is not one color. <paramref name="toDevice"/> maps the
    /// canvas's local coordinates to device pixels; the paint's own <see cref="LocalMatrix"/> comes first.
    /// </summary>
    /// <returns>The shader, or <see langword="null"/> when the paint draws nothing (a matrix that cannot be inverted).</returns>
    internal abstract Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace);

    /// <summary>Returns the shader for target pixels of <typeparamref name="TPixel"/>; by default the float shader, converted.</summary>
    internal virtual PixelShader<TPixel>? CreatePixelShader<TPixel>(Matrix3x2 toDevice, ColorSpace colorSpace)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Shader? shader = CreateShader(toDevice, colorSpace);
        return shader is null ? null : new ConvertingShader<TPixel>(shader, Dither);
    }

    /// <summary>The device-to-paint inverse of <see cref="LocalMatrix"/> then <paramref name="toDevice"/>, or <see langword="false"/> when there is none.</summary>
    private protected bool TryInverse(Matrix3x2 toDevice, out Matrix3x2 inverse)
        => Matrix3x2.Invert(LocalMatrix * toDevice, out inverse) && float.IsFinite(inverse.M11) && float.IsFinite(inverse.M12)
            && float.IsFinite(inverse.M21) && float.IsFinite(inverse.M22) && float.IsFinite(inverse.M31) && float.IsFinite(inverse.M32);
}

/// <summary>
/// One color everywhere, with straight alpha. The color keeps float precision, so float and 16-bit targets get it in full; values
/// outside 0 to 1 are kept on the paint, and the blend clamps them as Egami's Compositor does.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="paints"/>
/// </example>
public sealed record class SolidPaint : Paint
{
    private readonly RgbaF32 _color;

    /// <summary>Initializes a new instance of the <see cref="SolidPaint"/> class.</summary>
    /// <param name="color">The color.</param>
    /// <exception cref="ArgumentException">A channel is not finite.</exception>
    public SolidPaint(RgbaF32 color)
    {
        Color = color;
    }

    /// <summary>Initializes a new instance of the <see cref="SolidPaint"/> class.</summary>
    /// <param name="color">The color.</param>
    public SolidPaint(Rgba8 color)
        : this(RgbaF32.FromRgba8(color))
    {
    }

    /// <summary>Gets the color.</summary>
    /// <exception cref="ArgumentException">A channel is not finite.</exception>
    public RgbaF32 Color
    {
        get => _color;
        init => _color = float.IsFinite(value.R) && float.IsFinite(value.G) && float.IsFinite(value.B) && float.IsFinite(value.A)
            ? value
            : throw new ArgumentException("A paint's color must be finite.", nameof(value));
    }

    /// <inheritdoc/>
    internal override Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace) => new SolidShader(Color);
}
