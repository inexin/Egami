namespace Egami.Barcodes;

/// <summary>A barcode symbology.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-from-modules"/>
/// </example>
public enum BarcodeFormat
{
    /// <summary>QR Code (ISO/IEC 18004); quiet zone 4 modules.</summary>
    Qr,

    /// <summary>Data Matrix ECC 200 (ISO/IEC 16022); quiet zone 1 module.</summary>
    DataMatrix,

    /// <summary>PDF417 (ISO/IEC 15438); quiet zone 2 modules.</summary>
    Pdf417,

    /// <summary>Aztec Code (ISO/IEC 24778); no quiet zone, as its finder is in the middle.</summary>
    Aztec,

    /// <summary>Code 128 and GS1-128 (ISO/IEC 15417), linear; quiet zone 10 modules.</summary>
    Code128,

    /// <summary>Code 39 (ISO/IEC 16388), linear; quiet zone 10 modules.</summary>
    Code39,

    /// <summary>Interleaved 2 of 5 and ITF-14 (ISO/IEC 16390), linear; quiet zone 10 modules.</summary>
    Itf,

    /// <summary>EAN-13 (ISO/IEC 15420), linear; quiet zone 10 modules.</summary>
    Ean13,

    /// <summary>EAN-8 (ISO/IEC 15420), linear; quiet zone 10 modules.</summary>
    Ean8,

    /// <summary>UPC-A (ISO/IEC 15420), linear; quiet zone 10 modules.</summary>
    UpcA,

    /// <summary>UPC-E (ISO/IEC 15420), linear; quiet zone 10 modules.</summary>
    UpcE,
}
