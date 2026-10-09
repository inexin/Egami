using Egami.Metadata;

namespace Egami.Formats.Qoi;

/// <summary>The QOI format, registered in <see cref="EgamiContext.Default"/>.</summary>
public static class QoiFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "QOI",
        MimeTypes = ["image/qoi"],
        Extensions = ["qoi"],
        SignatureLength = 4,
        Matches = QoiReader.Matches,
        Reader = QoiReader.Instance,
        Writer = QoiWriter.Instance,
    };
}

/// <summary>Details only QOI has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
public sealed class QoiDetails : FormatDetails
{
    /// <summary>Gets or sets the channel count from the header: 3 (RGB) or 4 (RGBA). Defaults to 4.</summary>
    public int Channels { get; set; } = 4;

    /// <summary>Gets or sets whether the header says every channel is linear (colorspace 1) rather than sRGB with linear alpha (0).</summary>
    public bool Linear { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new QoiDetails { Channels = Channels, Linear = Linear };
    }
}

/// <summary>Options for writing QOI.</summary>
public sealed class QoiWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new QoiWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets whether to write 4 channels (<see langword="true"/>) or 3 (<see langword="false"/>, which drops alpha). Defaults to
    /// <see langword="null"/>: 3 channels when every pixel is opaque, else 4.
    /// </summary>
    public bool? IncludeAlpha { get; init; }
}
