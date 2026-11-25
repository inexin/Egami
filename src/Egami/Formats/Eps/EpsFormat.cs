using Egami.Metadata;

namespace Egami.Formats.Eps;

/// <summary>
/// Encapsulated PostScript, registered in <see cref="EgamiContext.Default"/> and read only. Egami never runs PostScript: Load
/// returns the preview image the file carries, in this order: a TIFF preview, a Windows metafile preview (drawn by a format the
/// context has registered for metafiles, such as Egami.Metafiles' <c>MetafileFormat.Info</c>, at the bounding box size), or an
/// EPSI bitmap. A file with only PostScript probes at its bounding box size and fails to load with an
/// <see cref="UnsupportedFormatException"/>. Detected by the DOS binary header or a <c>%!PS</c> first line; plain PostScript is
/// detected too, so it fails with that message rather than as an unknown format.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Eps.cs" region="eps-load"/>
/// </example>
public static class EpsFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature and reader. It has no writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "EPS",
        MimeTypes = ["application/postscript", "application/eps", "image/x-eps"],
        Extensions = ["eps", "epsf", "epsi", "ept"],
        SignatureLength = EpsFile.SignatureLength,
        Matches = EpsFile.Matches,
        Reader = EpsReader.Instance,
        MissingWriter = "Egami reads EPS previews but does not write EPS.",
    };
}

/// <summary>Which of an EPS file's images Load returns.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Eps.cs" region="eps-file"/>
/// </example>
public enum EpsPreviewKind
{
    /// <summary>The file has no preview: only PostScript, which Egami does not run.</summary>
    None,

    /// <summary>The TIFF preview the DOS binary header points at.</summary>
    Tiff,

    /// <summary>The Windows metafile preview the DOS binary header points at.</summary>
    Wmf,

    /// <summary>The EPSI bitmap between <c>%%BeginPreview</c> and <c>%%EndPreview</c>, 1 to 8-bit gray.</summary>
    Epsi,

    /// <summary>
    /// The image Photoshop saved with the file, which its <c>%ImageData</c> comment describes: the picture itself at full size, not a
    /// preview, in gray, RGB, CMYK or bitmap.
    /// </summary>
    PhotoshopImage,
}

/// <summary>An EPS bounding box in PostScript points (1/72 inch), lower-left and upper-right corners in PostScript's y-up space.</summary>
/// <param name="Left">The lower-left x.</param>
/// <param name="Bottom">The lower-left y.</param>
/// <param name="Right">The upper-right x.</param>
/// <param name="Top">The upper-right y.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Eps.cs" region="eps-file"/>
/// </example>
public readonly record struct EpsBoundingBox(double Left, double Bottom, double Right, double Top)
{
    /// <summary>Gets the width in points.</summary>
    public double Width => Right - Left;

    /// <summary>Gets the height in points.</summary>
    public double Height => Top - Bottom;
}

/// <summary>Details only EPS has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Eps.cs" region="eps-load"/>
/// </example>
public sealed class EpsDetails : FormatDetails
{
    /// <summary>Gets or sets which of the file's images was loaded.</summary>
    public EpsPreviewKind Preview { get; set; }

    /// <summary>Gets or sets the bounding box in points, as <see cref="EpsFile.BoundingBox"/>.</summary>
    public EpsBoundingBox? BoundingBox { get; set; }

    /// <summary>Gets or sets the <c>%%Creator</c> comment.</summary>
    public string? Creator { get; set; }

    /// <summary>Gets or sets the <c>%%Title</c> comment.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the <c>%%CreationDate</c> comment as written.</summary>
    public string? CreationDate { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new EpsDetails { Preview = Preview, BoundingBox = BoundingBox, Creator = Creator, Title = Title, CreationDate = CreationDate };
    }
}
