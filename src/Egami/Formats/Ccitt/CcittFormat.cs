namespace Egami.Formats.Ccitt;

/// <summary>
/// Raw CCITT fax data (ITU-T T.4 and T.6) as an image, read only. The data has no header, so detection never picks it: load it
/// with the format named and its parameters in <see cref="CcittReaderOptions"/>, as in
/// <see cref="Raster.Load(ReadOnlyMemory{byte}, FormatInfo, EgamiContext?, ReaderOptions?)"/>. It loads as
/// <see cref="Pixels.Gray8"/>, black 0 and white 255, whatever <see cref="CcittFaxParameters.BlackIs1"/> says. It is registered in
/// <see cref="EgamiContext.Default"/> so a context lists it.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ccitt.cs" region="ccitt-load"/>
/// </example>
public static class CcittFormat
{
    /// <summary>Gets the format's registration: name, MIME type and reader. It has no signature and no writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "CCITT",
        MimeTypes = ["image/g3fax"],
        Extensions = ["g3", "g4"],
        SignatureLength = 0,
        Matches = _ => false,
        Reader = CcittReader.Instance,
    };
}

/// <summary>Options for reading raw CCITT data: its parameters, which the data cannot carry itself.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ccitt.cs" region="ccitt-load"/>
/// </example>
public sealed record CcittReaderOptions : ReaderOptions
{
    /// <summary>Gets the data's parameters. Defaults to PDF's defaults: one-dimensional Group 3, 1728 columns, rows until the data ends.</summary>
    public CcittFaxParameters Parameters { get; init; } = new();
}
