using System.Numerics;

using Egami.Barcodes.Rendering;
using Egami.Drawing;
using Egami.Fonts;

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
        Layout layout = Layout.Create(barcode, options);

        // The module size that fits: by width for linear symbols, by both sides for 2D ones (the text takes one module of gap
        // plus its line).
        float module = rect.Width / layout.Columns;
        if (!barcode.Modules.IsLinear)
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

        float width = layout.Columns * moduleWidth;
        float barHeight, height;
        if (barcode.Modules.IsLinear)
        {
            barHeight = rect.Height - layout.TextBlock(moduleWidth);
            if (snap)
            {
                barHeight = MathF.Floor(barHeight * MathF.Abs(matrix.M22)) / MathF.Abs(matrix.M22);
            }

            height = barHeight + layout.TextBlock(moduleWidth);
        }
        else
        {
            barHeight = barcode.Modules.Height * moduleHeight;
            height = (layout.Rows * moduleHeight) + layout.TextBlock(moduleHeight);
        }

        if (!(barHeight > 0))
        {
            return;
        }

        var origin = new Vector2(rect.X + ((rect.Width - width) / 2), rect.Y + ((rect.Height - height) / 2));
        Draw(canvas, barcode, options, layout, origin, moduleWidth, moduleHeight, barHeight, snap ? matrix : null, paint);
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
        Layout layout = Layout.Create(barcode, options);
        float moduleWidth = moduleSize, moduleHeight = moduleSize;
        Matrix3x2 matrix = canvas.Matrix;
        bool snap = Snaps(canvas, matrix);
        if (snap)
        {
            moduleWidth = MathF.Max(1, MathF.Round(moduleSize * MathF.Abs(matrix.M11))) / MathF.Abs(matrix.M11);
            moduleHeight = MathF.Max(1, MathF.Round(moduleSize * MathF.Abs(matrix.M22))) / MathF.Abs(matrix.M22);
        }

        float barHeight = barcode.Modules.Height * (barcode.Modules.IsLinear ? moduleWidth : moduleHeight);
        Draw(canvas, barcode, options, layout, origin, moduleWidth, moduleHeight, barHeight, snap ? matrix : null, paint);
    }

    /// <summary>Draws the symbol and its text from the quiet zone's top-left corner.</summary>
    private static void Draw(
        Canvas canvas,
        Barcode barcode,
        BarcodeDrawOptions options,
        Layout layout,
        Vector2 origin,
        float moduleWidth,
        float moduleHeight,
        float barHeight,
        Matrix3x2? snapTo,
        Paint paint)
    {
        bool linear = barcode.Modules.IsLinear;
        float x = origin.X + (layout.QuietZone * moduleWidth), y = origin.Y + (linear ? 0 : layout.QuietZone * moduleHeight);
        float reduction = options.BarWidthReduction;
        float shrinkLeft = reduction / 2, shrinkRight = reduction / 2, shrinkTop = linear ? 0 : reduction / 2, shrinkBottom = shrinkTop;
        if (snapTo is Matrix3x2 m)
        {
            // Put the symbol's corner on a pixel corner; module sizes are already whole pixels.
            x = (MathF.Round((m.M11 * x) + m.M31) - m.M31) / m.M11;
            y = (MathF.Round((m.M22 * y) + m.M32) - m.M32) / m.M22;
            float sx = MathF.Abs(m.M11), sy = MathF.Abs(m.M22);
            float pixelsX = MathF.Round(reduction * sx), pixelsY = linear ? 0 : MathF.Round(reduction * sy);
            shrinkLeft = MathF.Floor(pixelsX / 2) / sx;
            shrinkRight = (pixelsX - MathF.Floor(pixelsX / 2)) / sx;
            shrinkTop = MathF.Floor(pixelsY / 2) / sy;
            shrinkBottom = (pixelsY - MathF.Floor(pixelsY / 2)) / sy;
        }

        var geometry = new BarcodeGeometry(x, y, moduleWidth, moduleHeight, barHeight, shrinkLeft, shrinkRight, shrinkTop, shrinkBottom);
        Path path = geometry.ToPath(barcode.Modules);
        if (!path.IsEmpty)
        {
            canvas.FillPath(path, paint);
        }

        if (layout.Text is string text)
        {
            float center = x + (barcode.Modules.Width * moduleWidth / 2);
            // Under the bars, or for a 2D symbol under its quiet zone, after one module of gap.
            float below = linear ? y + barHeight + moduleWidth : y + barHeight + ((layout.QuietZone + 1) * moduleHeight);
            canvas.DrawText(text, new Vector2(center - (layout.Measure.Advance / 2), below + layout.Measure.Ascent), layout.Font, paint);
        }
    }

    /// <summary>Returns whether to snap to whole pixels: a raster canvas whose transform only scales and moves.</summary>
    private static bool Snaps(Canvas canvas, Matrix3x2 matrix) =>
        canvas.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(RasterCanvas<>)
        && matrix.M12 == 0 && matrix.M21 == 0 && matrix.M11 != 0 && matrix.M22 != 0;

    /// <summary>The symbol's size in modules with its quiet zone, and its text line.</summary>
    private readonly record struct Layout(int QuietZone, int Columns, int Rows, string? Text, Font Font, TextMeasure Measure)
    {
        public bool HasText => Text is not null;

        /// <summary>Gets the height of the text line alone.</summary>
        public float TextLine => HasText ? Measure.Ascent - Measure.Descent : 0;

        public static Layout Create(Barcode barcode, BarcodeDrawOptions options)
        {
            int quiet = options.QuietZone ? barcode.QuietZone : 0;
            string? text = barcode.TextToDraw(options, out Font font);
            BarcodeModules modules = barcode.Modules;
            return new Layout(
                quiet,
                modules.Width + (2 * quiet),
                modules.IsLinear ? modules.Height : modules.Height + (2 * quiet),
                text,
                font,
                text is null ? default : font.MeasureText(text));
        }

        /// <summary>Returns the height under the bars the text takes: one module of gap and the text line.</summary>
        public float TextBlock(float module) => HasText ? module + TextLine : 0;
    }
}
