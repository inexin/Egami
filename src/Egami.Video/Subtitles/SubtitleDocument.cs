using System.Text;

namespace Egami.Video.Subtitles;

/// <summary>
/// A subtitle file in memory: its cues, and the styles, regions and header its format carries. Loads SRT, WebVTT, ASS and SSA
/// into one model, saves any of SRT, WebVTT and ASS, and shifts, scales, syncs and clips the timing. A document saved in the
/// format it was read from, unchanged, gives back the same bytes; changed cues are written in the format's canonical form, and
/// what a target format cannot hold is reported once per kind as <c>Subtitle.Dropped</c>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-load"/>
/// </example>
public sealed partial class SubtitleDocument
{
    /// <summary>Creates an empty document.</summary>
    /// <param name="format">The format <see cref="Save(string, SubtitleFormat?, SubtitleWriterOptions?, MediaContext?)"/> writes by default.</param>
    public SubtitleDocument(SubtitleFormat format = SubtitleFormat.Srt)
    {
        Format = format;
    }

    /// <summary>Gets or sets the format the document was read from, which saving writes unless told otherwise.</summary>
    public SubtitleFormat Format { get; set; }

    /// <summary>Gets the cues, in file order. <see cref="Sort"/> puts them in time order.</summary>
    public List<SubtitleCue> Cues { get; } = [];

    /// <summary>Gets the ASS and SSA styles cues name in <see cref="SubtitleCue.Style"/>.</summary>
    public List<SubtitleStyle> Styles { get; } = [];

    /// <summary>
    /// Gets the ASS <c>[Script Info]</c> fields in order, such as <c>Title</c>, <c>PlayResX</c> and <c>WrapStyle</c>, without
    /// <c>ScriptType</c>, which the writer sets.
    /// </summary>
    public List<KeyValuePair<string, string>> ScriptInfo { get; } = [];

    /// <summary>Gets the files an ASS script embeds (fonts and pictures), decoded.</summary>
    public List<SubtitleAttachment> Attachments { get; } = [];

    /// <summary>Gets the WebVTT regions cues can show in.</summary>
    public List<SubtitleRegion> Regions { get; } = [];

    /// <summary>Gets the WebVTT <c>STYLE</c> blocks: CSS for the <c>::cue</c> pseudo-element, as written.</summary>
    public List<string> StyleSheets { get; } = [];

    /// <summary>
    /// Gets or sets the WebVTT header: the text after <c>WEBVTT</c> on the first line and the header lines below it, with line
    /// breaks as <c>\n</c>, or <see langword="null"/> for none.
    /// </summary>
    public string? HeaderText { get; set; }

    /// <summary>Gets the script width ASS positions are in: <c>PlayResX</c>, or what libass assumes when it is missing.</summary>
    internal (double Width, double Height) PlayRes
    {
        get
        {
            double x = InfoNumber("PlayResX"), y = InfoNumber("PlayResY");
            return (x, y) switch
            {
                ( > 0, > 0) => (x, y),
                ( > 0, _) => (x, x == 1280 ? 1024 : x * 3 / 4),
                (_, > 0) => (y == 1024 ? 1280 : y * 4 / 3, y),
                _ => (384, 288),
            };
        }
    }

    /// <summary>The header and trailing text as read, for an exact round trip.</summary>
    internal DocumentSource? Source { get; set; }

    /// <summary>Loads a subtitle file. The format comes from the content, then the extension.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The encoding, strictness and format, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">Where diagnostics go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The document.</returns>
    /// <exception cref="UnsupportedFormatException">The file is not SRT, WebVTT, ASS or SSA.</exception>
    /// <exception cref="CorruptDataException">The file is broken past repair, or is malformed and <see cref="SubtitleReaderOptions.Strict"/> is set.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-load"/>
    /// </example>
    public static SubtitleDocument Load(string path, SubtitleReaderOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Read(File.ReadAllBytes(path), Path.GetExtension(path), options, context);
    }

    /// <summary>Loads a subtitle file from a stream, read to its end. The format comes from the content.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">The encoding, strictness and format, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">Where diagnostics go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The document.</returns>
    /// <exception cref="UnsupportedFormatException">The data is not SRT, WebVTT, ASS or SSA.</exception>
    /// <exception cref="CorruptDataException">The data is broken past repair, or is malformed and <see cref="SubtitleReaderOptions.Strict"/> is set.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-encoding"/>
    /// </example>
    public static SubtitleDocument Load(Stream stream, SubtitleReaderOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), null, options, context);
    }

    /// <summary>Reads a subtitle file's bytes. The format comes from the content.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="options">The encoding, strictness and format, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">Where diagnostics go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The document.</returns>
    /// <exception cref="UnsupportedFormatException">The data is not SRT, WebVTT, ASS or SSA.</exception>
    /// <exception cref="CorruptDataException">The data is broken past repair, or is malformed and <see cref="SubtitleReaderOptions.Strict"/> is set.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-encoding"/>
    /// </example>
    public static SubtitleDocument Load(ReadOnlySpan<byte> data, SubtitleReaderOptions? options = null, MediaContext? context = null)
    {
        return Read(data, null, options, context);
    }

    /// <summary>Reads subtitles from text already decoded.</summary>
    /// <param name="text">The text.</param>
    /// <param name="format">The format, or <see langword="null"/> to tell it from the text.</param>
    /// <param name="options">The strictness, or <see langword="null"/> for the defaults. The encoding is not used.</param>
    /// <param name="context">Where diagnostics go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The document.</returns>
    /// <exception cref="UnsupportedFormatException">The text is not SRT, WebVTT, ASS or SSA.</exception>
    /// <exception cref="CorruptDataException">The text is broken past repair, or is malformed and <see cref="SubtitleReaderOptions.Strict"/> is set.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-ass"/>
    /// </example>
    public static SubtitleDocument Parse(string text, SubtitleFormat? format = null, SubtitleReaderOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool bom = text.StartsWith('\uFEFF');
        if (bom)
        {
            text = text[1..];
        }

        SubtitleFormat resolved = format ?? options?.Format ?? Detect(text, null) ?? throw new UnsupportedFormatException("The text is not SRT, WebVTT, ASS or SSA.");
        using SubtitleLog log = new(context, Name(resolved), options?.Strict ?? false);
        return Parse(text, resolved, bom, log);
    }

    /// <summary>Tells a subtitle format from the start of a file, or returns <see langword="null"/>.</summary>
    /// <param name="data">The start of the file; a few hundred bytes are enough.</param>
    /// <returns>The format, or <see langword="null"/> when the data is not one of them.</returns>
    public static SubtitleFormat? DetectFormat(ReadOnlySpan<byte> data)
    {
        using SubtitleLog log = new(null, "subtitle");
        string text = SubtitleText.Decode(data[..Math.Min(data.Length, 4096)], null, log, out _);
        return Detect(text, null);
    }

    /// <summary>Saves the document. The format comes from <paramref name="format"/>, then the file extension, then <see cref="Format"/>.</summary>
    /// <param name="path">The file.</param>
    /// <param name="format">The format to write, or <see langword="null"/>.</param>
    /// <param name="options">The byte order mark and line ending, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">Where <c>Subtitle.Dropped</c> diagnostics go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="NotSupportedException">The format is <see cref="SubtitleFormat.Ssa"/>, which is read only.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-load"/>
    /// </example>
    public void Save(string path, SubtitleFormat? format = null, SubtitleWriterOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        SubtitleFormat target = format ?? FromExtension(Path.GetExtension(path)) ?? Format;
        File.WriteAllBytes(path, ToBytes(target, options, context));
    }

    /// <summary>Writes the document to a stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="format">The format to write, or <see langword="null"/> for <see cref="Format"/>.</param>
    /// <param name="options">The byte order mark and line ending, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">Where <c>Subtitle.Dropped</c> diagnostics go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="NotSupportedException">The format is <see cref="SubtitleFormat.Ssa"/>, which is read only.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-load"/>
    /// </example>
    public void Save(Stream stream, SubtitleFormat? format = null, SubtitleWriterOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(ToBytes(format ?? Format, options, context));
    }

    /// <summary>Returns the document as text in a format, without a byte order mark.</summary>
    /// <param name="format">The format to write, or <see langword="null"/> for <see cref="Format"/>.</param>
    /// <param name="options">The line ending, or <see langword="null"/> for the default.</param>
    /// <param name="context">Where <c>Subtitle.Dropped</c> diagnostics go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The text.</returns>
    /// <exception cref="NotSupportedException">The format is <see cref="SubtitleFormat.Ssa"/>, which is read only.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-cues"/>
    /// </example>
    public string ToText(SubtitleFormat? format = null, SubtitleWriterOptions? options = null, MediaContext? context = null)
    {
        SubtitleFormat target = Writable(format ?? Format);
        using SubtitleLog log = new(context, Name(target));
        return Write(target, options, log);
    }

    /// <summary>Returns a copy of the document whose cues and lists can change without touching this one.</summary>
    /// <returns>The copy.</returns>
    public SubtitleDocument Clone()
    {
        SubtitleDocument copy = new(Format) { HeaderText = HeaderText, Source = Source };
        copy.Cues.AddRange(Cues.Select(c => c.Clone()));
        copy.Styles.AddRange(Styles);
        copy.ScriptInfo.AddRange(ScriptInfo);
        copy.Attachments.AddRange(Attachments);
        copy.Regions.AddRange(Regions);
        copy.StyleSheets.AddRange(StyleSheets);
        return copy;
    }

    internal byte[] ToBytes(SubtitleFormat format, SubtitleWriterOptions? options, MediaContext? context)
    {
        SubtitleFormat target = Writable(format);
        using SubtitleLog log = new(context, Name(target));
        string text = Write(target, options, log);
        bool bom = options?.ByteOrderMark ?? (Source is { } source && source.Format == target && source.ByteOrderMark);
        byte[] bytes = new byte[(bom ? 3 : 0) + Encoding.UTF8.GetByteCount(text)];
        if (bom)
        {
            bytes[0] = 0xEF;
            bytes[1] = 0xBB;
            bytes[2] = 0xBF;
        }

        Encoding.UTF8.GetBytes(text, bytes.AsSpan(bom ? 3 : 0));
        return bytes;
    }

    internal static SubtitleDocument Read(ReadOnlySpan<byte> data, string? extension, SubtitleReaderOptions? options, MediaContext? context)
    {
        using SubtitleLog log = new(context, "subtitle", options?.Strict ?? false);
        string text = SubtitleText.Decode(data, options?.Encoding, log, out bool bom);
        SubtitleFormat format = options?.Format ?? Detect(text, extension)
            ?? throw new UnsupportedFormatException("The data is not SRT, WebVTT, ASS or SSA.");
        log.FormatName = Name(format);
        return Parse(text, format, bom, log);
    }

    internal static SubtitleDocument Parse(string text, SubtitleFormat format, bool bom, SubtitleLog log)
    {
        SubtitleDocument document = format switch
        {
            SubtitleFormat.Srt => SrtFormat.Read(text, log),
            SubtitleFormat.WebVtt => WebVttFormat.Read(text, log),
            _ => AssFormat.Read(text, format, log),
        };

        document.Source = document.Source! with { ByteOrderMark = bom, NewLine = SubtitleText.NewLineOf(text) };
        return document;
    }

    internal string Write(SubtitleFormat format, SubtitleWriterOptions? options, SubtitleLog log)
    {
        string newLine = options?.NewLine ?? (Source is { Format: var f, NewLine: { } own } && f == format ? own : format == SubtitleFormat.WebVtt ? "\n" : "\r\n");
        return format switch
        {
            SubtitleFormat.Srt => SrtFormat.Write(this, newLine, log),
            SubtitleFormat.WebVtt => WebVttFormat.Write(this, newLine, log),
            _ => AssFormat.Write(this, newLine, log),
        };
    }

    private static SubtitleFormat Writable(SubtitleFormat format)
    {
        return format == SubtitleFormat.Ssa
            ? throw new NotSupportedException("SSA is read only; save the document as SubtitleFormat.Ass.")
            : format;
    }

    internal static string Name(SubtitleFormat format) => format switch
    {
        SubtitleFormat.Srt => "SRT",
        SubtitleFormat.WebVtt => "WebVTT",
        SubtitleFormat.Ass => "ASS",
        _ => "SSA",
    };

    internal static SubtitleFormat? FromExtension(string? extension) => extension?.TrimStart('.').ToLowerInvariant() switch
    {
        "srt" => SubtitleFormat.Srt,
        "vtt" or "webvtt" => SubtitleFormat.WebVtt,
        "ass" => SubtitleFormat.Ass,
        "ssa" => SubtitleFormat.Ssa,
        _ => null,
    };

    private static SubtitleFormat? Detect(string text, string? extension)
    {
        ReadOnlySpan<char> s = text.AsSpan().TrimStart('\uFEFF');
        if (s.StartsWith("WEBVTT", StringComparison.Ordinal) && (s.Length == 6 || s[6] is ' ' or '\t' or '\r' or '\n'))
        {
            return SubtitleFormat.WebVtt;
        }

        ReadOnlySpan<char> start = s.TrimStart();
        if (start.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase))
        {
            return AssFormat.IsSsa(text) ? SubtitleFormat.Ssa : SubtitleFormat.Ass;
        }

        if (SrtFormat.LooksLikeSrt(text))
        {
            return SubtitleFormat.Srt;
        }

        SubtitleFormat? fromExtension = FromExtension(extension);
        return fromExtension == SubtitleFormat.WebVtt ? null : fromExtension;
    }

    /// <summary>Gets whether the header fields are as read from <paramref name="format"/>, so the raw header can be written back.</summary>
    internal bool HeaderUnchanged(SubtitleFormat format)
    {
        return Source is { } source && source.Format == format && source.HeaderText == HeaderText && source.Styles.SequenceEqual(Styles)
            && source.ScriptInfo.SequenceEqual(ScriptInfo) && source.Regions.SequenceEqual(Regions) && source.StyleSheets.SequenceEqual(StyleSheets)
            && source.Attachments.Length == Attachments.Count
            && source.Attachments.Zip(Attachments).All(p => p.First.Name == p.Second.Name && p.First.IsFont == p.Second.IsFont && p.First.Data.Span.SequenceEqual(p.Second.Data.Span));
    }

    /// <summary>Remembers the header as read, after the reader filled every field.</summary>
    internal void MarkRead(SubtitleFormat format, string header, string trailing, string[]? extraSections = null)
    {
        Source = new DocumentSource(format, header, trailing, HeaderText, [.. Styles], [.. ScriptInfo], [.. Regions], [.. StyleSheets], [.. Attachments], extraSections ?? []);
    }

    private double InfoNumber(string key)
    {
        foreach (KeyValuePair<string, string> pair in ScriptInfo)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                && double.TryParse(pair.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }
        }

        return 0;
    }
}

/// <summary>
/// A document as read: its format, the raw text before the first cue and after the last, its header fields at the time, the
/// ASS sections it does not model (kept raw), its byte order mark and its line ending.
/// </summary>
internal sealed record DocumentSource(SubtitleFormat Format, string Header, string Trailing, string? HeaderText, SubtitleStyle[] Styles,
    KeyValuePair<string, string>[] ScriptInfo, SubtitleRegion[] Regions, string[] StyleSheets, SubtitleAttachment[] Attachments, string[] ExtraSections)
{
    public bool ByteOrderMark { get; init; }

    public string? NewLine { get; init; }

    /// <summary>Gets the ASS <c>[Events]</c> Format fields, in the file's order, which events written under the raw header follow.</summary>
    public string[]? EventFormat { get; init; }
}
