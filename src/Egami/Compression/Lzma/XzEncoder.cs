using System.Buffers.Binary;

using Egami.Formats;

namespace Egami.Compression.Lzma;

/// <summary>
/// Writes an xz stream as libtiff's LZMA compression does: one block whose filters are delta (when asked) then LZMA2, no check,
/// the index and the footer. Presets 0 to 9 have XZ Utils' meanings (dictionary size, match finder, nice length and depth),
/// with the dictionary cut to the input, so a 64 KB strip never asks for a 64 MB dictionary.
/// </summary>
/// <remarks>Each thread keeps one encoder for its tables and buffers, so a warm call allocates nothing.</remarks>
internal static class XzEncoder
{
    /// <summary>The lowest preset.</summary>
    public const int MinPreset = 0;

    /// <summary>The highest preset.</summary>
    public const int MaxPreset = 9;

    // XZ Utils' presets: log2 of the dictionary size, then nice length; 0 to 3 are fast mode on hash chains (0 on 3-byte hashes),
    // 4 to 9 normal mode on binary trees, whose depth follows from the nice length when 0.
    private static ReadOnlySpan<byte> DictionaryLogs => [18, 20, 21, 22, 22, 23, 23, 24, 25, 26];

    private static ReadOnlySpan<short> NiceLengths => [128, 128, 273, 273, 16, 32, 64, 64, 64, 64];

    private static ReadOnlySpan<byte> FastDepths => [4, 8, 24, 48];

    [ThreadStatic]
    private static Lzma2Encoder? cached;

    [ThreadStatic]
    private static byte[]? scratch;

    /// <summary>Gets the most bytes <see cref="Encode"/> can write for <paramref name="length"/> bytes of input.</summary>
    public static int MaxEncodedLength(int length) => Lzma2Encoder.MaxEncodedLength(length) + 12 + 16 + 4 + 16 + 12;

    /// <summary>
    /// Compresses <paramref name="input"/> into one xz stream at <paramref name="output"/>, which needs room for
    /// <see cref="MaxEncodedLength"/> bytes; <paramref name="delta"/> is the delta filter's distance, or 0 for none. Returns the
    /// bytes written.
    /// </summary>
    public static int Encode(ReadOnlySpan<byte> input, Span<byte> output, int preset, int delta = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(preset, MinPreset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(preset, MaxPreset);
        ArgumentOutOfRangeException.ThrowIfNegative(delta);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delta, 256);
        if (output.Length < MaxEncodedLength(input.Length))
        {
            throw new ArgumentException("The output has less room than MaxEncodedLength gives.", nameof(output));
        }

        Lzma2Encoder encoder = cached ?? new Lzma2Encoder();
        cached = null;
        try
        {
            ReadOnlySpan<byte> data = input;
            if (delta > 0)
            {
                // The delta filter: each byte less the one a distance before (zero before the start).
                if (scratch is null || scratch.Length < input.Length)
                {
                    scratch = new byte[Math.Max(input.Length, 4096)];
                }

                Span<byte> differences = scratch.AsSpan(0, input.Length);
                for (int i = input.Length - 1; i >= delta; i--)
                {
                    differences[i] = (byte)(input[i] - input[i - delta]);
                }

                input[..Math.Min(delta, input.Length)].CopyTo(differences);
                data = differences;
            }

            // Stream header: magic, flags (no check), their CRC32.
            ReadOnlySpan<byte> magic = [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0, 0, 0];
            magic.CopyTo(output);
            BinaryPrimitives.WriteUInt32LittleEndian(output[8..], Crc32.Compute(output.Slice(6, 2)));
            int at = 12;

            // Block header without sizes, as libtiff's streaming encoder writes it, then the LZMA2 data.
            int dictionary = (int)Math.Min(1L << DictionaryLogs[preset], Math.Max(4096, input.Length));
            int property = Lzma2Encoder.DictionaryProperty(dictionary);
            Span<byte> header = stackalloc byte[32];
            int headerSize = BlockHeader(header, delta, property, 0, 0, sizes: false);
            int dataAt = at + headerSize;
            bool normal = preset >= 4;
            int nice = NiceLengths[preset];
            int depth = normal ? 16 + (nice / 2) : FastDepths[preset];
            int compressed = encoder.Encode(data, output[dataAt..], dictionary, normal, tree: normal, three: preset == 0, nice, depth);
            header[..headerSize].CopyTo(output[at..]);
            at = dataAt + compressed;
            while ((at & 3) != 0)
            {
                output[at++] = 0;
            }

            long unpadded = headerSize + compressed;

            // Index: one record, padded, and its CRC32.
            int indexStart = at;
            output[at++] = 0;
            at = WriteNumber(output, at, 1);
            at = WriteNumber(output, at, (ulong)unpadded);
            at = WriteNumber(output, at, (ulong)input.Length);
            while (((at - indexStart) & 3) != 0)
            {
                output[at++] = 0;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(output[at..], Crc32.Compute(output[indexStart..at]));
            at += 4;
            int indexSize = at - indexStart;

            // Footer: CRC32 of the backward size and flags, the backward size, the flags, "YZ".
            BinaryPrimitives.WriteUInt32LittleEndian(output[(at + 4)..], (uint)((indexSize / 4) - 1));
            output[at + 8] = 0;
            output[at + 9] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(output[at..], Crc32.Compute(output.Slice(at + 4, 6)));
            output[at + 10] = (byte)'Y';
            output[at + 11] = (byte)'Z';
            return at + 12;
        }
        finally
        {
            cached = encoder;
        }
    }

    /// <summary>Writes a block header: the sizes when asked, the filter list (delta, LZMA2), padding and CRC32; returns its size.</summary>
    private static int BlockHeader(Span<byte> header, int delta, int property, long compressed, long uncompressed, bool sizes)
    {
        int at = 2;
        header[1] = (byte)((delta > 0 ? 1 : 0) | (sizes ? 0xC0 : 0));
        if (sizes)
        {
            at = WriteNumber(header, at, (ulong)compressed);
            at = WriteNumber(header, at, (ulong)uncompressed);
        }

        if (delta > 0)
        {
            header[at++] = 0x03;
            header[at++] = 1;
            header[at++] = (byte)(delta - 1);
        }

        header[at++] = 0x21;
        header[at++] = 1;
        header[at++] = (byte)property;
        while (((at + 4) & 3) != 0)
        {
            header[at++] = 0;
        }

        header[0] = (byte)(((at + 4) / 4) - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[at..], Crc32.Compute(header[..at]));
        return at + 4;
    }

    private static int WriteNumber(Span<byte> output, int at, ulong value)
    {
        while (value >= 0x80)
        {
            output[at++] = (byte)(value | 0x80);
            value >>= 7;
        }

        output[at++] = (byte)value;
        return at;
    }
}
