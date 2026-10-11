using Egami.Operations.Filters;
using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Scopes;

/// <summary>A step that can run on a full-size plane in place and change only the pixels inside an area.</summary>
internal interface IAreaStep
{
    /// <summary>Runs on <paramref name="input"/>, whose source and destination are the same full-size plane, changing only <paramref name="area"/>.</summary>
    void RunArea<TPixel>(OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>;
}

/// <summary>A step whose coordinates are the raster's, so inside a scope it runs on a view of the area with its coordinates moved.</summary>
internal interface IAreaTranslatable
{
    /// <summary>Returns the step as it runs on a view of <paramref name="area"/>, or <see langword="null"/> when it changes nothing there.</summary>
    IRasterOperation? InArea(Rect area);
}

/// <summary>A built-in step that treats whatever plane it is given as the whole image, so it runs on a view of an area as it is.</summary>
internal interface IRunsOnViews
{
}

/// <summary>A scope: a recorded sub-chain that the pipeline plans and binds like the top-level chain.</summary>
internal interface IScope
{
    /// <summary>Returns the scope with its sub-chain planned, linear-light steps bound to <paramref name="curve"/>'s result.</summary>
    IRasterOperation Bind(Func<Colors.LinearCurve?> curve);

    /// <summary>Checks every step of the sub-chain keeps the size of the area it runs on, in an image of <paramref name="size"/>.</summary>
    void Validate(Size size);
}

/// <summary>
/// Runs a planned sub-chain on part of a full-size plane: steps that know areas run on the whole plane and write only the area;
/// steps in the raster's coordinates run on a view of the area with their coordinates moved; built-in steps that see their input
/// as the image run on the view; and other steps, custom ones included, run on a copy of the area that is copied back.
/// Consecutive convolutions from one call (the three boxes of a large blur) run together on one band, so they read real pixels
/// around the area as one blur would.
/// </summary>
internal static class ScopeRunner
{
    /// <summary>Records a scope's sub-chain and rejects steps that change the size or the color encoding, naming the step.</summary>
    internal static IReadOnlyList<IRasterOperation> Record(Action<RasterOps> build, string parameter)
    {
        ArgumentNullException.ThrowIfNull(build, parameter);
        RasterOps ops = new();
        build(ops);
        IReadOnlyList<IRasterOperation> steps = ops.Steps;
        for (int i = 0; i < steps.Count; i++)
        {
            string? reason = steps[i] switch
            {
                Resampling.ResizeOperation or CropOperation or PadOperation or RotateQuarterOperation or RotateOperation or AutoOrientOperation
                    => "changes the image's size",
                Color.ConvertColorOperation or Color.ToneMapOperation => "changes the color encoding, which part of an image cannot have alone",
                _ => null,
            };
            if (reason is not null)
            {
                throw new ArgumentException($"Step {i + 1} of the scope ({Name(steps[i])}) {reason}.", parameter);
            }
        }

        return steps;
    }

    /// <summary>Plans a recorded sub-chain and binds its linear-light steps and nested scopes.</summary>
    internal static IReadOnlyList<IRasterOperation> Bind(IReadOnlyList<IRasterOperation> recorded, Func<Colors.LinearCurve?> curve)
    {
        List<IRasterOperation> steps = OperationPipeline.PlanSteps(recorded);
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] is ILinearLight { UsesLinearLight: true } linear && curve() is Colors.LinearCurve bound)
            {
                steps[i] = linear.WithCurve(bound);
            }
            else if (steps[i] is IScope scope)
            {
                steps[i] = scope.Bind(curve);
            }
        }

        return steps;
    }

    /// <summary>Checks each step keeps <paramref name="area"/>'s size.</summary>
    internal static void Validate(IReadOnlyList<IRasterOperation> steps, Size image, Rect area)
    {
        if (area.IsEmpty)
        {
            return;
        }

        Size size = new(area.Width, area.Height);
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] is IScope scope)
            {
                scope.Validate(image);
                continue;
            }

            if (steps[i] is IAreaStep or IAreaTranslatable)
            {
                continue;
            }

            Size output = steps[i].OutputSize(size);
            if (output != size)
            {
                throw new ArgumentException($"Step {i + 1} of the scope ({Name(steps[i])}) would make its {size.Width}×{size.Height} area {output.Width}×{output.Height}; a scope must keep the size.");
            }
        }
    }

    /// <summary>Runs <paramref name="steps"/> on <paramref name="input"/>'s destination, changing only <paramref name="area"/>.</summary>
    internal static void Run<TPixel>(IReadOnlyList<IRasterOperation> steps, OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (area.IsEmpty)
        {
            return;
        }

        for (int i = 0; i < steps.Count; i++)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            IRasterOperation step = steps[i];
            if (step is ConvolutionOperation convolution)
            {
                int last = i;
                while (convolution.Group != 0 && last + 1 < steps.Count && steps[last + 1] is ConvolutionOperation next && next.Group == convolution.Group)
                {
                    last++;
                }

                Convolve(steps, i, last, input, area);
                i = last;
            }
            else if (step is IAreaStep areaStep)
            {
                areaStep.RunArea(input, area);
            }
            else if (step is IAreaTranslatable translatable)
            {
                if (translatable.InArea(area) is IRasterOperation moved)
                {
                    OnView(moved, input, area);
                }
            }
            else if (step is IRunsOnViews)
            {
                OnView(step, input, area);
            }
            else
            {
                OnCopy(step, input, area);
            }
        }
    }

    /// <summary>Allocates a scratch plane through the chain's budgeted memory, so it counts against MaxTotalBytes.</summary>
    internal static PixelPlane<TPixel> Scratch<TPixel>(OperationInput<TPixel> input, int width, int height)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return PixelPlane.Allocate<TPixel>(width, height, (IPixelMemorySource?)input.Memory ?? input.Context.Memory);
    }

    /// <summary>Copies a rectangle between planes; the rectangle is in the source's coordinates and lands at <paramref name="at"/>.</summary>
    internal static void Copy<TPixel>(PixelPlane<TPixel> source, Rect rect, PixelPlane<TPixel> destination, Point at)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int y = 0; y < rect.Height; y++)
        {
            source.Row(rect.Y + y).Slice(rect.X, rect.Width).CopyTo(destination.Row(at.Y + y).Slice(at.X, rect.Width));
        }
    }

    private static string Name(IRasterOperation step)
    {
        string name = step.GetType().Name;
        return name.EndsWith("Operation", StringComparison.Ordinal) ? name[..^"Operation".Length] : name;
    }

    private static void OnView<TPixel>(IRasterOperation step, OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> view = new(input.Destination, area);
        step.Run(new OperationInput<TPixel>(view, view, input.Context, input.FrameIndex, input.CancellationToken) { Memory = input.Memory });
    }

    /// <summary>A step that may assume anything about its planes: it runs on a copy of the area, and its result is copied back.</summary>
    private static void OnCopy<TPixel>(IRasterOperation step, OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> copy = Scratch(input, area.Width, area.Height);
        PixelPlane<TPixel>? result = null;
        try
        {
            Copy(input.Destination, area, copy, new Point(0, 0));
            result = step is INeedsNewPlane needs && needs.NeedsNewPlane(new Size(area.Width, area.Height)) ? Scratch(input, area.Width, area.Height) : copy;
            step.Run(new OperationInput<TPixel>(copy, result, input.Context, input.FrameIndex, input.CancellationToken) { Memory = input.Memory });
            Copy(result, new Rect(0, 0, area.Width, area.Height), input.Destination, new Point(area.X, area.Y));
        }
        finally
        {
            if (result is not null && !ReferenceEquals(result, copy))
            {
                result.Dispose();
            }

            copy.Dispose();
        }
    }

    /// <summary>
    /// Convolutions on the band around the area that their kernels reach (the whole image for wrapping edges and box means), so every pixel in
    /// the area gets the value a whole-image run would give; only the area is copied back.
    /// </summary>
    private static void Convolve<TPixel>(IReadOnlyList<IRasterOperation> steps, int first, int last, OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int rx = 0, ry = 0;
        bool wraps = false;
        for (int i = first; i <= last; i++)
        {
            ConvolutionOperation c = (ConvolutionOperation)steps[i];
            rx += c.RadiusX;
            ry += c.RadiusY;
            // Wrapping reads the far edge; running sums round by where they start: both need the whole image.
            wraps |= c.Edge == EdgeMode.Wrap || c.IsBox;
        }

        Rect full = new(0, 0, input.Destination.Width, input.Destination.Height);
        Rect band = wraps ? full : new Rect(area.X - rx, area.Y - ry, area.Width + (2 * rx), area.Height + (2 * ry)).Intersect(full);

        // The first pass reads a view of the band in place; its output, and each later pass's, goes to scratch planes.
        PixelPlane<TPixel> read = new(input.Destination, band);
        PixelPlane<TPixel> a = Scratch(input, band.Width, band.Height);
        PixelPlane<TPixel>? b = last > first ? Scratch(input, band.Width, band.Height) : null;
        try
        {
            PixelPlane<TPixel> source = read, target = a;
            for (int i = first; i <= last; i++)
            {
                steps[i].Run(new OperationInput<TPixel>(source, target, input.Context, input.FrameIndex, input.CancellationToken) { Memory = input.Memory });
                source = target;
                target = ReferenceEquals(target, a) ? b! : a;
            }

            Copy(source, new Rect(area.X - band.X, area.Y - band.Y, area.Width, area.Height), input.Destination, new Point(area.X, area.Y));
        }
        finally
        {
            a.Dispose();
            b?.Dispose();
        }
    }
}
