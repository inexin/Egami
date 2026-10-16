namespace Egami.Metafiles;

/// <summary>What a <see cref="Metafile"/> holds.</summary>
public enum MetafileKind
{
    /// <summary>A Windows Metafile (16-bit records), with or without a placeable header; WMZ when gzip-compressed.</summary>
    Wmf,

    /// <summary>An Enhanced Metafile with GDI records only; EMZ when gzip-compressed.</summary>
    Emf,

    /// <summary>An EMF with EMF+ records and, beside them, GDI records that draw the same picture for programs without GDI+.</summary>
    EmfPlusDual,

    /// <summary>An EMF whose picture is in EMF+ records only.</summary>
    EmfPlusOnly,
}
