using Egami.Metadata;

namespace Egami.Formats.Jxl;

/// <summary>The JPEG XL format (ISO/IEC 18181), registered in <see cref="EgamiContext.Default"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="probe"/>
/// </example>
public static class JxlFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature and reader.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "JPEG XL",
        MimeTypes = ["image/jxl"],
        Extensions = ["jxl"],
        SignatureLength = 12,
        Matches = JxlContainer.Matches,
        Reader = JxlReader.Instance,
    };
}

/// <summary>Details only JPEG XL has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="probe"/>
/// </example>
public sealed class JxlDetails : FormatDetails
{
    /// <summary>Gets or sets whether the file uses the box format of ISO/IEC 18181-2, rather than being a bare codestream.</summary>
    public bool IsContainer { get; set; }

    /// <summary>Gets or sets the conformance level the jxll box declares (5 or 10), or <see langword="null"/> when there is none (level 5 is meant then).</summary>
    public int? Level { get; set; }

    /// <summary>Gets or sets the bits a sample of the color channels has, as the file declares.</summary>
    public int BitsPerSample { get; set; } = 8;

    /// <summary>Gets or sets the exponent bits of floating-point samples, or 0 for integer samples.</summary>
    public int ExponentBits { get; set; }

    /// <summary>Gets or sets whether the color channels are coded in XYB, which lossy files use; a lossless file keeps its own color space.</summary>
    public bool IsXybEncoded { get; set; }

    /// <summary>Gets or sets whether the file has a jbrd box, so the JPEG it was recompressed from can be given back byte for byte.</summary>
    public bool HasJpegReconstruction { get; set; }

    /// <summary>Gets or sets the contents of each jumb (JUMBF) box, such as C2PA manifests, kept as stored.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Jumbf { get; set; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new JxlDetails
        {
            IsContainer = IsContainer,
            Level = Level,
            BitsPerSample = BitsPerSample,
            ExponentBits = ExponentBits,
            IsXybEncoded = IsXybEncoded,
            HasJpegReconstruction = HasJpegReconstruction,
            Jumbf = Jumbf,
        };
    }

    /// <summary>Gets the details of a file whose headers have been read.</summary>
    internal static JxlDetails From(JxlFile file)
    {
        return new JxlDetails
        {
            IsContainer = file.Container.IsContainer,
            Level = file.Container.Level,
            BitsPerSample = file.Header.BitDepth.BitsPerSample,
            ExponentBits = file.Header.BitDepth.ExponentBits,
            IsXybEncoded = file.Header.XybEncoded,
            HasJpegReconstruction = file.Container.JpegReconstruction is not null,
            Jumbf = [.. file.Container.Jumbf.Select(b => (ReadOnlyMemory<byte>)b)],
        };
    }
}
