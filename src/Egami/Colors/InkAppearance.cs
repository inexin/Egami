using System.Numerics;

using Egami.Pixels;

namespace Egami.Colors;

/// <summary>The colour model an <see cref="InkAppearance"/> is given in.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="inks"/>
/// </example>
public enum InkAppearanceKind
{
    /// <summary>Process CMYK: converted through the canvas's process profile, or FOGRA39 where there is none.</summary>
    Cmyk,

    /// <summary>sRGB.</summary>
    Rgb,

    /// <summary>CIELAB, D50.</summary>
    Lab,
}

/// <summary>
/// How an ink looks at full tint where it cannot be printed: on an RGB canvas, in SVG, in a proof of a raster without its plate,
/// or drawn into the process inks of a raster that lacks it. A process CMYK colour, an sRGB colour or a Lab colour, as PDF's
/// alternate colour spaces allow.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="inks"/>
/// </example>
public readonly struct InkAppearance : IEquatable<InkAppearance>
{
    private static readonly Lazy<ColorTransform> CmykToSrgb = new(() => ColorTransform.Create(ColorProfile.Fogra39, ColorProfile.Srgb));

    private InkAppearance(InkAppearanceKind kind, Vector4 values)
    {
        (Kind, Values) = (kind, values);
    }

    /// <summary>Gets the colour model.</summary>
    public InkAppearanceKind Kind { get; }

    /// <summary>
    /// Gets the values: cyan, magenta, yellow and black 0 to 1 for <see cref="InkAppearanceKind.Cmyk"/>; red, green and blue 0
    /// to 1 and 0 for <see cref="InkAppearanceKind.Rgb"/>; L, a, b and 0 for <see cref="InkAppearanceKind.Lab"/>.
    /// </summary>
    public Vector4 Values { get; }

    /// <summary>Returns a process CMYK appearance.</summary>
    /// <param name="c">Cyan, 0 to 1.</param>
    /// <param name="m">Magenta, 0 to 1.</param>
    /// <param name="y">Yellow, 0 to 1.</param>
    /// <param name="k">Black, 0 to 1.</param>
    /// <returns>The appearance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside 0 to 1.</exception>
    public static InkAppearance Cmyk(float c, float m, float y, float k) =>
        new(InkAppearanceKind.Cmyk, new Vector4(Unit(c, nameof(c)), Unit(m, nameof(m)), Unit(y, nameof(y)), Unit(k, nameof(k))));

    /// <summary>Returns an sRGB appearance.</summary>
    /// <param name="r">Red, 0 to 1.</param>
    /// <param name="g">Green, 0 to 1.</param>
    /// <param name="b">Blue, 0 to 1.</param>
    /// <returns>The appearance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside 0 to 1.</exception>
    public static InkAppearance Rgb(float r, float g, float b) =>
        new(InkAppearanceKind.Rgb, new Vector4(Unit(r, nameof(r)), Unit(g, nameof(g)), Unit(b, nameof(b)), 0));

    /// <summary>Returns a Lab appearance (D50, as ICC's PCS and PDF's Lab colours).</summary>
    /// <param name="lab">The colour: L 0 to 100.</param>
    /// <returns>The appearance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">L is outside 0 to 100, or a or b is not finite.</exception>
    public static InkAppearance Lab(LabColor lab)
    {
        if (!(lab.L is >= 0 and <= 100) || !float.IsFinite(lab.A) || !float.IsFinite(lab.B))
        {
            throw new ArgumentOutOfRangeException(nameof(lab), lab, "L must be from 0 to 100 and a and b finite.");
        }

        return new InkAppearance(InkAppearanceKind.Lab, new Vector4(lab.L, lab.A, lab.B, 0));
    }

    /// <summary>
    /// Returns the appearance as Lab: a CMYK appearance through FOGRA39 (relative colorimetric), an RGB one as sRGB.
    /// </summary>
    /// <returns>The colour.</returns>
    public LabColor ToLab()
    {
        Vector4 v = Values;
        switch (Kind)
        {
            case InkAppearanceKind.Lab:
                return new LabColor(v.X, v.Y, v.Z);
            case InkAppearanceKind.Rgb:
                return LabColor.From(new RgbaF32(v.X, v.Y, v.Z, 1));
            default:
                Span<RgbaF32> rgb = stackalloc RgbaF32[1];
                ReadOnlySpan<Cmyk16> cmyk = [new Cmyk16(Word(v.X), Word(v.Y), Word(v.Z), Word(v.W))];
                CmykToSrgb.Value.Apply(cmyk, rgb);
                return LabColor.From(rgb[0]);
        }
    }

    /// <summary>Returns whether two appearances have the same model and values.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(InkAppearance left, InkAppearance right) => left.Equals(right);

    /// <summary>Returns whether two appearances differ in model or values.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(InkAppearance left, InkAppearance right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(InkAppearance other) => Kind == other.Kind && Values == other.Values;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is InkAppearance other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, Values);

    /// <inheritdoc/>
    public override string ToString() => Kind switch
    {
        InkAppearanceKind.Cmyk => $"CMYK({Values.X:0.###}, {Values.Y:0.###}, {Values.Z:0.###}, {Values.W:0.###})",
        InkAppearanceKind.Rgb => $"RGB({Values.X:0.###}, {Values.Y:0.###}, {Values.Z:0.###})",
        _ => $"Lab({Values.X:0.###}, {Values.Y:0.###}, {Values.Z:0.###})",
    };

    /// <summary>
    /// Combines inks printed over one another, each scaled by its tint, as filters: CMYK appearances in complement space (each
    /// component 1 − Π(1 − tint × value)), sRGB ones by multiplying (each 1 − tint × (1 − value)), and a mix of models in Lab
    /// through the product of XYZ relative to white. <see langword="null"/> when no ink has an appearance.
    /// </summary>
    internal static InkAppearance? Combine(ReadOnlySpan<InkTint> inks)
    {
        InkAppearanceKind? kind = null;
        bool mixed = false;
        foreach (InkTint ink in inks)
        {
            if (ink.Colorant.Appearance is { } a)
            {
                mixed |= kind is not null && kind != a.Kind;
                kind ??= a.Kind;
            }
        }

        if (kind is null)
        {
            return null;
        }

        if (!mixed && kind != InkAppearanceKind.Lab)
        {
            Vector4 product = Vector4.One;
            foreach (InkTint ink in inks)
            {
                if (ink.Colorant.Appearance is { } a)
                {
                    Vector4 share = kind == InkAppearanceKind.Cmyk ? a.Values : Vector4.One - a.Values;
                    product *= Vector4.One - (ink.Tint * share);
                }
            }

            return kind == InkAppearanceKind.Cmyk
                ? new InkAppearance(InkAppearanceKind.Cmyk, Vector4.One - product)
                : new InkAppearance(InkAppearanceKind.Rgb, product with { W = 0 });
        }

        // Lab: each ink's XYZ relative to the white is how much light it passes; the inks over one another multiply.
        Vector3d white = Cie.D50;
        double x = 1, y = 1, z = 1;
        foreach (InkTint ink in inks)
        {
            if (ink.Colorant.Appearance is { } a)
            {
                LabColor lab = a.ToLab();
                Vector3d xyz = Cie.LabToXyz(new Vector3d(lab.L, lab.A, lab.B), white);
                x *= 1 - (ink.Tint * (1 - (xyz.X / white.X)));
                y *= 1 - (ink.Tint * (1 - (xyz.Y / white.Y)));
                z *= 1 - (ink.Tint * (1 - (xyz.Z / white.Z)));
            }
        }

        Vector3d result = Cie.XyzToLab(new Vector3d(x * white.X, y * white.Y, z * white.Z), white);
        return Lab(new LabColor((float)Math.Clamp(result.X, 0, 100), (float)result.Y, (float)result.Z));
    }

    private static float Unit(float value, string name) =>
        value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(name, value, "The value must be from 0 to 1.");

    private static ushort Word(float value) => (ushort)MathF.Round(value * 65535);
}
