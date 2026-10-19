using System.Numerics;

namespace Egami.Barcodes;

/// <summary>A barcode found in an image: what it is, what it holds and where it is.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-read"/>
/// </example>
public sealed class BarcodeResult
{
    internal BarcodeResult(BarcodeFormat format, string text, byte[] bytes, Vector2[] corners)
    {
        Format = format;
        Text = text;
        Bytes = bytes;
        Corners = corners;
    }

    /// <summary>Gets the symbology.</summary>
    public BarcodeFormat Format { get; }

    /// <summary>
    /// Gets the text: the data decoded with the symbol's ECI or, without one, as ISO 8859-1 (or UTF-8 or Shift JIS when the bytes
    /// are clearly that). GS1 data is in its human-readable form, such as <c>(01)09501101530003(10)AB12</c>. For EAN and UPC,
    /// the main digits only; an add-on is in <see cref="AddOn"/>.
    /// </summary>
    public string Text { get; }

    /// <summary>Gets the bytes the symbol holds, before any character set is applied; GS1 separators are byte 29.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>
    /// Gets the symbol's corners in image coordinates (pixels), clockwise from the top left of the symbol as it reads; for a
    /// linear symbol, the ends of the scan line(s) it was read on.
    /// </summary>
    public IReadOnlyList<Vector2> Corners { get; }

    /// <summary>Gets the Extended Channel Interpretation the symbol named, or <see langword="null"/>.</summary>
    public int? Eci { get; init; }

    /// <summary>Gets the GS1 element strings when the symbol is GS1 data (GS1-128, GS1 QR, GS1 Data Matrix), or <see langword="null"/>.</summary>
    public IReadOnlyList<Gs1ElementString>? Gs1 { get; init; }

    /// <summary>Gets the number of codewords error correction restored (0 for linear symbols).</summary>
    public int ErrorsCorrected { get; init; }

    /// <summary>Gets a value indicating whether the symbol was light on dark.</summary>
    public bool Inverted { get; init; }

    /// <summary>Gets a value indicating whether the symbol was mirrored.</summary>
    public bool Mirrored { get; init; }

    /// <summary>Gets the digits of an EAN-2 or EAN-5 add-on read after an EAN or UPC symbol, or <see langword="null"/> when there is none.</summary>
    public string? AddOn { get; init; }

    /// <inheritdoc/>
    public override string ToString() => $"{Format}: {Text}";
}
