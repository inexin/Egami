namespace Egami.Colors;

/// <summary>How a color conversion handles colors the target cannot show, with the ICC numbering (0 to 3).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="icc"/>
/// </example>
public enum RenderingIntent
{
    /// <summary>Compress the whole gamut to fit, keeping relations between colors; uses the profile's perceptual tables.</summary>
    Perceptual = 0,

    /// <summary>Keep in-gamut colors exact relative to each white point and clip the rest.</summary>
    RelativeColorimetric = 1,

    /// <summary>Keep colors vivid rather than exact; uses the profile's saturation tables.</summary>
    Saturation = 2,

    /// <summary>Keep in-gamut colors exact, without adapting the white point (paper white shows as it measures).</summary>
    AbsoluteColorimetric = 3,
}
