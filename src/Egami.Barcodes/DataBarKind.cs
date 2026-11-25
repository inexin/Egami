namespace Egami.Barcodes;

/// <summary>The forms of GS1 DataBar Omnidirectional that <see cref="Barcode.DataBar(string, DataBarOptions?)"/> makes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-databar"/>
/// </example>
public enum DataBarKind
{
    /// <summary>One row, 96 modules wide and 33 tall, for scanners at any angle; the default.</summary>
    Omnidirectional,

    /// <summary>One row 13 modules tall, for small items read by hand-held scanners.</summary>
    Truncated,

    /// <summary>The two halves stacked, 5 and 7 modules tall, for small items read by hand-held scanners.</summary>
    Stacked,

    /// <summary>The two halves stacked, each 33 modules tall, for scanners at any angle.</summary>
    StackedOmnidirectional,
}
