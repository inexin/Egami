using Egami.Operations.Color;
using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Tone;

/// <summary>
/// Runs a <see cref="ColorFilter"/> in one pass over the frame: the planner fuses adjacent filters and color matrices into one, so a
/// chain of tone steps reads and writes each pixel once.
/// </summary>
internal sealed class FilterOperation(ColorFilter filter, List<float[]>? owned = null) : IRasterOperation, Scopes.IRunsOnViews, IHoldsBuffers
{
    internal ColorFilter Filter => filter;

    public Size OutputSize(Size input) => input;

    /// <summary>Returns a color matrix chain as a filter step, stage for stage.</summary>
    internal static FilterOperation From(ColorMatrixOperation matrices)
    {
        return new FilterOperation(ColorFilter.FromStages([.. matrices.Stages.Select(m => new MatrixStage(m))]));
    }

    /// <summary>Returns the tables a measured step rented for this filter, once the chain has run.</summary>
    public void ReleaseBuffers()
    {
        if (owned is null)
        {
            return;
        }

        foreach (float[] table in owned)
        {
            System.Buffers.ArrayPool<float>.Shared.Return(table);
        }

        owned.Clear();
    }

    /// <summary>Returns this step followed by <paramref name="next"/> as one step.</summary>
    internal FilterOperation Then(FilterOperation next) => new(filter.Then(next.Filter));

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (filter.IsIdentity)
        {
            OperationRows.CopyIfSeparate(input);
            return;
        }

        using FilterProgram<TPixel> program = new(filter.Stages);
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                program.RunRow(input.Source.Row(y), input.Destination.Row(y));
            }
        });
    }
}
