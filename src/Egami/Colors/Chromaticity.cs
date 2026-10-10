namespace Egami.Colors;

/// <summary>A CIE 1931 chromaticity: the x and y coordinates of a color with its brightness left out.</summary>
/// <param name="X">The x coordinate.</param>
/// <param name="Y">The y coordinate.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="primaries"/>
/// </example>
public readonly record struct Chromaticity(double X, double Y)
{
    /// <summary>Gets the CIE standard illuminant D65 (x 0.3127, y 0.3290), the white of sRGB, Display P3 and Rec. 2020.</summary>
    public static Chromaticity D65 { get; } = new(0.3127, 0.3290);

    /// <summary>Gets the CIE standard illuminant D50 (x 0.3457, y 0.3585), the white of ProPhoto RGB and of ICC's connection space.</summary>
    public static Chromaticity D50 { get; } = new(0.3457, 0.3585);

    /// <summary>Returns the XYZ of this chromaticity scaled to Y = 1.</summary>
    internal Vector3d ToXyz()
    {
        return new Vector3d(X / Y, 1.0, (1.0 - X - Y) / Y);
    }

    /// <summary>Returns the chromaticity of an XYZ value; black gives (0, 0).</summary>
    internal static Chromaticity FromXyz(Vector3d xyz)
    {
        double sum = xyz.X + xyz.Y + xyz.Z;
        return sum == 0 ? default : new Chromaticity(xyz.X / sum, xyz.Y / sum);
    }
}
