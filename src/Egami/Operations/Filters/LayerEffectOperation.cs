using Egami.Metadata;
using Egami.Operations.Compose;
using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>Where a layer effect's layer goes relative to the image.</summary>
internal enum LayerPlacement
{
    /// <summary>The image is drawn source-over onto the layer: an outer glow or a shadow.</summary>
    Behind,

    /// <summary>The layer is drawn onto the image with the effect's blend mode: an inner glow, a bloom.</summary>
    Over,
}

/// <summary>
/// Glow and Bloom: a layer made from the image by a recorded chain of Egami's own steps, then combined with the image by
/// DrawImage's kernels, so the result is exactly what the same chain written by hand gives (Derive the layer, then DrawImage).
/// The layer's chain is a scope's sub-chain, planned and bound to the raster's linear-light curve as scopes are. Its blurs read the
/// whole image, so inside Within it runs on the whole image and only the area is copied back.
/// </summary>
internal sealed class LayerEffectOperation(
    IReadOnlyList<IRasterOperation> layer, LayerPlacement placement, BlendMode mode, bool linear, Colors.LinearCurve? curve = null)
    : IRasterOperation, Scopes.IScope, Scopes.INeighbourhoodStep
{
    /// <summary>Gets the layer's chain: recorded until bound, planned after.</summary>
    internal IReadOnlyList<IRasterOperation> Layer => layer;

    public (int X, int Y)? Reach => null;

    public Size OutputSize(Size input) => input;

    public IRasterOperation Bind(Func<Colors.LinearCurve?> curve)
    {
        return new LayerEffectOperation(Scopes.ScopeRunner.Bind(layer, curve), placement, mode, linear, linear ? curve() : null);
    }

    public void Validate(Size size)
    {
        Scopes.ScopeRunner.Validate(layer, size, new Rect(0, 0, size.Width, size.Height));
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width, height = input.Source.Height;
        Rect full = new(0, 0, width, height);
        PixelPlane<TPixel> plane = Scopes.ScopeRunner.Scratch(input, width, height);
        try
        {
            Scopes.ScopeRunner.Copy(input.Source, full, plane, new Point(0, 0));
            OperationInput<TPixel> onLayer = new(plane, plane, input.Context, input.FrameIndex, input.CancellationToken) { Memory = input.Memory };
            Scopes.ScopeRunner.Run(layer, onLayer, full);

            if (placement == LayerPlacement.Behind)
            {
                // The image over its layer, as DrawImage(image) on the layer does; the plain wrapper is not disposed, so the plane stays the caller's.
                Raster<TPixel> image = new(input.Source, new RasterMetadata(), input.Context);
                new DrawImageOperation(image, full, new Point(0, 0), 1, BlendMode.SourceOver).Run(onLayer);
                Scopes.ScopeRunner.Copy(plane, full, input.Destination, new Point(0, 0));
                return;
            }

            OperationRows.CopyIfSeparate(input);
            Raster<TPixel> drawn = new(plane, new RasterMetadata(), input.Context);
            OperationInput<TPixel> onImage = new(input.Destination, input.Destination, input.Context, input.FrameIndex, input.CancellationToken) { Memory = input.Memory };
            new DrawImageOperation(drawn, full, new Point(0, 0), 1, mode, linear, curve).Run(onImage);
        }
        finally
        {
            plane.Dispose();
        }
    }
}
