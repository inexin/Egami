using Egami.Compression.Zstd;

namespace Egami.Compression;

/// <summary>
/// Zstandard (RFC 8878) compression in managed code: the codec TIFF's ZSTD compression uses, for format plugins and apps that
/// need it too, such as KTX 2's supercompressed mip levels. Both directions work on spans the caller sizes, and each thread
/// keeps its tables, so a warm call allocates nothing.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Compression.cs" region="zstandard"/>
/// </example>
public static class Zstandard
{
    /// <summary>The lowest compression level, the fastest.</summary>
    public const int MinLevel = ZstdEncoder.MinLevel;

    /// <summary>The highest compression level, the smallest output.</summary>
    public const int MaxLevel = ZstdEncoder.MaxLevel;

    /// <summary>The level <see cref="Compress"/> uses when none is given, as the zstd tool's default.</summary>
    public const int DefaultLevel = 3;

    /// <summary>Gets the most bytes <see cref="Compress"/> can write for <paramref name="length"/> bytes of input.</summary>
    /// <param name="length">The number of bytes to compress.</param>
    /// <returns>The room the output needs.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative, or the result does not fit an <see cref="int"/>.</exception>
    public static int MaxCompressedLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, int.MaxValue - (int.MaxValue / 1000) - 1024);
        return ZstdEncoder.MaxEncodedLength(length);
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into one Zstandard frame that records its content size and has no checksum.
    /// </summary>
    /// <param name="source">The bytes to compress.</param>
    /// <param name="destination">Where the frame goes; it needs room for <see cref="MaxCompressedLength"/> bytes.</param>
    /// <param name="level">The level, from <see cref="MinLevel"/> to <see cref="MaxLevel"/>.</param>
    /// <returns>The bytes written.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="MaxCompressedLength"/>.</exception>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination, int level = DefaultLevel)
    {
        return ZstdEncoder.Encode(source, destination, level);
    }

    /// <summary>
    /// Decompresses the Zstandard frames of <paramref name="source"/>, one after another, into <paramref name="destination"/>.
    /// Skippable frames are skipped and a frame's checksum is checked when it has one.
    /// </summary>
    /// <param name="source">The compressed frames.</param>
    /// <param name="destination">Where the bytes go; the data must fit in it.</param>
    /// <returns>The bytes written. Bytes after that count may have been written to.</returns>
    /// <exception cref="CorruptDataException">The data breaks the format, or needs more room than <paramref name="destination"/> has.</exception>
    /// <exception cref="UnsupportedFormatException">A frame needs a dictionary.</exception>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        return ZstdDecoder.Decode(source, destination);
    }
}
