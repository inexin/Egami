using Egami.Pixels;

namespace Egami.Operations;

/// <summary>
/// One step of an operation chain. Built-in operations implement it, and so can callers: add one to a chain with
/// <see cref="RasterOps.Add"/>.
/// </summary>
/// <remarks>Implementations must be stateless or immutable: one instance runs on every frame and may run on several threads.</remarks>
public interface IRasterOperation
{
    /// <summary>Returns the frame size after this operation, so the pipeline can plan memory before any pixel changes.</summary>
    /// <param name="input">The frame size before it.</param>
    /// <returns>The size after it. Return <paramref name="input"/> for operations that keep the size.</returns>
    /// <exception cref="ArgumentException">The operation cannot apply to a frame of this size.</exception>
    Size OutputSize(Size input);

    /// <summary>
    /// Runs the operation on one frame: reads <see cref="OperationInput{TPixel}.Source"/> and writes every pixel of
    /// <see cref="OperationInput{TPixel}.Destination"/>. The two can be the same plane, so an operation must handle working in place.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="input">The planes, context and cancellation token.</param>
    void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>;
}
