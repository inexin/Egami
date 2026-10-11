using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>Ways into a canvas from Egami's rasters and operation chains.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="pipeline"/>
/// </example>
public static class DrawingExtensions
{
    /// <summary>Creates a canvas that draws on a raster's first frame. Dispose it when done.</summary>
    /// <typeparam name="TPixel">The raster's pixel type.</typeparam>
    /// <param name="raster">The raster.</param>
    /// <param name="options">The settings, or <see langword="null"/> for the defaults.</param>
    /// <returns>The canvas.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="canvas"/>
    /// </example>
    public static RasterCanvas<TPixel> CreateCanvas<TPixel>(this Raster<TPixel> raster, CanvasOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        return new RasterCanvas<TPixel>(raster, options);
    }

    /// <summary>
    /// Adds a step that draws on each frame with a canvas over the step's pixels. It keeps the image's size, so it composes with
    /// <c>Within</c> (the canvas then covers the area, with its origin at the area's corner), channel scopes and animations.
    /// </summary>
    /// <param name="operations">The chain.</param>
    /// <param name="draw">The drawing, called once per frame.</param>
    /// <param name="options">The canvas settings, or <see langword="null"/> for the defaults.</param>
    /// <returns>The chain.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="pipeline"/>
    /// </example>
    public static RasterOps Draw(this RasterOps operations, Action<Canvas> draw, CanvasOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(draw);
        return operations.Add(new DrawOperation((canvas, _) => draw(canvas), options));
    }

    /// <summary>Adds a step that draws on each frame, passing the frame's index, for animations.</summary>
    /// <param name="operations">The chain.</param>
    /// <param name="draw">The drawing, called once per frame with its index.</param>
    /// <param name="options">The canvas settings, or <see langword="null"/> for the defaults.</param>
    /// <returns>The chain.</returns>
    public static RasterOps Draw(this RasterOps operations, Action<Canvas, int> draw, CanvasOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(draw);
        return operations.Add(new DrawOperation(draw, options));
    }

    /// <summary>The pipeline step: copies the frame through when the chain gives it a new plane, then draws on it.</summary>
    private sealed class DrawOperation(Action<Canvas, int> draw, CanvasOptions? options) : IRasterOperation
    {
        public Size OutputSize(Size input) => input;

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            if (!input.IsInPlace)
            {
                for (int y = 0; y < input.Source.Height; y++)
                {
                    input.Source.Row(y).CopyTo(input.Destination.Row(y));
                }
            }

            using RasterCanvas<TPixel> canvas = new(input.Destination, input.Context, options);
            draw(canvas, input.FrameIndex);
        }
    }
}
