using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

using Egami.Pixels;
using Egami.Svg.Animation;
using Egami.Svg.Reading;
using Egami.Svg.Resolving;
using Egami.Svg.Styling;

namespace Egami.Svg;

/// <content>Drawing a document's animations as frames.</content>
public sealed partial class SvgDocument
{
    /// <summary>
    /// Draws the document's animations into one animated raster, ready to save as GIF, APNG, WebP or AVIF: a frame every
    /// 1/<see cref="SvgAnimationOptions.FrameRate"/> seconds from <see cref="SvgAnimationOptions.Start"/> for
    /// <see cref="SvgAnimationOptions.Length"/>, or, when that is not set, for one loop period when every animation loops,
    /// else up to and including the end of the last animation, else <see cref="SvgAnimationOptions.MaxLength"/>. Each frame's
    /// <see cref="Metadata.FrameMetadata.Delay"/> is how long it lasts; frames equal to the one before are merged into it
    /// unless <see cref="SvgAnimationOptions.MergeDuplicates"/> is off. The raster loops forever
    /// (<see cref="Metadata.RasterMetadata.LoopCount"/> 0) when the document has a <see cref="SvgAnimationInfo.LoopPeriod"/>
    /// that was not cut at the maximum length, else it plays once. A document without animations gives one frame. Frames are drawn on as many threads as the context
    /// allows.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the raster.</typeparam>
    /// <param name="options">The frame rate, start, length and drawing options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The raster, with one frame per distinct moment.</returns>
    /// <exception cref="LimitExceededException">
    /// A frame would break the context's size limits, or all the frames, before merging, would break
    /// <see cref="SafetyLimits.MaxFrames"/> or <see cref="SafetyLimits.MaxTotalBytes"/>. Checked before anything is drawn.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="rasterize-animation"/>
    /// </example>
    public Raster<TPixel> RasterizeAnimation<TPixel>(SvgAnimationOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        options ??= SvgAnimationOptions.Default;
        SvgRenderOptions render = options.Render ?? SvgRenderOptions.Default;
        EgamiContext context = render.Context ?? Options.EffectiveContext;
        return RasterizePlanned<TPixel>(Plan(options, context), render, context, options.MergeDuplicates);
    }

    /// <summary>Draws planned frames into one animated raster, after checking the size and frame limits.</summary>
    internal Raster<TPixel> RasterizePlanned<TPixel>(SvgFramePlan plan, SvgRenderOptions render, EgamiContext context, bool merge)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        (int w, int h) = RasterSize(render, context.Limits);
        CheckFrames<TPixel>(plan.Count, plan.Count, w, h, context.Limits);
        return Collect(Frames<TPixel>(plan, w, h, context, render, merge), plan.Loops);
    }

    /// <summary>
    /// Draws the document's animations frame by frame, as <see cref="RasterizeAnimation{TPixel}(SvgAnimationOptions?)"/> plans
    /// them, without holding them all: each is drawn as it is needed (a few ahead, one per thread the context allows) and
    /// handed over with the moment it shows and how long it lasts, for a video encoder or anything else that takes frames one
    /// at a time. Turn <see cref="SvgAnimationOptions.MergeDuplicates"/> off for a constant frame rate. Each frame's raster
    /// belongs to the caller, who disposes it; frames not yet taken when the enumeration stops are disposed.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type of the frames.</typeparam>
    /// <param name="options">The frame rate, start, length and drawing options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The frames, in order.</returns>
    /// <exception cref="LimitExceededException">
    /// A frame would break the context's size limits, the frame count <see cref="SafetyLimits.MaxFrames"/>, or the frames drawn
    /// at once <see cref="SafetyLimits.MaxTotalBytes"/>. Checked when this is called, before anything is drawn.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-frames"/>
    /// </example>
    public IEnumerable<SvgFrame<TPixel>> RenderFrames<TPixel>(SvgAnimationOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        options ??= SvgAnimationOptions.Default;
        SvgRenderOptions render = options.Render ?? SvgRenderOptions.Default;
        EgamiContext context = render.Context ?? Options.EffectiveContext;
        return RenderPlanned<TPixel>(Plan(options, context), render, context, options.MergeDuplicates);
    }

    /// <summary>Streams planned frames, after checking the size, the frame count and the frames held at once.</summary>
    internal IEnumerable<SvgFrame<TPixel>> RenderPlanned<TPixel>(SvgFramePlan plan, SvgRenderOptions render, EgamiContext context, bool merge)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        (int w, int h) = RasterSize(render, context.Limits);
        CheckFrames<TPixel>(plan.Count, Math.Min(plan.Count, Threads(plan.Count, context) + 1), w, h, context.Limits);
        return Frames<TPixel>(plan, w, h, context, render, merge);
    }

    /// <summary>
    /// Returns a static copy of the document as it looks at one moment: the values SMIL and CSS animations give then are
    /// written into the copy (SMIL's into the attributes they animate, CSS animations' into each element's <c>style</c>
    /// attribute, geometry such as <c>r</c> and <c>d</c> into its attribute, animateMotion's movement into the element's
    /// transform), elements a discard has removed by then are left out, and the animation elements and the elements' CSS
    /// animations are taken away. Drawing the copy gives the picture <see cref="SvgRenderOptions.Time"/> gives at that moment;
    /// saving it gives an SVG any viewer shows as that picture. This document is not changed.
    /// </summary>
    /// <param name="time">The moment since the document began; not negative.</param>
    /// <returns>The static copy, with this document's load options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="time"/> is negative.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="freeze"/>
    /// </example>
    public SvgDocument Freeze(TimeSpan time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);
        List<SvgElement> source = [Root, .. Root.Descendants()];
        SvgElement root = (SvgElement)Root.Clone();
        List<SvgElement> copy = [root, .. root.Descendants()];
        Dictionary<SvgElement, SvgElement> map = new(IdentityComparer.Instance);
        for (int i = 0; i < source.Count; i++)
        {
            map[source[i]] = copy[i];
        }

        SvgDocument frozen = new(root, Options);
        if (Frame(time, _treeLanguages) is not SvgAnimationFrame frame)
        {
            RemoveAnimationElements(source, map);
            return frozen;
        }

        // The values of this moment, as a draw at this time resolves them.
        SvgReport report = new(strict: false);
        SvgResolver resolver = new(this, report, _treeLanguages, frame) { MotionTransforms = new(IdentityComparer.Instance) };
        resolver.Resolve();

        // Elements of other documents (SVG images) are left alone: only this document's are copied.
        foreach (SvgElement element in frame.Stored)
        {
            if (map.TryGetValue(element, out SvgElement? target))
            {
                foreach ((string name, string value) in frame.For(element)!)
                {
                    // An attributeName no XML attribute can have (a typo such as "fi~l", another prefix) animates nothing.
                    if (IsAttributeName(name))
                    {
                        target.Attributes[name] = value;
                    }
                }
            }
        }

        if (resolver.Styler.AnimatedValues is { } animated)
        {
            foreach ((SvgElement element, Dictionary<string, string> values) in animated)
            {
                if (!map.TryGetValue(element, out SvgElement? target))
                {
                    continue;
                }

                StringBuilder style = new();
                foreach ((string name, string value) in values)
                {
                    if (SvgProperties.Geometry.TryGetValue(name, out string[]? owners) && owners.Contains(element.Name))
                    {
                        target.Attributes[name] = name == "d" ? PathData(value) : value;
                    }
                    else
                    {
                        style.Append(name).Append(": ").Append(value).Append("; ");
                    }
                }

                AppendStyle(target, style.ToString());
            }
        }

        foreach ((SvgElement element, Matrix3x2 m) in resolver.MotionTransforms)
        {
            if (!map.TryGetValue(element, out SvgElement? target))
            {
                continue;
            }

            // The whole transform of this moment, motion included, in place of what the element's style would make of it.
            AppendStyle(target, string.Create(CultureInfo.InvariantCulture, $"transform: matrix({m.M11:R}, {m.M12:R}, {m.M21:R}, {m.M22:R}, {m.M31:R}, {m.M32:R}) !important; transform-origin: 0 0 !important; translate: none !important; rotate: none !important; scale: none !important; offset-path: none !important; "));
        }

        lock (_timelineLock)
        {
            foreach ((SvgElement element, _) in Timeline(time.TotalSeconds).CssAnimations)
            {
                if (map.TryGetValue(element, out SvgElement? target) && !(target.Attributes["style"]?.Contains("animation-name: none !important", StringComparison.Ordinal) ?? false))
                {
                    AppendStyle(target, "animation-name: none !important; ");
                }
            }
        }

        foreach (SvgElement element in source)
        {
            if (element != Root && frame.IsDiscarded(element) && map[element].Parent is SvgElement parent)
            {
                parent.Children.Remove(map[element]);
            }
        }

        RemoveAnimationElements(source, map);
        return frozen;
    }

    // Takes the SMIL animation elements out of the copy.
    private void RemoveAnimationElements(List<SvgElement> source, Dictionary<SvgElement, SvgElement> map)
    {
        foreach (SvgElement element in source)
        {
            if (element.Name is "animate" or "set" or "animateTransform" or "animateMotion" or "discard" && element.IsSvgOrBare(this)
                && map[element].Parent is SvgElement parent)
            {
                parent.Children.Remove(map[element]);
            }
        }
    }

    // Adds declarations at the end of an element's style attribute, where they win over the same properties before them.
    private static bool IsAttributeName(string name)
    {
        if (name.Length == 0 || !XmlConvert.IsStartNCNameChar(name[0]))
        {
            return false;
        }

        foreach (char c in name.AsSpan(1))
        {
            if (!XmlConvert.IsNCNameChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendStyle(SvgElement element, string declarations)
    {
        if (declarations.Length == 0)
        {
            return;
        }

        string? style = element.Attributes["style"]?.TrimEnd();
        element.Attributes["style"] = string.IsNullOrEmpty(style) ? declarations.TrimEnd() : style.TrimEnd(';') + "; " + declarations.TrimEnd();
    }

    // The path data of a CSS path() value, as the d attribute holds it.
    private static string PathData(string value)
    {
        string v = value.Trim();
        if (v.StartsWith("path(", StringComparison.OrdinalIgnoreCase) && v.EndsWith(')'))
        {
            v = v[5..^1].Trim();
            if (v.Length >= 2 && v[0] is '"' or '\'' && v[^1] == v[0])
            {
                v = v[1..^1];
            }
        }

        return v;
    }

    /// <summary>
    /// Gathers frames into one animated raster: the first frame's raster becomes the result, the others are copied in and
    /// disposed; each frame's delay is how long it lasts, and the raster loops forever when the document loops.
    /// </summary>
    internal static Raster<TPixel> Collect<TPixel>(IEnumerable<SvgFrame<TPixel>> frames, bool loops)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel>? result = null;
        try
        {
            foreach (SvgFrame<TPixel> frame in frames)
            {
                if (result is null)
                {
                    result = frame.Raster;
                    result.Frames[0].Metadata.Delay = frame.Duration;
                    continue;
                }

                using (frame.Raster)
                {
                    result.Frames.Add(frame.Raster.Frames[0]).Metadata.Delay = frame.Duration;
                }
            }

            result!.Metadata.LoopCount = loops ? 0 : 1;
            return result;
        }
        catch
        {
            result?.Dispose();
            throw;
        }
    }

    /// <summary>Plans the frames, and reports to the document's diagnostics when the length was cut at the maximum.</summary>
    internal SvgFramePlan Plan(SvgAnimationOptions options, EgamiContext context)
    {
        SvgFramePlan plan = SvgFramePlan.Of(Animation, options);
        if (plan.Warning is string warning)
        {
            SvgReport report = new(strict: false);
            report.Note("Svg.AnimationLength", warning);
            report.Deliver(context.Diagnostics is null ? Options.EffectiveContext : context);
        }

        return plan;
    }

    /// <summary>Checks the frame count against MaxFrames, and the frames held at once against MaxTotalBytes.</summary>
    private static void CheckFrames<TPixel>(long count, long held, int width, int height, SafetyLimits limits)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (count > limits.MaxFrames)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxFrames), limits.MaxFrames, count);
        }

        long frameBytes = (long)width * height * Unsafe.SizeOf<TPixel>();
        if (held > limits.MaxTotalBytes / frameBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limits.MaxTotalBytes, held > long.MaxValue / frameBytes ? long.MaxValue : held * frameBytes);
        }
    }

    /// <summary>How many frames are drawn at once: one per thread the context allows, no more than there are frames after the first.</summary>
    private static int Threads(long count, EgamiContext context) => (int)Math.Max(1, Math.Min(context.MaxParallelism, count - 1));

    /// <summary>
    /// Draws the planned frames in order. The first is drawn alone, which builds the timeline and the caches the others share;
    /// the rest in batches, one frame per thread. A frame equal to the one before merges into it when <paramref name="merge"/>
    /// is set, so a frame is handed over only once the next different one is drawn.
    /// </summary>
    private IEnumerable<SvgFrame<TPixel>> Frames<TPixel>(SvgFramePlan plan, int w, int h, EgamiContext context, SvgRenderOptions render, bool merge)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int count = (int)plan.Count;
        int threads = Threads(count, context);
        Raster<TPixel>?[] batch = new Raster<TPixel>?[threads];
        SvgFrame<TPixel> pending = default;
        bool owned = false;
        try
        {
            pending = new SvgFrame<TPixel>(RasterizeAt<TPixel>(w, h, context, render, plan.TimeOf(0)), plan.TimeOf(0), plan.DurationOf(0));
            owned = true;
            for (int next = 1; next < count; next += threads)
            {
                int n = Math.Min(threads, count - next);
                DrawBatch(batch, n, next, plan, w, h, context, render);
                for (int k = 0; k < n; k++)
                {
                    Raster<TPixel> raster = batch[k]!;
                    batch[k] = null;
                    TimeSpan duration = plan.DurationOf(next + k);
                    if (merge && SamePixels(pending.Raster, raster))
                    {
                        raster.Dispose();
                        pending = pending with { Duration = pending.Duration + duration };
                        continue;
                    }

                    SvgFrame<TPixel> done = pending;
                    pending = new SvgFrame<TPixel>(raster, plan.TimeOf(next + k), duration);
                    yield return done;
                }
            }

            owned = false;
            yield return pending;
        }
        finally
        {
            if (owned)
            {
                pending.Raster.Dispose();
            }

            foreach (Raster<TPixel>? raster in batch)
            {
                raster?.Dispose();
            }
        }
    }

    /// <summary>Draws frames <paramref name="first"/> to <paramref name="first"/> + <paramref name="n"/> − 1 into the batch, on up to n threads.</summary>
    private void DrawBatch<TPixel>(Raster<TPixel>?[] batch, int n, int first, SvgFramePlan plan, int w, int h, EgamiContext context, SvgRenderOptions render)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (n == 1)
        {
            batch[0] = RasterizeAt<TPixel>(w, h, context, render, plan.TimeOf(first));
            return;
        }

        try
        {
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = n }, k => batch[k] = RasterizeAt<TPixel>(w, h, context, render, plan.TimeOf(first + k)));
        }
        catch (AggregateException e) when (e.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
        }
    }

    /// <summary>Returns whether two rasters of one size hold the same pixels.</summary>
    private static bool SamePixels<TPixel>(Raster<TPixel> a, Raster<TPixel> b)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int y = 0; y < a.Height; y++)
        {
            if (!MemoryMarshal.AsBytes(a.Row(y)).SequenceEqual(MemoryMarshal.AsBytes(b.Row(y))))
            {
                return false;
            }
        }

        return true;
    }
}
