using Egami.Cli.Steps;
using Egami.Pixels;

namespace Egami.Video.Cli.Host;

/// <summary>
/// egami run's steps on one video frame, chained as egami run chains them on an image it holds in memory: one
/// <c>RasterOps</c> chain, split only where a step that needs the current size follows a step that changes it, so a frame
/// comes out with the same pixels as <c>egami run</c> gives on the same picture.
/// </summary>
internal static class RunSteps
{
    /// <summary>Runs the steps over an image in place.</summary>
    public static void Apply(Raster image, IReadOnlyList<BoundStep> steps)
    {
        List<(BoundStep Step, Size Size)> pending = [];
        foreach (BoundStep step in steps)
        {
            if (step.Info.Sized is not null && pending.Any(p => !StepCatalog.KeepsSize(p.Step.Info)))
            {
                Flush(image, pending);
            }

            pending.Add((step, new Size(image.Width, image.Height)));
        }

        Flush(image, pending);
    }

    /// <summary>The size of a copy of <paramref name="image"/> after each step, run one at a time, to name the step that changes it.</summary>
    public static List<Size> SizesAfterEach(Raster<Rgba8> image, IReadOnlyList<BoundStep> steps)
    {
        List<Size> sizes = [];
        using Raster<Rgba8> copy = image.CopyFrame(0);
        foreach (BoundStep step in steps)
        {
            Size before = new(copy.Width, copy.Height);
            copy.Apply(ops => step.AddTo(ops, before));
            sizes.Add(new Size(copy.Width, copy.Height));
        }

        return sizes;
    }

    /// <summary>Whether any step can change the size of a frame.</summary>
    public static bool ChangesSize(IReadOnlyList<BoundStep> steps) => steps.Any(s => !StepCatalog.KeepsSize(s.Info));

    private static void Flush(Raster image, List<(BoundStep Step, Size Size)> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        (BoundStep Step, Size Size)[] chain = [.. pending];
        pending.Clear();
        image.Apply(ops =>
        {
            foreach ((BoundStep step, Size size) in chain)
            {
                step.AddTo(ops, size);
            }
        });
    }
}
