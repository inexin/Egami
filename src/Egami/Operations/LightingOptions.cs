namespace Egami.Operations;

/// <summary>The surface and output settings of <see cref="RasterOps.Lighting"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Lighting.cs" region="bevel"/>
/// </example>
public sealed record class LightingOptions
{
    private readonly float _surfaceScale = 1;
    private readonly HeightSource _height = HeightSource.Alpha;
    private readonly BlendMode? _blend;

    /// <summary>
    /// Gets how high the surface is where the height channel is 1, in pixels (SVG's surfaceScale, Skia's surfaceScale). Larger
    /// values give steeper slopes and stronger relief; negative values turn the surface inside out. Defaults to 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not from −1000 to 1000.</exception>
    public float SurfaceScale
    {
        get => _surfaceScale;
        init => _surfaceScale = value is >= -1000 and <= 1000 ? value : throw new ArgumentOutOfRangeException(nameof(SurfaceScale), value, "The surface scale must be from -1000 to 1000.");
    }

    /// <summary>Gets which channel is the height. Defaults to <see cref="HeightSource.Alpha"/>, as SVG and Skia.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public HeightSource Height
    {
        get => _height;
        init => _height = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Height), value, "The height source is not defined.");
    }

    /// <summary>
    /// Gets how the light map is applied. <see langword="null"/> (the default) replaces the image with the light map, as SVG and
    /// Skia return it; a <see cref="BlendMode"/> blends the light map onto the image as the source, and the image keeps its alpha:
    /// <see cref="BlendMode.Multiply"/> shades it (a bevel), <see cref="BlendMode.Screen"/> or <see cref="BlendMode.Plus"/> adds highlights.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined mode.</exception>
    public BlendMode? Blend
    {
        get => _blend;
        init => _blend = value is null || Enum.IsDefined(value.Value) ? value : throw new ArgumentOutOfRangeException(nameof(Blend), value, "The blend mode is not defined.");
    }
}
