namespace Egami.Video.Subtitles;

/// <summary>
/// One subtitle: when it shows, its text as <see cref="SubtitleSpan"/> runs, and where and how it is placed. Read from SRT,
/// WebVTT or ASS into the same fields, so a cue converts between them; a cue that is not changed is written back to its own
/// format exactly as it was read.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-cues"/>
/// </example>
public sealed class SubtitleCue
{
    /// <summary>Creates a cue with plain text.</summary>
    /// <param name="start">When the cue appears.</param>
    /// <param name="end">When it disappears.</param>
    /// <param name="text">The text, with line breaks as <c>\n</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public SubtitleCue(MediaTime start, MediaTime end, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Start = start;
        End = end;
        if (text.Length > 0)
        {
            Spans.Add(new SubtitleSpan(text));
        }
    }

    /// <summary>Creates a cue with formatted text.</summary>
    /// <param name="start">When the cue appears.</param>
    /// <param name="end">When it disappears.</param>
    /// <param name="spans">The text runs.</param>
    /// <exception cref="ArgumentNullException"><paramref name="spans"/> is <see langword="null"/>.</exception>
    public SubtitleCue(MediaTime start, MediaTime end, IEnumerable<SubtitleSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        Start = start;
        End = end;
        Spans.AddRange(spans);
    }

    /// <summary>Gets or sets when the cue appears.</summary>
    public MediaTime Start { get; set; }

    /// <summary>Gets or sets when the cue disappears.</summary>
    public MediaTime End { get; set; }

    /// <summary>Gets the time the cue shows, <see cref="End"/> minus <see cref="Start"/>.</summary>
    public MediaTime Duration => End - Start;

    /// <summary>Gets or sets the cue's id: the SRT number or the WebVTT identifier, or <see langword="null"/>.</summary>
    public string? Id { get; set; }

    /// <summary>Gets the text runs. Edit the list to change the text.</summary>
    public List<SubtitleSpan> Spans { get; } = [];

    /// <summary>Gets the text without formatting, with line breaks as <c>\n</c>.</summary>
    public string PlainText => Spans.Count == 1 ? Spans[0].Text : string.Concat(Spans.Select(s => s.Text));

    /// <summary>Gets or sets the ASS style name, or <see langword="null"/> for the default style.</summary>
    public string? Style { get; set; }

    /// <summary>Gets or sets who speaks (the ASS event's Name field), or <see langword="null"/>.</summary>
    public string? Actor { get; set; }

    /// <summary>Gets or sets the ASS layer: higher layers are drawn over lower ones.</summary>
    public int Layer { get; set; }

    /// <summary>Gets or sets whether the cue is an ASS <c>Comment</c> event, which players do not show.</summary>
    public bool IsComment { get; set; }

    /// <summary>
    /// Gets or sets where the cue sits (ASS <c>\an</c>, SRT <c>{\an8}</c>), or <see langword="null"/> for the style's place,
    /// usually bottom center.
    /// </summary>
    public SubtitleAlignment? Alignment { get; set; }

    /// <summary>Gets or sets the exact place (ASS <c>\pos</c>), in script pixels, or <see langword="null"/>.</summary>
    public SubtitlePosition? Position { get; set; }

    /// <summary>
    /// Gets or sets the WebVTT cue settings, or <see langword="null"/>. WebVTT output uses them when set and derives settings from
    /// <see cref="Alignment"/> otherwise.
    /// </summary>
    public WebVttCueSettings? WebVtt { get; set; }

    /// <summary>Gets or sets the ASS left margin override in script pixels, 0 for the style's.</summary>
    public int MarginLeft { get; set; }

    /// <summary>Gets or sets the ASS right margin override in script pixels, 0 for the style's.</summary>
    public int MarginRight { get; set; }

    /// <summary>Gets or sets the ASS vertical margin override in script pixels, 0 for the style's.</summary>
    public int MarginVertical { get; set; }

    /// <summary>Gets or sets the ASS transition effect (<c>Scroll up;...</c>, <c>Banner;...</c>), or <see langword="null"/>.</summary>
    public string? Effect { get; set; }

    /// <summary>
    /// Gets the cue's text as it was read, with the format's own tags (SRT <c>&lt;i&gt;</c>, WebVTT <c>&lt;c.loud&gt;</c>, ASS
    /// <c>{\i1}</c>), or <see langword="null"/> for a cue made in code.
    /// </summary>
    public string? RawText => Source?.RawText;

    /// <summary>Gets where the cue was read from, for an exact round trip; <see langword="null"/> for a cue made in code.</summary>
    internal CueSource? Source { get; set; }

    /// <inheritdoc/>
    public override string ToString() => $"{Start.ToTimeSpan():c} --> {End.ToTimeSpan():c} {PlainText}";

    /// <summary>Gets the fields a writer looks at, to tell whether the cue changed since it was read.</summary>
    internal CueState Capture() => new(Start, End, Id, Style, Actor, Layer, IsComment, Alignment, Position, WebVtt, MarginLeft, MarginRight, MarginVertical, Effect);

    /// <summary>Gets whether the cue is as read from <paramref name="format"/>: only then is its raw block written back.</summary>
    internal bool IsUnchanged(SubtitleFormat format)
    {
        return Source is { } source && source.Format == format && source.State.Equals(Capture()) && TextUnchanged(format);
    }

    /// <summary>Gets whether the text runs are as read from <paramref name="format"/>, so <see cref="RawText"/> still says them.</summary>
    internal bool TextUnchanged(SubtitleFormat format)
    {
        return Source is { } source && source.Format == format && source.Spans.AsSpan().SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Spans));
    }

    /// <summary>Remembers the cue as read, after the reader filled in every field.</summary>
    internal void MarkRead(SubtitleFormat format, string rawText, string rawBlock, string leading, int index)
    {
        Source = new CueSource(format, rawText, rawBlock, leading, index, Capture(), [.. Spans]);
    }

    /// <summary>Copies the cue, with its source, so an unchanged copy still writes back exactly.</summary>
    internal SubtitleCue Clone()
    {
        return new SubtitleCue(Start, End, Spans)
        {
            Id = Id,
            Style = Style,
            Actor = Actor,
            Layer = Layer,
            IsComment = IsComment,
            Alignment = Alignment,
            Position = Position,
            WebVtt = WebVtt,
            MarginLeft = MarginLeft,
            MarginRight = MarginRight,
            MarginVertical = MarginVertical,
            Effect = Effect,
            Source = Source,
        };
    }
}

/// <summary>The fields of a cue other than its text, to compare against the cue as read.</summary>
internal readonly record struct CueState(MediaTime Start, MediaTime End, string? Id, string? Style, string? Actor, int Layer, bool IsComment,
    SubtitleAlignment? Alignment, SubtitlePosition? Position, WebVttCueSettings? WebVtt, int MarginLeft, int MarginRight, int MarginVertical, string? Effect)
{
    public bool Equals(CueState other)
    {
        // MediaTime compares instants, so a time rescaled to another base is still unchanged: the raw timing line still says it.
        return Start == other.Start && End == other.End && Id == other.Id && Style == other.Style && Actor == other.Actor && Layer == other.Layer && IsComment == other.IsComment
            && Alignment == other.Alignment && Position == other.Position && Equals(WebVtt, other.WebVtt) && MarginLeft == other.MarginLeft
            && MarginRight == other.MarginRight && MarginVertical == other.MarginVertical && Effect == other.Effect;
    }

    public override int GetHashCode() => HashCode.Combine(Start, End, Id, Style);
}

/// <summary>
/// A cue as read: its format, its text and whole block as in the file, the text between the previous block and this one
/// (blank lines, WebVTT NOTE blocks, ASS comment lines), its place in the document, and its fields at the time.
/// </summary>
internal sealed record CueSource(SubtitleFormat Format, string RawText, string RawBlock, string Leading, int Index, CueState State, SubtitleSpan[] Spans);
