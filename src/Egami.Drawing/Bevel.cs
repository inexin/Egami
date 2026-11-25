namespace Egami.Drawing;

/// <summary>
/// The cross-section of a bevel: how its height rises from the shape's edge to the flat face inside it. The names are DrawingML's
/// bevel presets (<c>a:bevelT prst</c>); each is a smooth curve of Egami's own, tuned to the look of its name rather than to
/// another renderer's pixels.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="bevel"/>
/// </example>
public enum BevelProfile
{
    /// <summary>A quarter circle: steep at the edge, level at the face. DrawingML's default.</summary>
    Circle,

    /// <summary>A gentle S-curve, level at both ends.</summary>
    RelaxedInset,

    /// <summary>A ridge: up to full height halfway in, then down to half height at the face.</summary>
    Cross,

    /// <summary>A steep rise near the edge, then a long shallow slant up to the face.</summary>
    CoolSlant,

    /// <summary>A straight chamfer.</summary>
    Angle,

    /// <summary>A rounded rise, a quarter sine: firm at the edge, level at the face.</summary>
    SoftRound,

    /// <summary>A bulge: rises at a finite slope and levels off at the face.</summary>
    Convex,

    /// <summary>A concave ramp: shallow at the edge, steepening toward the face.</summary>
    Slope,

    /// <summary>A groove: a raised rim at the edge, a dip, then the face.</summary>
    Divot,

    /// <summary>A slope with three small ribs on it.</summary>
    Riblet,

    /// <summary>A short steep wall, then the face: the bevel takes the first quarter of its width.</summary>
    HardEdge,

    /// <summary>Three steps, each a short riser and a tread.</summary>
    ArtDeco,
}

/// <summary>
/// How a bevel's surface takes the light: how much it scatters (diffuse), how much it mirrors (specular) and how sharply. The
/// names are DrawingML's preset materials (<c>prstMaterial</c>); the values are Egami's own.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="bevel"/>
/// </example>
public enum BevelMaterial
{
    /// <summary>Diffuse only, no shine.</summary>
    Matte,

    /// <summary>Diffuse only, with highlights a little warm. DrawingML's default.</summary>
    WarmMatte,

    /// <summary>Diffuse with a small sharp shine.</summary>
    Plastic,

    /// <summary>Strong shine and deep shadows, little diffuse light.</summary>
    Metal,

    /// <summary>Metal with a broader, softer shine.</summary>
    SoftMetal,

    /// <summary>Matte, darkened toward the outer edge.</summary>
    DarkEdge,

    /// <summary>Matte, with the light and shade fading out toward the outer edge.</summary>
    SoftEdge,

    /// <summary>No light or shade at all: the bevel draws nothing.</summary>
    Flat,

    /// <summary>A faint diffuse light and a sharp shine, as glass.</summary>
    Clear,

    /// <summary>Soft diffuse light with low contrast.</summary>
    Powder,

    /// <summary>Powder at about half strength.</summary>
    TranslucentPowder,
}

/// <summary>The eight directions a DrawingML light rig shines from (<c>a:lightRig dir</c>).</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="bevel"/>
/// </example>
public enum LightDirection
{
    /// <summary>From the top left.</summary>
    TopLeft,

    /// <summary>From the top.</summary>
    Top,

    /// <summary>From the top right.</summary>
    TopRight,

    /// <summary>From the left.</summary>
    Left,

    /// <summary>From the right.</summary>
    Right,

    /// <summary>From the bottom left.</summary>
    BottomLeft,

    /// <summary>From the bottom.</summary>
    Bottom,

    /// <summary>From the bottom right.</summary>
    BottomRight,
}

/// <summary>A distant light over the page, as a bevel sees it.</summary>
/// <param name="Angle">
/// The direction the light comes from, in degrees counter-clockwise from the right as seen on the page (90 is from the top, 135
/// from the top left), as Photoshop's layer-effect angle. It is a device direction: rotating the canvas does not turn the light.
/// </param>
/// <param name="Elevation">The light's height above the page, in degrees from 0 (grazing) to 90 (straight down).</param>
/// <param name="Intensity">How strong the light and shade are: 1 as the material says, 0 none, more for stronger.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="bevel"/>
/// </example>
public readonly record struct BevelLight(float Angle, float Elevation, float Intensity = 1)
{
    /// <summary>Gets the default light: from the top left, 45 degrees up, at full strength.</summary>
    public static BevelLight Default { get; } = new(135, 45);

    /// <summary>Returns a light from one of DrawingML's eight rig directions, at the given elevation and strength.</summary>
    /// <param name="direction">The direction.</param>
    /// <param name="elevation">The height above the page in degrees.</param>
    /// <param name="intensity">The strength.</param>
    /// <returns>The light.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction"/> is not defined.</exception>
    public static BevelLight FromDirection(LightDirection direction, float elevation = 45, float intensity = 1) => new(
        direction switch
        {
            LightDirection.Right => 0,
            LightDirection.TopRight => 45,
            LightDirection.Top => 90,
            LightDirection.TopLeft => 135,
            LightDirection.Left => 180,
            LightDirection.BottomLeft => 225,
            LightDirection.Bottom => 270,
            LightDirection.BottomRight => 315,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
        },
        elevation,
        intensity);
}

/// <summary>
/// A bevel along a shape's edge, lit by a light: the rim the width wide rises by the profile to the height, and where it faces
/// the light more than the flat face does it is lightened, where less it is darkened. <see cref="Canvas.DrawBevel"/> draws it
/// over a shape that is already filled.
/// </summary>
/// <remarks>
/// This is DrawingML's top bevel (<c>a:sp3d/a:bevelT</c>) seen straight on, with its preset materials and the rig's direction.
/// Width and height are in the canvas's local units (DrawingML gives them in EMUs, 6 points by default).
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="bevel"/>
/// </example>
public sealed record class Bevel
{
    private readonly BevelProfile _profile = BevelProfile.Circle;
    private readonly BevelMaterial _material = BevelMaterial.WarmMatte;
    private readonly float _width = 6;
    private readonly float _height = 6;
    private readonly BevelLight _light = BevelLight.Default;

    /// <summary>Gets the cross-section. Defaults to <see cref="BevelProfile.Circle"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public BevelProfile Profile
    {
        get => _profile;
        init => _profile = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Gets how far in from the edge the bevel reaches, in local units. Defaults to 6; 0 draws nothing.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float Width
    {
        get => _width;
        init => _width = value >= 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The width must be a finite number, 0 or more.");
    }

    /// <summary>Gets how high the bevel rises, in local units: higher is steeper, so lighter and darker. Defaults to 6; 0 draws nothing.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float Height
    {
        get => _height;
        init => _height = value >= 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The height must be a finite number, 0 or more.");
    }

    /// <summary>Gets the surface. Defaults to <see cref="BevelMaterial.WarmMatte"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public BevelMaterial Material
    {
        get => _material;
        init => _material = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Gets the light. Defaults to <see cref="BevelLight.Default"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The angle is not finite, the elevation is not between 0 and 90, or the intensity is negative or not finite.
    /// </exception>
    public BevelLight Light
    {
        get => _light;
        init => _light = float.IsFinite(value.Angle) && value.Elevation >= 0 && value.Elevation <= 90 && value.Intensity >= 0 && float.IsFinite(value.Intensity)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The light needs a finite angle, an elevation from 0 to 90 and an intensity of 0 or more.");
    }
}
