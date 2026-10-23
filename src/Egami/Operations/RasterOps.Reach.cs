using Egami.Operations.Color;
using Egami.Operations.Compose;
using Egami.Operations.Filters;
using Egami.Operations.Geometry;
using Egami.Operations.Tone;

namespace Egami.Operations;

public sealed partial class RasterOps
{
    /// <summary>
    /// Returns how far a chain reads past each output pixel: the sum of its steps' reaches. Pixel-by-pixel steps (color matrices,
    /// arithmetic, color filters, blending a color, keying) reach nothing; blurs, convolutions, morphology, glows and drop shadows
    /// reach as far as their kernels and offsets. A step whose reach is unbounded or unknown (a resize, a geometric transform, a
    /// histogram step, a custom <see cref="IRasterOperation"/>) makes the whole chain's reach unknown.
    /// </summary>
    /// <param name="build">Records the chain, as for <see cref="Raster.Apply"/>; it is recorded, not run.</param>
    /// <returns>The reach, or <see langword="null"/> when some step's reach is unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="build"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="reach"/>
    /// </example>
    public static OperationReach? Reach(Action<RasterOps> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        RasterOps ops = new();
        build(ops);
        return ReachOf(ops.Steps);
    }

    private static OperationReach? ReachOf(IReadOnlyList<IRasterOperation> steps)
    {
        int x = 0, y = 0;
        foreach (IRasterOperation step in steps)
        {
            OperationReach? reach = step switch
            {
                LayerEffectOperation effect => ReachOf(effect.Layer),
                FastBlurOperation fast => new OperationReach(fast.ReadReach.X, fast.ReadReach.Y),
                ConvolutionOperation convolution => convolution.ReadReach is (int X, int Y) c ? new OperationReach(c.X, c.Y) : null,
                OffsetOperation offset => new OperationReach(Math.Abs(offset.Shift.X), Math.Abs(offset.Shift.Y)),
                Scopes.INeighbourhoodStep neighbourhood => neighbourhood.Reach is (int X, int Y) r ? new OperationReach(r.X, r.Y) : null,
                ColorMatrixOperation or ArithmeticOperation or FilterOperation or BlendColorOperation or MakeTransparentOperation
                    or ChromaKeyOperation or DespillOperation
                    or ReplaceColorOperation or LinearGrayscaleOperation or FlattenOperation or PosterizeOperation => new OperationReach(0, 0),
                _ => null,
            };
            if (reach is not OperationReach known)
            {
                return null;
            }

            x += known.X;
            y += known.Y;
        }

        return new OperationReach(x, y);
    }
}
