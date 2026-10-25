using Egami.Operations;
using Egami.Pixels;

namespace Egami.Video.Editing;

/// <summary>Where an <see cref="IFrameEffect"/> is: the frame's time and how far through its window it is.</summary>
/// <param name="Time">The frame's time in the edit.</param>
/// <param name="LocalTime">The frame's time from the start of what the effect is attached to: the edit, a clip or a layer.</param>
/// <param name="Progress">How far through the effect's window the frame is, from 0 at its start towards 1 at its end.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-effects"/>
/// </example>
public readonly record struct FrameEffectContext(MediaTime Time, MediaTime LocalTime, double Progress);

/// <summary>
/// An app's own effect for <see cref="EditEffect.Of"/>, which knows each frame's time. It changes the frame in place. The same
/// instance runs on every frame, so keep it stateless.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-effects"/>
/// </example>
public interface IFrameEffect
{
    /// <summary>Changes one frame.</summary>
    /// <param name="frame">The frame, which the effect changes in place; it keeps its size.</param>
    /// <param name="context">The frame's time and the progress through the effect's window.</param>
    void Apply(Raster<Rgba8> frame, FrameEffectContext context);
}

/// <summary>
/// Egami operations or an app's own effect on the frames of a <see cref="VideoEdit"/>, a <see cref="Clip"/> or a
/// <see cref="Layer"/>, optionally only inside a time window (<see cref="Between"/>). Effects keep the frame size.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-effects"/>
/// </example>
public sealed record EditEffect
{
    private EditEffect(Action<RasterOps>? build, IFrameEffect? effect, string? name)
    {
        Build = build;
        Effect = effect;
        Name = name;
    }

    /// <summary>Gets the time the effect starts, from the start of what it is attached to. Default zero.</summary>
    public MediaTime Start { get; init; } = MediaTime.Zero;

    /// <summary>Gets the time the effect ends, or <see langword="null"/> for the end.</summary>
    public MediaTime? End { get; init; }

    /// <summary>
    /// Gets a name for the effect, which a JSON edit file stores in its place; the CLI uses egami step text such as
    /// <c>blur 8</c>. <see cref="VideoEdit.FromJson"/> turns it back into an effect through a resolver.
    /// </summary>
    public string? Name { get; init; }

    internal Action<RasterOps>? Build { get; }

    internal IFrameEffect? Effect { get; }

    /// <summary>Returns an effect that runs Egami operations on each frame.</summary>
    /// <param name="build">Adds the operations, as for <see cref="Raster{TPixel}.Apply"/>.</param>
    /// <param name="name">A name to store in JSON edit files, or <see langword="null"/>.</param>
    /// <returns>The effect.</returns>
    public static EditEffect Apply(Action<RasterOps> build, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new EditEffect(build, null, name);
    }

    /// <summary>Returns an effect that runs one operation, such as an app's own <see cref="IRasterOperation"/>, on each frame.</summary>
    /// <param name="operation">The operation; it must keep the frame size.</param>
    /// <param name="name">A name to store in JSON edit files, or <see langword="null"/>.</param>
    /// <returns>The effect.</returns>
    public static EditEffect Apply(IRasterOperation operation, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new EditEffect(ops => ops.Add(operation), null, name);
    }

    /// <summary>Returns an app's own effect, which gets each frame's time.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="name">A name to store in JSON edit files, or <see langword="null"/>.</param>
    /// <returns>The effect.</returns>
    public static EditEffect Of(IFrameEffect effect, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return new EditEffect(null, effect, name);
    }

    /// <summary>Returns the effect limited to a time window.</summary>
    /// <param name="start">When it starts, from the start of what it is attached to.</param>
    /// <param name="end">When it ends, or <see langword="null"/> for the end.</param>
    /// <returns>The effect.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The start is negative, or the end is not after it.</exception>
    public EditEffect Between(MediaTime start, MediaTime? end = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, MediaTime.Zero);
        if (end is MediaTime e && e <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), e, "The end must be after the start.");
        }

        return this with { Start = start, End = end };
    }

    /// <summary>Runs the effect on a frame at a local time, if the time is inside its window.</summary>
    internal void Run(Raster<Rgba8> frame, MediaTime time, MediaTime local, MediaTime length)
    {
        MediaTime end = End ?? length;
        if (local < Start || local >= end)
        {
            return;
        }

        double span = Times.Sub(end, Start).Seconds;
        double progress = span <= 0 ? 0 : Times.Sub(local, Start).Seconds / span;
        if (Effect is not null)
        {
            Effect.Apply(frame, new FrameEffectContext(time, local, progress));
        }
        else
        {
            Size size = new(frame.Width, frame.Height);
            frame.Apply(Build!);
            if (frame.Width != size.Width || frame.Height != size.Height)
            {
                throw new InvalidOperationException($"An edit effect changed the frame size from {size.Width}x{size.Height} to {frame.Width}x{frame.Height}; effects must keep it.");
            }
        }
    }
}
