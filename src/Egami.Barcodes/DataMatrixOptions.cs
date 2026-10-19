namespace Egami.Barcodes;

/// <summary>Which sizes a Data Matrix symbol may take.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-datamatrix"/>
/// </example>
public enum DataMatrixShape
{
    /// <summary>Square symbols, 10 x 10 to 144 x 144; every reader reads them.</summary>
    Square,

    /// <summary>Rectangular symbols, 8 x 18 to 16 x 48 (and the DMRE sizes when <see cref="DataMatrixOptions.AllowDmre"/> is set), for narrow spaces.</summary>
    Rectangle,

    /// <summary>The smallest square or rectangular symbol.</summary>
    Any,
}

/// <summary>How <see cref="Barcode.DataMatrix(string, DataMatrixOptions?)"/> makes a symbol.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-datamatrix"/>
/// </example>
public sealed record class DataMatrixOptions
{
    /// <summary>Gets the defaults: square symbols.</summary>
    public static DataMatrixOptions Default { get; } = new();

    /// <summary>Gets the sizes the symbol may take; <see cref="DataMatrixShape.Square"/> by default.</summary>
    public DataMatrixShape Shape { get; init; }

    /// <summary>
    /// Gets a value indicating whether the rectangular sizes of ISO/IEC 21471 (DMRE, 8 x 48 to 26 x 64) may be used too; off by
    /// default, since older readers do not read them.
    /// </summary>
    public bool AllowDmre { get; init; }
}
