using System.Buffers;

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

    /// <summary>
    /// Finds and decodes every barcode in an 8-bit luminance buffer, such as a camera frame's Y plane, without copying it.
    /// </summary>
    /// <param name="luminance">The pixels, 0 black to 255 white, row by row.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="stride">The bytes from one row's start to the next's, at least <paramref name="width"/>.</param>
    /// <param name="options">The formats, effort and region, or <see langword="null"/> for <see cref="BarcodeReaderOptions.Default"/>.</param>
    /// <returns>The barcodes found, in reading order (top to bottom, then left to right); empty when there are none.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size or stride is not positive, or the stride is less than the width.</exception>
    /// <exception cref="ArgumentException">The buffer is too short for the size and stride.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-read-luminance"/>
    /// </example>
    public static IReadOnlyList<BarcodeResult> ReadBarcodes(ReadOnlySpan<byte> luminance, int width, int height, int stride, BarcodeReaderOptions? options = null) =>
        Read(luminance, width, height, stride, options, firstOnly: false);

    /// <summary>Finds and decodes the first barcode in an 8-bit luminance buffer, such as a camera frame's Y plane, without copying it.</summary>
    /// <param name="luminance">The pixels, 0 black to 255 white, row by row.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="stride">The bytes from one row's start to the next's, at least <paramref name="width"/>.</param>
    /// <param name="options">The formats, effort and region, or <see langword="null"/> for <see cref="BarcodeReaderOptions.Default"/>.</param>
    /// <returns>The barcode, or <see langword="null"/> when there is none.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The size or stride is not positive, or the stride is less than the width.</exception>
    /// <exception cref="ArgumentException">The buffer is too short for the size and stride.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-read-luminance"/>
    /// </example>
    public static BarcodeResult? ReadBarcode(ReadOnlySpan<byte> luminance, int width, int height, int stride, BarcodeReaderOptions? options = null)
    {
        IReadOnlyList<BarcodeResult> results = Read(luminance, width, height, stride, options, firstOnly: true);
        return results.Count > 0 ? results[0] : null;
    }

    private static unsafe IReadOnlyList<BarcodeResult> Read(ReadOnlySpan<byte> luminance, int width, int height, int stride, BarcodeReaderOptions? options, bool firstOnly)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        if (luminance.Length < ((long)(height - 1) * stride) + width)
        {
            throw new ArgumentException($"The buffer holds {luminance.Length} bytes; {width} x {height} at stride {stride} needs {((long)(height - 1) * stride) + width}.", nameof(luminance));
        }

        options ??= BarcodeReaderOptions.Default;

        // The buffer is pinned and viewed for the length of the search, which may run on several threads.
        fixed (byte* pointer = luminance)
        {
            using var memory = new PinnedMemory(pointer, luminance.Length);
            return Search.Run(LuminanceImage.View(memory.Memory, width, height, stride, options.Region), options, firstOnly);
        }
    }

    /// <summary>Memory over a pinned buffer, valid while it stays pinned.</summary>
    private sealed unsafe class PinnedMemory(byte* pointer, int length) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => new(pointer, length);

        public override MemoryHandle Pin(int elementIndex = 0) => new(pointer + elementIndex);

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
