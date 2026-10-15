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

    /// <summary>
    /// Gets or sets the extra channels, in the file's order: alpha and black join the pixels, spot colors are rendered into them,
    /// and every one can be read alone with <see cref="JxlImage.ReadExtraChannel(string, int, ReaderOptions?, EgamiContext?)"/>.
    /// </summary>
    public IReadOnlyList<JxlExtraChannel> ExtraChannels { get; set; } = [];

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
            ExtraChannels = ExtraChannels,
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
            ExtraChannels = [.. file.Header.ExtraChannels.Select(e => new JxlExtraChannel
            {
                Kind = (JxlExtraChannelKind)e.Type,
                Name = e.Name,
                BitsPerSample = e.BitDepth.BitsPerSample,
                ExponentBits = e.BitDepth.ExponentBits,
                Subsampling = e.DimShift,
                AlphaPremultiplied = e.AlphaAssociated,
                SpotColor = e.SpotColor,
            })],
        };
    }
}

/// <summary>What a JPEG XL extra channel holds (B.3, ExtraChannelInfo).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="extra-channels"/>
/// </example>
public enum JxlExtraChannelKind
{
    /// <summary>Alpha (opacity).</summary>
    Alpha = 0,

    /// <summary>Depth.</summary>
    Depth = 1,

    /// <summary>A spot color: ink coverage of the color in <see cref="JxlExtraChannel.SpotColor"/>.</summary>
    SpotColor = 2,

    /// <summary>A selection mask.</summary>
    SelectionMask = 3,

    /// <summary>The black (K) of a CMYK image.</summary>
    Black = 4,

    /// <summary>A color filter array (Bayer) channel.</summary>
    ColorFilterArray = 5,

    /// <summary>Thermal (infrared) intensity.</summary>
    Thermal = 6,

    /// <summary>Data an application needs to show the image correctly.</summary>
    NonOptional = 15,

    /// <summary>Data an application may ignore.</summary>
    Optional = 16,
}

/// <summary>One extra channel of a JPEG XL image, listed in <see cref="JxlDetails.ExtraChannels"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Jxl.cs" region="extra-channels"/>
/// </example>
public sealed class JxlExtraChannel
{
    /// <summary>Gets what the channel holds.</summary>
    public JxlExtraChannelKind Kind { get; init; }

    /// <summary>Gets the channel's name, or an empty string.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the bits of a sample.</summary>
    public int BitsPerSample { get; init; } = 8;

    /// <summary>Gets the exponent bits of floating-point samples, or 0 for integer samples.</summary>
    public int ExponentBits { get; init; }

    /// <summary>Gets how many times the channel is halved each way against the image (0 for full size).</summary>
    public int Subsampling { get; init; }

    /// <summary>Gets whether the color is premultiplied by this alpha channel in the file (Load gives it straight).</summary>
    public bool AlphaPremultiplied { get; init; }

    /// <summary>Gets a spot color's red, green, blue and solidity, or <see langword="null"/> for other channels.</summary>
    public IReadOnlyList<float>? SpotColor { get; init; }
}
