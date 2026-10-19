using Egami.Barcodes.Reading;
using Egami.Pixels;

namespace Egami.Barcodes;

/// <summary>
/// Reads barcodes from images: <c>raster.ReadBarcodes()</c> finds every symbol, <c>raster.ReadBarcode()</c> the first. The
/// reader is thread-safe and allocates per call only.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-read"/>
/// </example>
public static class BarcodeReader
{
    /// <summary>Finds and decodes every barcode of the allowed formats in an image.</summary>
    /// <typeparam name="TPixel">The pixel type; any is converted to luminance first.</typeparam>
    /// <param name="raster">The image.</param>
    /// <param name="options">The formats, effort and region, or <see langword="null"/> for <see cref="BarcodeReaderOptions.Default"/>.</param>
    /// <returns>The barcodes found, in reading order (top to bottom, then left to right); empty when there are none.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-read"/>
    /// </example>
    public static IReadOnlyList<BarcodeResult> ReadBarcodes<TPixel>(this Raster<TPixel> raster, BarcodeReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        options ??= BarcodeReaderOptions.Default;
        return Search.Run(LuminanceImage.From(raster, options.Region), options, firstOnly: false);
    }

    /// <summary>Finds and decodes the first barcode of the allowed formats in an image.</summary>
    /// <typeparam name="TPixel">The pixel type; any is converted to luminance first.</typeparam>
    /// <param name="raster">The image.</param>
    /// <param name="options">The formats, effort and region, or <see langword="null"/> for <see cref="BarcodeReaderOptions.Default"/>.</param>
    /// <returns>The barcode, or <see langword="null"/> when there is none.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-read-options"/>
    /// </example>
    public static BarcodeResult? ReadBarcode<TPixel>(this Raster<TPixel> raster, BarcodeReaderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        options ??= BarcodeReaderOptions.Default;
        IReadOnlyList<BarcodeResult> results = Search.Run(LuminanceImage.From(raster, options.Region), options, firstOnly: true);
        return results.Count > 0 ? results[0] : null;
    }
}
