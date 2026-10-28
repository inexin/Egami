using Egami.Metadata;

namespace Egami.Formats.Extended.Sgi;

/// <summary>
/// SGI, Silicon Graphics' image format (.rgb, .sgi): a 512-byte header, then one to four channels of 8-bit or 16-bit samples
/// stored as separate planes of bottom-up rows, plain or run-length coded. Registered with
/// <see cref="ExtendedContextExtensions.WithSgi"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="register-one"/>
/// </example>
public static class SgiFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "SGI",
        MimeTypes = ["image/sgi", "image/x-rgb"],
        Extensions = ["sgi", "rgb", "rgba", "bw", "int", "inta"],
        SignatureLength = 4,
        Matches = SgiReader.Matches,
        Reader = new SgiReader(),
        Writer = new SgiWriter(),
    };
}

/// <summary>Details only SGI has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="details"/>
/// </example>
public sealed class SgiDetails : FormatDetails
{
    /// <summary>Gets or sets the image name from the header, up to 79 ASCII characters. The writer writes it back.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets whether the rows were run-length coded.</summary>
    public bool Rle { get; set; }

    /// <summary>Gets or sets the bytes per sample: 1 or 2.</summary>
    public int BytesPerChannel { get; set; } = 1;

    /// <summary>Gets or sets the number of channels the file stores: 1 (gray), 2 (gray and alpha), 3 (RGB) or 4 (RGBA).</summary>
    public int Channels { get; set; } = 3;

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new SgiDetails { Name = Name, Rle = Rle, BytesPerChannel = BytesPerChannel, Channels = Channels };
    }
}

/// <summary>Options for writing SGI.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="options"/>
/// </example>
public sealed class SgiWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new SgiWriterOptions Default { get; } = new();

    /// <summary>Gets whether to run-length code the rows. Defaults to <see langword="true"/>.</summary>
    public bool Rle { get; init; } = true;
}
