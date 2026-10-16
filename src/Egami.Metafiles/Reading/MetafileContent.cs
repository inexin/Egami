namespace Egami.Metafiles.Reading;

/// <summary>One WMF or EMF record: its type (a WMF RecordFunction or an EMF record type), where it starts and its size in bytes.</summary>
internal readonly record struct MetaRecord(int Type, int Offset, int Size);

/// <summary>
/// One EMF+ record: its type and flags, its data (an object's pieces already joined), and the index of the EMF record that
/// carries it (the last piece's, for a continued object), so the player can interleave EMF+ and GDI records.
/// </summary>
internal readonly record struct PlusRecord(ushort Type, ushort Flags, ReadOnlyMemory<byte> Data, int EmfIndex);

/// <summary>A metafile's container read and checked: the header facts and the records, nothing drawn yet.</summary>
internal sealed class MetafileContent
{
    /// <summary>Gets the file's bytes, inflated when it was EMZ or WMZ.</summary>
    public required byte[] Data { get; init; }

    /// <summary>Gets what the file is.</summary>
    public required MetafileKind Kind { get; init; }

    /// <summary>Gets the WMF or EMF records in file order, the header and the end-of-file record included.</summary>
    public required List<MetaRecord> Records { get; init; }

    /// <summary>Gets the EMF+ records in file order; empty for WMF and plain EMF.</summary>
    public List<PlusRecord> PlusRecords { get; init; } = [];

    /// <summary>Gets the size of the object table: the WMF header's NumberOfObjects or the EMF header's nHandles.</summary>
    public int ObjectCount { get; init; }

    /// <summary>Gets the placeable WMF bounding box (left, top, right, bottom in logical units), or null.</summary>
    public (short Left, short Top, short Right, short Bottom)? PlaceableBox { get; init; }

    /// <summary>Gets the placeable WMF's logical units per inch.</summary>
    public int UnitsPerInch { get; init; }

    /// <summary>Gets the EMF header's bounds, inclusive, in reference device pixels.</summary>
    public (int Left, int Top, int Right, int Bottom) Bounds { get; init; }

    /// <summary>Gets the EMF header's frame, inclusive, in 0.01 millimetres.</summary>
    public (int Left, int Top, int Right, int Bottom) Frame { get; init; }

    /// <summary>Gets the EMF reference device's size in pixels.</summary>
    public (int Width, int Height) DevicePixels { get; init; }

    /// <summary>Gets the EMF reference device's size in millimetres.</summary>
    public (int Width, int Height) DeviceMillimeters { get; init; }

    /// <summary>Gets the EMF header's record count.</summary>
    public int HeaderRecordCount { get; init; }

    /// <summary>Gets the EMF description, its parts (application, picture) separated by line feeds, or null.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the EMF+ header's logical DPI, or zero when there is no EMF+ header.</summary>
    public (int X, int Y) PlusDpi { get; init; }

    /// <summary>Gets the first window extent a plain WMF sets, or null.</summary>
    public (short X, short Y)? WindowExtent { get; init; }
}
