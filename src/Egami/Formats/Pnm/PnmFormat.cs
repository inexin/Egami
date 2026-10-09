using Egami.Metadata;

namespace Egami.Formats.Pnm;

/// <summary>The Netpbm formats PBM, PGM, PPM and PAM as one format, registered in <see cref="EgamiContext.Default"/>.</summary>
public static class PnmFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "PNM",
        MimeTypes = ["image/x-portable-anymap", "image/x-portable-bitmap", "image/x-portable-graymap", "image/x-portable-pixmap", "image/x-portable-arbitrarymap"],
        Extensions = ["pnm", "pbm", "pgm", "ppm", "pam"],
        SignatureLength = 3,
        Matches = PnmReader.Matches,
        Reader = PnmReader.Instance,
        Writer = PnmWriter.Instance,
    };
}

/// <summary>Which Netpbm variant a file is, or which one to write.</summary>
public enum PnmKind
{
    /// <summary>When writing: PGM for gray pixel types, PPM for other types without alpha, PAM for types with alpha.</summary>
    Auto,

    /// <summary>PBM (P1 or P4): one bit a pixel, 1 for black.</summary>
    Bitmap,

    /// <summary>PGM (P2 or P5): gray.</summary>
    Graymap,

    /// <summary>PPM (P3 or P6): RGB.</summary>
    Pixmap,

    /// <summary>PAM (P7): gray, RGB, or either with alpha.</summary>
    Pam,
}

/// <summary>Details only Netpbm files have, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
public sealed class PnmDetails : FormatDetails
{
    /// <summary>Gets or sets the variant.</summary>
    public PnmKind Kind { get; set; }

    /// <summary>Gets or sets whether the samples were stored as text (P1, P2 or P3).</summary>
    public bool PlainText { get; set; }

    /// <summary>Gets or sets the maxval: the sample value that means full intensity (1 for PBM).</summary>
    public int MaxValue { get; set; }

    /// <summary>Gets or sets the PAM tuple type, such as "RGB_ALPHA", or <see langword="null"/> for other variants.</summary>
    public string? TupleType { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new PnmDetails { Kind = Kind, PlainText = PlainText, MaxValue = MaxValue, TupleType = TupleType };
    }
}

/// <summary>Options for writing PBM, PGM, PPM or PAM.</summary>
public sealed class PnmWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new PnmWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the variant to write. Defaults to <see cref="PnmKind.Auto"/>. <see cref="PnmKind.Bitmap"/> thresholds pixels at 50%
    /// gray, <see cref="PnmKind.Graymap"/> converts to gray and <see cref="PnmKind.Pixmap"/> drops alpha.
    /// </summary>
    public PnmKind Kind { get; init; }

    /// <summary>
    /// Gets whether to write samples as text (P1, P2 or P3) in lines of at most 70 characters. Defaults to <see langword="false"/>.
    /// PAM has no text form, so it is always binary.
    /// </summary>
    public bool PlainText { get; init; }
}
