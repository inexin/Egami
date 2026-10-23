using System.Text;

namespace Egami.Video.Subtitles;

/// <summary>How <see cref="SubtitleDocument"/> reads a subtitle file.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-encoding"/>
/// </example>
public sealed record SubtitleReaderOptions
{
    /// <summary>
    /// Gets the encoding of files that have no byte order mark and are not valid UTF-8, or <see langword="null"/> to read them as
    /// Windows-1252 and report <c>Subtitle.Encoding</c>. WebVTT is always UTF-8, as its spec says, unless this is set. Code
    /// pages beyond .NET's built-in ones need <c>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)</c> first.
    /// </summary>
    public Encoding? Encoding { get; init; }

    /// <summary>
    /// Gets whether a malformed file throws instead of being repaired. False by default: SRT with missing blank lines, <c>.</c> for
    /// <c>,</c> or missing numbers, and ASS with bad lines are read, and <c>Subtitle.Repaired</c> says what was fixed. WebVTT
    /// always follows the W3C parsing algorithm, which skips what it cannot read; strict reading throws there too.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>Gets the format to read, or <see langword="null"/> to tell it from the content, then the file extension.</summary>
    public SubtitleFormat? Format { get; init; }
}

/// <summary>How <see cref="SubtitleDocument"/> writes a subtitle file. Output is always UTF-8.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-encoding"/>
/// </example>
public sealed record SubtitleWriterOptions
{
    /// <summary>
    /// Gets whether to start with a UTF-8 byte order mark, or <see langword="null"/> to keep the one the document was read with
    /// when writing its own format, and write none otherwise.
    /// </summary>
    public bool? ByteOrderMark { get; init; }

    /// <summary>
    /// Gets the line ending, or <see langword="null"/> to keep the document's own when writing its own format, and otherwise use
    /// <c>\r\n</c> for SRT and ASS and <c>\n</c> for WebVTT.
    /// </summary>
    public string? NewLine { get; init; }
}
