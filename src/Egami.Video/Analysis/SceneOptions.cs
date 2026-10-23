namespace Egami.Video.Analysis;

/// <summary>How <see cref="SceneDetector"/> finds scenes. The defaults suit most footage; lower the threshold to find softer cuts.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-scene-options"/>
/// </example>
public sealed record SceneOptions
{
    /// <summary>Gets the default options.</summary>
    public static SceneOptions Default { get; } = new();

    /// <summary>Gets how changes are scored. Default <see cref="SceneMethod.Adaptive"/>.</summary>
    public SceneMethod Method { get; init; } = SceneMethod.Adaptive;

    /// <summary>
    /// Gets the lowest score, from 0 to 1, that starts a scene, or <see langword="null"/> for the method's own: 0.15 for
    /// <see cref="SceneMethod.Adaptive"/> (which also needs <see cref="AdaptiveRatio"/>), 0.4 for <see cref="SceneMethod.Content"/>
    /// and <see cref="SceneMethod.Histogram"/>. Lower finds more cuts.
    /// </summary>
    public double? Threshold { get; init; }

    /// <summary>
    /// Gets how many times the mean change of the frames around it a change must be to count as a cut, for
    /// <see cref="SceneMethod.Adaptive"/>. Default 3, as PySceneDetect's adaptive detector.
    /// </summary>
    public double AdaptiveRatio { get; init; } = 3;

    /// <summary>Gets how many frames on each side the adaptive mean takes, from 1 to 8. Default 2. A cut is reported this many frames late.</summary>
    public int Window { get; init; } = 2;

    /// <summary>
    /// Gets the shortest scene: a cut closer than this to the previous scene's start (the first frame for the first cut) is not
    /// taken. Default 0.6 s, as PySceneDetect's 15 frames at 25 frames a second.
    /// </summary>
    public MediaTime MinSceneLength { get; init; } = MediaTime.FromSeconds(0.6);

    /// <summary>Gets what happens to a cut that comes back within <see cref="MinSceneLength"/>. Default <see cref="FlashMode.Merge"/>.</summary>
    public FlashMode Flash { get; init; } = FlashMode.Merge;

    /// <summary>
    /// Gets whether fades and cuts through black start a scene in the middle of the dark run instead of at its two ends. Default
    /// on. The fades themselves are listed in <see cref="SceneDetector.Fades"/>.
    /// </summary>
    public bool DetectFades { get; init; } = true;

    /// <summary>Gets whether runs of white count like runs of black for <see cref="DetectFades"/>. Off by default, since a white shot is not a fade.</summary>
    public bool FadeToWhite { get; init; }

    /// <summary>
    /// Gets the mean luma, from black level (0) to white level (1), at or under which a frame counts as black for
    /// <see cref="DetectFades"/>. Default 0.06, about PySceneDetect's 12 on its 0 to 255 scale; white uses 1 less this.
    /// </summary>
    public double FadeLevel { get; init; } = 0.06;

    /// <summary>
    /// Gets where in a dark run the new scene starts, from -1 (the first dark frame) through 0 (the middle, the default) to 1 (the
    /// first frame after it), as PySceneDetect's fade bias.
    /// </summary>
    public double FadeBias { get; init; }

    /// <summary>Gets whether <see cref="SceneDetector.Scores"/> keeps every frame's score and luma. Off by default to keep memory flat.</summary>
    public bool KeepSeries { get; init; }

    internal double EffectiveThreshold => Threshold ?? (Method == SceneMethod.Adaptive ? 0.15 : 0.4);

    internal void Validate()
    {
        if (Threshold is { } t && (t < 0 || t > 1 || double.IsNaN(t)))
        {
            throw new ArgumentOutOfRangeException(nameof(Threshold), t, "The threshold must be from 0 to 1.");
        }

        if (AdaptiveRatio < 1 || double.IsNaN(AdaptiveRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(AdaptiveRatio), AdaptiveRatio, "The adaptive ratio must be 1 or more.");
        }

        if (Window is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(Window), Window, "The window must be from 1 to 8 frames.");
        }

        if (MinSceneLength.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinSceneLength), MinSceneLength, "The minimum scene length cannot be negative.");
        }

        if (FadeLevel is < 0 or > 0.5 || double.IsNaN(FadeLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(FadeLevel), FadeLevel, "The fade level must be from 0 to 0.5.");
        }

        if (FadeBias is < -1 or > 1 || double.IsNaN(FadeBias))
        {
            throw new ArgumentOutOfRangeException(nameof(FadeBias), FadeBias, "The fade bias must be from -1 to 1.");
        }
    }
}
