using System.Buffers.Binary;
using System.ComponentModel;

using Egami.Formats.Ico;
using Egami.Pixels;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

using Size = System.Drawing.Size;

/// <summary>
/// A Windows icon: an ICO file with images at several sizes, of which the icon shows the one nearest the size asked for (32 by
/// 32 when none is), as System.Drawing chooses it. <see cref="Save(Stream)"/> writes the file it was loaded from.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="icon"/>
/// </example>
public sealed class Icon : MarshalByRefObject, ICloneable, IDisposable
{
    private readonly byte[] _data;
    private readonly int _entry;
    private Bitmap? _image;

    /// <summary>Initializes a new instance of the <see cref="Icon"/> class from an ICO file.</summary>
    /// <param name="fileName">The file.</param>
    public Icon(string fileName)
        : this(fileName, 0, 0)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Icon"/> class from an ICO file, choosing the image nearest a size.</summary>
    /// <param name="fileName">The file.</param>
    /// <param name="size">The size.</param>
    public Icon(string fileName, Size size)
        : this(fileName, size.Width, size.Height)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Icon"/> class from an ICO file, choosing the image nearest a size.</summary>
    /// <param name="fileName">The file.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public Icon(string fileName, int width, int height)
        : this(File.ReadAllBytes(fileName ?? throw new ArgumentNullException(nameof(fileName))), width, height)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Icon"/> class from ICO data in a stream.</summary>
    /// <param name="stream">The stream.</param>
    public Icon(Stream stream)
        : this(stream, 0, 0)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Icon"/> class from ICO data, choosing the image nearest a size.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="size">The size.</param>
    public Icon(Stream stream, Size size)
        : this(stream, size.Width, size.Height)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Icon"/> class from ICO data, choosing the image nearest a size.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public Icon(Stream stream, int width, int height)
        : this(Read(stream), width, height)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Icon"/> class from another icon's file, choosing the image nearest a size.</summary>
    /// <param name="original">The icon.</param>
    /// <param name="size">The size.</param>
    public Icon(Icon original, Size size)
        : this(original, size.Width, size.Height)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Icon"/> class from another icon's file, choosing the image nearest a size.</summary>
    /// <param name="original">The icon.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    public Icon(Icon original, int width, int height)
        : this((original ?? throw new ArgumentNullException(nameof(original)))._data, width, height)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Icon"/> class from an icon embedded in an assembly.</summary>
    /// <param name="type">A type whose assembly and namespace hold the resource.</param>
    /// <param name="resource">The resource's name within the type's namespace.</param>
    public Icon(Type type, string resource)
        : this(Read((type ?? throw new ArgumentNullException(nameof(type))).Assembly.GetManifestResourceStream(type, resource) ?? throw new ArgumentException($"Resource '{resource}' cannot be found in class '{type.FullName}'.")), 0, 0)
    {
    }

    private Icon(byte[] data, int width, int height)
    {
        _data = data;
        _entry = Choose(data, width == 0 ? 32 : width, height == 0 ? 32 : height);
    }

    /// <summary>Gets the chosen image's width.</summary>
    public int Width => Image.Width;

    /// <summary>Gets the chosen image's height.</summary>
    public int Height => Image.Height;

    /// <summary>Gets the chosen image's size.</summary>
    public Size Size => new(Width, Height);

    /// <summary>Not supported: Windows icon handles. Always throws.</summary>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [Browsable(false)]
    public IntPtr Handle => throw GdiErrors.WindowsOnly("Icon.Handle", "Use ToBitmap for the pixels.");

    private Bitmap Image => _image ??= Decode();

    /// <summary>Not supported: Windows icon handles. Always throws.</summary>
    /// <param name="handle">The handle.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon FromHandle(IntPtr handle) => throw GdiErrors.WindowsOnly("Icon.FromHandle", "Load the icon file with new Icon(path).");

    /// <summary>Not supported: Windows' icons for files. Always throws.</summary>
    /// <param name="filePath">The file.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon? ExtractAssociatedIcon(string filePath) => throw GdiErrors.WindowsOnly("Icon.ExtractAssociatedIcon", "Load an icon file with new Icon(path).");

    /// <summary>Not supported: icons inside Windows programs and libraries. Always throws.</summary>
    /// <param name="filePath">The file.</param>
    /// <param name="id">The icon's id.</param>
    /// <param name="smallIcon">Whether to take the small icon.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon? ExtractIcon(string filePath, int id, bool smallIcon = false) => throw GdiErrors.WindowsOnly("Icon.ExtractIcon", "Load an icon file with new Icon(path).");

    /// <summary>Not supported: icons inside Windows programs and libraries. Always throws.</summary>
    /// <param name="filePath">The file.</param>
    /// <param name="id">The icon's id.</param>
    /// <param name="size">The size.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.StockIconsMessage, DiagnosticId = Internal.Diagnostics.StockIcons, UrlFormat = Internal.Diagnostics.Url)]
    public static Icon? ExtractIcon(string filePath, int id, int size) => throw GdiErrors.WindowsOnly("Icon.ExtractIcon", "Load an icon file with new Icon(path).");

    /// <summary>The chosen image as a 32-bit ARGB bitmap.</summary>
    /// <returns>A new bitmap.</returns>
    public Bitmap ToBitmap() => (Bitmap)Image.Clone();

    /// <summary>Writes the ICO file the icon was loaded from, with every size.</summary>
    /// <param name="outputStream">The stream.</param>
    public void Save(Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        outputStream.Write(_data);
    }

    /// <summary>A copy of the icon.</summary>
    /// <returns>The copy.</returns>
    public object Clone() => new Icon(_data, Width, Height);

    /// <summary>Releases the decoded image.</summary>
    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }

    /// <summary>Returns "(Icon)", as System.Drawing does.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => "(Icon)";

    /// <summary>An icon from a bitmap, written as a one-image ICO file (for Icon.FromBitmap-style use within the package).</summary>
    internal static Icon FromBitmap(Bitmap bitmap)
    {
        using Raster<Rgba8> raster = bitmap.ToRgba8();
        using MemoryStream output = new();
        raster.Save(output, IcoFormat.Info);
        return new Icon(output.ToArray(), bitmap.Width, bitmap.Height);
    }

    private static byte[] Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// The directory entry nearest a size, as System.Drawing chooses: the smallest |width difference| + |height difference|,
    /// with a width or height byte of 0 counted as 0 (so a 256-pixel image is chosen only when it is nearest anyway), then the
    /// most bits a pixel, then the first.
    /// </summary>
    private static int Choose(byte[] data, int width, int height)
    {
        if (data.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) is not (1 or 2))
        {
            throw GdiErrors.InvalidParameter();
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
        int best = 0, bestDistance = int.MaxValue, bestBits = -1;
        for (int i = 0; i < count && 6 + (16 * (i + 1)) <= data.Length; i++)
        {
            ReadOnlySpan<byte> entry = data.AsSpan(6 + (16 * i), 16);
            int distance = Math.Abs(entry[0] - width) + Math.Abs(entry[1] - height);
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            if (distance < bestDistance || (distance == bestDistance && bits > bestBits))
            {
                (best, bestDistance, bestBits) = (i, distance, bits);
            }
        }

        if (count == 0)
        {
            throw GdiErrors.InvalidParameter();
        }

        return best;
    }

    private Bitmap Decode()
    {
        using RasterList all = Raster.LoadAll(_data);
        Raster raster = all.FirstOrDefault(r => r.Metadata.FormatDetails is IcoDetails d && d.Index == _entry) ?? all[Math.Min(_entry, all.Count - 1)];
        Bitmap bitmap = new(Codecs.FromRgba(raster, PixelFormat.Format32bppArgb));
        return bitmap;
    }
}
