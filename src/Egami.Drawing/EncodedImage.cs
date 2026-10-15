using Egami.Formats;
using Egami.Operations;

namespace Egami.Drawing;

/// <summary>
/// An image kept as its file's bytes, such as a JPEG, for <see cref="Canvas.DrawImage(EncodedImage, RectF, SamplingOptions?, Paint?)"/>:
/// a backend that can store the bytes as they are, as a PDF page stores a JPEG as DCTDecode, gets them through
/// <c>OnDrawEncodedImage</c> without decoding and encoding again; every other backend draws the decoded pixels. The bytes are
/// probed when the image is made and decoded at most once, on first need. Immutable and thread-safe.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="encoded-image"/>
/// </example>
public sealed class EncodedImage
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly EgamiContext? _context;
    private readonly Lazy<Raster> _decoded;

    private EncodedImage(ReadOnlyMemory<byte> data, RasterInfo info, EgamiContext? context)
    {
        _data = data;
        Info = info;
        _context = context;
        _decoded = new Lazy<Raster>(() => Raster.Load(_data, _context), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Gets the file's bytes.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>Gets what probing the bytes found: the format, the size, the pixel layout and the orientation.</summary>
    public RasterInfo Info { get; }

    /// <summary>Gets the width of the decoded image in pixels.</summary>
    public int Width => Info.Width;

    /// <summary>Gets the height of the decoded image in pixels.</summary>
    public int Height => Info.Height;

    /// <summary>Makes an encoded image from a file's bytes, which are kept as they are (not copied) and probed but not decoded; they must not change while the image is in use.</summary>
    /// <param name="data">The file's bytes, in any format Egami reads.</param>
    /// <param name="context">The context whose formats and limits apply when probing and decoding, or <see langword="null"/> for the default.</param>
    /// <returns>The image.</returns>
    /// <exception cref="UnsupportedFormatException">The bytes are not an image Egami reads.</exception>
    /// <exception cref="CorruptDataException">The header is damaged.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="encoded-image"/>
    /// </example>
    public static EncodedImage Load(ReadOnlyMemory<byte> data, EgamiContext? context = null) => new(data, Raster.Probe(data, context), context);

    /// <summary>Reads a stream to its end into an encoded image.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="context">The context whose formats and limits apply, or <see langword="null"/> for the default.</param>
    /// <returns>The image.</returns>
    /// <exception cref="UnsupportedFormatException">The data is not an image Egami reads.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="encoded-image"/>
    /// </example>
    public static EncodedImage Load(Stream stream, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return Load(copy.ToArray(), context);
    }

    /// <summary>Reads a file into an encoded image.</summary>
    /// <param name="path">The file.</param>
    /// <param name="context">The context whose formats and limits apply, or <see langword="null"/> for the default.</param>
    /// <returns>The image.</returns>
    /// <exception cref="UnsupportedFormatException">The file is not an image Egami reads.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="encoded-image"/>
    /// </example>
    public static EncodedImage Load(string path, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Load(File.ReadAllBytes(path), context);
    }

    /// <summary>Returns the decoded image, decoding it on the first call; later calls return the same raster, which must not be changed or disposed.</summary>
    /// <returns>The decoded image.</returns>
    /// <exception cref="CorruptDataException">The image data is damaged.</exception>
    public Raster Decode() => _decoded.Value;
}
