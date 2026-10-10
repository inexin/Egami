namespace Egami.Colors;

/// <summary>
/// The chromaticities of an RGB color space's red, green and blue primaries and of its white. With a transfer function they
/// make a <see cref="ColorProfile"/>.
/// </summary>
/// <param name="Red">The red primary.</param>
/// <param name="Green">The green primary.</param>
/// <param name="Blue">The blue primary.</param>
/// <param name="White">The white point.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="primaries"/>
/// </example>
public sealed record ColorPrimaries(Chromaticity Red, Chromaticity Green, Chromaticity Blue, Chromaticity White)
{
    /// <summary>Gets the ITU-R BT.709 primaries with D65 white, which sRGB uses.</summary>
    public static ColorPrimaries Rec709 { get; } = new(new(0.64, 0.33), new(0.30, 0.60), new(0.15, 0.06), Chromaticity.D65);

    /// <summary>Gets the Display P3 primaries (SMPTE EG 432-1) with D65 white.</summary>
    public static ColorPrimaries DisplayP3 { get; } = new(new(0.680, 0.320), new(0.265, 0.690), new(0.150, 0.060), Chromaticity.D65);

    /// <summary>Gets the ITU-R BT.2020 primaries with D65 white, used by Rec. 2020 and Rec. 2100.</summary>
    public static ColorPrimaries Rec2020 { get; } = new(new(0.708, 0.292), new(0.170, 0.797), new(0.131, 0.046), Chromaticity.D65);

    /// <summary>Gets the Adobe RGB (1998) primaries with D65 white.</summary>
    public static ColorPrimaries AdobeRgb { get; } = new(new(0.64, 0.33), new(0.21, 0.71), new(0.15, 0.06), Chromaticity.D65);

    /// <summary>Gets the ProPhoto RGB (ROMM RGB) primaries with D50 white.</summary>
    public static ColorPrimaries ProPhoto { get; } = new(new(0.734699, 0.265301), new(0.159597, 0.840403), new(0.036598, 0.000105), Chromaticity.D50);

    /// <summary>Gets a value indicating whether the primaries can form a color space: every point valid and the triangle not flat.</summary>
    public bool IsValid
    {
        get
        {
            foreach (Chromaticity c in (ReadOnlySpan<Chromaticity>)[Red, Green, Blue, White])
            {
                if (!double.IsFinite(c.X) || !double.IsFinite(c.Y) || c.Y <= 0 || c.X < 0 || c.X + c.Y > 1.0000001)
                {
                    return false;
                }
            }

            double area = ((Green.X - Red.X) * (Blue.Y - Red.Y)) - ((Blue.X - Red.X) * (Green.Y - Red.Y));
            return Math.Abs(area) > 1e-6;
        }
    }

    /// <summary>Returns whether two sets of primaries are the same within <paramref name="tolerance"/> in x and y.</summary>
    /// <param name="other">The other primaries.</param>
    /// <param name="tolerance">The largest difference allowed in any coordinate.</param>
    /// <returns><see langword="true"/> if every coordinate matches.</returns>
    public bool IsCloseTo(ColorPrimaries other, double tolerance = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Close(Red, other.Red) && Close(Green, other.Green) && Close(Blue, other.Blue) && Close(White, other.White);

        bool Close(Chromaticity a, Chromaticity b) => Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;
    }

    /// <summary>The matrix from linear RGB in these primaries to XYZ relative to their own white (white maps to Y = 1).</summary>
    internal Matrix3d ToXyz()
    {
        Matrix3d m = Matrix3d.FromColumns(Red.ToXyz(), Green.ToXyz(), Blue.ToXyz());
        Vector3d s = m.Inverse() * White.ToXyz();
        return m * Matrix3d.Diagonal(s);
    }
}
