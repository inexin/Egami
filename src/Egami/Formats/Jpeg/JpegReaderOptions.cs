namespace Egami.Formats.Jpeg;

/// <summary>Options for reading JPEG, on top of the general <see cref="ReaderOptions"/>.</summary>
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
}
