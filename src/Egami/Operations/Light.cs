using System.Numerics;

using Egami.Pixels;

namespace Egami.Operations;

/// <summary>
/// A light for <see cref="RasterOps.Lighting"/>: <see cref="DistantLight"/>, <see cref="PointLight"/> or <see cref="SpotLight"/>,
/// the three light sources of the SVG 1.1 lighting filters (feDistantLight, fePointLight and feSpotLight), with Skia's parameters.
/// Positions are in pixels of the raster, x to the right, y down and z up out of the image; a pixel's center is at (x + 0.5, y + 0.5),
/// as in Skia.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Lighting.cs" region="lights"/>
/// </example>
public abstract record class Light
{
    private protected Light()
    {
    }

    /// <summary>Gets the light's color; alpha is ignored. Defaults to white. The color is used as stored, not converted to linear light.</summary>
    public Rgba8 Color { get; init; } = Rgba8.White;

    private protected static float Finite(float value, string name)
    {
        return float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, "The value must be finite.");
    }

    private protected static Vector3 Finite(Vector3 value, string name)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? value
            : throw new ArgumentOutOfRangeException(name, value, "Every coordinate must be finite.");
    }
}

/// <summary>
/// A light infinitely far away, shining from one direction on every pixel, as SVG's feDistantLight. The direction toward the light
/// is (cos <see cref="Azimuth"/> · cos <see cref="Elevation"/>, sin <see cref="Azimuth"/> · cos <see cref="Elevation"/>,
/// sin <see cref="Elevation"/>) in the raster's coordinates (y down), which is the vector Skia's DistantLit factories take.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Lighting.cs" region="lights"/>
/// </example>
public sealed record class DistantLight : Light
{
    private readonly float _azimuth;
    private readonly float _elevation;

    /// <summary>Initializes a new instance of the <see cref="DistantLight"/> class.</summary>
    /// <param name="azimuth">The direction of the light in the image plane, in degrees clockwise from the x axis (y points down), as SVG's azimuth.</param>
    /// <param name="elevation">The angle of the light above the image plane, in degrees, from −90 to 90; 90 shines straight down.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="azimuth"/> is not finite, or <paramref name="elevation"/> is not from −90 to 90.</exception>
    public DistantLight(float azimuth, float elevation)
    {
        Azimuth = azimuth;
        Elevation = elevation;
    }

    /// <summary>Gets the direction of the light in the image plane, in degrees clockwise from the x axis (y points down).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite.</exception>
    public float Azimuth
    {
        get => _azimuth;
        init => _azimuth = Finite(value, nameof(Azimuth));
    }

    /// <summary>Gets the angle of the light above the image plane, in degrees.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not from −90 to 90.</exception>
    public float Elevation
    {
        get => _elevation;
        init => _elevation = value is >= -90 and <= 90 ? value : throw new ArgumentOutOfRangeException(nameof(Elevation), value, "The elevation must be from -90 to 90 degrees.");
    }

    /// <summary>Gets the unit vector from the surface toward the light.</summary>
    internal Vector3 Direction
    {
        get
        {
            double a = _azimuth * Math.PI / 180, e = _elevation * Math.PI / 180;
            return new Vector3((float)(Math.Cos(a) * Math.Cos(e)), (float)(Math.Sin(a) * Math.Cos(e)), (float)Math.Sin(e));
        }
    }
}

/// <summary>A light at one point, shining in every direction, as SVG's fePointLight and Skia's PointLit factories.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Lighting.cs" region="lights"/>
/// </example>
public sealed record class PointLight : Light
{
    private readonly Vector3 _position;

    /// <summary>Initializes a new instance of the <see cref="PointLight"/> class.</summary>
    /// <param name="position">Where the light is, in pixels: x to the right, y down, z above the image.</param>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate of <paramref name="position"/> is not finite.</exception>
    public PointLight(Vector3 position)
    {
        Position = position;
    }

    /// <summary>Gets where the light is, in pixels: x to the right, y down, z above the image.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is not finite.</exception>
    public Vector3 Position
    {
        get => _position;
        init => _position = Finite(value, nameof(Position));
    }
}

/// <summary>
/// A light at one point that shines toward a target, as SVG's feSpotLight and Skia's SpotLit factories. Light falls off away
/// from the axis as cos(angle)^<see cref="Exponent"/> and stops at <see cref="ConeAngle"/>, with Skia's soft cone edge.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Lighting.cs" region="lights"/>
/// </example>
public sealed record class SpotLight : Light
{
    private readonly Vector3 _position;
    private readonly Vector3 _target;
    private readonly float _exponent;
    private readonly float _coneAngle;

    /// <summary>Initializes a new instance of the <see cref="SpotLight"/> class.</summary>
    /// <param name="position">Where the light is, in pixels: x to the right, y down, z above the image.</param>
    /// <param name="target">The point the light shines at, in the same coordinates; it must differ from <paramref name="position"/>.</param>
    /// <param name="exponent">How fast the light falls off away from its axis (SVG's specularExponent, Skia's falloffExponent), from 0 to 128.</param>
    /// <param name="coneAngle">The half-angle of the cone outside which nothing is lit, in degrees (SVG's limitingConeAngle, Skia's cutoffAngle), above 0 and at most 180.</param>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is not finite, the two points are the same, or a value is out of range.</exception>
    public SpotLight(Vector3 position, Vector3 target, float exponent = 1, float coneAngle = 90)
    {
        Position = position;
        Target = target;
        Exponent = exponent;
        ConeAngle = coneAngle;
        if (position == target)
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "The target must differ from the position.");
        }
    }

    /// <summary>Gets where the light is.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is not finite.</exception>
    public Vector3 Position
    {
        get => _position;
        init => _position = Finite(value, nameof(Position));
    }

    /// <summary>Gets the point the light shines at.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is not finite.</exception>
    public Vector3 Target
    {
        get => _target;
        init => _target = Finite(value, nameof(Target));
    }

    /// <summary>Gets how fast the light falls off away from its axis: the light is scaled by cos(angle)^Exponent.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not from 0 to 128.</exception>
    public float Exponent
    {
        get => _exponent;
        init => _exponent = value is >= 0 and <= 128 ? value : throw new ArgumentOutOfRangeException(nameof(Exponent), value, "The exponent must be from 0 to 128.");
    }

    /// <summary>Gets the half-angle of the lit cone, in degrees.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not above 0 and at most 180.</exception>
    public float ConeAngle
    {
        get => _coneAngle;
        init => _coneAngle = value is > 0 and <= 180 ? value : throw new ArgumentOutOfRangeException(nameof(ConeAngle), value, "The cone angle must be above 0 and at most 180 degrees.");
    }
}
