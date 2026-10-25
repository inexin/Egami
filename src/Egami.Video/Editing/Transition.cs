using Egami.Pixels;
using Egami.Video.Audio.Filters;

namespace Egami.Video.Editing;

/// <summary>The built-in kinds of <see cref="Transition"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-transitions"/>
/// </example>
public enum TransitionKind
{
    /// <summary>A cross-dissolve: the two clips blended by progress, like ffmpeg's <c>xfade=fade</c>.</summary>
    Fade,

    /// <summary>The first clip fades to a colour, then the second fades in from it, like <c>fadeblack</c> and <c>fadewhite</c>.</summary>
    FadeThroughColor,

    /// <summary>The second clip appears pixel by pixel in a fixed noise order, like <c>dissolve</c>.</summary>
    Dissolve,

    /// <summary>A straight edge moves across in <see cref="Transition.Direction"/>, uncovering the second clip, like <c>wipeleft</c>.</summary>
    Wipe,

    /// <summary>The second clip slides in from the far side, pushing the first one out in <see cref="Transition.Direction"/>, like <c>slideleft</c>.</summary>
    Slide,

    /// <summary>The second clip shows inside a circle that grows from the centre, like <c>circleopen</c>.</summary>
    CircleOpen,

    /// <summary>The first clip stays inside a circle that shrinks to the centre, like <c>circleclose</c>.</summary>
    CircleClose,

    /// <summary>The second clip shows behind a hand that sweeps clockwise from twelve o'clock, like <c>radial</c>.</summary>
    Clock,

    /// <summary>The first clip zooms in towards its centre while the second fades in, like <c>zoomin</c>.</summary>
    Zoom,

    /// <summary>The first clip breaks up into growing blocks, then the second sharpens out of them, like <c>pixelize</c>.</summary>
    Pixelize,

    /// <summary>The first clip blurs out while the second blurs in, like <c>hblur</c> in both directions.</summary>
    Blur,

    /// <summary>An app's own <see cref="ITransition"/>.</summary>
    Custom,
}

/// <summary>Which way a <see cref="TransitionKind.Wipe"/> edge or a <see cref="TransitionKind.Slide"/> moves.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-transitions"/>
/// </example>
public enum TransitionDirection
{
    /// <summary>Towards the left.</summary>
    Left,

    /// <summary>Towards the right.</summary>
    Right,

    /// <summary>Upwards.</summary>
    Up,

    /// <summary>Downwards.</summary>
    Down,
}

/// <summary>
/// An app's own picture transition for <see cref="Transition.Custom"/>: it draws one frame of the change from one clip to the
/// next. The two frames and the target have the canvas size. The same instance draws every frame, possibly on several threads,
/// so keep it stateless.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-custom-transition"/>
/// </example>
public interface ITransition
{
    /// <summary>Draws one frame of the transition.</summary>
    /// <param name="outgoing">The outgoing clip's frame.</param>
    /// <param name="incoming">The incoming clip's frame.</param>
    /// <param name="progress">How far the transition is, from 0 (all <paramref name="outgoing"/>) towards 1 (all <paramref name="incoming"/>), after easing.</param>
    /// <param name="target">The frame to draw; every pixel must be written.</param>
    void Render(Raster<Rgba8> outgoing, Raster<Rgba8> incoming, double progress, Raster<Rgba8> target);
}

/// <summary>
/// How one clip on an <see cref="EditTrack"/> changes into the next. The two clips overlap by <see cref="Duration"/>, so the
/// edit gets shorter by it; their audio crossfades over the same time, equal power by default. Make one with the static
/// methods, such as <see cref="Dissolve"/> or <see cref="Wipe"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-transitions"/>
/// </example>
public sealed record Transition
{
    private Transition(TransitionKind kind, MediaTime duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, MediaTime.Zero);
        Kind = kind;
        Duration = duration;
    }

    /// <summary>Gets the kind.</summary>
    public TransitionKind Kind { get; }

    /// <summary>Gets how long the two clips overlap.</summary>
    public MediaTime Duration { get; }

    /// <summary>Gets which way a wipe or slide moves. Default <see cref="TransitionDirection.Left"/>.</summary>
    public TransitionDirection Direction { get; init; }

    /// <summary>Gets the colour of <see cref="TransitionKind.FadeThroughColor"/>. Default black.</summary>
    public Rgba8 Color { get; init; } = Rgba8.Black;

    /// <summary>Gets the width of a wipe's soft edge as a fraction of the picture, from 0 (hard) to 1. Default 0.</summary>
    public double Softness { get; init; }

    /// <summary>Gets how progress moves over the transition. Default <see cref="Video.Easing.Linear"/>; <see cref="Video.Easing.EaseInOut"/> starts and ends slowly.</summary>
    public Easing Easing { get; init; }

    /// <summary>Gets the shape of the audio crossfade. Default <see cref="FadeCurve.EqualPower"/>; <see cref="FadeCurve.Linear"/> suits the same material on both sides.</summary>
    public FadeCurve AudioCurve { get; init; } = FadeCurve.EqualPower;

    /// <summary>Gets the app's own transition for <see cref="TransitionKind.Custom"/>, or <see langword="null"/>.</summary>
    public ITransition? Renderer { get; private init; }

    /// <summary>Returns a cross-dissolve.</summary>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition Fade(MediaTime duration) => new(TransitionKind.Fade, duration);

    /// <summary>Returns a fade through a colour.</summary>
    /// <param name="color">The colour.</param>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition FadeThrough(Rgba8 color, MediaTime duration) => new(TransitionKind.FadeThroughColor, duration) { Color = color };

    /// <summary>Returns a noise dissolve.</summary>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition Dissolve(MediaTime duration) => new(TransitionKind.Dissolve, duration);

    /// <summary>Returns a wipe.</summary>
    /// <param name="direction">Which way the edge moves.</param>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition Wipe(TransitionDirection direction, MediaTime duration) => new(TransitionKind.Wipe, duration) { Direction = direction };

    /// <summary>Returns a slide.</summary>
    /// <param name="direction">Which way the pictures move.</param>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition Slide(TransitionDirection direction, MediaTime duration) => new(TransitionKind.Slide, duration) { Direction = direction };

    /// <summary>Returns a circle that opens on the second clip.</summary>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition CircleOpen(MediaTime duration) => new(TransitionKind.CircleOpen, duration);

    /// <summary>Returns a circle that closes on the first clip.</summary>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition CircleClose(MediaTime duration) => new(TransitionKind.CircleClose, duration);

    /// <summary>Returns a clock wipe.</summary>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition Clock(MediaTime duration) => new(TransitionKind.Clock, duration);

    /// <summary>Returns a zoom into the first clip.</summary>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition Zoom(MediaTime duration) => new(TransitionKind.Zoom, duration);

    /// <summary>Returns a pixelize.</summary>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition Pixelize(MediaTime duration) => new(TransitionKind.Pixelize, duration);

    /// <summary>Returns a blur from one clip to the next.</summary>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition Blur(MediaTime duration) => new(TransitionKind.Blur, duration);

    /// <summary>Returns an app's own transition. It cannot be saved as JSON.</summary>
    /// <param name="renderer">The transition.</param>
    /// <param name="duration">The overlap.</param>
    /// <returns>The transition.</returns>
    public static Transition Custom(ITransition renderer, MediaTime duration)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return new(TransitionKind.Custom, duration) { Renderer = renderer };
    }

    internal static Transition Of(TransitionKind kind, MediaTime duration) => new(kind, duration);
}
