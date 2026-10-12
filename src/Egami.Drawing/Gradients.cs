using System.Numerics;

using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// One color of a gradient at a position along it, from 0 (the start) to 1 (the end). Two stops at the same offset make a hard
/// edge: the later one applies from that offset on. A gradient whose first stop is after 0 or whose last is before 1 repeats the
/// nearest stop's color to the end, as CSS does.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="gradients"/>
/// </example>
public readonly record struct GradientStop
{
    /// <summary>Initializes a new instance of the <see cref="GradientStop"/> struct.</summary>
    /// <param name="offset">The position along the gradient, from 0 to 1.</param>
    /// <param name="color">The color, with straight alpha.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside 0 to 1.</exception>
    /// <exception cref="ArgumentException">A channel of <paramref name="color"/> is not finite.</exception>
    public GradientStop(float offset, RgbaF32 color)
    {
        if (!(offset is >= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "A stop's offset must be from 0 to 1.");
        }

        if (!(float.IsFinite(color.R) && float.IsFinite(color.G) && float.IsFinite(color.B) && float.IsFinite(color.A)))
        {
            throw new ArgumentException("A stop's color must be finite.", nameof(color));
        }

        Offset = offset;
        Color = color;
    }

    /// <summary>Initializes a new instance of the <see cref="GradientStop"/> struct.</summary>
    /// <param name="offset">The position along the gradient, from 0 to 1.</param>
    /// <param name="color">The color.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside 0 to 1.</exception>
    public GradientStop(float offset, Rgba8 color)
        : this(offset, RgbaF32.FromRgba8(color))
    {
    }

    /// <summary>Gets the position along the gradient, from 0 to 1.</summary>
    public float Offset { get; }

    /// <summary>Gets the color, with straight alpha.</summary>
    public RgbaF32 Color { get; }
}

/// <summary>What a gradient or an image paint does past its ends or edges, as Skia's tile modes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="gradients"/>
/// </example>
public enum TileMode
{
    /// <summary>Repeats the end color (or the edge pixels) outward. With a gradient's <see cref="Gradient.ExtendStart"/> or <see cref="Gradient.ExtendEnd"/> off, that side is left transparent instead.</summary>
    Clamp,

    /// <summary>Starts over at each end.</summary>
    Repeat,

    /// <summary>Repeats, every other copy reflected, so the copies meet without a seam.</summary>
    Mirror,

    /// <summary>Leaves everything past the ends or edges transparent.</summary>
    Decal,
}

/// <summary>Which way around the hue circle an Oklch gradient goes between two stops, as CSS Color 4's hue interpolation methods.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="gradients"/>
/// </example>
public enum HueMethod
{
    /// <summary>The shorter way: at most 180 degrees. The default.</summary>
    Shorter,

    /// <summary>The longer way: at least 180 degrees.</summary>
    Longer,

    /// <summary>Always with increasing hue.</summary>
    Increasing,

    /// <summary>Always with decreasing hue.</summary>
    Decreasing,
}

/// <summary>The color space a gradient's stops are mixed in.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="gradients"/>
/// </example>
public enum InterpolationSpace
{
    /// <summary>The stored sRGB-encoded values, as most libraries, browsers' legacy colors and both of Egami's references do.</summary>
    Encoded,

    /// <summary>Linear-light sRGB: physically even mixing, brighter midpoints.</summary>
    LinearSrgb,

    /// <summary>Oklab: perceptually even mixing without the gray midpoints of opposite colors' encoded mixes.</summary>
    Oklab,

    /// <summary>Oklch, Oklab's polar form: lightness, chroma and hue mix separately, the hue around the circle by a <see cref="HueMethod"/>.</summary>
    Oklch,
}

/// <summary>
/// How a gradient mixes its stops: the color space, as CSS Color 4 defines them, and for <see cref="InterpolationSpace.Oklch"/> the
/// way around the hue circle.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="gradients"/>
/// </example>
public sealed record class GradientInterpolation
{
    private GradientInterpolation(InterpolationSpace space, HueMethod hue)
    {
        Space = space;
        Hue = hue;
    }

    /// <summary>Gets mixing in the stored encoded values: the default.</summary>
    public static GradientInterpolation Encoded { get; } = new(InterpolationSpace.Encoded, HueMethod.Shorter);

    /// <summary>Gets mixing in linear-light sRGB.</summary>
    public static GradientInterpolation LinearSrgb { get; } = new(InterpolationSpace.LinearSrgb, HueMethod.Shorter);

    /// <summary>Gets mixing in Oklab.</summary>
    public static GradientInterpolation Oklab { get; } = new(InterpolationSpace.Oklab, HueMethod.Shorter);

    /// <summary>Gets the color space.</summary>
    public InterpolationSpace Space { get; }

    /// <summary>Gets the way around the hue circle; used by <see cref="InterpolationSpace.Oklch"/> only.</summary>
    public HueMethod Hue { get; }

    /// <summary>Returns mixing in Oklch with a hue method.</summary>
    /// <param name="hue">The way around the hue circle.</param>
    /// <returns>The interpolation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hue"/> is not defined.</exception>
    public static GradientInterpolation Oklch(HueMethod hue = HueMethod.Shorter)
        => Enum.IsDefined(hue) ? new(InterpolationSpace.Oklch, hue) : throw new ArgumentOutOfRangeException(nameof(hue), hue, null);
}

/// <summary>
/// A paint whose color changes along a line, around a center or between two circles by its stops. Each pixel center maps back
/// through the canvas transform and <see cref="Paint.LocalMatrix"/> to a position t along the gradient, which <see cref="Tile"/>
/// brings into 0 to 1 and the stops turn into a color. On 8-bit targets t picks one of 1024 colors made once per paint; float and
/// 16-bit targets mix the stops exactly.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="gradients"/>
/// </example>
public abstract record class Gradient : Paint
{
    private readonly GradientStop[] _stops;
    private readonly TileMode _tile;
    private readonly GradientInterpolation _interpolation = GradientInterpolation.Encoded;

    /// <summary>Initializes a new instance of the <see cref="Gradient"/> class.</summary>
    /// <param name="stops">The stops, in order of offset; at least one.</param>
    /// <exception cref="ArgumentException">There are no stops.</exception>
    private protected Gradient(ReadOnlySpan<GradientStop> stops)
    {
        if (stops.IsEmpty)
        {
            throw new ArgumentException("A gradient needs at least one stop.", nameof(stops));
        }

        _stops = stops.ToArray();
    }

    /// <summary>Gets the stops, in the order given. An offset below the one before it is treated as equal to it, as CSS does.</summary>
    public IReadOnlyList<GradientStop> Stops => Array.AsReadOnly(_stops);

    /// <summary>Gets what happens past the gradient's ends. Defaults to <see cref="TileMode.Clamp"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public TileMode Tile
    {
        get => _tile;
        init => _tile = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets a value indicating whether a <see cref="TileMode.Clamp"/> gradient repeats its first color before the start; when
    /// <see langword="false"/> nothing is drawn there, as a PDF shading with Extend false. Other tile modes ignore it. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    public bool ExtendStart { get; init; } = true;

    /// <summary>Gets a value indicating whether a <see cref="TileMode.Clamp"/> gradient repeats its last color past the end. Defaults to <see langword="true"/>.</summary>
    public bool ExtendEnd { get; init; } = true;

    /// <summary>Gets the color space the stops mix in. Defaults to <see cref="GradientInterpolation.Encoded"/>.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public GradientInterpolation Interpolation
    {
        get => _interpolation;
        init => _interpolation = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets a value indicating whether colors mix premultiplied by their alpha, as CSS specifies, so a fade to a transparent color
    /// never darkens toward that color's hidden channels. <see langword="false"/> mixes straight colors, as Skia does by default.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool Premultiplied { get; init; } = true;

    /// <inheritdoc/>
    public virtual bool Equals(Gradient? other)
        => other is not null && base.Equals(other) && _stops.AsSpan().SequenceEqual(other._stops) && _tile == other._tile
            && ExtendStart == other.ExtendStart && ExtendEnd == other.ExtendEnd && _interpolation == other._interpolation
            && Premultiplied == other.Premultiplied;

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(base.GetHashCode());
        foreach (GradientStop stop in _stops)
        {
            hash.Add(stop);
        }

        hash.Add(_tile);
        hash.Add(ExtendStart);
        hash.Add(ExtendEnd);
        hash.Add(_interpolation);
        hash.Add(Premultiplied);
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    internal override Shader? CreateShader(Matrix3x2 toDevice, ColorSpace colorSpace)
    {
        if (!TryInverse(toDevice, out Matrix3x2 inverse))
        {
            return null;
        }

        GradientRamp ramp = GradientRamp.Of(this);
        GradientGeometry? geometry = CreateGeometry(inverse);
        return geometry is null ? Degenerate(ramp) : new GradientShader(geometry, ramp, this, useTable: false);
    }

    /// <inheritdoc/>
    internal override PixelShader<TPixel>? CreatePixelShader<TPixel>(Matrix3x2 toDevice, ColorSpace colorSpace)
    {
        if (TPixel.Layout.ChannelType != PixelChannelType.Unorm8)
        {
            return base.CreatePixelShader<TPixel>(toDevice, colorSpace);
        }

        if (!TryInverse(toDevice, out Matrix3x2 inverse))
        {
            return null;
        }

        GradientRamp ramp = GradientRamp.Of(this);
        GradientGeometry? geometry = CreateGeometry(inverse);
        if (geometry is null)
        {
            Shader? degenerate = Degenerate(ramp);
            return degenerate is null ? null : new ConvertingShader<TPixel>(degenerate, Dither);
        }

        // An 8-bit target reads the 1024-entry table; dithering rounds the table's float colors itself.
        return Dither
            ? new ConvertingShader<TPixel>(new GradientShader(geometry, ramp, this, useTable: true), dither: true)
            : new GradientPixelShader<TPixel>(geometry, ramp, this);
    }

    /// <summary>Returns the device-to-t mapping, or <see langword="null"/> when the gradient's geometry has no extent.</summary>
    internal abstract GradientGeometry? CreateGeometry(Matrix3x2 inverse);

    /// <summary>A gradient with no extent, as Skia draws one: nothing for Decal, the mean color for Repeat and Mirror, the last color for Clamp.</summary>
    private PremulShader? Degenerate(GradientRamp ramp) => _tile switch
    {
        TileMode.Decal => null,
        TileMode.Clamp => ExtendEnd ? new PremulShader(ramp.Evaluate(1)) : null,
        _ => new PremulShader(ramp.Mean),
    };

    /// <summary>Checks a point is finite.</summary>
    private protected static Vector2 FinitePoint(Vector2 value, string name)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) ? value : throw new ArgumentException("The point must be finite.", name);

    /// <summary>Checks a radius is finite and not negative.</summary>
    private protected static float CheckRadius(float value, string name)
        => value >= 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, "A radius must be finite and not negative.");

    /// <summary>The stops of two colors at the ends.</summary>
    private protected static GradientStop[] Ends(RgbaF32 startColor, RgbaF32 endColor) => [new(0, startColor), new(1, endColor)];
}

/// <summary>A gradient along a line: t is 0 on the line through <see cref="Start"/> square to it, and 1 through <see cref="End"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="gradients"/>
/// </example>
public sealed record class LinearGradient : Gradient
{
    private readonly Vector2 _start;
    private readonly Vector2 _end;

    /// <summary>Initializes a new instance of the <see cref="LinearGradient"/> class.</summary>
    /// <param name="start">Where t is 0, in paint coordinates.</param>
    /// <param name="end">Where t is 1.</param>
    /// <param name="stops">The stops; at least one.</param>
    /// <exception cref="ArgumentException">A point is not finite, or there are no stops.</exception>
    public LinearGradient(Vector2 start, Vector2 end, params ReadOnlySpan<GradientStop> stops)
        : base(stops)
    {
        Start = start;
        End = end;
    }

    /// <summary>Initializes a new instance of the <see cref="LinearGradient"/> class from one color to another.</summary>
    /// <param name="start">Where t is 0.</param>
    /// <param name="end">Where t is 1.</param>
    /// <param name="startColor">The color at the start.</param>
    /// <param name="endColor">The color at the end.</param>
    /// <exception cref="ArgumentException">A point or color is not finite.</exception>
    public LinearGradient(Vector2 start, Vector2 end, RgbaF32 startColor, RgbaF32 endColor)
        : this(start, end, Ends(startColor, endColor))
    {
    }

    /// <summary>Gets where t is 0, in paint coordinates.</summary>
    /// <exception cref="ArgumentException">The point is not finite.</exception>
    public Vector2 Start
    {
        get => _start;
        init => _start = FinitePoint(value, nameof(value));
    }

    /// <summary>Gets where t is 1. A gradient whose end is its start has no extent and draws as Skia draws one: the last color for Clamp, the mean color for Repeat and Mirror, nothing for Decal.</summary>
    /// <exception cref="ArgumentException">The point is not finite.</exception>
    public Vector2 End
    {
        get => _end;
        init => _end = FinitePoint(value, nameof(value));
    }

    /// <inheritdoc/>
    internal override GradientGeometry? CreateGeometry(Matrix3x2 inverse) => LinearGeometry.Create(inverse, _start, _end);
}

/// <summary>A gradient in circles around a center: t is the distance from <see cref="Center"/> divided by <see cref="Radius"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="gradients"/>
/// </example>
public sealed record class RadialGradient : Gradient
{
    private readonly Vector2 _center;
    private readonly float _radius;

    /// <summary>Initializes a new instance of the <see cref="RadialGradient"/> class.</summary>
    /// <param name="center">The center, in paint coordinates.</param>
    /// <param name="radius">Where t is 1; 0 gives a gradient with no extent.</param>
    /// <param name="stops">The stops; at least one.</param>
    /// <exception cref="ArgumentException">The center is not finite, or there are no stops.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or not finite.</exception>
    public RadialGradient(Vector2 center, float radius, params ReadOnlySpan<GradientStop> stops)
        : base(stops)
    {
        Center = center;
        Radius = radius;
    }

    /// <summary>Initializes a new instance of the <see cref="RadialGradient"/> class from one color at the center to another at the radius.</summary>
    /// <param name="center">The center.</param>
    /// <param name="radius">Where t is 1.</param>
    /// <param name="centerColor">The color at the center.</param>
    /// <param name="edgeColor">The color at the radius.</param>
    public RadialGradient(Vector2 center, float radius, RgbaF32 centerColor, RgbaF32 edgeColor)
        : this(center, radius, Ends(centerColor, edgeColor))
    {
    }

    /// <summary>Gets the center, in paint coordinates.</summary>
    /// <exception cref="ArgumentException">The point is not finite.</exception>
    public Vector2 Center
    {
        get => _center;
        init => _center = FinitePoint(value, nameof(value));
    }

    /// <summary>Gets the radius, where t is 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float Radius
    {
        get => _radius;
        init => _radius = CheckRadius(value, nameof(value));
    }

    /// <inheritdoc/>
    internal override GradientGeometry? CreateGeometry(Matrix3x2 inverse) => RadialGeometry.Create(inverse, _center, _radius);
}

/// <summary>
/// A gradient between two circles, as the HTML canvas's createRadialGradient, Skia's two-point conical gradient and a PDF type 3
/// shading define it: for each t, the circle whose center and radius are t of the way from the start circle to the end one; a
/// pixel takes the largest t whose circle passes through it with a radius of at least 0. It covers a focal point inside a circle
/// (a start radius of 0), one circle inside the other, and cones drawn on one side only.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="gradients"/>
/// </example>
public sealed record class ConicalGradient : Gradient
{
    private readonly Vector2 _start;
    private readonly float _startRadius;
    private readonly Vector2 _end;
    private readonly float _endRadius;

    /// <summary>Initializes a new instance of the <see cref="ConicalGradient"/> class.</summary>
    /// <param name="start">The start circle's center, in paint coordinates.</param>
    /// <param name="startRadius">The start circle's radius, where t is 0.</param>
    /// <param name="end">The end circle's center.</param>
    /// <param name="endRadius">The end circle's radius, where t is 1.</param>
    /// <param name="stops">The stops; at least one.</param>
    /// <exception cref="ArgumentException">A center is not finite, or there are no stops.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A radius is negative or not finite.</exception>
    public ConicalGradient(Vector2 start, float startRadius, Vector2 end, float endRadius, params ReadOnlySpan<GradientStop> stops)
        : base(stops)
    {
        Start = start;
        StartRadius = startRadius;
        End = end;
        EndRadius = endRadius;
    }

    /// <summary>Gets the start circle's center.</summary>
    /// <exception cref="ArgumentException">The point is not finite.</exception>
    public Vector2 Start
    {
        get => _start;
        init => _start = FinitePoint(value, nameof(value));
    }

    /// <summary>Gets the start circle's radius.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float StartRadius
    {
        get => _startRadius;
        init => _startRadius = CheckRadius(value, nameof(value));
    }

    /// <summary>Gets the end circle's center.</summary>
    /// <exception cref="ArgumentException">The point is not finite.</exception>
    public Vector2 End
    {
        get => _end;
        init => _end = FinitePoint(value, nameof(value));
    }

    /// <summary>Gets the end circle's radius.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float EndRadius
    {
        get => _endRadius;
        init => _endRadius = CheckRadius(value, nameof(value));
    }

    /// <inheritdoc/>
    internal override GradientGeometry? CreateGeometry(Matrix3x2 inverse)
        => ConicalGeometry.Create(inverse, _start, _startRadius, _end, _endRadius, this);
}

/// <summary>
/// A gradient around a center, as Skia's sweep gradient and CSS's conic-gradient: t goes from 0 at <see cref="StartAngle"/> to 1
/// at <see cref="EndAngle"/>, in degrees clockwise (on a y-down screen) from the positive x axis.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="gradients"/>
/// </example>
public sealed record class SweepGradient : Gradient
{
    private readonly Vector2 _center;
    private readonly float _startAngle;
    private readonly float _endAngle = 360;

    /// <summary>Initializes a new instance of the <see cref="SweepGradient"/> class going once around.</summary>
    /// <param name="center">The center, in paint coordinates.</param>
    /// <param name="stops">The stops; at least one.</param>
    /// <exception cref="ArgumentException">The center is not finite, or there are no stops.</exception>
    public SweepGradient(Vector2 center, params ReadOnlySpan<GradientStop> stops)
        : base(stops)
    {
        Center = center;
    }

    /// <summary>Initializes a new instance of the <see cref="SweepGradient"/> class between two angles.</summary>
    /// <param name="center">The center, in paint coordinates.</param>
    /// <param name="startAngle">Where t is 0, in degrees.</param>
    /// <param name="endAngle">Where t is 1, in degrees, after <paramref name="startAngle"/>.</param>
    /// <param name="stops">The stops; at least one.</param>
    /// <exception cref="ArgumentException">The center is not finite, or there are no stops.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An angle is not finite, or <paramref name="endAngle"/> is not after <paramref name="startAngle"/>.</exception>
    public SweepGradient(Vector2 center, float startAngle, float endAngle, params ReadOnlySpan<GradientStop> stops)
        : base(stops)
    {
        if (!(float.IsFinite(startAngle) && float.IsFinite(endAngle) && endAngle > startAngle))
        {
            throw new ArgumentOutOfRangeException(nameof(endAngle), endAngle, "The angles must be finite, the end after the start.");
        }

        Center = center;
        _startAngle = startAngle;
        _endAngle = endAngle;
    }

    /// <summary>Gets the center, in paint coordinates.</summary>
    /// <exception cref="ArgumentException">The point is not finite.</exception>
    public Vector2 Center
    {
        get => _center;
        init => _center = FinitePoint(value, nameof(value));
    }

    /// <summary>Gets the angle where t is 0, in degrees. Defaults to 0.</summary>
    public float StartAngle => _startAngle;

    /// <summary>Gets the angle where t is 1, in degrees. Defaults to 360.</summary>
    public float EndAngle => _endAngle;

    /// <inheritdoc/>
    internal override GradientGeometry? CreateGeometry(Matrix3x2 inverse) => new SweepGeometry(inverse, _center, _startAngle, _endAngle);
}
