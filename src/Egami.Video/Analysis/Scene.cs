namespace Egami.Video.Analysis;

/// <summary>A scene: a run of frames from one cut to the next, as <see cref="SceneDetector"/> finds them.</summary>
/// <param name="Number">The scene's number, from 1.</param>
/// <param name="StartFrame">Its first frame, counted from 0 in presentation order among the frames the detector saw.</param>
/// <param name="EndFrame">Its last frame, inclusive.</param>
/// <param name="Start">Its first frame's timestamp.</param>
/// <param name="End">When it ends: the next scene's start, or the end of the video for the last scene.</param>
/// <param name="Score">How sharp the change that starts it is, from 0 to 1; 0 for the first scene.</param>
/// <param name="StartKind">How it starts: the first frame, a cut, or the middle of a fade through black.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-run"/>
/// </example>
public sealed record Scene(int Number, long StartFrame, long EndFrame, MediaTime Start, MediaTime End, double Score, SceneStart StartKind)
{
    /// <summary>Gets the scene's length.</summary>
    public MediaTime Duration => End - Start;

    /// <summary>Gets the time halfway through the scene, where a typical frame for it is usually found.</summary>
    public MediaTime Middle => Start.Add(new MediaTime(Duration.Ticks / 2, Duration.TimeBase));
}

/// <summary>A point where a new scene starts, as <see cref="SceneDetector"/> reports it while frames come in.</summary>
/// <param name="Frame">The new scene's first frame, counted from 0.</param>
/// <param name="Time">That frame's timestamp.</param>
/// <param name="Score">How sharp the change is, from 0 to 1; 0 for the first frame.</param>
/// <param name="Kind">What kind of change it is.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-detectors"/>
/// </example>
public sealed record SceneCut(long Frame, MediaTime Time, double Score, SceneStart Kind);

/// <summary>A fade to or from black (or white, when asked for) that <see cref="SceneDetector"/> found.</summary>
/// <param name="Kind">Whether the picture fades out or in.</param>
/// <param name="StartFrame">The first frame of the fade.</param>
/// <param name="EndFrame">The last frame of the fade: the first dark frame of a fade-out, or the last brightening frame of a fade-in.</param>
/// <param name="Start">The first frame's timestamp.</param>
/// <param name="End">The last frame's timestamp.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-scene-options"/>
/// </example>
public sealed record Fade(FadeKind Kind, long StartFrame, long EndFrame, MediaTime Start, MediaTime End);

/// <summary>One frame's scene measures, kept when <see cref="SceneOptions.KeepSeries"/> is set, to plot or tune with.</summary>
/// <param name="Frame">The frame, counted from 0.</param>
/// <param name="Time">Its timestamp.</param>
/// <param name="Score">The method's change score against the frame before, from 0 to 1.</param>
/// <param name="Luma">The frame's mean luma from black level (0) to white level (1).</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-scene-options"/>
/// </example>
public readonly record struct SceneScore(long Frame, MediaTime Time, double Score, double Luma);

/// <summary>How a scene starts.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-run"/>
/// </example>
public enum SceneStart
{
    /// <summary>It is the first scene, at the first frame.</summary>
    First,

    /// <summary>A cut: the picture changes sharply from one frame to the next.</summary>
    Cut,

    /// <summary>A fade or cut through black (or white): the scene starts in the middle of the dark run, moved by <see cref="SceneOptions.FadeBias"/>.</summary>
    Fade,
}

/// <summary>Whether a fade goes out to black or in from it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-scene-options"/>
/// </example>
public enum FadeKind
{
    /// <summary>The picture darkens to black (or brightens to white).</summary>
    Out,

    /// <summary>The picture comes up from black (or down from white).</summary>
    In,
}

/// <summary>How <see cref="SceneDetector"/> scores the change between frames.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-scene-options"/>
/// </example>
public enum SceneMethod
{
    /// <summary>
    /// The default. The content change (luma and chroma of a 16 by 9 grid) between neighbors, kept only when it is
    /// <see cref="SceneOptions.AdaptiveRatio"/> times the mean change of the frames around it and at least the threshold. Fast
    /// camera motion changes every frame a lot and is not taken for cuts. Reports cuts two frames after they happen.
    /// </summary>
    Adaptive,

    /// <summary>
    /// The content change between neighbors less the mean change of the eight frames before, against a fixed threshold: what the
    /// <c>scenes</c> command did before V1.4 and what ffmpeg's <c>scene</c> value means. Reports cuts at once.
    /// </summary>
    Content,

    /// <summary>
    /// The chi-square distance between neighbors' 64-bin luma histograms, against a fixed threshold. Ignores motion within a shot,
    /// but misses cuts between shots of the same exposure.
    /// </summary>
    Histogram,
}

/// <summary>What <see cref="SceneDetector"/> does with a cut that comes back within the minimum scene length, as a camera flash does.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-scene-options"/>
/// </example>
public enum FlashMode
{
    /// <summary>
    /// The default. A cut is held for the minimum scene length; if a frame in that time matches the frame before the cut, the
    /// picture came back, so it was a flash and the cut is dropped.
    /// </summary>
    Merge,

    /// <summary>Only the minimum length applies: a cut closer than it to the previous one is dropped.</summary>
    Suppress,
}
