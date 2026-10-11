namespace Egami.Operations;

/// <summary>
/// The less common settings of <see cref="RasterOps.DropShadow"/>: a different vertical blur, whether the canvas grows, whether
/// only the shadow is kept, and how the blur is computed.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="drop-shadow"/>
/// </example>
public sealed record class DropShadowOptions
{
    private readonly float? _sigmaY;
    private readonly BlurMethod _method = BlurMethod.Fast;

    /// <summary>
    /// Gets the vertical blur in pixels, from 0 to 1000, as Skia's sigmaY; <see langword="null"/> (the default) uses the horizontal
    /// sigma.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, above 1000 or not a number.</exception>
    public float? SigmaY
    {
        get => _sigmaY;
        init => _sigmaY = value is null || (value >= 0 && value <= 1000)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "SigmaY must be from 0 to 1000.");
    }

    /// <summary>
    /// Gets a value indicating whether the canvas grows to hold the whole shadow: by 3σ plus the offset, on the sides the shadow
    /// reaches past the image, so nothing is cut off. Defaults to <see langword="true"/>; then the step changes the size and
    /// cannot run inside <see cref="RasterOps.Within(Rect, Action{RasterOps})"/>. <see langword="false"/> keeps the size, clipping
    /// the shadow at the image's edges.
    /// </summary>
    public bool Expand { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether only the shadow is kept, without the image on it, as Skia's DropShadowOnly and ImageMagick's
    /// -shadow. Defaults to <see langword="false"/>.
    /// </summary>
    public bool ShadowOnly { get; init; }

    /// <summary>
    /// Gets how the blur is computed. Defaults to <see cref="BlurMethod.Fast"/>, whose cost does not grow with σ;
    /// <see cref="BlurMethod.Exact"/> uses the Gaussian kernel itself.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public BlurMethod Method
    {
        get => _method;
        init => _method = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined blur method.");
    }
}
