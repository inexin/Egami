using System.Numerics;

using Egami.Barcodes.Rendering;
using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Barcodes;

/// <summary>
/// A barcode symbol: its format, its modules, the quiet zone its standard asks for and the human-readable text that goes with
/// it. It draws on any canvas as vector paths (<see cref="CanvasBarcodeExtensions"/>), becomes one <see cref="Path"/>, or
/// renders to a raster with whole-pixel modules. A barcode is immutable and safe to use from many threads.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-from-modules"/>
/// </example>
public sealed partial class Barcode
{
    private Barcode(BarcodeFormat format, BarcodeModules modules, int quietZone, string? humanReadableText, LinearDecoration? decoration = null)
    {
        Format = format;
        Modules = modules;
        QuietZone = quietZone;
        HumanReadableText = humanReadableText;
        Decoration = decoration;
    }

    /// <summary>Gets the symbology.</summary>
    public BarcodeFormat Format { get; }

    /// <summary>Gets the symbol's modules, without the quiet zone.</summary>
    public BarcodeModules Modules { get; }

    /// <summary>
    /// Gets the width of the clear margin readers need around the symbol, in modules: 4 for QR, 1 for Data Matrix, 2 for PDF417,
    /// 0 for Aztec and 10 at each end of a linear symbol.
    /// </summary>
    public int QuietZone { get; }

    /// <summary>Gets the text printed under a linear symbol, or <see langword="null"/>; it is drawn when <see cref="BarcodeDrawOptions.Font"/> is set.</summary>
    public string? HumanReadableText { get; }

    /// <summary>Gets a linear symbol's guard bars, add-on, bearers and text placement, or <see langword="null"/>.</summary>
    internal LinearDecoration? Decoration { get; }

    /// <summary>Makes a barcode from modules you already have, with the quiet zone of its format's standard.</summary>
    /// <param name="format">The symbology the modules are.</param>
    /// <param name="modules">The modules: a linear symbol for the linear formats, a 2D grid for the others.</param>
    /// <param name="humanReadableText">The text to print under the symbol, or <see langword="null"/>.</param>
    /// <returns>The barcode.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a <see cref="BarcodeFormat"/>.</exception>
    /// <exception cref="ArgumentException">The modules are linear for a 2D format, or a 2D grid for a linear one.</exception>
    public static Barcode FromModules(BarcodeFormat format, BarcodeModules modules, string? humanReadableText = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        int quietZone = format switch
        {
            BarcodeFormat.Qr => 4,
            BarcodeFormat.DataMatrix => 1,
            BarcodeFormat.Pdf417 => 2,
            BarcodeFormat.Aztec => 0,
            BarcodeFormat.Code128 or BarcodeFormat.Code39 or BarcodeFormat.Itf or BarcodeFormat.Ean13 or BarcodeFormat.Ean8
                or BarcodeFormat.UpcA or BarcodeFormat.UpcE => 10,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown barcode format."),
        };
        if (modules.IsLinear != (quietZone == 10))
        {
            throw new ArgumentException(modules.IsLinear ? $"{format} is a 2D format; the modules are linear." : $"{format} is a linear format; the modules are a 2D grid.", nameof(modules));
        }

        return new Barcode(format, modules, quietZone, humanReadableText);
    }

    /// <summary>
    /// Returns the symbol as one path: a rectangle per run of dark modules in a row, all filled with the nonzero rule. The quiet
    /// zone's top-left corner is at the origin, so with the quiet zone on the symbol starts at <see cref="QuietZone"/> modules in.
    /// Linear bars are <see cref="BarcodeModules.Height"/> modules tall. No text is included.
    /// </summary>
    /// <param name="moduleSize">The width of a module in path units; 1 by default.</param>
    /// <param name="options">The quiet zone and bar width reduction, or <see langword="null"/> for <see cref="BarcodeDrawOptions.Default"/>.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="moduleSize"/> is not positive and finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-path"/>
    /// </example>
    public Path ToPath(float moduleSize = 1, BarcodeDrawOptions? options = null)
    {
        if (!(moduleSize > 0 && float.IsFinite(moduleSize)))
        {
            throw new ArgumentOutOfRangeException(nameof(moduleSize), moduleSize, "The module size must be positive and finite.");
        }

        var layout = BarcodeLayout.Create(this, (options ?? BarcodeDrawOptions.Default) with { Font = null });
        return new BarcodeGeometry(Vector2.Zero, moduleSize, moduleSize, Modules.Height * moduleSize).ToPath(layout);
    }

    /// <summary>
    /// Renders the symbol black on white, each module a square of whole pixels, with the quiet zone around it and, when
    /// <see cref="BarcodeDrawOptions.Font"/> is set, the human-readable text under it. Linear bars are
    /// <see cref="BarcodeModules.Height"/> modules tall.
    /// </summary>
    /// <param name="pixelsPerModule">The width of a module in pixels; 1 by default.</param>
    /// <param name="options">The quiet zone, bar width reduction (rounded to whole pixels) and font, or <see langword="null"/> for <see cref="BarcodeDrawOptions.Default"/>.</param>
    /// <returns>The raster, which the caller disposes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pixelsPerModule"/> is not positive, or the raster would be too large.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-raster"/>
    /// </example>
    public Raster<Gray8> ToRaster(int pixelsPerModule = 1, BarcodeDrawOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelsPerModule);
        var layout = BarcodeLayout.Create(this, options ?? BarcodeDrawOptions.Default);
        var geometry = new BarcodeGeometry(Vector2.Zero, pixelsPerModule, pixelsPerModule, (float)Modules.Height * pixelsPerModule, 1, 1);
        double width = (double)layout.Columns * pixelsPerModule;
        double height = Math.Ceiling(layout.Height(pixelsPerModule, pixelsPerModule, geometry.BarHeight) - 1e-3);
        if (width > 1 << 20 || height > 1 << 20)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerModule), pixelsPerModule, $"A {width} by {height} raster is too large.");
        }

        Raster<Gray8> raster = Raster.Create((int)width, (int)height, new Gray8(255));
        try
        {
            // Every edge is a whole pixel: module sizes are whole, and the geometry rounds the rest.
            foreach (RectF rect in geometry.Rectangles(layout))
            {
                int left = Math.Max(0, (int)MathF.Round(rect.X)), right = Math.Min(raster.Width, (int)MathF.Round(rect.X + rect.Width));
                int top = Math.Max(0, (int)MathF.Round(rect.Y)), bottom = Math.Min(raster.Height, (int)MathF.Round(rect.Y + rect.Height));
                for (int y = top; y < bottom && left < right; y++)
                {
                    raster.Row(y)[left..right].Clear();
                }
            }

            List<(string Text, Vector2 Origin)> texts = geometry.Texts(layout);
            if (texts.Count > 0)
            {
                using RasterCanvas<Gray8> canvas = raster.CreateCanvas();
                foreach ((string text, Vector2 at) in texts)
                {
                    canvas.DrawText(text, at, layout.Font, Rgba8.Black);
                }
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }
}
