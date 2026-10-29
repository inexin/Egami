using System.ComponentModel;
using System.Runtime.InteropServices;

using Egami.Pixels;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Imaging;

/// <summary>
/// A WMF, EMF or EMF+ picture, read and drawn by Egami.Metafiles: an <see cref="Image"/> sized by its frame, which
/// <see cref="Graphics.DrawImage(Image, float, float)"/> plays as vectors and <see cref="Graphics.EnumerateMetafile(Metafile, PointF, Graphics.EnumerateMetafileProc)"/>
/// walks record by record. Egami reads metafiles but does not record them, so the recording constructors throw.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="metafiles"/>
/// </example>
public sealed partial class Metafile : Image
{
    /// <summary>GDI+'s image flags for a metafile: scalable, alpha, read-only and a resolution of its own.</summary>
    private const int MetafileFlags = 327683;

    private readonly MetafileScan _scan;
    private bool _played;

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class from a file.</summary>
    /// <param name="filename">The file.</param>
    public Metafile(string filename)
        : this(File.ReadAllBytes(filename ?? throw new ArgumentNullException(nameof(filename))))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Metafile"/> class from a stream.</summary>
    /// <param name="stream">The stream.</param>
    public Metafile(Stream stream)
        : this(ReadAll(stream))
    {
    }

    internal Metafile(byte[] data)
        : this(MetafileScan.Read(data))
    {
    }

    private Metafile(MetafileScan scan)
    {
        _scan = scan;
        try
        {
            Picture = Egami.Metafiles.Metafile.Load(scan.Data);
        }
        catch (EgamiException)
        {
            throw GdiErrors.Generic();
        }

        Rectangle bounds = scan.Bounds;
        Frame frame = new(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format32bppArgb);
        Rasterize(frame);
        SetFrames([frame], FrameDimension.Page.Guid);
        SetFileState(scan.Type is MetafileType.Wmf or MetafileType.WmfPlaceable ? ImageFormat.Wmf.Guid : ImageFormat.Emf.Guid, MetafileFlags, scan.Dpi.X, scan.Dpi.Y);
    }

    /// <summary>Gets the picture Egami.Metafiles plays.</summary>
    internal Egami.Metafiles.Metafile Picture { get; }

    /// <summary>Gets the frame in pixels at the metafile's resolution, as GDI+ maps a source rectangle onto it.</summary>
    internal RectangleF FrameBounds => _scan.Frame;

    /// <summary>Gets the records as GDI+ enumerates them.</summary>
    internal MetafileScan Scan => _scan;

    /// <summary>The header.</summary>
    /// <returns>The header.</returns>
    public MetafileHeader GetMetafileHeader() => new(_scan);

    /// <summary>The header of a metafile file.</summary>
    /// <param name="fileName">The file.</param>
    /// <returns>The header.</returns>
    public static MetafileHeader GetMetafileHeader(string fileName) => new(MetafileScan.Read(File.ReadAllBytes(fileName ?? throw new ArgumentNullException(nameof(fileName)))));

    /// <summary>The header of a metafile stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The header.</returns>
    public static MetafileHeader GetMetafileHeader(Stream stream) => new(MetafileScan.Read(ReadAll(stream)));

    /// <inheritdoc/>
    public override object Clone() => new Metafile(_scan.Data);

    /// <summary>
    /// Marks a record as played during <see cref="Graphics.EnumerateMetafile(Metafile, PointF, Graphics.EnumerateMetafileProc)"/>:
    /// Egami plays metafiles whole, so once the walk ends the whole metafile is drawn.
    /// </summary>
    /// <param name="recordType">The record's type.</param>
    /// <param name="flags">Its flags.</param>
    /// <param name="dataSize">The size of its data.</param>
    /// <param name="data">Its data, or null.</param>
    public void PlayRecord(EmfPlusRecordType recordType, int flags, int dataSize, byte[]? data) => _played = true;

    /// <summary>Whether a record was played since the last call, clearing the mark.</summary>
    internal bool TakePlayed()
    {
        bool played = _played;
        _played = false;
        return played;
    }

    /// <summary>Draws the picture into its frame-sized pixels, transparent around it, so the image can be saved and copied as GDI+ allows.</summary>
    private void Rasterize(Frame frame)
    {
        using Raster<Bgra8> pixels = Picture.Rasterize<Bgra8>(new Egami.Metafiles.MetafileRenderOptions { Width = frame.Width, Height = frame.Height });
        for (int y = 0; y < frame.Height; y++)
        {
            MemoryMarshal.Cast<Bgra8, byte>(pixels.Row(y)).CopyTo(frame.Row(y));
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>A metafile's header: its type, size, resolution and version, as GDI+ reports them.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="metafiles"/>
/// </example>
public sealed class MetafileHeader
{
    private readonly MetafileScan _scan;

    internal MetafileHeader(MetafileScan scan)
    {
        _scan = scan;
    }

    /// <summary>Gets what the metafile is.</summary>
    public MetafileType Type => _scan.Type;

    /// <summary>Gets the version: the WMF header's, the EMF header's, or for EMF+ the EMF+ header's.</summary>
    public int Version => _scan.Version;

    /// <summary>Gets the frame in pixels, rounded.</summary>
    public Rectangle Bounds => _scan.Bounds;

    /// <summary>Gets the horizontal resolution of the device the metafile was made for.</summary>
    public float DpiX => _scan.Dpi.X;

    /// <summary>Gets the vertical resolution of the device the metafile was made for.</summary>
    public float DpiY => _scan.Dpi.Y;

    /// <summary>Gets the EMF+ header's logical horizontal resolution, or 0.</summary>
    public int LogicalDpiX => _scan.LogicalDpi.X;

    /// <summary>Gets the EMF+ header's logical vertical resolution, or 0.</summary>
    public int LogicalDpiY => _scan.LogicalDpi.Y;

    /// <summary>Gets the size in bytes: the EMF's, or the WMF's without a placeable header.</summary>
    public int MetafileSize => _scan.Size;

    /// <summary>Gets the size of the EMF+ header record, or 0.</summary>
    public int EmfPlusHeaderSize => _scan.EmfPlusHeaderSize;

    /// <summary>Gets the WMF header.</summary>
    /// <exception cref="ArgumentException">The metafile is not a WMF.</exception>
    public MetaHeader WmfHeader => _scan.WmfHeader ?? throw GdiErrors.InvalidParameter();

    /// <summary>Whether the metafile is a WMF.</summary>
    /// <returns><see langword="true"/> for WMF.</returns>
    public bool IsWmf() => Type is MetafileType.Wmf or MetafileType.WmfPlaceable;

    /// <summary>Whether the metafile is a placeable WMF.</summary>
    /// <returns><see langword="true"/> for a placeable WMF.</returns>
    public bool IsWmfPlaceable() => Type == MetafileType.WmfPlaceable;

    /// <summary>Whether the metafile is a plain EMF.</summary>
    /// <returns><see langword="true"/> for EMF without EMF+.</returns>
    public bool IsEmf() => Type == MetafileType.Emf;

    /// <summary>Whether the metafile is an EMF, with or without EMF+.</summary>
    /// <returns><see langword="true"/> for EMF and EMF+.</returns>
    public bool IsEmfOrEmfPlus() => Type is MetafileType.Emf or MetafileType.EmfPlusOnly or MetafileType.EmfPlusDual;

    /// <summary>Whether the metafile has EMF+ records.</summary>
    /// <returns><see langword="true"/> for EMF+.</returns>
    public bool IsEmfPlus() => Type is MetafileType.EmfPlusOnly or MetafileType.EmfPlusDual;

    /// <summary>Whether the metafile has both EMF+ and GDI records.</summary>
    /// <returns><see langword="true"/> for dual EMF+.</returns>
    public bool IsEmfPlusDual() => Type == MetafileType.EmfPlusDual;

    /// <summary>Whether the metafile has only EMF+ records.</summary>
    /// <returns><see langword="true"/> for EMF+ only.</returns>
    public bool IsEmfPlusOnly() => Type == MetafileType.EmfPlusOnly;

    /// <summary>Whether an EMF+ metafile was recorded for a display.</summary>
    /// <returns><see langword="true"/> when its EMF+ header says so.</returns>
    public bool IsDisplay() => IsEmfPlus() && _scan.Display;
}

/// <summary>A WMF's own header ([MS-WMF] 2.3.2.2).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="metafiles"/>
/// </example>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public sealed class MetaHeader
{
    /// <summary>Gets or sets where the metafile lives: 1 in memory, 2 on disk.</summary>
    public short Type { get; set; }

    /// <summary>Gets or sets the header's size in 16-bit words, 9.</summary>
    public short HeaderSize { get; set; }

    /// <summary>Gets or sets the version, 0x300 or 0x100.</summary>
    public short Version { get; set; }

    /// <summary>Gets or sets the metafile's size in 16-bit words.</summary>
    public int Size { get; set; }

    /// <summary>Gets or sets the most objects in use at once.</summary>
    public short NoObjects { get; set; }

    /// <summary>Gets or sets the largest record's size in 16-bit words.</summary>
    public int MaxRecord { get; set; }

    /// <summary>Gets or sets an unused field.</summary>
    public short NoParameters { get; set; }
}

/// <summary>A placeable WMF's header: its box in its own units and how many make an inch.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="metafiles"/>
/// </example>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public sealed class WmfPlaceableFileHeader
{
    /// <summary>Gets or sets the key, 0x9AC6CDD7.</summary>
    public int Key { get; set; } = unchecked((int)0x9AC6CDD7);

    /// <summary>Gets or sets a handle field, 0 in files.</summary>
    public short Hmf { get; set; }

    /// <summary>Gets or sets the box's left edge.</summary>
    public short BboxLeft { get; set; }

    /// <summary>Gets or sets the box's top edge.</summary>
    public short BboxTop { get; set; }

    /// <summary>Gets or sets the box's right edge.</summary>
    public short BboxRight { get; set; }

    /// <summary>Gets or sets the box's bottom edge.</summary>
    public short BboxBottom { get; set; }

    /// <summary>Gets or sets the units to the inch.</summary>
    public short Inch { get; set; }

    /// <summary>Gets or sets a reserved field.</summary>
    public int Reserved { get; set; }

    /// <summary>Gets or sets the checksum of the header's first ten words.</summary>
    public short Checksum { get; set; }
}

/// <summary>Plays a record back while a metafile is enumerated; Egami does not play single records, so it is never given.</summary>
/// <param name="recordType">The record's type.</param>
/// <param name="flags">Its flags.</param>
/// <param name="dataSize">The size of its data.</param>
/// <param name="recordData">Its data.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="metafiles"/>
/// </example>
public delegate void PlayRecordCallback(EmfPlusRecordType recordType, int flags, int dataSize, IntPtr recordData);
