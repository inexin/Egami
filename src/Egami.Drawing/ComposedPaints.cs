using System.Numerics;

using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// Two paints combined pixel by pixel with a blend mode, as Skia's blend shader: <see cref="Source"/> is blended onto
/// <see cref="Destination"/>, and the result is what this paint draws. Each part keeps its own <see cref="Paint.LocalMatrix"/>
/// and <see cref="Paint.Opacity"/> (its blend mode, blur and dithering are not used), inside this paint's own local matrix.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="composed-paints"/>
/// </example>
public sealed record class BlendPaint : Paint
{
    private readonly Paint _destination;
    private readonly Paint _source;
    private readonly BlendMode _mode;

    /// <summary>Initializes a new instance of the <see cref="BlendPaint"/> class.</summary>
    /// <param name="destination">The paint underneath.</param>
    /// <param name="source">The paint blended onto it.</param>
    /// <param name="mode">How they combine.</param>
    /// <exception cref="ArgumentNullException">A paint is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not defined.</exception>
    public BlendPaint(Paint destination, Paint source, BlendMode mode)
    {
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Mode = mode;
    }

    /// <summary>Gets the paint underneath.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Paint Destination
    {
        get => _destination;
        init => _destination = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the paint blended onto <see cref="Destination"/>.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Paint Source
    {
        get => _source;
        init => _source = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets how the two combine.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public BlendMode Mode
    {
        get => _mode;
        init => _mode = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <inheritdoc/>
    internal override Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace)
    {
        if (!TryInverse(toDevice, out _))
        {
            return null;
        }

        Matrix3x2 inner = LocalMatrix * toDevice;
        Shader? destination = Part(_destination, inner, colorSpace), source = Part(_source, inner, colorSpace);
        return new BlendShader(destination, source, _mode, colorSpace);
    }

    private static Shader? Part(Paint paint, Matrix3x2 toDevice, ColorSpace colorSpace)
    {
        Shader? shader = paint.CreateShader(toDevice, colorSpace);
        return shader is null || paint.Opacity >= 1 ? shader : new OpacityShader(shader, paint.Opacity);
    }
}

/// <summary>
/// Another paint with a color matrix run on its colors, as Skia's color-filtered shader: each color, unpremultiplied, goes through
/// the matrix, is clamped to 0 to 1 and is premultiplied again. <see cref="Paint.WithColorMatrix"/> makes one.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="composed-paints"/>
/// </example>
public sealed record class ColorMatrixPaint : Paint
{
    private readonly Paint _source;

    /// <summary>Initializes a new instance of the <see cref="ColorMatrixPaint"/> class.</summary>
    /// <param name="source">The paint whose colors are changed; its blend mode, blur and dithering are not used.</param>
    /// <param name="matrix">The matrix.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public ColorMatrixPaint(Paint source, ColorMatrix matrix)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Matrix = matrix;
    }

    /// <summary>Gets the paint whose colors are changed.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Paint Source
    {
        get => _source;
        init => _source = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the matrix.</summary>
    public ColorMatrix Matrix { get; init; }

    /// <summary>Runs a color matrix on one straight color, clamping the result to 0 to 1.</summary>
    internal static RgbaF32 Apply(ColorMatrix m, RgbaF32 c)
    {
        Span<float> v = [c.R, c.G, c.B, c.A];
        Span<float> o = stackalloc float[4];
        for (int row = 0; row < 4; row++)
        {
            o[row] = Math.Clamp((m[row, 0] * v[0]) + (m[row, 1] * v[1]) + (m[row, 2] * v[2]) + (m[row, 3] * v[3]) + m[row, 4], 0, 1);
        }

        return new RgbaF32(o[0], o[1], o[2], o[3]);
    }

    /// <inheritdoc/>
    internal override Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace)
    {
        if (!TryInverse(toDevice, out _))
        {
            return null;
        }

        Shader? source = _source.CreateShader(LocalMatrix * toDevice, colorSpace);
        if (source is not null && _source.Opacity < 1)
        {
            source = new OpacityShader(source, _source.Opacity);
        }

        return new ColorMatrixShader(source, Matrix);
    }
}

/// <summary>
/// A paint whose colors come from C# code: override <see cref="Shade"/> to fill a row of premultiplied float colors. Rows are
/// shaded in parallel bands, so <see cref="Shade"/> must be thread-safe and must give a pixel the same color whichever band or
/// thread asks; then the result is the same at any core count. A backend that keeps vectors, such as a PDF page, cannot run C#
/// and draws the call through its raster fallback instead. Derive a <c>record class</c> from it.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="custom-paint"/>
/// </example>
public abstract record class CustomPaint : Paint
{
    /// <summary>Initializes a new instance of the <see cref="CustomPaint"/> class.</summary>
    protected CustomPaint()
    {
    }

    /// <summary>
    /// Fills one row of pixels: <paramref name="row"/>[i] is the device pixel (<paramref name="x"/> + i, <paramref name="y"/>),
    /// whose center (x + i + 0.5, y + 0.5) maps to paint coordinates through <paramref name="inverse"/>. Colors are premultiplied:
    /// each channel at most the alpha, all from 0 to 1.
    /// </summary>
    /// <param name="y">The device row.</param>
    /// <param name="x">The device column of the first pixel.</param>
    /// <param name="row">The colors to write, one per pixel.</param>
    /// <param name="inverse">The transform from device pixels to paint coordinates: the inverse of the local matrix then the canvas transform.</param>
    public abstract void Shade(int y, int x, Span<PremulRgbaF32> row, in Matrix3x2 inverse);

    /// <inheritdoc/>
    internal override Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace)
        => TryInverse(toDevice, out Matrix3x2 inverse) ? new CustomShader(this, inverse) : null;
}
