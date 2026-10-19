using System.Numerics;

using Egami.Barcodes.Rendering;
using Egami.Drawing;

namespace Egami.Barcodes;

/// <summary>Draws barcodes on any <see cref="Canvas"/>: rasters, PDF and SVG pages, recordings.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-draw"/>
/// </example>
public static class CanvasBarcodeExtensions
{
    /// <summary>
    /// Draws a barcode fitted into a rectangle and centered in it, as one filled path, with its quiet zone kept clear inside the
    /// rectangle. 2D symbols keep square modules; linear bars fill the rectangle's height, less the text line. On a raster canvas
    /// with no rotation or skew, modules snap to whole pixels (as large as still fit), so edges stay sharp.
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="barcode">The barcode.</param>
    /// <param name="rect">The rectangle, in local coordinates.</param>
    /// <param name="paint">The paint for the bars and the text.</param>
    /// <param name="options">The quiet zone, bar width reduction and font, or <see langword="null"/> for <see cref="BarcodeDrawOptions.Default"/>.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-draw"/>
    /// </example>
    public static void DrawBarcode(this Canvas canvas, Barcode barcode, RectF rect, Paint paint, BarcodeDrawOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(barcode);
        ArgumentNullException.ThrowIfNull(paint);
        options ??= BarcodeDrawOptions.Default;
        var layout = BarcodeLayout.Create(barcode, options);
        bool linear = barcode.Modules.IsLinear;

        // The module size that fits: by width for linear symbols, by both sides for 2D ones (the text takes one module of gap
        // plus its line).
        float module = rect.Width / layout.Columns;
        if (!linear)
        {
            module = MathF.Min(module, (rect.Height - layout.TextLine) / (layout.Rows + (layout.HasText ? 1 : 0)));
        }

        if (!(module > 0 && float.IsFinite(module)))
        {
            return;
        }

        float moduleWidth = module, moduleHeight = module;
        Matrix3x2 matrix = canvas.Matrix;
        bool snap = Snaps(canvas, matrix);
        if (snap)
        {
            float pixelsX = MathF.Floor(module * MathF.Abs(matrix.M11)), pixelsY = MathF.Floor(module * MathF.Abs(matrix.M22));
            snap = pixelsX >= 1 && pixelsY >= 1;
            if (snap)
            {
                moduleWidth = pixelsX / MathF.Abs(matrix.M11);
                moduleHeight = pixelsY / MathF.Abs(matrix.M22);
            }
        }

        float barHeight = linear ? rect.Height - layout.Height(moduleWidth, moduleHeight, 0) : barcode.Modules.Height * moduleHeight;
        if (snap && linear)
        {
            barHeight = MathF.Floor(barHeight * MathF.Abs(matrix.M22)) / MathF.Abs(matrix.M22);
        }

        if (!(barHeight > 0))
        {
            return;
        }

        float width = layout.Columns * moduleWidth, height = layout.Height(moduleWidth, moduleHeight, barHeight);
        var origin = new Vector2(rect.X + ((rect.Width - width) / 2), rect.Y + ((rect.Height - height) / 2));
        Draw(canvas, layout, origin, moduleWidth, moduleHeight, barHeight, snap ? matrix : null, paint);
    }

    /// <summary>
    /// Draws a barcode at a given module size, with the top-left corner of its quiet zone at a point. Linear bars are
    /// <see cref="BarcodeModules.Height"/> modules tall. On a raster canvas with no rotation or skew, the module size rounds to
    /// whole pixels and the symbol to the pixel grid, so edges stay sharp.
    /// </summary>
    /// <param name="canvas">The canvas.</param>
    /// <param name="barcode">The barcode.</param>
    /// <param name="origin">The top-left corner of the quiet zone, in local coordinates.</param>
    /// <param name="moduleSize">The width of a module in local units, the X dimension of the standards.</param>
    /// <param name="paint">The paint for the bars and the text.</param>
    /// <param name="options">The quiet zone, bar width reduction and font, or <see langword="null"/> for <see cref="BarcodeDrawOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="moduleSize"/> is not positive and finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-print"/>
    /// </example>
    public static void DrawBarcode(this Canvas canvas, Barcode barcode, Vector2 origin, float moduleSize, Paint paint, BarcodeDrawOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(barcode);
        ArgumentNullException.ThrowIfNull(paint);
        if (!(moduleSize > 0 && float.IsFinite(moduleSize)))
        {
            throw new ArgumentOutOfRangeException(nameof(moduleSize), moduleSize, "The module size must be positive and finite.");
        }

        options ??= BarcodeDrawOptions.Default;
        var layout = BarcodeLayout.Create(barcode, options);
        float moduleWidth = moduleSize, moduleHeight = moduleSize;
        Matrix3x2 matrix = canvas.Matrix;
        bool snap = Snaps(canvas, matrix);
        if (snap)
        {
            moduleWidth = MathF.Max(1, MathF.Round(moduleSize * MathF.Abs(matrix.M11))) / MathF.Abs(matrix.M11);
            moduleHeight = MathF.Max(1, MathF.Round(moduleSize * MathF.Abs(matrix.M22))) / MathF.Abs(matrix.M22);
        }

        float barHeight = barcode.Modules.Height * (barcode.Modules.IsLinear ? moduleWidth : moduleHeight);
        Draw(canvas, layout, origin, moduleWidth, moduleHeight, barHeight, snap ? matrix : null, paint);
    }

    /// <summary>Draws the symbol and its text from the top-left corner of its quiet zone.</summary>
    private static void Draw(Canvas canvas, BarcodeLayout layout, Vector2 origin, float moduleWidth, float moduleHeight, float barHeight, Matrix3x2? snapTo, Paint paint)
    {
        float pixelsX = 0, pixelsY = 0;
        if (snapTo is Matrix3x2 m)
        {
            // Put the corner on a pixel corner; module sizes are already whole pixels.
            origin = new Vector2((MathF.Round((m.M11 * origin.X) + m.M31) - m.M31) / m.M11, (MathF.Round((m.M22 * origin.Y) + m.M32) - m.M32) / m.M22);
            pixelsX = MathF.Abs(m.M11);
            pixelsY = MathF.Abs(m.M22);
        }

        var geometry = new BarcodeGeometry(origin, moduleWidth, moduleHeight, barHeight, pixelsX, pixelsY);
        Path path = geometry.ToPath(layout);
        if (!path.IsEmpty)
        {
            canvas.FillPath(path, paint);
        }

        foreach ((string text, Vector2 at) in geometry.Texts(layout))
        {
            canvas.DrawText(text, at, layout.Font, paint);
        }
    }

    /// <summary>Returns whether to snap to whole pixels: a raster canvas whose transform only scales and moves.</summary>
    private static bool Snaps(Canvas canvas, Matrix3x2 matrix) =>
        canvas.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(RasterCanvas<>)
        && matrix.M12 == 0 && matrix.M21 == 0 && matrix.M11 != 0 && matrix.M22 != 0;
}
