using Egami.Pixels;

namespace Egami.Video.Subtitles;

/// <summary>
/// A run of a cue's text with one look: bold, italic, color, font and so on, as SRT tags, WebVTT cue tags and ASS override tags
/// set them. Line breaks are <c>\n</c> inside <see cref="Text"/>. Fields a format cannot show are dropped when it is written, and
/// reported as <c>Subtitle.Dropped</c>.
/// </summary>
/// <param name="Text">The text, with line breaks as <c>\n</c>.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-cues"/>
/// </example>
public sealed record SubtitleSpan(string Text)
{
    /// <summary>Gets whether the text is bold; for ASS, the style's bold counts.</summary>
    public bool Bold { get; init; }

    /// <summary>Gets whether the text is italic; for ASS, the style's italics count.</summary>
    public bool Italic { get; init; }

    /// <summary>Gets whether the text is underlined; for ASS, the style's underline counts.</summary>
    public bool Underline { get; init; }

    /// <summary>Gets whether the text is struck out; for ASS, the style's strikeout counts.</summary>
    public bool Strikeout { get; init; }

    /// <summary>Gets the text color (SRT <c>font color</c>, ASS <c>\c</c>), or <see langword="null"/> for the style's color.</summary>
    public Rgba8? Color { get; init; }

    /// <summary>Gets the font family (SRT <c>font face</c>, ASS <c>\fn</c>), or <see langword="null"/> for the style's font.</summary>
    public string? Font { get; init; }

    /// <summary>Gets the font size (SRT <c>font size</c>, ASS <c>\fs</c> in script pixels), or <see langword="null"/>.</summary>
    public double? FontSize { get; init; }

    /// <summary>Gets the speaker (WebVTT <c>&lt;v&gt;</c>), or <see langword="null"/>.</summary>
    public string? Voice { get; init; }

    /// <summary>Gets the WebVTT class names (<c>&lt;c.name&gt;</c> and the classes on other tags), in order.</summary>
    public IReadOnlyList<string> Classes { get; init; } = [];

    /// <summary>Gets the language (WebVTT <c>&lt;lang&gt;</c>) as a BCP 47 tag, or <see langword="null"/>.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the ruby text shown over this span's text (WebVTT <c>&lt;ruby&gt;</c> and <c>&lt;rt&gt;</c>), or <see langword="null"/>.</summary>
    public string? Ruby { get; init; }

    /// <summary>
    /// Gets when this span starts being spoken, for karaoke (a WebVTT timestamp tag, an ASS <c>\k</c> run), or
    /// <see langword="null"/>. The time is on the same clock as the cue's start and end.
    /// </summary>
    public MediaTime? Timestamp { get; init; }

    /// <summary>
    /// Gets the ASS override tags before this span that no other field holds, as written (for example <c>\blur2\fad(100,100)</c>),
    /// or <see langword="null"/>. ASS output writes them back; SRT and WebVTT drop them.
    /// </summary>
    public string? OtherTags { get; init; }

    /// <summary>
    /// Gets whether <see cref="Text"/> holds ASS drawing commands (after <c>\p1</c>) rather than text. SRT and WebVTT drop such
    /// spans.
    /// </summary>
    public bool IsDrawing { get; init; }

    /// <summary>Gets whether the span has the same text and look as <paramref name="other"/>, comparing <see cref="Classes"/> by content.</summary>
    /// <param name="other">The other span.</param>
    /// <returns>Whether they are equal.</returns>
    public bool Equals(SubtitleSpan? other)
    {
        return other is not null && Text == other.Text && SameLook(other) && Ruby == other.Ruby && Nullable.Equals(Timestamp, other.Timestamp)
            && OtherTags == other.OtherTags && IsDrawing == other.IsDrawing;
    }

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Text, Bold, Italic, Underline, Color, Font, Voice, Classes.Count);

    /// <summary>Gets whether the look (everything but text, ruby, timestamp and other tags) is the same, so two spans can merge.</summary>
    internal bool SameLook(SubtitleSpan other)
    {
        return Bold == other.Bold && Italic == other.Italic && Underline == other.Underline && Strikeout == other.Strikeout
            && Nullable.Equals(Color, other.Color) && Font == other.Font && Nullable.Equals(FontSize, other.FontSize) && Voice == other.Voice
            && Language == other.Language && Classes.SequenceEqual(other.Classes);
    }

    /// <summary>Gets whether the span has no formatting at all.</summary>
    internal bool IsPlain => !Bold && !Italic && !Underline && !Strikeout && Color is null && Font is null && FontSize is null && Voice is null
        && Language is null && Classes.Count == 0 && Ruby is null && Timestamp is null && OtherTags is null && !IsDrawing;
}
