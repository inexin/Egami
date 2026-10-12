using System.Buffers;

using Egami.Metadata;
using Egami.Operations.Compose;
using Egami.Pixels;

namespace Egami.Operations.Animation;

/// <summary>One frame while a chain with frame steps runs: its plane, its metadata, and whether the chain owns the plane.</summary>
internal sealed class FrameSlot<TPixel>(PixelPlane<TPixel> plane, FrameMetadata metadata, bool owned)
    where TPixel : unmanaged, IPixelType<TPixel>
{
    internal PixelPlane<TPixel> Plane { get; set; } = plane;

    internal FrameMetadata Metadata { get; set; } = metadata;

    internal bool Owned { get; set; } = owned;
}

/// <summary>
/// A step on the whole animation rather than on each frame: it drops, merges, reorders or makes frames. The planner runs the
/// per-frame steps before it on every frame, then this, then the rest; a frame step keeps the frame size.
/// </summary>
internal interface IFrameStep : IRasterOperation
{
    /// <summary>
    /// Returns the new frames. Slots it keeps may be reused as they are; a dropped slot's plane is disposed here when the chain
    /// owns it, and every new plane comes from <paramref name="memory"/> and is owned.
    /// </summary>
    List<FrameSlot<TPixel>> Apply<TPixel>(List<FrameSlot<TPixel>> frames, RasterMetadata metadata, EgamiContext context, BudgetedMemorySource memory, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>;
}

/// <summary>The common part of the frame steps: their frame size is the input's and they never run frame by frame.</summary>
internal abstract class FrameStep : IFrameStep
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        throw new InvalidOperationException($"{GetType().Name} works on whole animations; run it through Apply, Derive or Transcode.");
    }

    public abstract List<FrameSlot<TPixel>> Apply<TPixel>(List<FrameSlot<TPixel>> frames, RasterMetadata metadata, EgamiContext context, BudgetedMemorySource memory, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <summary>Disposes the plane of a slot that is dropped, when the chain owns it.</summary>
    protected static void Drop<TPixel>(FrameSlot<TPixel> slot)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (slot.Owned)
        {
            slot.Plane.Dispose();
        }
    }

    /// <summary>Returns whether every pixel of two frames lies within <paramref name="tolerance"/> by 2.5.1's fuzz distance.</summary>
    protected static bool Same<TPixel>(PixelPlane<TPixel> a, PixelPlane<TPixel> b, float tolerance)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (tolerance == 0)
        {
            for (int y = 0; y < a.Height; y++)
            {
                if (!a.Row(y).SequenceEqual(b.Row(y)))
                {
                    return false;
                }
            }

            return true;
        }

        double threshold = FuzzColor.Threshold(tolerance);
        RgbaF32[] ra = ArrayPool<RgbaF32>.Shared.Rent(a.Width);
        RgbaF32[] rb = ArrayPool<RgbaF32>.Shared.Rent(a.Width);
        try
        {
            for (int y = 0; y < a.Height; y++)
            {
                PixelConvert.Convert<TPixel, RgbaF32>(a.Row(y), ra.AsSpan(0, a.Width));
                PixelConvert.Convert<TPixel, RgbaF32>(b.Row(y), rb.AsSpan(0, a.Width));
                for (int x = 0; x < a.Width; x++)
                {
                    if (new FuzzColor(ra[x]).Distance2(rb[x]) > threshold)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(ra);
            ArrayPool<RgbaF32>.Shared.Return(rb);
        }
    }

    /// <summary>Returns a delay rounded to whole milliseconds.</summary>
    protected static TimeSpan Milliseconds(double ms) => TimeSpan.FromMilliseconds(Math.Round(ms, MidpointRounding.AwayFromZero));
}

/// <summary>
/// ImageMagick's -layers RemoveDups: a frame equal to the one before it (every pixel within the tolerance) is removed and its
/// delay added to the frame that stays.
/// </summary>
internal sealed class MergeDuplicatesStep(float tolerance) : FrameStep
{
    public override List<FrameSlot<TPixel>> Apply<TPixel>(List<FrameSlot<TPixel>> frames, RasterMetadata metadata, EgamiContext context, BudgetedMemorySource memory, CancellationToken cancellationToken)
    {
        List<FrameSlot<TPixel>> kept = [];
        List<FrameSlot<TPixel>> dropped = [];
        for (int i = 0; i < frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i > 0 && Same(frames[i - 1].Plane, frames[i].Plane, tolerance))
            {
                kept[^1].Metadata.Delay += frames[i].Metadata.Delay;
                dropped.Add(frames[i]);
                continue;
            }

            kept.Add(frames[i]);
        }

        // Dropped planes go once every comparison is done, since each frame is compared with the one before it.
        foreach (FrameSlot<TPixel> slot in dropped)
        {
            Drop(slot);
        }

        return kept;
    }
}

/// <summary>
/// ImageMagick's -layers RemoveZero: frames with no delay, which viewers never show, are removed; when every frame has none,
/// nothing is, as ImageMagick does, since the animation then has no timing to keep.
/// </summary>
internal sealed class DropZeroDelayStep : FrameStep
{
    public override List<FrameSlot<TPixel>> Apply<TPixel>(List<FrameSlot<TPixel>> frames, RasterMetadata metadata, EgamiContext context, BudgetedMemorySource memory, CancellationToken cancellationToken)
    {
        if (!frames.Any(f => f.Metadata.Delay > TimeSpan.Zero))
        {
            return frames;
        }

        List<FrameSlot<TPixel>> kept = [];
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].Metadata.Delay > TimeSpan.Zero)
            {
                kept.Add(frames[i]);
            }
            else
            {
                Drop(frames[i]);
            }
        }

        return kept;
    }
}

/// <summary>Divides every delay by a factor, rounding to whole milliseconds.</summary>
internal sealed class ChangeSpeedStep(double factor) : FrameStep
{
    public override List<FrameSlot<TPixel>> Apply<TPixel>(List<FrameSlot<TPixel>> frames, RasterMetadata metadata, EgamiContext context, BudgetedMemorySource memory, CancellationToken cancellationToken)
    {
        foreach (FrameSlot<TPixel> frame in frames)
        {
            frame.Metadata.Delay = Milliseconds(frame.Metadata.Delay.TotalMilliseconds / factor);
        }

        return frames;
    }
}

/// <summary>Plays the animation backwards; each delay moves with its frame.</summary>
internal sealed class ReverseStep : FrameStep
{
    public override List<FrameSlot<TPixel>> Apply<TPixel>(List<FrameSlot<TPixel>> frames, RasterMetadata metadata, EgamiContext context, BudgetedMemorySource memory, CancellationToken cancellationToken)
    {
        frames.Reverse();
        return frames;
    }
}

/// <summary>
/// Resamples the timeline to fixed ticks of 1 / fps seconds, as ffmpeg's fps filter: each tick shows the frame visible at its
/// start, then neighbours showing the same frame merge. Frames are never added, and frames no tick starts on are dropped.
/// Tick boundaries are rounded to whole milliseconds and the last tick ends with the animation, so the total duration is kept.
/// A rate at or above the animation's own (one frame per shortest delay) changes nothing.
/// </summary>
internal sealed class RetimeStep(double framesPerSecond) : FrameStep
{
    public override List<FrameSlot<TPixel>> Apply<TPixel>(List<FrameSlot<TPixel>> frames, RasterMetadata metadata, EgamiContext context, BudgetedMemorySource memory, CancellationToken cancellationToken)
    {
        double tick = 1000 / framesPerSecond;
        double[] start = new double[frames.Count + 1];
        for (int i = 0; i < frames.Count; i++)
        {
            start[i + 1] = start[i] + frames[i].Metadata.Delay.TotalMilliseconds;
        }

        double total = start[^1];
        double shortest = frames.Min(f => f.Metadata.Delay.TotalMilliseconds);
        if (frames.Count < 2 || total <= 0 || (shortest > 0 && tick <= shortest))
        {
            // A rate at or above the animation's own (its shortest delay) changes nothing.
            return frames;
        }

        // Which frame each tick shows, and the tick start times rounded to whole milliseconds.
        List<(int Frame, double Start)> shown = [];
        int frame = 0;
        for (long k = 0; ; k++)
        {
            double t = k * tick;
            if (t >= total)
            {
                break;
            }

            while (start[frame + 1] <= t)
            {
                frame++;
            }

            if (shown.Count == 0 || shown[^1].Frame != frame)
            {
                shown.Add((frame, Math.Round(t, MidpointRounding.AwayFromZero)));
            }
        }

        List<FrameSlot<TPixel>> result = [];
        HashSet<int> used = [.. shown.Select(s => s.Frame)];
        for (int i = 0; i < shown.Count; i++)
        {
            FrameSlot<TPixel> slot = frames[shown[i].Frame];
            double end = i + 1 < shown.Count ? shown[i + 1].Start : total;
            slot.Metadata.Delay = Milliseconds(end - shown[i].Start);
            result.Add(slot);
        }

        for (int i = 0; i < frames.Count; i++)
        {
            if (!used.Contains(i))
            {
                Drop(frames[i]);
            }
        }

        return result;
    }
}

/// <summary>
/// Draws one animation over another. By time, both timelines are cut at every frame boundary of either (boundaries closer than
/// 10 ms snap together, as GIF stores delays in 10 ms units) and each piece becomes a frame: the base frame of that moment with
/// the overlay frame of that moment drawn on it; pieces showing the same pair merge. By index, base frame i takes overlay frame i,
/// as ImageMagick's -layers Composite. The output frame count is checked against MaxFrames and MaxTotalBytes before anything is drawn.
/// </summary>
internal sealed class DrawAnimationStep(Raster overlay, Point position, AnimationOverlayOptions options) : FrameStep
{
    private const double Snap = 10;

    public override List<FrameSlot<TPixel>> Apply<TPixel>(List<FrameSlot<TPixel>> frames, RasterMetadata metadata, EgamiContext context, BudgetedMemorySource memory, CancellationToken cancellationToken)
    {
        int overlayCount = overlay.FrameCount;
        double[] overlayDelays = [.. Enumerable.Range(0, overlayCount).Select(i => overlay.FrameMetadataAt(i).Delay.TotalMilliseconds)];
        List<(int Base, int Overlay, double Length)> pieces = options.Timing == AnimationTiming.ByIndex
            ? ByIndex(frames, overlayCount)
            : ByTime(frames, overlayDelays);

        Size size = new(frames[0].Plane.Width, frames[0].Plane.Height);
        context.Limits.CheckFrameCount(pieces.Count);
        memory.Check((long)pieces.Count * size.Width * size.Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());

        // A still base takes the overlay's loop count, since the result now moves as the overlay does.
        if (frames.Count == 1 && overlayCount > 1)
        {
            metadata.LoopCount = overlay.Metadata.LoopCount;
        }

        using Raster<TPixel> typed = overlay.As<TPixel>();
        List<FrameSlot<TPixel>> result = [];
        try
        {
            foreach ((int b, int o, double length) in pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(size.Width, size.Height, memory);
                result.Add(new FrameSlot<TPixel>(plane, frames[b].Metadata.Copy(), owned: true));
                for (int y = 0; y < size.Height; y++)
                {
                    frames[b].Plane.Row(y).CopyTo(plane.Row(y));
                }

                if (o >= 0)
                {
                    using Raster<TPixel> single = typed.CopyFrame(o);
                    new DrawImageOperation(single, position, options.Opacity, options.Blend).Run(
                        new OperationInput<TPixel>(plane, plane, context, result.Count - 1, cancellationToken) { Memory = memory });
                }

                if (options.Timing == AnimationTiming.ByTime)
                {
                    result[^1].Metadata.Delay = Milliseconds(length);
                }
            }
        }
        catch
        {
            foreach (FrameSlot<TPixel> slot in result)
            {
                slot.Plane.Dispose();
            }

            throw;
        }

        foreach (FrameSlot<TPixel> slot in frames)
        {
            Drop(slot);
        }

        return result;
    }

    /// <summary>Base frame i with overlay frame i, repeating the overlay as Repeat allows; the base's delays stay.</summary>
    private List<(int, int, double)> ByIndex<TPixel>(List<FrameSlot<TPixel>> frames, int overlayCount)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        long shown = options.Repeat == 0 ? long.MaxValue : (long)options.Repeat * overlayCount;
        return [.. frames.Select((f, i) => (i, i < shown ? i % overlayCount : -1, f.Metadata.Delay.TotalMilliseconds))];
    }

    /// <summary>Cuts both timelines at every boundary and returns each piece's base frame, overlay frame (−1 for none) and length.</summary>
    private List<(int, int, double)> ByTime<TPixel>(List<FrameSlot<TPixel>> frames, double[] overlayDelays)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        double cycle = overlayDelays.Sum();
        double begin = options.Start.TotalMilliseconds;
        bool stillOverlay = overlayDelays.Length == 1 || cycle <= 0;
        int repeat = options.Repeat;

        // The base's timeline; a still base lasts as long as the overlay plays.
        double[] baseStart = new double[frames.Count + 1];
        for (int i = 0; i < frames.Count; i++)
        {
            baseStart[i + 1] = baseStart[i] + frames[i].Metadata.Delay.TotalMilliseconds;
        }

        double total = baseStart[^1];
        if (frames.Count == 1)
        {
            total = stillOverlay ? Math.Max(total, begin) : begin + (cycle * Math.Max(repeat, 1));
            baseStart[1] = total;
        }

        double overlayEnd = stillOverlay || repeat == 0 ? total : Math.Min(total, begin + (cycle * repeat));
        SortedSet<double> cuts = [0];
        for (int i = 1; i < frames.Count; i++)
        {
            cuts.Add(baseStart[i]);
        }

        if (begin < total)
        {
            cuts.Add(begin);
            cuts.Add(overlayEnd);
            if (!stillOverlay)
            {
                for (double t = begin; t < overlayEnd; t += cycle)
                {
                    double at = t;
                    foreach (double d in overlayDelays)
                    {
                        if (at > begin && at < overlayEnd)
                        {
                            cuts.Add(at);
                        }

                        at += d;
                    }
                }
            }
        }

        // Boundaries closer than 10 ms to the one before snap onto it.
        List<double> points = [];
        foreach (double c in cuts)
        {
            if (c >= total)
            {
                break;
            }

            if (points.Count == 0 || c - points[^1] >= Snap)
            {
                points.Add(c);
            }
        }

        List<(int Base, int Overlay, double Length)> pieces = [];
        for (int p = 0; p < points.Count; p++)
        {
            double t = points[p];
            double end = p + 1 < points.Count ? points[p + 1] : total;
            int b = Array.FindLastIndex(baseStart, frames.Count - 1, frames.Count, s => s <= t);
            int o = -1;
            if (t >= begin && t < overlayEnd)
            {
                o = 0;
                if (!stillOverlay)
                {
                    double local = (t - begin) % cycle;
                    double acc = 0;
                    for (o = 0; o < overlayDelays.Length - 1 && acc + overlayDelays[o] <= local; o++)
                    {
                        acc += overlayDelays[o];
                    }
                }
            }

            if (pieces.Count > 0 && pieces[^1].Base == b && pieces[^1].Overlay == o)
            {
                pieces[^1] = (b, o, pieces[^1].Length + (end - t));
            }
            else
            {
                pieces.Add((b, o, end - t));
            }
        }

        return pieces;
    }
}
