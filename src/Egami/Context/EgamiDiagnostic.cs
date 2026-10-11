namespace Egami;

/// <summary>How much an <see cref="EgamiDiagnostic"/> matters. Maps one to one onto the Debug, Information and Warning log levels.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Diagnostics.cs" region="level"/>
/// </example>
public enum DiagnosticLevel
{
    /// <summary>Detail for someone chasing a problem, such as a safety limit about to throw or the band count of a parallel decode.</summary>
    Debug,

    /// <summary>A notable decision Egami made for the caller, such as the format detected or the factor picked for a scaled decode.</summary>
    Info,

    /// <summary>
    /// A problem Egami recovered from, or something lost on purpose, such as a bad checksum read leniently or alpha dropped when
    /// saving JPEG. Every Warning raised by a load is also in <see cref="Metadata.RasterMetadata.Warnings"/>.
    /// </summary>
    Warning,
}

/// <summary>
/// One event reported to <see cref="EgamiContext.Diagnostics"/>, and the type of each entry in
/// <see cref="Metadata.RasterMetadata.Warnings"/>. Ids are listed in the diagnostics article and never renamed or reused.
/// </summary>
/// <param name="Level">How much the event matters.</param>
/// <param name="Source">The part of Egami that found it, such as "Png", "Tiff", "Exif" or "Limits": the id up to its first dot.</param>
/// <param name="Id">A stable id, such as "Png.ChecksumMismatch", for filtering and counting without parsing <paramref name="Message"/>.</param>
/// <param name="Message">What happened and what Egami did, as a sentence.</param>
/// <param name="Offset">The byte offset in the input the event is about, or <see langword="null"/> when not known.</param>
/// <param name="Frame">The frame or page index the event is about, or <see langword="null"/> when not known or not frame-specific.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Diagnostics.cs" region="logger"/>
/// </example>
public readonly record struct EgamiDiagnostic(DiagnosticLevel Level, string Source, string Id, string Message, long? Offset, int? Frame)
{
    /// <summary>Returns the id and the message, such as "Png.ChecksumMismatch: The IDAT checksum is wrong…".</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"{Id}: {Message}";

    /// <summary>Builds a diagnostic whose <see cref="Source"/> is the part of <paramref name="id"/> before its first dot.</summary>
    internal static EgamiDiagnostic Create(DiagnosticLevel level, string id, string message, long? offset = null, int? frame = null)
    {
        return new EgamiDiagnostic(level, SourceOf(id), id, message, offset, frame);
    }

    /// <summary>Gets the source part of an id: everything before its first dot, or the whole id when it has none.</summary>
    internal static string SourceOf(string id)
    {
        int dot = id.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? id : Sources.Intern(id.AsSpan(0, dot));
    }

    // The built-in sources, so Source costs no allocation for them.
    private static class Sources
    {
        private static readonly string[] s_known =
        [
            "Bmp", "Gif", "Jpeg", "Png", "Pnm", "Qoi", "Tga", "Tiff", "Webp", "Exif", "Xmp", "Iptc", "Icc", "Detect", "Limits",
            "Parallel", "Streaming", "Color", "Resize", "Operation", "Decode", "Save",
        ];

        public static string Intern(ReadOnlySpan<char> source)
        {
            foreach (string known in s_known)
            {
                if (source.SequenceEqual(known))
                {
                    return known;
                }
            }

            return source.ToString();
        }
    }
}
