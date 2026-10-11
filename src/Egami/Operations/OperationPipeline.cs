using Egami.Metadata;
using Egami.Operations.Color;
using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations;

/// <summary>
/// Runs a recorded chain: records it, plans it, then runs each step on every frame, allocating a new plane only for steps that
/// change the size. A chain holds at most two full planes per frame at once.
/// </summary>
internal static class OperationPipeline
{
    /// <summary>Records the chain from the caller's lambda and plans it, for an image with <paramref name="metadata"/>.</summary>
    /// <returns>The planned steps and the size after each one.</returns>
    internal static Plan Build(Action<RasterOps> build, Size size, RasterMetadata? metadata = null, bool describe = false)
    {
        return Build(build, size, metadata?.Exif?.Orientation ?? ImageOrientation.Normal, metadata is null ? null : metadata.GetColorProfile, metadata?.Hdr, describe);
    }

    /// <summary>
    /// Records and plans the chain for an image stored with <paramref name="orientation"/>, whose pixels are in the encoding
    /// <paramref name="colorProfile"/> returns (sRGB when <see langword="null"/>); it is asked only if the chain converts color.
    /// </summary>
    /// <remarks>With <paramref name="describe"/>, <see cref="Plan.Notes"/> says what the planner changed, for an Info event.</remarks>
    internal static Plan Build(
        Action<RasterOps> build, Size size, ImageOrientation orientation, Func<Colors.ColorProfile>? colorProfile = null, Colors.HdrMetadata? hdr = null, bool describe = false)
    {
        ArgumentNullException.ThrowIfNull(build);
        RasterOps ops = new();
        build(ops);
        List<string>? notes = describe ? [] : null;
        List<IRasterOperation> steps = Planner.Plan(ops.Steps, orientation, notes);
        Colors.ColorProfile? current = null;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] is ILinearLight { UsesLinearLight: true } linear && colorProfile is not null)
            {
                // Linear light is the raster's own curve at this point in the chain (sRGB's for CMYK and table profiles).
                current ??= colorProfile();
                steps[i] = linear.WithCurve(Colors.LinearCurve.For(current.Transfer));
            }
            else if (steps[i] is Color.ConvertColorOperation convert)
            {
                current ??= colorProfile?.Invoke() ?? Colors.ColorProfile.Srgb;
                steps[i] = convert.Bind(current, hdr);
                hdr = convert.Target.IsHdr ? hdr : null;
                current = convert.Target;
            }
            else if (steps[i] is Color.ToneMapOperation tone)
            {
                steps[i] = tone.Bind(hdr);
                hdr = null;
            }
            else if (steps[i] is Scopes.IScope scope)
            {
                // A scope's linear-light steps take the curve the raster has at this point; a scope cannot change it.
                steps[i] = scope.Bind(() => colorProfile is null ? null : Colors.LinearCurve.For((current ??= colorProfile()).Transfer));
            }
        }

        Plan plan = new(steps, new Size[steps.Count]) { Input = size, Notes = notes is { Count: > 0 } ? $"The chain of {ops.Steps.Count} steps runs as {steps.Count}: {string.Join(" ", notes)}" : null };
        plan.ComputeSizes(0);
        return plan;
    }

    /// <summary>
    /// Runs the plan on one frame. The first step reads <paramref name="source"/>; when <paramref name="inPlace"/> is
    /// <see langword="false"/>, <paramref name="source"/> is never written or disposed, as <c>Derive</c> requires.
    /// </summary>
    /// <returns>The frame's final plane: <paramref name="source"/> itself if every step ran in place on it.</returns>
    internal static PixelPlane<TPixel> RunFrame<TPixel>(
        Plan plan, PixelPlane<TPixel> source, bool inPlace, EgamiContext context, BudgetedMemorySource memory, int frameIndex, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return RunFrame(plan, 0, plan.Steps.Count, source, inPlace, context, memory, frameIndex, cancellationToken);
    }

    /// <summary>Runs steps <paramref name="from"/> (inclusive) to <paramref name="to"/> (exclusive) of the plan on one frame.</summary>
    /// <returns>The frame's plane after them: <paramref name="source"/> itself if every step ran in place on it.</returns>
    internal static PixelPlane<TPixel> RunFrame<TPixel>(
        Plan plan, int from, int to, PixelPlane<TPixel> source, bool inPlace, EgamiContext context, BudgetedMemorySource memory, int frameIndex, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> current = source;
        bool ownsCurrent = false;
        try
        {
            for (int i = from; i < to; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Size size = plan.Sizes[i];
                bool sameSize = size.Width == current.Width && size.Height == current.Height;
                bool mayWriteCurrent = inPlace || ownsCurrent;
                PixelPlane<TPixel> destination = sameSize && mayWriteCurrent && !NeedsNewPlane(plan.Steps[i], size) ? current : Allocate<TPixel>(size, memory);
                try
                {
                    plan.Steps[i].Run(new OperationInput<TPixel>(current, destination, context, frameIndex, cancellationToken) { Memory = memory });
                }
                catch
                {
                    if (!ReferenceEquals(destination, current))
                    {
                        destination.Dispose();
                    }

                    throw;
                }

                if (!ReferenceEquals(destination, current))
                {
                    // An intermediate plane is freed as soon as the next one exists; the frame's own plane is left to the caller.
                    if (ownsCurrent)
                    {
                        current.Dispose();
                    }

                    current = destination;
                    ownsCurrent = true;
                }
            }

            return current;
        }
        catch
        {
            if (ownsCurrent)
            {
                current.Dispose();
            }

            throw;
        }
    }

    /// <summary>Plans a recorded chain as the top level is planned, for a scope's sub-chain (which holds no AutoOrient).</summary>
    internal static List<IRasterOperation> PlanSteps(IReadOnlyList<IRasterOperation> steps)
    {
        return Planner.Plan(steps, ImageOrientation.Normal);
    }

    private static bool NeedsNewPlane(IRasterOperation step, Size size)
    {
        return step is INeedsNewPlane needs && needs.NeedsNewPlane(size);
    }

    private static PixelPlane<TPixel> Allocate<TPixel>(Size size, BudgetedMemorySource memory)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        memory.Check((long)size.Width * size.Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
        return PixelPlane.Allocate<TPixel>(size.Width, size.Height, memory);
    }

    /// <summary>
    /// Runs the plan on every frame, splitting it at each measured step: the steps before it run on all frames, the step
    /// measures them and becomes a step of known size, and the rest is planned from the real size. Each frame's plane is
    /// replaced through <paramref name="planes"/>; <paramref name="owned"/> says which planes the chain may write and must
    /// dispose when it replaces them (the caller's own frames, for Apply; none at first, for Derive).
    /// </summary>
    internal static void RunFrames<TPixel>(
        Plan plan, PixelPlane<TPixel>[] planes, bool[] owned, EgamiContext context, BudgetedMemorySource memory, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int from = 0;
        while (true)
        {
            int to = plan.Pending;
            for (int i = 0; i < planes.Length; i++)
            {
                PixelPlane<TPixel> result = RunFrame(plan, from, to, planes[i], owned[i], context, memory, i, cancellationToken);
                if (!ReferenceEquals(result, planes[i]))
                {
                    if (owned[i])
                    {
                        planes[i].Dispose();
                    }

                    planes[i] = result;
                    owned[i] = true;
                }
            }

            if (to == plan.Steps.Count)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            plan.Resolve(planes, context, memory, cancellationToken);
            from = to;
        }
    }

    /// <summary>The planned steps and the frame size after each.</summary>
    internal sealed record Plan(IReadOnlyList<IRasterOperation> Steps, Size[] Sizes) : IDisposable
    {
        /// <summary>Gets the frame size before the first step.</summary>
        internal Size Input { get; init; }

        /// <summary>Gets what the planner changed in the chain, when asked to describe it and it changed something.</summary>
        internal string? Notes { get; init; }

        /// <summary>
        /// Gets the index of the first measured step still to measure, or the step count when there is none: the sizes of that
        /// step and every later one are not known yet.
        /// </summary>
        internal int Pending { get; private set; } = Steps.Count;

        /// <summary>Gets whether the chain holds a measured step, whose size only the pixels can tell.</summary>
        internal bool HasMeasuredSteps => Steps.Any(step => step is IMeasuredStep);

        /// <summary>Computes the sizes from step <paramref name="from"/> on, up to the next measured step.</summary>
        internal void ComputeSizes(int from)
        {
            Size size = from == 0 ? Input : Sizes[from - 1];
            int i = from;
            for (; i < Steps.Count && Steps[i] is not IMeasuredStep; i++)
            {
                (Steps[i] as Scopes.IScope)?.Validate(size);
                Size next = Steps[i].OutputSize(size);
                if (next.Width < 1 || next.Height < 1)
                {
                    throw new ArgumentException($"Step {i + 1} ({Steps[i].GetType().Name}) would make the image {next.Width}×{next.Height}.");
                }

                Sizes[i] = next;
                size = next;
            }

            Pending = i;
        }

        /// <summary>
        /// Measures the pending step on every frame, puts the step it becomes in its place, sizes the rest of the chain from the
        /// real size, and checks the largest plane the rest will allocate against <see cref="SafetyLimits.MaxTotalBytes"/> before
        /// any of it runs.
        /// </summary>
        internal void Resolve<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, EgamiContext context, BudgetedMemorySource memory, CancellationToken cancellationToken)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            int index = Pending;
            IMeasuredStep measured = (IMeasuredStep)Steps[index];
            ((List<IRasterOperation>)Steps)[index] = measured.Measure(frames, context, cancellationToken);
            ComputeSizes(index);
            long largest = 0;
            for (int i = index; i < Pending; i++)
            {
                largest = Math.Max(largest, (long)Sizes[i].Width * Sizes[i].Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
            }

            memory.Check(largest);
        }

        /// <summary>Returns the pooled buffers the built-in steps kept for the chain.</summary>
        public void Dispose()
        {
            foreach (IRasterOperation step in Steps)
            {
                (step as IHoldsBuffers)?.ReleaseBuffers();
            }
        }

        /// <summary>Applies each step's metadata changes (EXIF orientation, dimensions, thumbnail, density) in order.</summary>
        internal void UpdateMetadata(RasterMetadata metadata, Size input)
        {
            if (Steps.Count > 0)
            {
                metadata.FormatDetails?.PixelsChanged();
            }

            for (int i = 0; i < Steps.Count; i++)
            {
                if (Steps[i] is Color.IUpdatesColor color)
                {
                    color.UpdateMetadata(metadata);
                }
                else if (Steps[i] is IUpdatesMetadata updates)
                {
                    updates.UpdateMetadata(metadata, input, Sizes[i]);
                }
                else if (Sizes[i] != input)
                {
                    // A custom step that changes the size: the EXIF dimensions follow it and the thumbnail no longer matches.
                    Geometry.ExifUpkeep.Dimensions(metadata, Sizes[i]);
                    Geometry.ExifUpkeep.DropThumbnail(metadata);
                    Geometry.ExifUpkeep.Subject(metadata, null);
                }

                input = Sizes[i];
            }
        }

        /// <summary>
        /// Gets whether a step moves pixels (resize, crop, pad, rotate, flip, or a custom step that changes the size), so frames
        /// lose where they were stored.
        /// </summary>
        internal bool ChangesGeometry(Size input)
        {
            for (int i = 0; i < Steps.Count; i++)
            {
                if (Steps[i] is IUpdatesMetadata || Sizes[i] != input)
                {
                    return true;
                }

                input = Sizes[i];
            }

            return false;
        }

        /// <summary>Gets the size after the last step, or <paramref name="input"/> for an empty chain.</summary>
        internal Size FinalSize(Size input)
        {
            return Sizes.Length == 0 ? input : Sizes[^1];
        }
    }

    /// <summary>
    /// Merges steps that can run as one: a Crop followed by a resize becomes one resize that reads only the crop rectangle, and
    /// adjacent color matrices and filters become one pass, matrices multiplied together where no clamp falls between them. AutoOrient becomes the
    /// step its orientation needs; followed by a resize, the resize goes first, on the stored image, so the turn runs on the
    /// small result.
    /// </summary>
    private static class Planner
    {
        internal static List<IRasterOperation> Plan(IReadOnlyList<IRasterOperation> steps, ImageOrientation orientation, List<string>? notes = null)
        {
            List<IRasterOperation> planned = new(steps.Count);
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i] is AutoOrientOperation)
                {
                    IRasterOperation? turn = AutoOrientOperation.Orient(orientation);
                    if (turn is not null && i + 1 < steps.Count && steps[i + 1] is ResizeOperation next && !next.HasCrop)
                    {
                        planned.Add(next.BeforeOrienting(orientation));
                        planned.Add(turn);
                        notes?.Add("The resize runs before AutoOrient's turn, so fewer pixels are turned.");
                        i++;
                    }
                    else if (turn is not null)
                    {
                        planned.Add(turn);
                    }

                    // Once turned, the image is upright for any later AutoOrient.
                    orientation = ImageOrientation.Normal;
                    continue;
                }

                if (steps[i] is CropOperation crop && i + 1 < steps.Count && steps[i + 1] is ResizeOperation resize && !resize.HasCrop)
                {
                    planned.Add(resize.WithCrop(crop.Rect));
                    notes?.Add("A crop and the resize after it run as one resampling pass.");
                    i++;
                    continue;
                }

                // Drawing with Destination keeps every pixel as it is.
                if (steps[i] is Compose.DrawImageOperation { Mode: BlendMode.Destination } or Compose.BlendColorOperation { Mode: BlendMode.Destination })
                {
                    notes?.Add("A draw with BlendMode.Destination changes nothing and was dropped.");
                    continue;
                }

                if (steps[i] is ColorMatrixOperation color && planned.Count > 0 && planned[^1] is ColorMatrixOperation previous)
                {
                    planned[^1] = previous.Then(color);
                    notes?.Add("Two neighbouring color steps run as one matrix.");
                    continue;
                }

                // Filters fuse with each other and with color matrices on either side into one pass.
                if (steps[i] is Tone.FilterOperation or ColorMatrixOperation && planned.Count > 0 && planned[^1] is Tone.FilterOperation or ColorMatrixOperation)
                {
                    planned[^1] = AsFilter(planned[^1]).Then(AsFilter(steps[i]));
                    continue;
                }

                planned.Add(steps[i]);
            }

            return planned;
        }

        private static Tone.FilterOperation AsFilter(IRasterOperation step)
        {
            return step as Tone.FilterOperation ?? Tone.FilterOperation.From((ColorMatrixOperation)step);
        }
    }
}

/// <summary>
/// A step whose output size depends on the pixels (Trim, EntropyCrop, Deskew). The planner plans the chain up to it, runs that
/// part, lets it measure every frame, and plans the rest from the real size. Its <see cref="IRasterOperation.OutputSize"/> is
/// never asked.
/// </summary>
internal interface IMeasuredStep : IRasterOperation
{
    /// <summary>
    /// Measures the frames and returns the step of known size that runs in this one's place, the same for every frame, so frames
    /// keep one size (a crop to the union of the frames' rectangles, say).
    /// </summary>
    IRasterOperation Measure<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>;
}

/// <summary>A step that can work in linear light, which decodes with the curve the pipeline binds to it.</summary>
internal interface ILinearLight
{
    bool UsesLinearLight { get; }

    IRasterOperation WithCurve(Colors.LinearCurve curve);
}
