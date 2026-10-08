using Egami.Operations.Color;
using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
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
    internal IReadOnlyList<IRasterOperation> Steps => _operations;

    /// <summary>Adds a step, such as a custom <see cref="IRasterOperation"/>.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>This builder.</returns>
    public RasterOps Add(IRasterOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _operations.Add(operation);
        return this;
    }

    /// <summary>
    /// Resizes the image to <paramref name="width"/> × <paramref name="height"/>. Pass 0 for one dimension to keep the aspect ratio.
    /// Resizing to the current size changes nothing. A <see cref="Crop(Rect)"/> just before a resize is merged into it, so only the
    /// crop rectangle is read.
    /// </summary>
    /// <param name="width">The new width, or 0 to derive it from <paramref name="height"/>.</param>
    /// <param name="height">The new height, or 0 to derive it from <paramref name="width"/>.</param>
    /// <param name="filter">The resampling kernel. Defaults to <see cref="ResampleFilter.Bicubic"/>.</param>
    /// <param name="colorSpace">Where pixels are blended. Defaults to <see cref="ColorSpace.Encoded"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is negative, or both are 0.</exception>
    public RasterOps Resize(int width, int height, ResampleFilter filter = ResampleFilter.Bicubic, ColorSpace colorSpace = ColorSpace.Encoded)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (width == 0 && height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "At least one of width and height must be greater than 0.");
        }

        CheckFilter(filter, colorSpace);
        return Add(new ResizeOperation(ResizeMode.Stretch, width, height, filter, colorSpace));
    }

    /// <summary>
    /// Scales the image to fit inside <paramref name="width"/> × <paramref name="height"/> with its aspect ratio kept, so one side
    /// matches the box and the other is no larger.
    /// </summary>
    /// <param name="width">The width of the box, at least 1.</param>
    /// <param name="height">The height of the box, at least 1.</param>
    /// <param name="filter">The resampling kernel. Defaults to <see cref="ResampleFilter.Bicubic"/>.</param>
    /// <param name="colorSpace">Where pixels are blended. Defaults to <see cref="ColorSpace.Encoded"/>.</param>
    /// <param name="enlarge">Whether an image smaller than the box is enlarged. Pass <see langword="false"/> for thumbnails.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1.</exception>
    public RasterOps Fit(int width, int height, ResampleFilter filter = ResampleFilter.Bicubic, ColorSpace colorSpace = ColorSpace.Encoded, bool enlarge = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        CheckFilter(filter, colorSpace);
        return Add(new ResizeOperation(ResizeMode.Fit, width, height, filter, colorSpace, enlarge: enlarge));
    }

    /// <summary>
    /// Scales the image to cover <paramref name="width"/> × <paramref name="height"/> with its aspect ratio kept, keeping the part at
    /// <paramref name="anchor"/>. Only the kept part is read; nothing is cropped first.
    /// </summary>
    /// <param name="width">The output width, at least 1.</param>
    /// <param name="height">The output height, at least 1.</param>
    /// <param name="anchor">Which part of the image to keep. Defaults to the center.</param>
    /// <param name="filter">The resampling kernel. Defaults to <see cref="ResampleFilter.Bicubic"/>.</param>
    /// <param name="colorSpace">Where pixels are blended. Defaults to <see cref="ColorSpace.Encoded"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1.</exception>
    public RasterOps Fill(int width, int height, Anchor anchor = Anchor.Center, ResampleFilter filter = ResampleFilter.Bicubic, ColorSpace colorSpace = ColorSpace.Encoded)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        CheckFilter(filter, colorSpace);
        return Add(new ResizeOperation(ResizeMode.Fill, width, height, filter, colorSpace, anchor));
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

    /// <summary>
    /// Rotates the image clockwise. Multiples of 90° move pixels exactly; other angles resample onto a canvas the size of the
    /// rotated bounding box, with the corners filled with <paramref name="background"/>.
    /// </summary>
    /// <param name="degrees">The angle, clockwise. Negative angles turn counter-clockwise, so −90 equals 270.</param>
    /// <param name="filter">The resampling kernel for angles that are not multiples of 90°. Defaults to <see cref="ResampleFilter.Bicubic"/>.</param>
    /// <param name="background">The color of the canvas outside the rotated image. Defaults to transparent black.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degrees"/> is not finite, or <paramref name="filter"/> is not defined.</exception>
    public RasterOps Rotate(float degrees, ResampleFilter filter = ResampleFilter.Bicubic, Rgba8 background = default)
    {
        if (!float.IsFinite(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "The angle must be finite.");
        }

        CheckFilter(filter, ColorSpace.Encoded);
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
            _ => Add(new RotateOperation(turn, filter, background)),
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

    /// <summary>Multiplies red, green and blue by <paramref name="amount"/>: 1 changes nothing, 0 gives black. See <see cref="Operations.ColorMatrix.Brightness"/>.</summary>
    /// <param name="amount">The factor, 0 or more.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative or not finite.</exception>
    public RasterOps Brightness(float amount)
    {
        return ColorMatrix(Operations.ColorMatrix.Brightness(amount));
    }

    /// <summary>Scales red, green and blue around mid-gray: 1 changes nothing, 0 gives flat gray. See <see cref="Operations.ColorMatrix.Contrast"/>.</summary>
    /// <param name="amount">The factor, 0 or more.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative or not finite.</exception>
    public RasterOps Contrast(float amount)
    {
        return ColorMatrix(Operations.ColorMatrix.Contrast(amount));
    }

    /// <summary>Mixes each color with its luma: 1 changes nothing, 0 gives gray, above 1 oversaturates. See <see cref="Operations.ColorMatrix.Saturation"/>.</summary>
    /// <param name="amount">The factor, 0 or more.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative or not finite.</exception>
    public RasterOps Saturation(float amount)
    {
        return ColorMatrix(Operations.ColorMatrix.Saturation(amount));
    }

    /// <summary>Rotates hue around the luma axis. See <see cref="Operations.ColorMatrix.HueRotate"/>.</summary>
    /// <param name="degrees">The angle; 0 changes nothing.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degrees"/> is not finite.</exception>
    public RasterOps HueRotate(float degrees)
    {
        return ColorMatrix(Operations.ColorMatrix.HueRotate(degrees));
    }

    /// <summary>Tints toward sepia. See <see cref="Operations.ColorMatrix.Sepia"/>.</summary>
    /// <param name="amount">0 changes nothing, 1 gives full sepia.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    public RasterOps Sepia(float amount = 1)
    {
        return ColorMatrix(Operations.ColorMatrix.Sepia(amount));
    }

    /// <summary>
    /// Converts toward gray. By default the luma is taken from the stored, gamma-encoded values, as CSS filters do; pass
    /// <see cref="ColorSpace.Linear"/> to weigh the colors in linear light, which keeps perceived brightness better.
    /// </summary>
    /// <param name="mode">The luma weights. Defaults to <see cref="GrayscaleMode.Rec709"/>.</param>
    /// <param name="amount">0 changes nothing, 1 gives full gray.</param>
    /// <param name="colorSpace">Where the luma is taken. Defaults to <see cref="ColorSpace.Encoded"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public RasterOps Grayscale(GrayscaleMode mode = GrayscaleMode.Rec709, float amount = 1, ColorSpace colorSpace = ColorSpace.Encoded)
    {
        ColorMatrix matrix = Operations.ColorMatrix.Grayscale(mode, amount);
        CheckFilter(ResampleFilter.Bicubic, colorSpace);
        return colorSpace == ColorSpace.Linear ? Add(new LinearGrayscaleOperation(mode, amount)) : ColorMatrix(matrix);
    }

    /// <summary>Replaces red, green and blue with 1 − value, blended by <paramref name="amount"/>. See <see cref="Operations.ColorMatrix.Invert"/>.</summary>
    /// <param name="amount">0 changes nothing, 1 inverts fully.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    public RasterOps Invert(float amount = 1)
    {
        return ColorMatrix(Operations.ColorMatrix.Invert(amount));
    }

    /// <summary>Multiplies alpha by <paramref name="amount"/>. Has no effect on pixel types without alpha.</summary>
    /// <param name="amount">0 makes the image transparent, 1 changes nothing.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    public RasterOps Opacity(float amount)
    {
        return ColorMatrix(Operations.ColorMatrix.Opacity(amount));
    }

    /// <summary>
    /// Applies a color matrix to straight RGBA and clamps the result. Adjacent color steps are multiplied into one matrix before
    /// anything runs, so the chain clamps once, at its end.
    /// </summary>
    /// <param name="matrix">The matrix.</param>
    /// <returns>This builder.</returns>
    public RasterOps ColorMatrix(ColorMatrix matrix)
    {
        return Add(new ColorMatrixOperation(matrix));
    }

    private static void CheckFilter(ResampleFilter filter, ColorSpace colorSpace)
    {
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter, null);
        }

        if (!Enum.IsDefined(colorSpace))
        {
            throw new ArgumentOutOfRangeException(nameof(colorSpace), colorSpace, null);
        }
    }
}
