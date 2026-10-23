namespace Egami.Video.Subtitles;

/// <summary>A text subtitle format <see cref="SubtitleDocument"/> reads and writes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-load"/>
/// </example>
public enum SubtitleFormat
{
    /// <summary>SubRip (.srt): numbered cues with HTML-style <c>b</c>, <c>i</c>, <c>u</c>, <c>s</c> and <c>font</c> tags.</summary>
    Srt,

    /// <summary>WebVTT (.vtt), the W3C format browsers show: cue settings, regions, STYLE and NOTE blocks, and cue tags.</summary>
    WebVtt,

    /// <summary>Advanced SubStation Alpha (.ass, script type v4.00+): styles, layers, positions and override tags.</summary>
    Ass,

    /// <summary>SubStation Alpha (.ssa, script type v4.00), ASS's predecessor. Read only; written as <see cref="Ass"/>.</summary>
    Ssa,
}

/// <summary>
/// Where a cue sits on the screen, as the keys of a numeric keypad lay out (ASS <c>\an</c>): 1 is bottom left, 2 bottom
/// center (the usual place), 9 top right.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-cues"/>
/// </example>
public enum SubtitleAlignment
{
    /// <summary>Bottom left.</summary>
    BottomLeft = 1,

    /// <summary>Bottom center, where subtitles go unless told otherwise.</summary>
    BottomCenter = 2,

    /// <summary>Bottom right.</summary>
    BottomRight = 3,

    /// <summary>Middle left.</summary>
    MiddleLeft = 4,

    /// <summary>The middle of the screen.</summary>
    Middle = 5,

    /// <summary>Middle right.</summary>
    MiddleRight = 6,

    /// <summary>Top left.</summary>
    TopLeft = 7,

    /// <summary>Top center.</summary>
    TopCenter = 8,

    /// <summary>Top right.</summary>
    TopRight = 9,
}

/// <summary>
/// An exact place for a cue (ASS <c>\pos</c>), in the script's pixels (<c>PlayResX</c> by <c>PlayResY</c>, 384 by 288 when the
/// script does not say). The cue's <see cref="SubtitleCue.Alignment"/> says which point of the text sits there.
/// </summary>
/// <param name="X">The distance from the left edge.</param>
/// <param name="Y">The distance from the top edge.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-cues"/>
/// </example>
public readonly record struct SubtitlePosition(double X, double Y);

/// <summary>An embedded file of an ASS script (its <c>[Fonts]</c> or <c>[Graphics]</c> section), decoded.</summary>
/// <param name="Name">The file name.</param>
/// <param name="Data">The file's bytes.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-ass"/>
/// </example>
public sealed record SubtitleAttachment(string Name, ReadOnlyMemory<byte> Data)
{
    /// <summary>Gets whether the file is a font (<c>[Fonts]</c>) rather than a picture (<c>[Graphics]</c>).</summary>
    public bool IsFont { get; init; } = true;
}
