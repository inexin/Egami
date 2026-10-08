using Egami.Pixels;

namespace Egami.Operations;

/// <summary>
/// Runs a recorded chain: records it, plans it, then runs each step on every frame, allocating a new plane only for steps that
/// change the size. A chain holds at most two full planes per frame at once.
/// </summary>
internal static class OperationPipeline
{
    /// <summary>Records the chain from the caller's lambda and plans it.</summary>
    /// <returns>The planned steps and the size after each one.</returns>
    internal static Plan Build(Action<RasterOps> build, Size size)
    {
        ArgumentNullException.ThrowIfNull(build);
        RasterOps ops = new();
        build(ops);
        IReadOnlyList<IRasterOperation> steps = Planner.Plan(ops.Operations);
        Size[] sizes = new Size[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
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
                PixelPlane<TPixel> destination = sameSize && mayWriteCurrent ? current : Allocate<TPixel>(size, memory);
                try
                {
                    plan.Steps[i].Run(new OperationInput<TPixel>(current, destination, context, frameIndex, cancellationToken));
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

    private static PixelPlane<TPixel> Allocate<TPixel>(Size size, BudgetedMemorySource memory)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        memory.Check((long)size.Width * size.Height * TPixel.Layout.BytesPerPixel);
        return PixelPlane.Allocate<TPixel>(size.Width, size.Height, memory);
    }

    /// <summary>The planned steps and the frame size after each.</summary>
    internal sealed record Plan(IReadOnlyList<IRasterOperation> Steps, Size[] Sizes)
    {
        /// <summary>Gets the size after the last step, or <paramref name="input"/> for an empty chain.</summary>
        internal Size FinalSize(Size input)
        {
            return Sizes.Length == 0 ? input : Sizes[^1];
        }
    }

    /// <summary>
    /// Merges steps that can run as one. The skeleton keeps every step as it is; color matrices (task 6) and crop-then-resize
    /// (task 3) will merge here.
    /// </summary>
    private static class Planner
    {
        internal static IReadOnlyList<IRasterOperation> Plan(IReadOnlyList<IRasterOperation> steps)
        {
            return [.. steps];
        }
    }
}
