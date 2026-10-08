using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations;

/// <summary>
/// Records a chain of operations for <see cref="Raster{TPixel}.Apply"/> and <see cref="Raster{TPixel}.Derive"/>.
/// Each method adds one step and returns this builder, so steps chain. Arguments are checked as steps are added, before any
/// pixel changes.
/// </summary>
/// <example>
/// <code>
/// raster.Apply(ops => ops.Add(new MyOperation()));
/// </code>
/// </example>
public sealed class RasterOps
{
    private readonly List<IRasterOperation> _operations = [];

    internal RasterOps()
    {
    }

    /// <summary>Gets the recorded steps, in order.</summary>
    internal IReadOnlyList<IRasterOperation> Operations => _operations;

    /// <summary>Adds a step, such as a custom <see cref="IRasterOperation"/>.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>This builder.</returns>
    public RasterOps Add(IRasterOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _operations.Add(operation);
        return this;
    }

    /// <summary>Keeps one rectangle of the image. The pixels are copied exactly.</summary>
    /// <param name="rect">The rectangle to keep. It must lie inside the image, which is checked before any pixel changes.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty, or leaves the image when the chain runs.</exception>
    public RasterOps Crop(Rect rect)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rect.Width, 1, nameof(rect));
        ArgumentOutOfRangeException.ThrowIfLessThan(rect.Height, 1, nameof(rect));
        return Add(new CropOperation(rect));
    }

    /// <summary>Keeps one rectangle of the image. The pixels are copied exactly.</summary>
    /// <param name="x">The left edge of the rectangle.</param>
    /// <param name="y">The top edge of the rectangle.</param>
    /// <param name="width">The width of the rectangle, at least 1.</param>
    /// <param name="height">The height of the rectangle, at least 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty, or leaves the image when the chain runs.</exception>
    public RasterOps Crop(int x, int y, int width, int height)
    {
        return Crop(new Rect(x, y, width, height));
    }

    /// <summary>
    /// Enlarges the canvas to <paramref name="width"/> × <paramref name="height"/>, placing the image at <paramref name="anchor"/>
    /// and filling the new area with <paramref name="color"/>.
    /// </summary>
    /// <param name="width">The new width, at least the image's width.</param>
    /// <param name="height">The new height, at least the image's height.</param>
    /// <param name="color">The color of the new area. Defaults to transparent black.</param>
    /// <param name="anchor">Where the image sits on the new canvas. Defaults to the center, rounding toward the top left.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1, or smaller than the image when the chain runs; use <see cref="Crop(Rect)"/> to shrink.</exception>
    public RasterOps Pad(int width, int height, Rgba8 color = default, Anchor anchor = Anchor.Center)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        return Add(new PadOperation(width, height, color, anchor));
    }

    /// <summary>Mirrors the image. Flipping both ways is the same as rotating by 180°.</summary>
    /// <param name="horizontal">Whether to mirror left to right.</param>
    /// <param name="vertical">Whether to mirror top to bottom.</param>
    /// <returns>This builder.</returns>
    public RasterOps Flip(bool horizontal, bool vertical)
    {
        return horizontal || vertical ? Add(new FlipOperation(horizontal, vertical)) : this;
    }

    /// <summary>Rotates the image clockwise by a multiple of 90°. The pixels are moved exactly, without resampling.</summary>
    /// <param name="degrees">The angle, clockwise. Negative angles turn counter-clockwise, so −90 equals 270.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degrees"/> is not a multiple of 90.</exception>
    public RasterOps Rotate(float degrees)
    {
        float turn = degrees % 360f;
        if (turn < 0)
        {
            turn += 360f;
        }

        return turn switch
        {
            0f => this,
            90f => Add(new RotateQuarterOperation(clockwise: true)),
            180f => Add(new FlipOperation(horizontal: true, vertical: true)),
            270f => Add(new RotateQuarterOperation(clockwise: false)),
            _ => throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Only multiples of 90° are supported."),
        };
    }

    /// <summary>Sets every pixel in <paramref name="rect"/>, or the whole image, to one color. Alpha is replaced, not blended.</summary>
    /// <param name="color">The color.</param>
    /// <param name="rect">The area to fill, which must lie inside the image, or <see langword="null"/> for the whole image.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty, or leaves the image when the chain runs.</exception>
    public RasterOps FillColor(Rgba8 color, Rect? rect = null)
    {
        if (rect is Rect r)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(r.Width, 1, nameof(rect));
            ArgumentOutOfRangeException.ThrowIfLessThan(r.Height, 1, nameof(rect));
        }

        return Add(new FillColorOperation(color, rect));
    }
}
