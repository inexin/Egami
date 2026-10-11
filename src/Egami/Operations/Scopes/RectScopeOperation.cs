using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Scopes;

/// <summary>
/// Within(rect): runs a sub-chain so that only pixels inside the rectangle change. Filters read the real pixels around it, so the
/// result inside equals the whole-image result there; flips mirror the rectangle itself.
/// </summary>
internal sealed class RectScopeOperation(Rect rect, IReadOnlyList<IRasterOperation> steps) : IRasterOperation, IAreaStep, IScope
{
    /// <summary>Gets the sub-chain: recorded until bound, planned after.</summary>
    internal IReadOnlyList<IRasterOperation> Steps => steps;

    public Size OutputSize(Size input)
    {
        return input;
    }

    public IRasterOperation Bind(Func<Colors.LinearCurve?> curve)
    {
        return new RectScopeOperation(rect, ScopeRunner.Bind(steps, curve));
    }

    public void Validate(Size size)
    {
        ScopeRunner.Validate(steps, size, rect.Intersect(new Rect(0, 0, size.Width, size.Height)));
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        PixelPlane<TPixel> plane = input.Destination;
        RunArea(new OperationInput<TPixel>(plane, plane, input.Context, input.FrameIndex, input.CancellationToken) { Memory = input.Memory }, new Rect(0, 0, plane.Width, plane.Height));
    }

    public void RunArea<TPixel>(OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ScopeRunner.Run(steps, input, rect.Intersect(area));
    }
}
