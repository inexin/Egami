namespace Egami.Formats;

/// <summary>Returns whether the first bytes of a file belong to a format.</summary>
/// <param name="header">
/// The first bytes of the data, up to the longest <see cref="FormatInfo.SignatureLength"/> of all registered formats.
/// It is shorter only if the data itself is shorter.
/// </param>
/// <returns><see langword="true"/> if the data looks like this format.</returns>
public delegate bool SignatureMatcher(ReadOnlySpan<byte> header);

/// <summary>Describes a file format: its names, how to recognize it, and its reader and writer.</summary>
/// <remarks>Instances are immutable and safe to share across threads. Register custom formats with <see cref="EgamiContext.WithFormat"/>.</remarks>
public sealed record FormatInfo
{
    private readonly int _signatureLength;

    /// <summary>Gets the short name, such as "PNG". Names are matched without regard to case.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the MIME types, the preferred one first.</summary>
    public IReadOnlyList<string> MimeTypes { get; init; } = [];

    /// <summary>Gets the file extensions without the dot, such as "png", the preferred one first. Used only to pick a writer when saving to a path.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>Gets how many leading bytes <see cref="Matches"/> needs.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 65,536.</exception>
    public required int SignatureLength
    {
        get => _signatureLength;
        init => _signatureLength = value is >= 0 and <= 64 * 1024 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A signature is 0 to 65,536 bytes.");
    }

    /// <summary>Gets the function that recognizes the format from its first bytes. File extensions are never used for detection.</summary>
    public required SignatureMatcher Matches { get; init; }

    /// <summary>Gets the reader, or <see langword="null"/> if the format can only be written.</summary>
    public IFormatReader? Reader { get; init; }

    /// <summary>Gets the writer, or <see langword="null"/> if the format can only be read.</summary>
    public IFormatWriter? Writer { get; init; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return Name;
    }
}
