namespace Egami.Colors;

/// <summary>
/// How a color conversion maps colors: the rendering intent, black point compensation, and for HDR sources, tone mapping, the
/// source peak and reference white.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="transform"/>
/// </example>
public sealed record ColorTransformOptions
{
    private readonly double? _sourcePeakNits;
    private readonly double _referenceWhiteNits = 203;

    /// <summary>Gets the defaults: perceptual, without black point compensation, as Little CMS and Pillow default to; BT.2390 for HDR.</summary>
    public static ColorTransformOptions Default { get; } = new();

    /// <summary>Gets the rendering intent. Defaults to <see cref="RenderingIntent.Perceptual"/>.</summary>
    public RenderingIntent Intent { get; init; } = RenderingIntent.Perceptual;

    /// <summary>
    /// Gets a value indicating whether to map the source's black to the target's (Adobe's black point compensation), which keeps
    /// shadow detail when converting between devices with different blacks. Defaults to <see langword="false"/>. Perceptual and
    /// saturation conversions into a version 4 ICC profile always compensate, as ICC requires.
    /// </summary>
    public bool BlackPointCompensation { get; init; }

    /// <summary>
    /// Gets how a conversion from an HDR encoding (PQ or HLG) to an SDR one brings highlights into range, then clips what is still
    /// out of the target's gamut toward each pixel's luminance, which keeps hue. Defaults to <see cref="ToneMapping.Bt2390"/>;
    /// <see cref="ToneMapping.Clip"/> turns it off. HDR to HDR and SDR conversions never tone map.
    /// </summary>
    public ToneMapping ToneMapping { get; init; } = ToneMapping.Bt2390;

    /// <summary>
    /// Gets the brightest light in the source, in cd/m², or <see langword="null"/> to take it from the raster's
    /// <see cref="Metadata.RasterMetadata.Hdr"/> (MaxCLL, then the mastering display's peak), else 1,000 cd/m².
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is not a positive finite number.</exception>
    public double? SourcePeakNits
    {
        get => _sourcePeakNits;
        init => _sourcePeakNits = value is null || (double.IsFinite(value.Value) && value > 0) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The peak must be a positive finite number.");
    }

    /// <summary>
    /// Gets the luminance an HDR encoding's reference white has, in cd/m², which becomes 1.0 in linear light and SDR white.
    /// Defaults to 203, as ITU-R BT.2408 recommends.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is not a positive finite number.</exception>
    public double ReferenceWhiteNits
    {
        get => _referenceWhiteNits;
        init => _referenceWhiteNits = double.IsFinite(value) && value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The reference white must be a positive finite number.");
    }

    /// <summary>The source peak to use: the option, else the metadata's, else 1,000 cd/m².</summary>
    internal double PeakFor(HdrMetadata? hdr) => SourcePeakNits ?? (hdr?.MaxContentLightLevel is > 0 and double cll ? cll : hdr?.MasteringMaxLuminance is > 0 and double max ? max : 1000);
}
