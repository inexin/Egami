using Egami.Pixels;

namespace Egami.Video.Editing;

/// <summary>
/// A piece of video, with its audio, as it goes into a <see cref="VideoEdit"/>: a file or stream, or a <see cref="Stack"/> of
/// clips. The methods return a changed copy, so they chain: <c>Clip.Open("a.mp4").Trim(start, end).FadeIn(duration)</c>.
/// Nothing is decoded until the edit renders.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-join"/>
/// </example>
public sealed record Clip
{
    private Clip(ClipSource source)
    {
        Source = source;
    }

    /// <summary>Gets the path of the file the clip reads, or <see langword="null"/> for a stream or a stack.</summary>
    public string? Path => (Source as FileClipSource)?.Path;

    /// <summary>Gets the time in the source where the clip starts, or <see langword="null"/> for the start.</summary>
    public MediaTime? In { get; init; }

    /// <summary>Gets the time in the source where the clip ends (the first moment not used), or <see langword="null"/> for the end.</summary>
    public MediaTime? Out { get; init; }

    /// <summary>Gets how long the clip fades in from <see cref="FadeColor"/>, and its audio from silence. Zero for none.</summary>
    public MediaTime FadeInDuration { get; init; } = MediaTime.Zero;

    /// <summary>Gets how long the clip fades out to <see cref="FadeColor"/>, and its audio to silence. Zero for none.</summary>
    public MediaTime FadeOutDuration { get; init; } = MediaTime.Zero;

    /// <summary>Gets the colour the picture fades from and to. Default black.</summary>
    public Rgba8 FadeColor { get; init; } = Rgba8.Black;

    /// <summary>Gets the gain of the clip's audio in decibels. Default 0.</summary>
    public double VolumeDecibels { get; init; }

    /// <summary>Gets whether the clip's audio is left out.</summary>
    public bool IsMuted { get; init; }

    /// <summary>Gets the effects that run on the clip's frames, after it is fitted to the canvas, timed from the clip's start.</summary>
    public IReadOnlyList<EditEffect> Effects { get; init; } = [];

    internal ClipSource Source { get; init; }

    /// <summary>Returns a clip that reads a file's first video track and its first audio track.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The clip.</returns>
    public static Clip Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new Clip(new FileClipSource(path));
    }

    /// <summary>
    /// Returns a clip that reads a stream's first video track and its first audio track. The stream must be seekable and stay
    /// open until the edit has rendered. A clip from a stream cannot be saved as JSON.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The clip.</returns>
    public static Clip Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("A clip's stream must be seekable: video and audio are read from it separately.", nameof(stream));
        }

        return new Clip(new StreamClipSource(stream));
    }

    /// <summary>Returns the clip cut to a part of its source, on the exact frame and sample.</summary>
    /// <param name="start">The time in the source where the clip starts.</param>
    /// <param name="end">The time where it ends, or <see langword="null"/> for the end of the source.</param>
    /// <returns>The trimmed clip.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The start is negative or the end is not after it.</exception>
    public Clip Trim(MediaTime start, MediaTime? end = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, MediaTime.Zero);
        if (end is MediaTime e && e <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), e, "The end must be after the start.");
        }

        return this with { In = start, Out = end };
    }

    /// <summary>Returns the clip with a fade in from a colour at its start, and a fade in of its audio.</summary>
    /// <param name="duration">How long the fade lasts.</param>
    /// <param name="color">The colour, or <see langword="null"/> to keep <see cref="FadeColor"/> (black unless set).</param>
    /// <returns>The clip.</returns>
    public Clip FadeIn(MediaTime duration, Rgba8? color = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, MediaTime.Zero);
        return this with { FadeInDuration = duration, FadeColor = color ?? FadeColor };
    }

    /// <summary>Returns the clip with a fade out to a colour at its end, and a fade out of its audio.</summary>
    /// <param name="duration">How long the fade lasts.</param>
    /// <param name="color">The colour, or <see langword="null"/> to keep <see cref="FadeColor"/> (black unless set).</param>
    /// <returns>The clip.</returns>
    public Clip FadeOut(MediaTime duration, Rgba8? color = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, MediaTime.Zero);
        return this with { FadeOutDuration = duration, FadeColor = color ?? FadeColor };
    }

    /// <summary>Returns the clip with its audio turned up or down.</summary>
    /// <param name="decibels">The gain in decibels.</param>
    /// <returns>The clip.</returns>
    public Clip Volume(double decibels)
    {
        if (!double.IsFinite(decibels))
        {
            throw new ArgumentOutOfRangeException(nameof(decibels), decibels, "The gain must be a finite number of decibels.");
        }

        return this with { VolumeDecibels = decibels };
    }

    /// <summary>Returns the clip without its audio.</summary>
    /// <returns>The clip.</returns>
    public Clip Muted() => this with { IsMuted = true };

    /// <summary>Returns the clip with one more effect on its frames.</summary>
    /// <param name="effect">The effect, timed from the clip's start.</param>
    /// <returns>The clip.</returns>
    public Clip WithEffect(EditEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return this with { Effects = [.. Effects, effect] };
    }

    internal static Clip FromSource(ClipSource source) => new(source);
}

/// <summary>How a <see cref="Stack"/> lays out its clips.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-stack"/>
/// </example>
public enum StackLayout
{
    /// <summary>Side by side, left to right, each scaled to the first clip's height, like ffmpeg's <c>hstack</c>.</summary>
    Horizontal,

    /// <summary>One above the other, each scaled to the first clip's width, like ffmpeg's <c>vstack</c>.</summary>
    Vertical,

    /// <summary>A grid of equal cells the first clip's size, filled row by row, like ffmpeg's <c>xstack</c> with a grid.</summary>
    Grid,
}

/// <summary>How a <see cref="Stack"/> is drawn and how long it lasts.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-stack"/>
/// </example>
public sealed record StackOptions
{
    /// <summary>Gets the defaults: no gap, black, longest, the first clip's audio.</summary>
    public static StackOptions Default { get; } = new();

    /// <summary>Gets the gap between cells in pixels. Default 0.</summary>
    public int Gap { get; init; }

    /// <summary>Gets the colour of the gaps and of cells a clip does not cover. Default black.</summary>
    public Rgba8 Background { get; init; } = Rgba8.Black;

    /// <summary>Gets how a clip is fitted to a grid cell of another size. Default <see cref="ClipFit.Contain"/>.</summary>
    public ClipFit Fit { get; init; }

    /// <summary>Gets whether the stack ends with its shortest clip instead of its longest; a clip that ends sooner holds its last frame.</summary>
    public bool Shortest { get; init; }

    /// <summary>Gets whether the stack's audio is every clip's audio mixed, instead of the first clip's only.</summary>
    public bool MixAudio { get; init; }
}

/// <summary>
/// Builds one clip out of several shown at once: side by side, one above the other, or in a grid. A stack is an ordinary
/// <see cref="Clip"/>, so it can be trimmed, faded or joined with a transition.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-stack"/>
/// </example>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "A stack of clips, the V9 Design's name (Q8), not a stack collection.")]
public static class Stack
{
    /// <summary>Returns the clips side by side.</summary>
    /// <param name="clips">The clips, left to right; at least two.</param>
    /// <param name="options">The gap, background and timing, or <see langword="null"/> for the defaults.</param>
    /// <returns>The stack.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-stack"/>
    /// </example>
    public static Clip Horizontal(IEnumerable<Clip> clips, StackOptions? options = null) => Make(StackLayout.Horizontal, 0, 0, clips, options);

    /// <summary>Returns the clips side by side.</summary>
    /// <param name="clips">The clips, left to right; at least two.</param>
    /// <returns>The stack.</returns>
    public static Clip Horizontal(params Clip[] clips) => Horizontal((IEnumerable<Clip>)clips);

    /// <summary>Returns the clips one above the other.</summary>
    /// <param name="clips">The clips, top to bottom; at least two.</param>
    /// <param name="options">The gap, background and timing, or <see langword="null"/> for the defaults.</param>
    /// <returns>The stack.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-stack"/>
    /// </example>
    public static Clip Vertical(IEnumerable<Clip> clips, StackOptions? options = null) => Make(StackLayout.Vertical, 0, 0, clips, options);

    /// <summary>Returns the clips one above the other.</summary>
    /// <param name="clips">The clips, top to bottom; at least two.</param>
    /// <returns>The stack.</returns>
    public static Clip Vertical(params Clip[] clips) => Vertical((IEnumerable<Clip>)clips);

    /// <summary>Returns the clips in a grid, filled row by row; cells without a clip show the background.</summary>
    /// <param name="columns">The number of columns.</param>
    /// <param name="rows">The number of rows.</param>
    /// <param name="clips">The clips; at least one and at most columns times rows.</param>
    /// <param name="options">The gap, background and timing, or <see langword="null"/> for the defaults.</param>
    /// <returns>The stack.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-stack"/>
    /// </example>
    public static Clip Grid(int columns, int rows, IEnumerable<Clip> clips, StackOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        return Make(StackLayout.Grid, columns, rows, clips, options);
    }

    private static Clip Make(StackLayout layout, int columns, int rows, IEnumerable<Clip> clips, StackOptions? options)
    {
        ArgumentNullException.ThrowIfNull(clips);
        Clip[] all = [.. clips];
        int least = layout == StackLayout.Grid ? 1 : 2;
        if (all.Length < least || (layout == StackLayout.Grid && all.Length > columns * rows))
        {
            throw new ArgumentException(layout == StackLayout.Grid
                ? $"A {columns} by {rows} grid takes 1 to {columns * rows} clips, not {all.Length}."
                : $"A stack needs at least two clips, not {all.Length}.", nameof(clips));
        }

        ArgumentOutOfRangeException.ThrowIfNegative((options ?? StackOptions.Default).Gap, nameof(options));
        return Clip.FromSource(new StackClipSource(layout, columns, rows, all, options ?? StackOptions.Default));
    }
}

/// <summary>Where a clip's pictures and sound come from.</summary>
internal abstract class ClipSource;

internal sealed class FileClipSource(string path) : ClipSource
{
    public string Path { get; } = path;
}

internal sealed class StreamClipSource(Stream stream) : ClipSource
{
    public Stream Stream { get; } = stream;
}

internal sealed class StackClipSource(StackLayout layout, int columns, int rows, IReadOnlyList<Clip> clips, StackOptions options) : ClipSource
{
    public StackLayout Layout { get; } = layout;

    public int Columns { get; } = columns;

    public int Rows { get; } = rows;

    public IReadOnlyList<Clip> Clips { get; } = clips;

    public StackOptions Options { get; } = options;
}
