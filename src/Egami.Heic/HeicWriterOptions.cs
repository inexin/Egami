using Egami.Formats;
using Egami.Pixels;

namespace Egami.Heic;

/// <summary>
/// Options for writing HEIC. Lossy images are coded on heif-enc's quality scale, so the same <see cref="Quality"/> gives about the
/// same file as heif-enc -q with x265; lossless images keep every sample of 4:4:4 RGB. The names and meanings match
/// <see cref="Egami.Formats.Avif.AvifWriterOptions"/> where the two formats share an option.
/// </summary>
/// <remarks>
/// HEVC is covered by patents, encoding as well as decoding. Egami's authors grant no patent rights and are not lawyers. Whether
/// you need a patent licence depends on your country and on how you use and distribute this package; commercial products may need
/// one from the HEVC patent pools or holders.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="write"/>
/// </example>
public sealed class HeicWriterOptions : WriterOptions
{
    private readonly int _quality = 50;
    private readonly int _effort = 4;
    private readonly int _alphaQuality = 100;
    private readonly int? _bitDepth;
    private readonly Size? _gridTileSize;
    private readonly int? _targetSize;
    private readonly int? _gainMapQuality;
    private readonly int? _thumbnailSize;

    /// <summary>Gets the default options.</summary>
    public static new HeicWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the lossy quality, 0 (smallest) to 100 (best), on heif-enc's -q scale. Defaults to 50, as heif-enc does. Ignored for
    /// lossless images.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int Quality
    {
        get => _quality;
        init => _quality = InRange(value, 0, 100);
    }

    /// <summary>
    /// Gets how hard the encoder works, 0 (fastest) to 10 (smallest files). Defaults to 4, which makes files about the size of x265's
    /// medium preset in about its time.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 10.</exception>
    public int Effort
    {
        get => _effort;
        init => _effort = InRange(value, 0, 10);
    }

    /// <summary>
    /// Gets whether to write lossless (every sample of the RGB or gray image exact: 4:4:4 with the identity matrix, coded without
    /// transform or quantization) or lossy data, or <see langword="null"/> (the default) for lossy. Lossless HEIC needs a decoder of
    /// HEVC's format range extensions, which some phones and operating systems lack.
    /// </summary>
    public bool? Lossless { get; init; }

    /// <summary>
    /// Gets the chroma format of lossy color, or <see langword="null"/> (the default) for 4:2:0, which every HEVC decoder reads.
    /// 4:4:4, 4:2:2 and 4:0:0 use HEVC's format range extensions, which some phones and operating systems cannot decode. 4:1:1 and
    /// 4:1:0 choose as <see langword="null"/> does.
    /// </summary>
    public ChromaFormat? Chroma { get; init; }

    /// <summary>
    /// Gets the bits a sample has, 8, 10 or 12, or <see langword="null"/> (the default) for 8 with 8-bit pixels and 10 (Main 10)
    /// with deeper ones; a lossless file of deeper pixels takes 12. 12 bits use HEVC's format range extensions.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 8, 10 or 12.</exception>
    public int? BitDepth
    {
        get => _bitDepth;
        init => _bitDepth = value is null or 8 or 10 or 12 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The bit depth is 8, 10 or 12.");
    }

    /// <summary>
    /// Gets the quality of the alpha plane, 0 to 100 on the same scale as <see cref="Quality"/>: 100 (the default) keeps alpha
    /// exactly. Alpha is written straight, not premultiplied, as an auxiliary image every HEVC decoder reads.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int AlphaQuality
    {
        get => _alphaQuality;
        init => _alphaQuality = InRange(value, 0, 100);
    }

    /// <summary>
    /// Gets the size of the cells of a grid image, or <see langword="null"/> (the default) to write one picture up to 8,912,896
    /// samples (HEVC level 5.1, which phone and PC decoders take) and a grid of 512 x 512 cells, as iPhones write, for larger
    /// images.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is below 64 or above 8192.</exception>
    public Size? GridTileSize
    {
        get => _gridTileSize;
        init => _gridTileSize = value is not Size size || (size.Width is >= 64 and <= 8192 && size.Height is >= 64 and <= 8192)
            ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Grid cells are 64 to 8192 samples a side.");
    }

    /// <summary>
    /// Gets the largest size the file may have, in bytes, or <see langword="null"/> (the default) to code at <see cref="Quality"/>:
    /// the quality is searched for the largest file not above the size, then the quantizer between that quality and the next, so the
    /// file lands close under the size. When even quality 0 is larger, that file is written and a Heic.TargetSizeExceeded warning
    /// says so. Ignored for lossless images.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1.</exception>
    public int? TargetSize
    {
        get => _targetSize;
        init => _targetSize = value is null or >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The target size is 1 byte or more.");
    }

    /// <summary>
    /// Gets the quality of a gain map, 0 to 100 on the same scale as <see cref="Quality"/> (100 is lossless), or
    /// <see langword="null"/> (the default) for the image's quality.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int? GainMapQuality
    {
        get => _gainMapQuality;
        init => _gainMapQuality = value is int q ? InRange(q, 0, 100) : null;
    }

    /// <summary>
    /// Gets the size of the square a thumbnail fits in, 16 to 1024 samples, or <see langword="null"/> (the default) for no
    /// thumbnail. A thumbnail is a small HEVC image linked to the image, which file browsers show without decoding the whole picture;
    /// iPhones write one 320 samples wide.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 16 to 1024.</exception>
    public int? ThumbnailSize
    {
        get => _thumbnailSize;
        init => _thumbnailSize = value is int size ? InRange(size, 16, 1024) : null;
    }

    private static int InRange(int value, int min, int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, min);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, max);
        return value;
    }
}
