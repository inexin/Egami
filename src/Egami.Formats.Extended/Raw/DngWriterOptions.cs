namespace Egami.Formats.Extended.Raw;

/// <summary>How a DNG stores its raw data.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-dng"/>
/// </example>
public enum DngCompression
{
    /// <summary>
    /// Lossless JPEG (compression 7) in tiles, a colour filter array row coded as pairs as Adobe DNG Converter writes it. Integer
    /// data only.
    /// </summary>
    LosslessJpeg,

    /// <summary>Uncompressed: the largest files, the fastest to read.</summary>
    None,

    /// <summary>Deflate (compression 8) with TIFF's horizontal predictor for integers and its floating-point predictor for floats.</summary>
    Deflate,

    /// <summary>Lossless JPEG XL (compression 52546), from DNG 1.7: small files that readers older than DNG 1.7 cannot open.</summary>
    JpegXl,
}

/// <summary>Which rendered images a DNG carries besides its raw data.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-dng"/>
/// </example>
public enum DngPreview
{
    /// <summary>None: the raw data is the file's first image. The smallest files.</summary>
    None,

    /// <summary>An 8-bit RGB thumbnail of 256 pixels on the long side, as the file's first image.</summary>
    Thumbnail,

    /// <summary>The thumbnail and a JPEG preview of 1024 pixels on the long side, as Adobe DNG Converter writes by default.</summary>
    Medium,

    /// <summary>The thumbnail and a full-size JPEG preview.</summary>
    Full,
}

/// <summary>Options for writing DNG files: how the raw data is stored and which previews come with it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="raw-dng"/>
/// </example>
public sealed class DngWriterOptions : WriterOptions
{
    private readonly int _tileSize = 256;

    /// <summary>Gets the default options.</summary>
    public static new DngWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets how the raw data is stored, or <see langword="null"/> (the default) for <see cref="DngCompression.LosslessJpeg"/> for
    /// integer data and <see cref="DngCompression.Deflate"/> for floating-point data.
    /// </summary>
    public DngCompression? Compression { get; init; }

    /// <summary>Gets the width and height of the raw data's tiles: a multiple of 16 from 16 to 4096. Defaults to 256, as Adobe writes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a multiple of 16 from 16 to 4096.</exception>
    public int TileSize
    {
        get => _tileSize;
        init
        {
            if (value is < 16 or > 4096 || value % 16 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A DNG tile size is a multiple of 16 from 16 to 4096.");
            }

            _tileSize = value;
        }
    }

    /// <summary>Gets which rendered images come with the raw data. Defaults to <see cref="DngPreview.Medium"/>.</summary>
    public DngPreview Preview { get; init; } = DngPreview.Medium;

    /// <summary>Gets whether tiles are compressed on several threads, up to the context's <see cref="EgamiContext.MaxParallelism"/>. On by default; the file is the same either way.</summary>
    public bool ParallelCompression { get; init; } = true;
}
