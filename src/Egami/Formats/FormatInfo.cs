namespace Egami.Formats;

/// <summary>Returns whether the first bytes of a file belong to a format.</summary>
/// <param name="header">
/// The first bytes of the data, up to the longest <see cref="FormatInfo.SignatureLength"/> of all registered formats.
/// It is shorter only if the data itself is shorter.
/// </param>
/// <returns><see langword="true"/> if the data looks like this format.</returns>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="format-info"/>
/// </example>
public delegate bool SignatureMatcher(ReadOnlySpan<byte> header);

/// <summary>Describes a file format: its names, how to recognize it, and its reader and writer.</summary>
/// <remarks>Instances are immutable and safe to share across threads. Register custom formats with <see cref="EgamiContext.WithFormat"/>.</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="format-info"/>
/// </example>
public sealed record FormatInfo
{
    private readonly int _signatureLength;

    /// <summary>Gets the short name, such as "PNG". Names are matched without regard to case.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the MIME types, the preferred one first.</summary>
    public IReadOnlyList<string> MimeTypes { get; init; } = [];

    /// <summary>Gets the file extensions without the dot, such as "png", the preferred one first. Used only to pick a writer when saving to a path.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>Gets how many leading bytes <see cref="Matches"/> needs; data shorter than the longest signature registered is passed to it whole.</summary>
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

    /// <summary>Gets what Save says when the format has no writer, or null for the general message.</summary>
    internal string? MissingWriter { get; init; }

    /// <summary>
    /// Gets whether the reader produces <see cref="ReaderOptions.ConvertColorTo"/>'s colour space itself, as part of its own
    /// rendering (a camera RAW develop renders straight into it), so a load passes the option to the reader rather than
    /// converting the pixels it returns. Such a reader returns rasters whose metadata names that colour space. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool ConvertsColor { get; init; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return Name;
    }
}
