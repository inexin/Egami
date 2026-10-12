namespace Egami.Formats.Jpeg;

/// <summary>Options for reading JPEG, on top of the general <see cref="ReaderOptions"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="jpeg-details"/>
/// </example>
public sealed record JpegReaderOptions : ReaderOptions
{
    /// <summary>Gets the default options.</summary>
    public static new JpegReaderOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether subsampled chroma is replicated instead of smoothly interpolated. Off by default, which
    /// matches libjpeg-turbo, Skia and browsers; on is a little faster and gives blockier color edges, the way some other
    /// decoders look.
    /// </summary>
    public bool FastUpsampling { get; init; }

    /// <summary>Gets which image of the file's MPF index to load: 0, the primary, unless LoadAll asks for another.</summary>
    internal int MultiPictureIndex { get; init; }
}
