namespace Egami.Colors;

/// <summary>How a color conversion maps colors: the rendering intent and black point compensation.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="transform"/>
/// </example>
public sealed record ColorTransformOptions
{
    /// <summary>Gets the defaults: perceptual, without black point compensation, as Little CMS and Pillow default to.</summary>
    public static ColorTransformOptions Default { get; } = new();

    /// <summary>Gets the rendering intent. Defaults to <see cref="RenderingIntent.Perceptual"/>.</summary>
    public RenderingIntent Intent { get; init; } = RenderingIntent.Perceptual;

    /// <summary>
    /// Gets a value indicating whether to map the source's black to the target's (Adobe's black point compensation), which keeps
    /// shadow detail when converting between devices with different blacks. Defaults to <see langword="false"/>. Perceptual and
    /// saturation conversions into a version 4 ICC profile always compensate, as ICC requires.
    /// </summary>
    public bool BlackPointCompensation { get; init; }
}
