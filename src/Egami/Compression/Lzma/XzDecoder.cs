using System.Buffers.Binary;
using System.Security.Cryptography;

using Egami.Formats;

namespace Egami.Compression.Lzma;

/// <summary>
/// A decoder of the xz format (The .xz File Format 1.2.1, public domain) for TIFF's LZMA compression: streams and stream
/// padding, blocks with the delta and LZMA2 filters, the index, and the None, CRC32, CRC64 and SHA-256 checks, each verified.
/// Other filters (the branch converters, ARM64, RISC-V) are refused.
/// </summary>
/// <remarks>
/// The whole output goes into one span the caller sizes, which is the dictionary too: nothing as large as a dictionary is ever
/// allocated, whatever size a block's LZMA2 filter asks for, and a block that would write past the span is corrupt. Each
/// thread keeps one LZMA2 decoder for its probabilities, so a warm call allocates nothing.
/// </remarks>
internal static class XzDecoder
{
    private const int HeaderSize = 12;

    private static ReadOnlySpan<byte> HeaderMagic => [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0];

    [ThreadStatic]
    private static Lzma2Decoder? cached;

    /// <summary>
    /// Decodes the xz streams of <paramref name="input"/> one after another into <paramref name="output"/>; returns the bytes
    /// written. Throws a <see cref="CorruptDataException"/> when the data breaks the format or needs more room than the output
    /// has, and an <see cref="UnsupportedFormatException"/> for a filter other than delta and LZMA2.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output) => Run(input, output, out _);

    /// <summary>
    /// Decodes as <see cref="Decode"/> does, but on corrupt data returns the bytes of the blocks decoded before it instead of
    /// throwing, for readers that keep what a damaged strip still holds.
    /// </summary>
    public static int DecodePartial(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int done = 0;
        try
        {
            return Run(input, output, out done);
        }
        catch (CorruptDataException)
        {
            return done;
        }
    }

    /// <summary>Gets the exception for corrupt data.</summary>
    internal static CorruptDataException Corrupt(string problem) => new("xz", -1, problem);

    private static int Run(ReadOnlySpan<byte> input, Span<byte> output, out int done)
    {
        Lzma2Decoder decoder = cached ?? new Lzma2Decoder();
        cached = null;
        try
        {
            int at = 0;
            done = 0;
            bool any = false;
            while (at < input.Length)
            {
                // Stream padding: zeros in groups of four between and after streams.
                if (any && input[at] == 0)
                {
                    int zeros = 0;
                    while (at < input.Length && input[at] == 0)
                    {
                        at++;
                        zeros++;
                    }

                    if (zeros % 4 != 0)
                    {
                        throw Corrupt("Stream padding is not a multiple of four bytes.");
                    }

                    continue;
                }

                at = Stream(input, at, output, ref done, decoder);
                any = true;
            }

            if (!any)
            {
                throw Corrupt("The data holds no xz stream.");
            }

            return done;
        }
        finally
        {
            cached = decoder;
        }
    }

    private static int Stream(ReadOnlySpan<byte> input, int at, Span<byte> output, ref int done, Lzma2Decoder decoder)
    {
        if (input.Length - at < HeaderSize || !input.Slice(at, 6).SequenceEqual(HeaderMagic))
        {
            throw Corrupt("No xz stream starts here.");
        }

        ReadOnlySpan<byte> flags = input.Slice(at + 6, 2);
        if (Crc32.Compute(flags) != BinaryPrimitives.ReadUInt32LittleEndian(input[(at + 8)..]))
        {
            throw Corrupt("The stream header's CRC32 does not match.");
        }

        if (flags[0] != 0 || (flags[1] & 0xF0) != 0)
        {
            throw new UnsupportedFormatException("xz: the stream flags use reserved bits.");
        }

        int check = flags[1];
        int checkSize = check == 0 ? 0 : 4 << ((check - 1) / 3);
        at += HeaderSize;

        // Blocks until the index indicator, each noted for the index to match.
        int recordCount = 0;
        long blocksUnpadded = 0;
        long blocksUncompressed = 0;
        ulong recordHash = 0;
        while (true)
        {
            if (at >= input.Length)
            {
                throw Corrupt("The stream ends before its index.");
            }

            if (input[at] == 0)
            {
                break;
            }

            int start = done;
            (int next, long unpadded) = Block(input, at, output, ref done, decoder, check, checkSize);
            at = next;
            long uncompressed = done - start;
            blocksUnpadded += unpadded;
            blocksUncompressed += uncompressed;
            recordHash = Mix(recordHash, unpadded, uncompressed);
            recordCount++;
        }

        // The index: its records must name the blocks just read.
        int indexStart = at;
        at++;
        ulong count = ReadNumber(input, ref at);
        if (count != (ulong)recordCount)
        {
            throw Corrupt("The index lists a different number of blocks than the stream holds.");
        }

        ulong indexHash = 0;
        long indexUnpadded = 0;
        long indexUncompressed = 0;
        for (ulong i = 0; i < count; i++)
        {
            long unpadded = (long)ReadNumber(input, ref at);
            long uncompressed = (long)ReadNumber(input, ref at);
            indexUnpadded += unpadded;
            indexUncompressed += uncompressed;
            indexHash = Mix(indexHash, unpadded, uncompressed);
        }

        if (indexHash != recordHash || indexUnpadded != blocksUnpadded || indexUncompressed != blocksUncompressed)
        {
            throw Corrupt("The index does not match the blocks.");
        }

        at = Pad(input, at, at - indexStart);
        if (input.Length - at < 4 || Crc32.Compute(input[indexStart..at]) != BinaryPrimitives.ReadUInt32LittleEndian(input[at..]))
        {
            throw Corrupt("The index's CRC32 does not match.");
        }

        int indexSize = at - indexStart + 4;
        at += 4;

        // The footer repeats the flags and gives the index's size.
        if (input.Length - at < HeaderSize || input[at + 10] != (byte)'Y' || input[at + 11] != (byte)'Z')
        {
            throw Corrupt("The stream footer is missing.");
        }

        if (Crc32.Compute(input.Slice(at + 4, 6)) != BinaryPrimitives.ReadUInt32LittleEndian(input[at..])
            || !input.Slice(at + 8, 2).SequenceEqual(flags)
            || ((long)BinaryPrimitives.ReadUInt32LittleEndian(input[(at + 4)..]) + 1) * 4 != indexSize)
        {
            throw Corrupt("The stream footer does not match the header or the index.");
        }

        return at + HeaderSize;
    }

    private static (int Next, long Unpadded) Block(ReadOnlySpan<byte> input, int at, Span<byte> output, ref int done,
        Lzma2Decoder decoder, int check, int checkSize)
    {
        int headerSize = (input[at] + 1) * 4;
        if (input.Length - at < headerSize)
        {
            throw Corrupt("A block header is cut short.");
        }

        ReadOnlySpan<byte> header = input.Slice(at, headerSize);
        if (Crc32.Compute(header[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(header[^4..]))
        {
            throw Corrupt("A block header's CRC32 does not match.");
        }

        int flags = header[1];
        if ((flags & 0x3C) != 0)
        {
            throw new UnsupportedFormatException("xz: a block header uses reserved bits.");
        }

        int h = 2;
        long compressedSize = (flags & 0x40) != 0 ? (long)ReadNumber(header, ref h) : -1;
        long uncompressedSize = (flags & 0x80) != 0 ? (long)ReadNumber(header, ref h) : -1;
        int filters = (flags & 3) + 1;
        int delta = 0;
        bool lzma2 = false;
        for (int f = 0; f < filters; f++)
        {
            ulong id = ReadNumber(header, ref h);
            int size = (int)Math.Min(ReadNumber(header, ref h), int.MaxValue);
            if (header.Length - 4 - h < size)
            {
                throw Corrupt("A block header's filter list is cut short.");
            }

            ReadOnlySpan<byte> properties = header.Slice(h, size);
            h += size;
            bool last = f == filters - 1;
            if (id == 0x03 && !last && delta == 0 && size == 1)
            {
                delta = properties[0] + 1;
            }
            else if (id == 0x21 && last && size == 1)
            {
                if (properties[0] > 40)
                {
                    throw Corrupt("An LZMA2 filter's dictionary size is out of range.");
                }

                lzma2 = true;
            }
            else
            {
                throw new UnsupportedFormatException(id is 0x03 or 0x21
                    ? "xz: a block's filter chain is not delta then LZMA2."
                    : $"xz: filter 0x{id:X} is not read (only delta and LZMA2 are).");
            }
        }

        if (!lzma2)
        {
            throw new UnsupportedFormatException("xz: a block's filter chain does not end in LZMA2.");
        }

        if (header[h..^4].ContainsAnyExcept((byte)0))
        {
            throw Corrupt("A block header's padding is not zero.");
        }

        at += headerSize;
        int start = done;
        int limit = uncompressedSize >= 0 ? (int)Math.Min(output.Length, start + uncompressedSize) : output.Length;
        if (uncompressedSize > output.Length - start)
        {
            throw Corrupt($"A block holds {uncompressedSize} bytes, more than the {output.Length - start} the output has room for.");
        }

        ReadOnlySpan<byte> data = compressedSize >= 0
            ? (compressedSize <= input.Length - at ? input.Slice(at, (int)compressedSize) : throw Corrupt("A block runs past the end of the data."))
            : input[at..];
        int used = decoder.Decode(data, output[..limit], start, out int end);
        if ((compressedSize >= 0 && used != compressedSize) || (uncompressedSize >= 0 && end - start != uncompressedSize))
        {
            throw Corrupt("A block's sizes do not match its header.");
        }

        if (delta > 0)
        {
            // The delta filter came first when encoding, so it is undone last: each byte adds the one a distance before.
            Span<byte> block = output[start..end];
            for (int i = delta; i < block.Length; i++)
            {
                block[i] += block[i - delta];
            }
        }

        done = end;
        long unpadded = headerSize + used + checkSize;
        at = Pad(input, at + used, used);
        if (input.Length - at < checkSize)
        {
            throw Corrupt("A block's check is cut short.");
        }

        Verify(check, output[start..end], input.Slice(at, checkSize));
        return (at + checkSize, unpadded);
    }

    private static void Verify(int check, ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        bool good = check switch
        {
            0 => true,
            1 => Crc32.Compute(data) == BinaryPrimitives.ReadUInt32LittleEndian(expected),
            4 => Crc64.Compute(data) == BinaryPrimitives.ReadUInt64LittleEndian(expected),
            10 => SHA256.HashData(data).AsSpan().SequenceEqual(expected),

            // A check type xz reserves: its size is known, so the data reads, unchecked.
            _ => true,
        };
        if (!good)
        {
            throw Corrupt("A block's check does not match.");
        }
    }

    /// <summary>Skips the zero padding after <paramref name="length"/> bytes to the next multiple of four.</summary>
    private static int Pad(ReadOnlySpan<byte> input, int at, long length)
    {
        int padding = (int)((4 - (length & 3)) & 3);
        if (input.Length - at < padding || input.Slice(at, padding).ContainsAnyExcept((byte)0))
        {
            throw Corrupt("Padding is missing or not zero.");
        }

        return at + padding;
    }

    /// <summary>Reads a variable-length integer: seven bits a byte, least significant first, at most nine bytes.</summary>
    private static ulong ReadNumber(ReadOnlySpan<byte> data, ref int at)
    {
        ulong value = 0;
        for (int i = 0; i < 9; i++)
        {
            if (at >= data.Length)
            {
                throw Corrupt("A number is cut short.");
            }

            byte b = data[at++];
            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                if (b == 0 && i > 0)
                {
                    throw Corrupt("A number has a needless zero byte.");
                }

                return value;
            }
        }

        throw Corrupt("A number is longer than nine bytes.");
    }

    private static ulong Mix(ulong hash, long unpadded, long uncompressed) =>
        ((hash ^ (ulong)unpadded) * 0x100000001B3) ^ ((ulong)uncompressed * 0x9E3779B97F4A7C15);
}
