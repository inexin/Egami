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
    internal static Plan Build(Action<RasterOps> build, Size size, RasterMetadata? metadata = null)
    {
        return Build(build, size, metadata?.Exif?.Orientation ?? ImageOrientation.Normal, metadata is null ? null : metadata.GetColorProfile, metadata?.Hdr);
    }

    /// <summary>
    /// Records and plans the chain for an image stored with <paramref name="orientation"/>, whose pixels are in the encoding
    /// <paramref name="colorProfile"/> returns (sRGB when <see langword="null"/>); it is asked only if the chain converts color.
    /// </summary>
    internal static Plan Build(Action<RasterOps> build, Size size, ImageOrientation orientation, Func<Colors.ColorProfile>? colorProfile = null, Colors.HdrMetadata? hdr = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        RasterOps ops = new();
        build(ops);
        List<IRasterOperation> steps = Planner.Plan(ops.Steps, orientation);
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

        Size[] sizes = new Size[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            (steps[i] as Scopes.IScope)?.Validate(size);
            Size next = steps[i].OutputSize(size);
            if (next.Width < 1 || next.Height < 1)
            {
                throw new ArgumentException($"Step {i + 1} ({steps[i].GetType().Name}) would make the image {next.Width}×{next.Height}.", nameof(build));
            }

            sizes[i] = next;
            size = next;
        }

        return new Plan(steps, sizes);
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
        PixelPlane<TPixel> current = source;
        bool ownsCurrent = false;
        try
        {
            for (int i = 0; i < plan.Steps.Count; i++)
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

    /// <summary>The planned steps and the frame size after each.</summary>
    internal sealed record Plan(IReadOnlyList<IRasterOperation> Steps, Size[] Sizes) : IDisposable
    {
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
    /// adjacent color matrices become one pass, multiplied together where no clamp falls between them. AutoOrient becomes the
    /// step its orientation needs; followed by a resize, the resize goes first, on the stored image, so the turn runs on the
    /// small result.
    /// </summary>
    private static class Planner
    {
        internal static List<IRasterOperation> Plan(IReadOnlyList<IRasterOperation> steps, ImageOrientation orientation)
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
                    i++;
                    continue;
                }

                // Drawing with Destination keeps every pixel as it is.
                if (steps[i] is Compose.DrawImageOperation { Mode: BlendMode.Destination } or Compose.BlendColorOperation { Mode: BlendMode.Destination })
                {
                    continue;
                }

                if (steps[i] is ColorMatrixOperation color && planned.Count > 0 && planned[^1] is ColorMatrixOperation previous)
                {
                    planned[^1] = previous.Then(color);
                    continue;
                }

                planned.Add(steps[i]);
            }

            return planned;
        }
    }
}

/// <summary>A step that can work in linear light, which decodes with the curve the pipeline binds to it.</summary>
internal interface ILinearLight
{
    bool UsesLinearLight { get; }

    IRasterOperation WithCurve(Colors.LinearCurve curve);
}
