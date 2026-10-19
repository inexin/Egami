namespace Egami.Barcodes;

/// <summary>
/// A barcode symbology, or a set of them: <see cref="Barcode.Format"/> and <see cref="BarcodeResult.Format"/> hold one, and
/// <see cref="BarcodeReaderOptions.Formats"/> any combination.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-from-modules"/>
/// </example>
[Flags]
public enum BarcodeFormat
{
    /// <summary>No symbology.</summary>
    None = 0,

    /// <summary>QR Code (ISO/IEC 18004); quiet zone 4 modules.</summary>
    Qr = 1 << 0,

    /// <summary>Data Matrix ECC 200 (ISO/IEC 16022); quiet zone 1 module.</summary>
    DataMatrix = 1 << 1,

    /// <summary>PDF417 (ISO/IEC 15438); quiet zone 2 modules.</summary>
    Pdf417 = 1 << 2,

    /// <summary>Aztec Code (ISO/IEC 24778); no quiet zone, as its finder is in the middle.</summary>
    Aztec = 1 << 3,

    /// <summary>Code 128 and GS1-128 (ISO/IEC 15417), linear; quiet zone 10 modules.</summary>
    Code128 = 1 << 4,

    /// <summary>Code 39 (ISO/IEC 16388), linear; quiet zone 10 modules.</summary>
    Code39 = 1 << 5,

    /// <summary>Interleaved 2 of 5 and ITF-14 (ISO/IEC 16390), linear; quiet zone 10 modules.</summary>
    Itf = 1 << 6,

    /// <summary>EAN-13 (ISO/IEC 15420), linear; quiet zone 11 modules.</summary>
    Ean13 = 1 << 7,

    /// <summary>EAN-8 (ISO/IEC 15420), linear; quiet zone 7 modules.</summary>
    Ean8 = 1 << 8,

    /// <summary>UPC-A (ISO/IEC 15420), linear; quiet zone 9 modules.</summary>
    UpcA = 1 << 9,

    /// <summary>UPC-E (ISO/IEC 15420), linear; quiet zone 9 modules.</summary>
    UpcE = 1 << 10,

    /// <summary>The linear symbologies.</summary>
    Linear = Code128 | Code39 | Itf | Ean13 | Ean8 | UpcA | UpcE,

    /// <summary>The 2D symbologies.</summary>
    Matrix = Qr | DataMatrix | Pdf417 | Aztec,

    /// <summary>Every symbology.</summary>
    All = Linear | Matrix,
}
