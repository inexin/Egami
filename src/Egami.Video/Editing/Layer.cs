using Egami.Operations;
using Egami.Pixels;

namespace Egami.Video.Editing;

/// <summary>
/// A chroma key for <see cref="Layer.Key"/>: pixels near <see cref="Color"/> in the CbCr plane become transparent with
/// Egami's <see cref="RasterOps.ChromaKey"/>, then <see cref="RasterOps.Despill"/> takes the screen's colour out of the
/// edges and see-through pixels that are left.
/// </summary>
/// <param name="Color">The screen colour, such as a studio green.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-chroma-key"/>
/// </example>
public sealed record ChromaKey(Rgba8 Color)
{
    /// <summary>Gets how close to the colour a pixel must be to be keyed out, as a CbCr distance from 0 to 1. Default 0.1.</summary>
    public float Similarity { get; init; } = 0.1f;

    /// <summary>Gets the width of the soft edge beyond <see cref="Similarity"/>, from 0 (hard) to 1. Default 0.05.</summary>
    public float Blend { get; init; } = 0.05f;

    /// <summary>Gets how much spill to remove, from 0 (no despill) to 1. Default 0.5.</summary>
    public float Despill { get; init; } = 0.5f;

    /// <summary>Gets the spill colour: green or blue. Default from <see cref="Color"/>: blue when its blue is above its green.</summary>
    public DespillColor? SpillColor { get; init; }

    /// <summary>Gets how far despill reaches into opaque pixels, from 0 to 1. Default 0.</summary>
    public float Expand { get; init; }

    internal void Apply(Raster<Rgba8> frame)
    {
        DespillColor screen = SpillColor ?? (Color.B > Color.G ? DespillColor.Blue : DespillColor.Green);
        frame.Apply(ops =>
        {
            ops.ChromaKey(Color, Similarity, Blend);
            if (Despill > 0)
            {
                ops.Despill(screen, Despill, Expand);
            }
        });
    }
}

/// <summary>A property of a <see cref="Layer"/> that <see cref="Layer.Animate"/> moves over time.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-layers"/>
/// </example>
public enum LayerProperty
{
    /// <summary>An offset to the right in pixels, added to the placement.</summary>
    X,

    /// <summary>An offset down in pixels, added to the placement.</summary>
    Y,

    /// <summary>A size factor around the layer's centre, 1 for its placed size.</summary>
    Scale,

    /// <summary>A clockwise rotation in degrees around the layer's centre.</summary>
    Rotation,

    /// <summary>An opacity factor from 0 to 1, multiplied with <see cref="Layer.OpacityAmount"/>.</summary>
    Opacity,
}

/// <summary>
/// A clip drawn over the main track of a <see cref="VideoEdit"/> inside a time window: a logo, a picture-in-picture, a lower
/// third or a keyed presenter. It has a place, an opacity, a blend mode, fades, an optional <see cref="ChromaKey"/> and
/// keyframed motion. Layers draw in order, the last on top. The methods return a changed copy, so they chain.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-layers"/>
/// </example>
public sealed record Layer
{
    private Layer(Clip clip)
    {
        Clip = clip;
    }

    /// <summary>Gets the clip the layer shows.</summary>
    public Clip Clip { get; init; }

    /// <summary>Gets the rectangle the layer is scaled into, or <see langword="null"/> to place it by <see cref="Anchor"/>.</summary>
    public Rect? Area { get; init; }

    /// <summary>Gets the canvas corner, edge or centre the layer sits at when it has no <see cref="Area"/>. Default the centre.</summary>
    public Anchor Anchor { get; init; } = Anchor.Center;

    /// <summary>Gets the space in pixels between the layer and the canvas edge it is anchored to. Default 0.</summary>
    public int Margin { get; init; }

    /// <summary>Gets the size of an anchored layer as a factor of the clip's own size. Default 1.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Gets the opacity from 0 to 1. Default 1.</summary>
    public float OpacityAmount { get; init; } = 1;

    /// <summary>Gets how the layer blends with what is under it. Default <see cref="BlendMode.SourceOver"/>.</summary>
    public BlendMode BlendMode { get; init; } = BlendMode.SourceOver;

    /// <summary>Gets the edit time the layer starts. Default zero.</summary>
    public MediaTime Start { get; init; } = MediaTime.Zero;

    /// <summary>Gets the edit time the layer ends, or <see langword="null"/> for when its clip ends.</summary>
    public MediaTime? End { get; init; }

    /// <summary>Gets whether the layer holds its clip's last frame until <see cref="End"/> or the edit's end, instead of disappearing when the clip ends.</summary>
    public bool HoldsLastFrame { get; init; }

    /// <summary>Gets how long the layer fades in (its opacity, and its audio). Zero for none.</summary>
    public MediaTime FadeInDuration { get; init; } = MediaTime.Zero;

    /// <summary>Gets how long the layer fades out. Zero for none.</summary>
    public MediaTime FadeOutDuration { get; init; } = MediaTime.Zero;

    /// <summary>Gets the chroma key run on the layer's frames, or <see langword="null"/> for none.</summary>
    public ChromaKey? ChromaKey { get; init; }

    /// <summary>Gets the keyframed properties; times are from the layer's start.</summary>
    public IReadOnlyDictionary<LayerProperty, IReadOnlyList<Keyframe>> Animations { get; init; } = new Dictionary<LayerProperty, IReadOnlyList<Keyframe>>();

    /// <summary>Gets the effects that run on the layer's frames before it is placed, timed from the layer's start.</summary>
    public IReadOnlyList<EditEffect> Effects { get; init; } = [];

    /// <summary>Returns a layer that shows a clip over the whole edit, centred at its own size.</summary>
    /// <param name="clip">The clip. Its audio is mixed in unless the clip is muted.</param>
    /// <returns>The layer.</returns>
    public static Layer Of(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return new Layer(clip);
    }

    /// <summary>Returns the layer scaled into a rectangle of the canvas.</summary>
    /// <param name="area">The rectangle, in canvas pixels.</param>
    /// <returns>The layer.</returns>
    public Layer Place(Rect area)
    {
        if (area.Width < 1 || area.Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(area), area, "The area must be at least 1 by 1.");
        }

        return this with { Area = area };
    }

    /// <summary>Returns the layer anchored to a corner, an edge or the centre of the canvas.</summary>
    /// <param name="anchor">Where it sits.</param>
    /// <param name="margin">The space to the canvas edges in pixels.</param>
    /// <param name="scale">Its size as a factor of the clip's own size.</param>
    /// <returns>The layer.</returns>
    public Layer Place(Anchor anchor, int margin = 0, double scale = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(margin);
        if (!(scale > 0) || !double.IsFinite(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be above 0.");
        }

        return this with { Area = null, Anchor = anchor, Margin = margin, Scale = scale };
    }

    /// <summary>Returns the layer with an opacity.</summary>
    /// <param name="amount">From 0 (invisible) to 1 (opaque).</param>
    /// <returns>The layer.</returns>
    public Layer Opacity(float amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(amount, 1f);
        return this with { OpacityAmount = amount };
    }

    /// <summary>Returns the layer with a blend mode.</summary>
    /// <param name="mode">How it blends with what is under it.</param>
    /// <returns>The layer.</returns>
    public Layer Blend(BlendMode mode) => this with { BlendMode = mode };

    /// <summary>Returns the layer shown only from one edit time to another.</summary>
    /// <param name="start">When it starts; its clip starts here.</param>
    /// <param name="end">When it ends, or <see langword="null"/> for when its clip ends.</param>
    /// <returns>The layer.</returns>
    public Layer Between(MediaTime start, MediaTime? end = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, MediaTime.Zero);
        if (end is MediaTime e && e <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), e, "The end must be after the start.");
        }

        return this with { Start = start, End = end };
    }

    /// <summary>Returns the layer holding its clip's last frame to its end.</summary>
    /// <returns>The layer.</returns>
    public Layer HoldLastFrame() => this with { HoldsLastFrame = true };

    /// <summary>Returns the layer fading in at its start.</summary>
    /// <param name="duration">How long the fade lasts.</param>
    /// <returns>The layer.</returns>
    public Layer FadeIn(MediaTime duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, MediaTime.Zero);
        return this with { FadeInDuration = duration };
    }

    /// <summary>Returns the layer fading out at its end.</summary>
    /// <param name="duration">How long the fade lasts.</param>
    /// <returns>The layer.</returns>
    public Layer FadeOut(MediaTime duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, MediaTime.Zero);
        return this with { FadeOutDuration = duration };
    }

    /// <summary>Returns the layer with a chroma key on its frames.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The layer.</returns>
    public Layer Key(ChromaKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return this with { ChromaKey = key };
    }

    /// <summary>Returns the layer with a property moving through keyframes, timed from the layer's start.</summary>
    /// <param name="property">The property.</param>
    /// <param name="keyframes">The keyframes; at least one. Before the first and after the last the value holds.</param>
    /// <returns>The layer.</returns>
    public Layer Animate(LayerProperty property, params Keyframe[] keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        if (keyframes.Length == 0)
        {
            throw new ArgumentException("An animation needs at least one keyframe.", nameof(keyframes));
        }

        Dictionary<LayerProperty, IReadOnlyList<Keyframe>> animations = new(Animations) { [property] = [.. keyframes.OrderBy(k => k.Time)] };
        return this with { Animations = animations };
    }

    /// <summary>Returns the layer with one more effect on its frames.</summary>
    /// <param name="effect">The effect, timed from the layer's start.</param>
    /// <returns>The layer.</returns>
    public Layer WithEffect(EditEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return this with { Effects = [.. Effects, effect] };
    }

    /// <summary>Returns an animated property's value at a time from the layer's start, or the fallback when it is not animated.</summary>
    internal double ValueAt(LayerProperty property, MediaTime local, double fallback)
    {
        if (!Animations.TryGetValue(property, out IReadOnlyList<Keyframe>? keys) || keys.Count == 0)
        {
            return fallback;
        }

        if (local <= keys[0].Time)
        {
            return keys[0].Value;
        }

        for (int i = 1; i < keys.Count; i++)
        {
            if (local < keys[i].Time)
            {
                Keyframe a = keys[i - 1], b = keys[i];
                double t = Times.Sub(local, a.Time).Seconds / Times.Sub(b.Time, a.Time).Seconds;
                return a.Value + ((b.Value - a.Value) * Keyframe.Ease(a.Easing, t));
            }
        }

        return keys[^1].Value;
    }
}
