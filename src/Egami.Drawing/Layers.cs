using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// How a layer started by <see cref="Canvas.SaveLayer(LayerOptions?)"/> is merged at the matching <see cref="Canvas.Restore"/>:
/// filtered, masked, faded by <see cref="Opacity"/> and blended into what lies below with <see cref="BlendMode"/>, through the
/// clip. A layer is what PDF calls a transparency group.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="layers"/>
/// </example>
public sealed record class LayerOptions
{
    private readonly float _opacity = 1;
    private readonly BlendMode _blendMode;
    private readonly RectF? _bounds;

    /// <summary>
    /// Gets a rectangle, in local coordinates at the time of the call, that limits the layer: nothing drawn outside it shows, and
    /// only its part inside the clip is allocated. <see langword="null"/> (the default) uses the clip alone.
    /// </summary>
    /// <exception cref="ArgumentException">The rectangle is not finite.</exception>
    public RectF? Bounds
    {
        get => _bounds;
        init => _bounds = value is not RectF r || r.IsFinite ? value : throw new ArgumentException("The bounds must be finite.", nameof(value));
    }

    /// <summary>Gets the opacity the whole layer is merged with, 0 to 1: overlapping shapes inside fade as one. Defaults to 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float Opacity
    {
        get => _opacity;
        init => _opacity = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Opacity must be from 0 to 1.");
    }

    /// <summary>Gets how the layer combines with what lies below it. Defaults to <see cref="BlendMode.SourceOver"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public BlendMode BlendMode
    {
        get => _blendMode;
        init => _blendMode = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets a chain of Egami operations run on the layer's pixels before it is merged: a blur, a drop shadow, a color matrix, any
    /// step that keeps the size. The layer is first grown by how far the chain reads past each pixel (<see cref="RasterOps.Reach"/>),
    /// so a blur near the clip's edge still sees what was drawn just outside it. For a drop shadow, pass
    /// <see cref="DropShadowOptions.Expand"/> = <see langword="false"/>. A vector backend draws a filtered layer through its raster
    /// fallback. <see langword="null"/> (the default) runs nothing.
    /// </summary>
    public Action<RasterOps>? Filter { get; init; }

    /// <summary>
    /// Gets a chain of Egami operations run on what lies behind the layer when it starts; the result is the layer's first content,
    /// so a blur here frosts the glass a translucent panel is drawn on, as Skia's backdrop filter and the reference drawing library's backdrop
    /// effects do. Only the part inside the clip is used. <see langword="null"/> (the default) starts the layer as
    /// <see cref="Isolated"/> says.
    /// </summary>
    public Action<RasterOps>? Backdrop { get; init; }

    /// <summary>Gets a soft mask the layer is multiplied by before it is merged, as a PDF soft mask. <see langword="null"/> (the default) for none.</summary>
    public SoftMask? Mask { get; init; }

    /// <summary>
    /// Gets a value indicating whether the layer starts transparent (<see langword="true"/>, the default) or as a copy of what lies
    /// below it, as a PDF non-isolated group, so blend modes inside it reach the backdrop. A non-isolated layer starts from the
    /// backdrop as Skia's InitWithPrevious does.
    /// </summary>
    public bool Isolated { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the layer is a knockout group, as PDF's: each call drawn inside it composites with what the
    /// layer started as, not with the calls before it, and replaces them where it covers, by its coverage. Overlapping translucent
    /// shapes in a knockout group do not darken each other. Layers nested inside it merge as ordinary calls. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool Knockout { get; init; }
}

/// <summary>Which value of a soft mask's drawing becomes the mask, as PDF's soft mask subtypes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="soft-mask"/>
/// </example>
public enum SoftMaskKind
{
    /// <summary>The drawing's alpha.</summary>
    Alpha,

    /// <summary>The drawing's luminosity over the backdrop color: Rec. 709 weights on the encoded values, as PDF uses.</summary>
    Luminosity,
}

/// <summary>
/// A mask made by drawing, as a PDF soft mask: <see cref="Draw"/> runs on a canvas of its own with the layer's transform and
/// clip, and its alpha, or its luminosity over <see cref="Backdrop"/>, through the optional <see cref="Transfer"/> curve,
/// multiplies the layer. A luminosity mask of a white shape on black shows the layer only inside the shape.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="soft-mask"/>
/// </example>
public sealed record class SoftMask
{
    /// <summary>Initializes a new instance of the <see cref="SoftMask"/> class.</summary>
    /// <param name="draw">Draws the mask, in the layer's local coordinates.</param>
    /// <param name="kind">Whether alpha or luminosity becomes the mask.</param>
    /// <param name="backdrop">The color a luminosity mask is drawn over; black when <see langword="null"/>, so undrawn areas mask everything out. An alpha mask ignores it.</param>
    /// <param name="transfer">A curve from the mask value to the value used, both 0 to 1, as PDF's transfer function; <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="draw"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not defined.</exception>
    public SoftMask(Action<Canvas> draw, SoftMaskKind kind = SoftMaskKind.Luminosity, RgbaF32? backdrop = null, Func<float, float>? transfer = null)
    {
        Draw = draw ?? throw new ArgumentNullException(nameof(draw));
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        Backdrop = backdrop ?? new RgbaF32(0, 0, 0, 1);
        Transfer = transfer;
    }

    /// <summary>Gets the drawing that makes the mask.</summary>
    public Action<Canvas> Draw { get; }

    /// <summary>Gets whether alpha or luminosity becomes the mask.</summary>
    public SoftMaskKind Kind { get; }

    /// <summary>Gets the color a luminosity mask is drawn over.</summary>
    public RgbaF32 Backdrop { get; }

    /// <summary>Gets the curve applied to the mask value, or <see langword="null"/> for none.</summary>
    public Func<float, float>? Transfer { get; }
}

/// <summary>How a <see cref="MaskBlur"/> keeps or removes the blurred shape's inside and outside, as Skia's blur styles.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="blur"/>
/// </example>
public enum BlurStyle
{
    /// <summary>Blurs inside and out.</summary>
    Normal,

    /// <summary>Keeps the inside solid and blurs outward.</summary>
    Solid,

    /// <summary>Keeps only the blur outside the shape: a halo with a hole.</summary>
    Outer,

    /// <summary>Keeps only the blur inside the shape: a soft inner edge.</summary>
    Inner,
}

/// <summary>
/// A Gaussian blur of a shape's coverage before the paint is applied, as Skia's blur mask filter: <see cref="Sigma"/> is in device
/// pixels, so it does not scale with the canvas transform. Set it as <see cref="Paint.Blur"/>. Egami blurs with its fast blur; an
/// axis-aligned rectangle with <see cref="BlurStyle.Normal"/> is computed exactly from the error function, which is faster still.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="blur"/>
/// </example>
public readonly record struct MaskBlur
{
    /// <summary>The largest sigma, as for Egami's blurs.</summary>
    public const float MaxSigma = 1000;

    /// <summary>Initializes a new instance of the <see cref="MaskBlur"/> struct.</summary>
    /// <param name="sigma">The blur's standard deviation in device pixels, from 0 (no blur) to 1000.</param>
    /// <param name="style">What of the blur is kept.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is outside 0 to 1000, or <paramref name="style"/> is not defined.</exception>
    public MaskBlur(float sigma, BlurStyle style = BlurStyle.Normal)
    {
        Sigma = sigma is >= 0 and <= MaxSigma ? sigma : throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "The sigma must be from 0 to 1000.");
        Style = Enum.IsDefined(style) ? style : throw new ArgumentOutOfRangeException(nameof(style), style, null);
    }

    /// <summary>Gets the blur's standard deviation in device pixels.</summary>
    public float Sigma { get; }

    /// <summary>Gets what of the blur is kept.</summary>
    public BlurStyle Style { get; }
}

/// <summary>
/// The light and colors of <see cref="Canvas.DrawShadow"/>, after Skia's shadow model (the Material Design one): a shape lifted
/// <see cref="Elevation"/> above the canvas casts an ambient shadow, blurred by its height, and a spot shadow from a round light
/// at <see cref="LightPosition"/>, moved away from the light and blurred by the light's size.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="shadows"/>
/// </example>
public sealed record class ShadowOptions
{
    private readonly float _elevation = 8;
    private readonly Vector3? _lightPosition;
    private readonly float _lightRadius = 800;
    private readonly RgbaF32 _ambientColor = new(0, 0, 0, 0.039f);
    private readonly RgbaF32 _spotColor = new(0, 0, 0, 0.19f);

    /// <summary>Gets how high above the canvas the shape is, in device pixels, from 0 to 10000. Defaults to 8.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 10000.</exception>
    public float Elevation
    {
        get => _elevation;
        init => _elevation = value is >= 0 and <= 10000 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The elevation must be from 0 to 10000.");
    }

    /// <summary>
    /// Gets the light's position in device pixels, z above the canvas, or <see langword="null"/> (the default) for a light above
    /// the canvas's top center at height 600, as Material Design places it.
    /// </summary>
    /// <exception cref="ArgumentException">A coordinate is not finite, or z is not positive.</exception>
    public Vector3? LightPosition
    {
        get => _lightPosition;
        init => _lightPosition = value is not Vector3 v || (float.IsFinite(v.X) && float.IsFinite(v.Y) && v.Z > 0 && float.IsFinite(v.Z))
            ? value
            : throw new ArgumentException("The light's position must be finite, with z above 0.", nameof(value));
    }

    /// <summary>Gets the light's radius in device pixels: a larger light softens the spot shadow. Defaults to 800.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float LightRadius
    {
        get => _lightRadius;
        init => _lightRadius = value >= 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The radius must be finite and not negative.");
    }

    /// <summary>Gets the ambient shadow's color. Defaults to black at 3.9%, Material Design's ambient opacity.</summary>
    public RgbaF32 AmbientColor
    {
        get => _ambientColor;
        init => _ambientColor = Check(value);
    }

    /// <summary>Gets the spot shadow's color. Defaults to black at 19%, Material Design's spot opacity.</summary>
    public RgbaF32 SpotColor
    {
        get => _spotColor;
        init => _spotColor = Check(value);
    }

    /// <summary>
    /// Gets a value indicating whether the shape is see-through, so the shadow under it is drawn too; when <see langword="false"/>
    /// (the default) the shadow is left out where the shape covers it, as Skia does for an opaque occluder.
    /// </summary>
    public bool Transparent { get; init; }

    private static RgbaF32 Check(RgbaF32 c)
        => float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B) && float.IsFinite(c.A) ? c : throw new ArgumentException("The color must be finite.", nameof(c));
}
