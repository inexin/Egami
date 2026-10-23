namespace Egami.Video.Subtitles;

/// <summary>
/// A WebVTT cue's settings (the text after the timings: <c>line:0 position:20% align:start</c>), with the defaults the W3C
/// spec gives and the same meaning as the browser's <c>VTTCue</c>. Read from WebVTT; WebVTT output writes them back, and
/// builds them from <see cref="SubtitleCue.Alignment"/> for cues that have none.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-webvtt"/>
/// </example>
public sealed record WebVttCueSettings
{
    /// <summary>Gets the writing direction: horizontal, or vertical growing left or right (<c>vertical:rl</c>, <c>vertical:lr</c>).</summary>
    public WebVttVertical Vertical { get; init; }

    /// <summary>
    /// Gets the line: a line number when <see cref="SnapToLines"/> is set (negative counts from the bottom), a percentage of the
    /// video's height otherwise, or <see langword="null"/> for auto (the bottom).
    /// </summary>
    public double? Line { get; init; }

    /// <summary>Gets whether <see cref="Line"/> is a line number (true) or a percentage (false). True by default.</summary>
    public bool SnapToLines { get; init; } = true;

    /// <summary>Gets which edge of the cue box the line places.</summary>
    public WebVttLineAlign LineAlign { get; init; }

    /// <summary>Gets the position across the line as a percentage of the video's width, or <see langword="null"/> for auto.</summary>
    public double? Position { get; init; }

    /// <summary>Gets which point of the cue box the position places.</summary>
    public WebVttPositionAlign PositionAlign { get; init; }

    /// <summary>Gets the cue box's size as a percentage of the video's width (height for vertical text). 100 by default.</summary>
    public double Size { get; init; } = 100;

    /// <summary>Gets how the text lines are aligned in the cue box. Center by default.</summary>
    public WebVttAlign Align { get; init; } = WebVttAlign.Center;

    /// <summary>Gets the id of the <see cref="SubtitleRegion"/> the cue shows in, or <see langword="null"/>.</summary>
    public string? Region { get; init; }

    /// <summary>Gets whether every setting has its default value.</summary>
    internal bool IsDefault => Vertical == WebVttVertical.Horizontal && Line is null && SnapToLines && LineAlign == WebVttLineAlign.Start
        && Position is null && PositionAlign == WebVttPositionAlign.Auto && Size == 100 && Align == WebVttAlign.Center && Region is null;
}

/// <summary>A WebVTT cue's writing direction (<c>vertical</c> setting).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-webvtt"/>
/// </example>
public enum WebVttVertical
{
    /// <summary>Horizontal text, the default.</summary>
    Horizontal,

    /// <summary>Vertical text whose lines grow to the left (<c>vertical:rl</c>).</summary>
    RightToLeft,

    /// <summary>Vertical text whose lines grow to the right (<c>vertical:lr</c>).</summary>
    LeftToRight,
}

/// <summary>Which edge of a WebVTT cue box its line places (the part after the comma in <c>line:50%,center</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-webvtt"/>
/// </example>
public enum WebVttLineAlign
{
    /// <summary>The start edge, the default.</summary>
    Start,

    /// <summary>The center.</summary>
    Center,

    /// <summary>The end edge.</summary>
    End,
}

/// <summary>Which point of a WebVTT cue box its position places (the part after the comma in <c>position:10%,line-left</c>).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-webvtt"/>
/// </example>
public enum WebVttPositionAlign
{
    /// <summary>Follows the text alignment, the default.</summary>
    Auto,

    /// <summary>The line's left edge.</summary>
    LineLeft,

    /// <summary>The center.</summary>
    Center,

    /// <summary>The line's right edge.</summary>
    LineRight,
}

/// <summary>How a WebVTT cue's text lines are aligned in its box (<c>align</c> setting).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-webvtt"/>
/// </example>
public enum WebVttAlign
{
    /// <summary>At the start of the line, following the text's direction.</summary>
    Start,

    /// <summary>Centered, the default.</summary>
    Center,

    /// <summary>At the end of the line, following the text's direction.</summary>
    End,

    /// <summary>At the left.</summary>
    Left,

    /// <summary>At the right.</summary>
    Right,
}

/// <summary>
/// A WebVTT region (a <c>REGION</c> block): a box cues can show in, which scrolls lines up as new cues arrive. Cues name it
/// in <see cref="WebVttCueSettings.Region"/>.
/// </summary>
/// <param name="Id">The region's id.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-webvtt"/>
/// </example>
public sealed record SubtitleRegion(string Id)
{
    /// <summary>Gets the width as a percentage of the video's width. 100 by default.</summary>
    public double Width { get; init; } = 100;

    /// <summary>Gets the height in lines. 3 by default; a long, as browsers read any 32-bit count.</summary>
    public long Lines { get; init; } = 3;

    /// <summary>Gets the anchor point's distance from the region's left edge, as a percentage of its width. 0 by default.</summary>
    public double RegionAnchorX { get; init; }

    /// <summary>Gets the anchor point's distance from the region's top edge, as a percentage of its height. 100 by default.</summary>
    public double RegionAnchorY { get; init; } = 100;

    /// <summary>Gets where the anchor point sits across the video, as a percentage of its width. 0 by default.</summary>
    public double ViewportAnchorX { get; init; }

    /// <summary>Gets where the anchor point sits down the video, as a percentage of its height. 100 by default.</summary>
    public double ViewportAnchorY { get; init; } = 100;

    /// <summary>Gets whether lines scroll up when a new cue arrives (<c>scroll:up</c>).</summary>
    public bool ScrollUp { get; init; }
}
