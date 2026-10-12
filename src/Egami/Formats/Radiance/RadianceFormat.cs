using Egami.Colors;
using Egami.Metadata;

namespace Egami.Formats.Radiance;

/// <summary>The Radiance picture format (.hdr, .pic), registered in <see cref="EgamiContext.Default"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="radiance"/>
/// </example>
public static class RadianceFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "Radiance",
        MimeTypes = ["image/vnd.radiance"],
        Extensions = ["hdr", "pic", "rgbe", "xyze"],
        SignatureLength = 10,
        Matches = RadianceReader.Matches,
        Reader = RadianceReader.Instance,
        Writer = RadianceWriter.Instance,
    };
}

/// <summary>What a Radiance file's four bytes a pixel hold.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="radiance"/>
/// </example>
public enum RadiancePixelFormat
{
    /// <summary>Red, green and blue mantissas with a shared exponent (FORMAT=32-bit_rle_rgbe).</summary>
    Rgbe,

    /// <summary>CIE X, Y and Z mantissas with a shared exponent (FORMAT=32-bit_rle_xyze). Egami converts them to RGB on load.</summary>
    Xyze,
}

/// <summary>Details only Radiance pictures have, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="radiance"/>
/// </example>
public sealed class RadianceDetails : FormatDetails
{
    /// <summary>Gets or sets what the stored pixels hold.</summary>
    public RadiancePixelFormat PixelFormat { get; set; }

    /// <summary>
    /// Gets or sets the product of the EXPOSURE lines: the factor the stored values were multiplied by after rendering, so the
    /// original radiance is the pixel value divided by it. <see langword="null"/> when the header has none. Egami keeps the stored values.
    /// </summary>
    public double? Exposure { get; set; }

    /// <summary>Gets or sets the product of the COLORCORR lines, red, green and blue, or <see langword="null"/> when the header has none.</summary>
    public (double Red, double Green, double Blue)? ColorCorrection { get; set; }

    /// <summary>Gets or sets the PRIMARIES line, or <see langword="null"/> when the header has none (Rec. 709 primaries are assumed then).</summary>
    public ColorPrimaries? Primaries { get; set; }

    /// <summary>Gets or sets the SOFTWARE line, or <see langword="null"/>.</summary>
    public string? Software { get; set; }

    /// <summary>Gets or sets the VIEW line, the camera the picture was rendered with, or <see langword="null"/>.</summary>
    public string? View { get; set; }

    /// <summary>Gets or sets the product of the PIXASPECT lines (pixel height over width), or <see langword="null"/>.</summary>
    public double? PixelAspectRatio { get; set; }

    /// <summary>Gets or sets the resolution line as stored, such as "-Y 512 +X 768"; the raster is always top row first, left to right.</summary>
    public string Orientation { get; set; } = "-Y 0 +X 0";

    /// <summary>Gets the header lines between the signature and the blank line, in file order, comments included.</summary>
    public IList<string> HeaderLines { get; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        RadianceDetails copy = new()
        {
            PixelFormat = PixelFormat,
            Exposure = Exposure,
            ColorCorrection = ColorCorrection,
            Primaries = Primaries,
            Software = Software,
            View = View,
            PixelAspectRatio = PixelAspectRatio,
            Orientation = Orientation,
        };
        foreach (string line in HeaderLines)
        {
            copy.HeaderLines.Add(line);
        }

        return copy;
    }
}

/// <summary>Options for writing Radiance pictures: RGBE with per-channel run-length encoding, top row first.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="radiance"/>
/// </example>
public sealed class RadianceWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new RadianceWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the EXPOSURE line to write, or <see langword="null"/> (the default) for none. It only labels the file: the pixel values
    /// are written as they are.
    /// </summary>
    public double? Exposure { get; init; }

    /// <summary>Gets whether to run-length encode each row, as Radiance itself does. Defaults to <see langword="true"/>.</summary>
    public bool Compress { get; init; } = true;
}
