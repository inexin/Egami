using Egami.Metadata;

namespace Egami.Formats.Jpeg;

/// <summary>The color model a JPEG's components are stored in.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="jpeg-details"/>
/// </example>
public enum JpegColorSpace
{
    /// <summary>One component of luma.</summary>
    Gray,

    /// <summary>Three components of luma and chroma: the usual JFIF case.</summary>
    YCbCr,

    /// <summary>Three components of red, green and blue, as an Adobe segment with transform 0 says.</summary>
    Rgb,

    /// <summary>Four components of cyan, magenta, yellow and black (Adobe transform 0). Converted to RGB when decoded.</summary>
    Cmyk,

    /// <summary>Four components, YCbCr plus black (Adobe transform 2). Converted to RGB when decoded.</summary>
    Ycck,
}

/// <summary>One component of a JPEG frame and its sampling factors.</summary>
/// <param name="Id">The component identifier from the frame header.</param>
/// <param name="HorizontalSampling">The horizontal sampling factor, 1 to 4.</param>
/// <param name="VerticalSampling">The vertical sampling factor, 1 to 4.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="jpeg-details"/>
/// </example>
public sealed record JpegComponent(int Id, int HorizontalSampling, int VerticalSampling);

/// <summary>Facts about a decoded JPEG that have no general home in <see cref="RasterMetadata"/>. Read-only facts; not written.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="jpeg-details"/>
/// </example>
public sealed class JpegDetails : FormatDetails
{
    /// <summary>Gets or sets whether the file is progressive rather than baseline (sequential).</summary>
    public bool Progressive { get; set; }

    /// <summary>Gets or sets the color model the components are stored in.</summary>
    public JpegColorSpace ColorSpace { get; set; }

    /// <summary>Gets the components, in frame order, with their sampling factors (for example 2×2, 1×1, 1×1 for 4:2:0).</summary>
    public IList<JpegComponent> Components { get; } = [];

    /// <summary>Gets or sets the restart interval in MCUs, or 0 for none.</summary>
    public int RestartInterval { get; set; }

    /// <summary>
    /// Gets or sets the IJG quality (1 to 100) whose scaled Annex K table is closest to the file's luma quantization table, or
    /// <see langword="null"/> when the file has no such table. An estimate: encoders with their own tables have no true quality.
    /// </summary>
    public int? EstimatedQuality { get; set; }

    /// <summary>Gets or sets the scale the pixels were decoded at: 1, 2, 4 or 8 for full size, 1/2, 1/4 or 1/8.</summary>
    public int ScaleDenominator { get; set; } = 1;

    /// <summary>
    /// Gets or sets the extended XMP packet (the part that did not fit in one segment), joined from its segments, or empty. The
    /// main packet is in <see cref="RasterMetadata.Xmp"/>.
    /// </summary>
    public ReadOnlyMemory<byte> ExtendedXmp { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        JpegDetails copy = new()
        {
            Progressive = Progressive,
            ColorSpace = ColorSpace,
            RestartInterval = RestartInterval,
            EstimatedQuality = EstimatedQuality,
            ScaleDenominator = ScaleDenominator,
            ExtendedXmp = ExtendedXmp.ToArray(),
        };
        foreach (JpegComponent component in Components)
        {
            copy.Components.Add(component);
        }

        return copy;
    }
}
