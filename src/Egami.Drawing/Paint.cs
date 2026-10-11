using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// What a drawing call puts on the canvas, and how it combines with what is there: the colors (from the subclass), the
/// <see cref="BlendMode"/>, an <see cref="Opacity"/> and whether edges are antialiased. <see cref="SolidPaint"/> is the one kind in
/// this release; gradients, image and noise paints are planned as further subclasses. A color converts to a solid paint where a
/// paint is expected.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="paints"/>
/// </example>
public abstract record class Paint
{
    private readonly BlendMode _blendMode;
    private readonly float _opacity = 1;

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

    /// <summary>Converts a color to a <see cref="SolidPaint"/>.</summary>
    /// <param name="color">The color.</param>
    public static implicit operator Paint(Rgba8 color) => new SolidPaint(color);

    /// <summary>Converts a float color to a <see cref="SolidPaint"/>.</summary>
    /// <param name="color">The color.</param>
    public static implicit operator Paint(RgbaF32 color) => new SolidPaint(color);
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
}
