namespace Egami.Colors;

/// <summary>
/// What an HDR file says about the display it was mastered on and the light levels in it (SMPTE ST 2086 and CTA-861.3, as PNG's
/// mDCV and cLLI chunks store them). Tone mapping takes its source peak from here. Luminances are in cd/m².
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="hdr-metadata"/>
/// </example>
public sealed record HdrMetadata
{
    /// <summary>Gets the mastering display's primaries and white point, or <see langword="null"/>.</summary>
    public ColorPrimaries? MasteringPrimaries { get; init; }

    /// <summary>Gets the mastering display's peak luminance, or <see langword="null"/>.</summary>
    public double? MasteringMaxLuminance { get; init; }

    /// <summary>Gets the mastering display's black luminance, or <see langword="null"/>.</summary>
    public double? MasteringMinLuminance { get; init; }

    /// <summary>Gets the maximum content light level (MaxCLL): the brightest pixel's luminance, or <see langword="null"/>.</summary>
    public double? MaxContentLightLevel { get; init; }

    /// <summary>Gets the maximum frame-average light level (MaxFALL), or <see langword="null"/>.</summary>
    public double? MaxFrameAverageLightLevel { get; init; }
}
