namespace Egami.Video.Editing;

/// <summary>Where one clip of the main track falls in a planned edit, in whole output frames.</summary>
/// <param name="Index">The clip's position on the track.</param>
/// <param name="Clip">The clip.</param>
/// <param name="StartFrame">The output frame it starts on.</param>
/// <param name="FrameCount">How many output frames it lasts, its transitions included.</param>
/// <param name="TransitionFrames">How many of its first frames overlap the clip before it.</param>
/// <param name="SourceIn">The time in its source it starts at.</param>
/// <param name="SourceSize">The size of its picture.</param>
/// <param name="HasAudio">Whether it brings audio into the edit.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-plan"/>
/// </example>
public sealed record PlannedClip(int Index, Clip Clip, long StartFrame, long FrameCount, long TransitionFrames, MediaTime SourceIn, Size SourceSize, bool HasAudio);

/// <summary>Where one layer falls in a planned edit, in whole output frames.</summary>
/// <param name="Index">The layer's position in <see cref="VideoEdit.Layers"/>.</param>
/// <param name="Layer">The layer.</param>
/// <param name="StartFrame">The output frame it starts on.</param>
/// <param name="FrameCount">How many output frames it shows.</param>
/// <param name="ClipFrames">How many of those frames its clip covers; after them it holds its last frame.</param>
/// <param name="SourceSize">The size of its clip's picture.</param>
/// <param name="HasAudio">Whether it brings audio into the edit.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-plan"/>
/// </example>
public sealed record PlannedLayer(int Index, Layer Layer, long StartFrame, long FrameCount, long ClipFrames, Size SourceSize, bool HasAudio);

/// <summary>
/// An edit's timeline worked out before rendering by <see cref="VideoEdit.Plan"/>: the canvas, where every clip and layer
/// falls in whole frames, the length, and warnings about things it fixed up, such as a trim past a clip's end.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-plan"/>
/// </example>
public sealed class EditPlan
{
    internal EditPlan(VideoEdit edit, EditCanvas canvas, long frames, IReadOnlyList<PlannedClip> clips, IReadOnlyList<PlannedLayer> layers,
        IReadOnlyList<string> warnings, IReadOnlyDictionary<Clip, SourceInfo> sources)
    {
        Edit = edit;
        Canvas = canvas;
        FrameCount = frames;
        Clips = clips;
        Layers = layers;
        Warnings = warnings;
        Sources = sources;
    }

    /// <summary>Gets the canvas: the edit's own, or the one taken from its first clip.</summary>
    public EditCanvas Canvas { get; }

    /// <summary>Gets the number of output frames.</summary>
    public long FrameCount { get; }

    /// <summary>Gets the length: <see cref="FrameCount"/> frames at the canvas frame rate.</summary>
    public MediaTime Duration => new(FrameCount * Canvas.FrameRate.Denominator, new Rational(1, Canvas.FrameRate.Numerator));

    /// <summary>Gets the main track's clips in order.</summary>
    public IReadOnlyList<PlannedClip> Clips { get; }

    /// <summary>Gets the layers that show, in drawing order.</summary>
    public IReadOnlyList<PlannedLayer> Layers { get; }

    /// <summary>Gets whether any clip or layer brings audio.</summary>
    public bool HasAudio => Clips.Any(c => c.HasAudio) || Layers.Any(l => l.HasAudio);

    /// <summary>Gets what the plan fixed up or left out, one sentence each.</summary>
    public IReadOnlyList<string> Warnings { get; }

    internal VideoEdit Edit { get; }

    internal IReadOnlyDictionary<Clip, SourceInfo> Sources { get; }

    /// <summary>Returns the time of an output frame.</summary>
    internal MediaTime TimeOf(long frame) => new(frame * Canvas.FrameRate.Denominator, new Rational(1, Canvas.FrameRate.Numerator));
}

/// <summary>What the planner read from a clip's source.</summary>
internal sealed record SourceInfo(Size Size, Rational FrameRate, MediaTime Duration, bool HasVideo, bool HasAudio, MediaTime In, MediaTime Out);
