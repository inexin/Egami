using Egami.Drawing;
using Egami.Fonts;

namespace Egami.Barcodes;

/// <summary>
/// How <see cref="CanvasBarcodeExtensions.DrawBarcode(Canvas, Barcode, RectF, Paint, BarcodeDrawOptions?)"/>,
/// <see cref="Barcode.ToPath(float, BarcodeDrawOptions?)"/> and <see cref="Barcode.ToRaster(int, BarcodeDrawOptions?)"/> draw a
/// barcode.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-print"/>
/// </example>
public sealed record class BarcodeDrawOptions
{
    /// <summary>Gets the defaults: quiet zone on, no bar width reduction, no text.</summary>
    public static BarcodeDrawOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether the symbol's quiet zone (<see cref="Barcode.QuietZone"/>) is kept clear around it;
    /// <see langword="true"/> by default. Readers need it; turn it off only when the space around the symbol is blank anyway.
    /// Linear symbols have it at the left and right only.
    /// </summary>
    public bool QuietZone { get; init; } = true;

    /// <summary>
    /// Gets how much narrower each dark bar or module is drawn, in output units (pixels for
    /// <see cref="Barcode.ToRaster(int, BarcodeDrawOptions?)"/>), to make up for ink spreading on a printer; 0 by default. Half is
    /// taken off each side. 2D modules shrink in height too. It changes drawing only, never the modules.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float BarWidthReduction
    {
        get;
        init => field = value >= 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The bar width reduction must be zero or more.");
    }

    /// <summary>
    /// Gets the font for the human-readable text under the bars (<see cref="Barcode.HumanReadableText"/>), or
    /// <see langword="null"/>, the default, to draw no text. Its size is in output units.
    /// <see cref="Barcode.ToPath(float, BarcodeDrawOptions?)"/> draws no text.
    /// </summary>
    public Font? Font { get; init; }
}
