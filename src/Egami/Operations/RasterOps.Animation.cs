using Egami.Operations.Animation;

namespace Egami.Operations;

/// <summary>
/// Animation steps: they work on the frames of the whole animation rather than on each frame, so they run in Apply, Derive and
/// Transcode (which then decodes the whole animation first) but not inside Within. Frames in memory are already full pictures of
/// what is on screen, so there is no Coalesce, and the writers find each frame's changed area themselves, so there is no Optimize.
/// </summary>
public sealed partial class RasterOps
{
    /// <summary>
    /// Removes each frame equal to the one before it and adds its delay to the frame that stays, as ImageMagick's -layers
    /// RemoveDups: a screen recording that sat still for seconds becomes one long frame. What is on screen at every moment is
    /// unchanged. Removed frames take their metadata with them.
    /// </summary>
    /// <param name="tolerance">How far a pixel may differ and still count as equal, by ImageMagick's -fuzz distance, from 0 (the default, exact) to 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tolerance"/> is outside 0 to 1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationTools.cs" region="shrink"/>
    /// </example>
    public RasterOps MergeDuplicates(float tolerance = 0)
    {
        CheckTolerance(tolerance, nameof(tolerance));
        return Add(new MergeDuplicatesStep(tolerance));
    }

    /// <summary>
    /// Removes frames with no delay, which no viewer shows, as ImageMagick's -layers RemoveZero; when every frame has none,
    /// nothing is removed.
    /// </summary>
    /// <returns>This builder.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationTools.cs" region="shrink"/>
    /// </example>
    public RasterOps DropZeroDelay()
    {
        return Add(new DropZeroDelayStep());
    }

    /// <summary>
    /// Resamples the animation to <paramref name="framesPerSecond"/>, as ffmpeg's fps filter: fixed ticks, each showing the frame
    /// visible when it starts, with neighbours that show the same frame merged. No frame is ever added and the total duration is
    /// kept; frames shorter than a tick may be dropped. A rate at or above the animation's own changes nothing. Browsers show
    /// GIF delays under 20 ms as 100 ms, so 50 is the useful ceiling for GIF.
    /// </summary>
    /// <param name="framesPerSecond">The rate, more than 0 and at most 1,000.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="framesPerSecond"/> is out of range or not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationTools.cs" region="shrink"/>
    /// </example>
    public RasterOps Retime(double framesPerSecond)
    {
        if (!(framesPerSecond > 0 && framesPerSecond <= 1000))
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), framesPerSecond, "The rate must be more than 0 and at most 1,000 frames a second.");
        }

        return Add(new RetimeStep(framesPerSecond));
    }

    /// <summary>Plays the animation <paramref name="factor"/> times as fast: every delay is divided by it, rounded to whole milliseconds.</summary>
    /// <param name="factor">The speed-up, more than 0: 2 plays twice as fast, 0.5 at half speed.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not finite or not positive.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationTools.cs" region="speed"/>
    /// </example>
    public RasterOps ChangeSpeed(double factor)
    {
        if (!(double.IsFinite(factor) && factor > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The factor must be finite and more than 0.");
        }

        return Add(new ChangeSpeedStep(factor));
    }

    /// <summary>Plays the animation backwards; each frame keeps its delay and metadata.</summary>
    /// <returns>This builder.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationTools.cs" region="speed"/>
    /// </example>
    public RasterOps Reverse()
    {
        return Add(new ReverseStep());
    }

    /// <summary>
    /// Draws an animation (or a still) onto this one at <paramref name="position"/>. By time (the default) both timelines are cut
    /// at every frame boundary of either, so an overlay at another speed keeps its speed; each piece shows the base frame and the
    /// overlay frame of that moment, drawn as DrawImage does with the blend mode and opacity. The overlay loops for as long as the
    /// base runs unless <see cref="AnimationOverlayOptions.Repeat"/> says otherwise; a still base lasts as long as the overlay
    /// plays and takes its loop count. The overlay is converted to this raster's pixel type. The output frame count is checked
    /// against <see cref="SafetyLimits.MaxFrames"/> and <see cref="SafetyLimits.MaxTotalBytes"/> before anything is drawn.
    /// </summary>
    /// <param name="overlay">The animation or still to draw.</param>
    /// <param name="position">Where its top-left corner goes; it may lie partly or wholly outside.</param>
    /// <param name="options">The blending, repeat count, start and timing, or <see langword="null"/> for <see cref="AnimationOverlayOptions.Default"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="overlay"/> is <see langword="null"/>.</exception>
    /// <exception cref="LimitExceededException">The result would break MaxFrames or MaxTotalBytes.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationTools.cs" region="sticker"/>
    /// </example>
    public RasterOps DrawAnimation(Raster overlay, Point position, AnimationOverlayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        return Add(new DrawAnimationStep(overlay, position, options ?? AnimationOverlayOptions.Default));
    }
}
