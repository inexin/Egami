using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Compression.Zstd;

/// <summary>
/// A Zstandard decoder (RFC 8878), for TIFF's ZSTD compression: frames and skippable frames, raw, RLE and compressed blocks,
/// Huffman literals and FSE-coded sequences, and the content checksum. Frames that need a dictionary are refused.
/// </summary>
/// <remarks>
/// The whole output goes into one span the caller sizes, so matches copy straight from it and no window is ever allocated,
/// whatever window a frame asks for; a frame that would write past the span is corrupt. Each thread keeps one instance for its
/// tables and literals buffer, so a warm call allocates nothing.
/// </remarks>
internal sealed class ZstdDecoder
{
    private const uint FrameMagic = 0xFD2FB528;
    private const uint SkippableMagic = 0x184D2A50;
    private const int MaxBlockSize = 128 * 1024;

    // The extra bytes after the literals that a 16-byte copy may read, and the room a copy needs before the output's end.
    private const int CopySlack = 32;

    private static readonly uint[] LiteralLengthBase =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048,
        4096, 8192, 16384, 32768, 65536,
    ];

    private static readonly byte[] LiteralLengthBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
    ];

    private static readonly uint[] MatchLengthBase =
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539,
    ];

    private static readonly byte[] MatchLengthBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
    ];

    private static readonly uint[] OffsetBase = [.. Enumerable.Range(0, 32).Select(code => 1u << code)];

    private static readonly byte[] OffsetBits = [.. Enumerable.Range(0, 32).Select(code => (byte)code)];

    // The predefined distributions (RFC 8878 section 3.1.1.3.2.2).
    private static readonly short[] LiteralLengthDefault =
    [
        4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1,
    ];

    private static readonly short[] MatchLengthDefault =
    [
        1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1,
    ];

    private static readonly short[] OffsetDefault =
    [
        1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
    ];

    private static readonly ZstdFseEntry[] LiteralLengthPredefined = Predefined(LiteralLengthDefault, 6, LiteralLengthBase, LiteralLengthBits);
    private static readonly ZstdFseEntry[] MatchLengthPredefined = Predefined(MatchLengthDefault, 6, MatchLengthBase, MatchLengthBits);
    private static readonly ZstdFseEntry[] OffsetPredefined = Predefined(OffsetDefault, 5, OffsetBase, OffsetBits);

    // For matches under eight bytes back: where the second four bytes of the pattern start, and how far back the source then moves
    // so that it lies a whole number of periods behind.
    private static ReadOnlySpan<byte> ShortDistanceForward => [0, 1, 2, 1, 4, 4, 4, 4];

    private static ReadOnlySpan<byte> ShortDistanceBack => [8, 8, 8, 7, 8, 9, 10, 11];

    [ThreadStatic]
    private static ZstdDecoder? cached;

    private readonly byte[] literals = new byte[MaxBlockSize + CopySlack];
    private readonly ushort[] huffman = new ushort[1 << ZstdHuffman.MaxBits];
    private readonly ZstdFseEntry[] literalLengthOwn = new ZstdFseEntry[1 << 9];
    private readonly ZstdFseEntry[] matchLengthOwn = new ZstdFseEntry[1 << 9];
    private readonly ZstdFseEntry[] offsetOwn = new ZstdFseEntry[1 << 8];

    // The tables in use, kept from block to block for the repeat modes; null before a frame's first.
    private ZstdFseEntry[]? literalLengthTable;
    private ZstdFseEntry[]? matchLengthTable;
    private ZstdFseEntry[]? offsetTable;
    private int literalLengthLog;
    private int matchLengthLog;
    private int offsetLog;

    // The Huffman table's longest code; 0 before a frame's first tree.
    private int huffmanBits;
    private uint repeat1;
    private uint repeat2;
    private uint repeat3;

    /// <summary>
    /// Decodes the frames of <paramref name="input"/> one after another into <paramref name="output"/>; returns the bytes written.
    /// Throws a <see cref="CorruptDataException"/> when the data breaks the format or needs more room than the output has,
    /// and an <see cref="UnsupportedFormatException"/> for a frame that needs a dictionary. Bytes past the count returned may
    /// have been written to.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ZstdDecoder decoder = cached ?? new ZstdDecoder();
        cached = null;
        try
        {
            return decoder.Run(input, output);
        }
        finally
        {
            cached = decoder;
        }
    }

    /// <summary>Gets the exception for corrupt data.</summary>
    internal static CorruptDataException Corrupt(string problem, long offset = -1) => new("Zstandard", offset, problem);

    private static ZstdFseEntry[] Predefined(short[] counts, int log, uint[] baseValues, byte[] extraBits)
    {
        ZstdFseEntry[] table = new ZstdFseEntry[1 << log];
        ZstdFse.Build(counts, counts.Length, log, baseValues, extraBits, table);
        return table;
    }

    private int Run(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int at = 0;
        int written = 0;
        while (at < input.Length)
        {
            if (input.Length - at < 4)
            {
                throw Corrupt("The data ends inside a frame's magic number.", at);
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(input[at..]);
            if ((magic & 0xFFFFFFF0) == SkippableMagic)
            {
                if (input.Length - at < 8)
                {
                    throw Corrupt("A skippable frame's size is cut short.", at);
                }

                uint size = BinaryPrimitives.ReadUInt32LittleEndian(input[(at + 4)..]);
                if (size > (uint)(input.Length - at - 8))
                {
                    throw Corrupt("A skippable frame runs past the end of the data.", at);
                }

                at += 8 + (int)size;
                continue;
            }

            if (magic != FrameMagic)
            {
                throw Corrupt("No Zstandard frame starts here.", at);
            }

            at = Frame(input, at + 4, output, ref written);
        }

        return written;
    }

    private int Frame(ReadOnlySpan<byte> input, int at, Span<byte> output, ref int written)
    {
        if (at >= input.Length)
        {
            throw Corrupt("The data ends inside a frame header.", at);
        }

        int descriptor = input[at++];
        bool single = (descriptor & 0x20) != 0;
        bool checksum = (descriptor & 4) != 0;
        if ((descriptor & 8) != 0)
        {
            throw Corrupt("A frame header's reserved bit is set.", at - 1);
        }

        int idSize = (descriptor & 3) == 3 ? 4 : descriptor & 3;
        int sizeSize = (descriptor >> 6) switch
        {
            0 => single ? 1 : 0,
            1 => 2,
            2 => 4,
            _ => 8,
        };
        if (input.Length - at < (single ? 0 : 1) + idSize + sizeSize)
        {
            throw Corrupt("The data ends inside a frame header.", at);
        }

        long window = 0;
        if (!single)
        {
            int exponent = input[at] >> 3;
            int mantissa = input[at] & 7;
            long windowBase = 1L << (10 + exponent);
            window = windowBase + ((windowBase >> 3) * mantissa);
            at++;
        }

        ulong id = ReadLittleEndian(input.Slice(at, idSize));
        at += idSize;
        if (id != 0)
        {
            throw new UnsupportedFormatException($"Zstandard: the frame needs dictionary {id}, and dictionaries are not read.");
        }

        long contentSize = -1;
        if (sizeSize > 0)
        {
            ulong size = ReadLittleEndian(input.Slice(at, sizeSize)) + (sizeSize == 2 ? 256u : 0u);
            at += sizeSize;
            if (size > (ulong)(output.Length - written))
            {
                throw Corrupt($"A frame holds {size} bytes, more than the {output.Length - written} the output has room for.", at);
            }

            contentSize = (long)size;
        }

        if (single)
        {
            window = contentSize;
        }

        int blockMax = (int)Math.Min(MaxBlockSize, window);
        literalLengthTable = null;
        matchLengthTable = null;
        offsetTable = null;
        huffmanBits = 0;
        repeat1 = 1;
        repeat2 = 4;
        repeat3 = 8;
        int start = written;
        while (true)
        {
            if (input.Length - at < 3)
            {
                throw Corrupt("The data ends inside a block header.", at);
            }

            int header = input[at] | (input[at + 1] << 8) | (input[at + 2] << 16);
            at += 3;
            int size = header >> 3;
            int type = (header >> 1) & 3;
            if (type == 3)
            {
                throw Corrupt("A block has the reserved type.", at - 3);
            }

            if (size > blockMax)
            {
                throw Corrupt($"A block of {size} bytes is over the frame's block size of {blockMax}.", at - 3);
            }

            int stored = type == 1 ? 1 : size;
            if (input.Length - at < stored)
            {
                throw Corrupt("A block runs past the end of the data.", at - 3);
            }

            if (type != 2 && size > output.Length - written)
            {
                throw Corrupt("The data holds more than the output has room for.", at - 3);
            }

            if (type == 0)
            {
                input.Slice(at, size).CopyTo(output[written..]);
                written += size;
            }
            else if (type == 1)
            {
                output.Slice(written, size).Fill(input[at]);
                written += size;
            }
            else
            {
                int before = written;
                written = Block(input.Slice(at, size), output, written, start, window, blockMax, at);
                if (written - before > blockMax)
                {
                    throw Corrupt($"A block decodes to more than the frame's block size of {blockMax}.", at - 3);
                }
            }

            at += stored;
            if ((header & 1) != 0)
            {
                break;
            }
        }

        if (contentSize >= 0 && written - start != contentSize)
        {
            throw Corrupt($"A frame decodes to {written - start} bytes, not the {contentSize} its header gives.", at);
        }

        if (checksum)
        {
            if (input.Length - at < 4)
            {
                throw Corrupt("The data ends inside a frame's checksum.", at);
            }

            uint expected = BinaryPrimitives.ReadUInt32LittleEndian(input[at..]);
            if ((uint)XxHash64.Hash(output[start..written]) != expected)
            {
                throw Corrupt("A frame's content checksum does not match.", at);
            }

            at += 4;
        }

        return at;
    }

    private static ulong ReadLittleEndian(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            value |= (ulong)bytes[i] << (8 * i);
        }

        return value;
    }

    /// <summary>Decodes a compressed block at <paramref name="written"/>; returns the new end of the output.</summary>
    private int Block(ReadOnlySpan<byte> block, Span<byte> output, int written, int frameStart, long window, int blockMax, long offset)
    {
        int used = Literals(block, blockMax, out int literalCount, offset);
        ReadOnlySpan<byte> rest = block[used..];
        if (rest.IsEmpty)
        {
            throw Corrupt("A block has no sequences section.", offset);
        }

        int sequences = rest[0];
        int at = 1;
        if (sequences >= 128)
        {
            if (sequences < 255)
            {
                if (rest.Length < 2)
                {
                    throw Corrupt("A sequences header is cut short.", offset);
                }

                sequences = ((sequences - 128) << 8) + rest[1];
                at = 2;
            }
            else
            {
                if (rest.Length < 3)
                {
                    throw Corrupt("A sequences header is cut short.", offset);
                }

                sequences = rest[1] + (rest[2] << 8) + 0x7F00;
                at = 3;
            }
        }

        if (sequences == 0)
        {
            if (rest.Length != 1)
            {
                throw Corrupt("A block without sequences has bytes after its literals.", offset);
            }

            if (literalCount > output.Length - written)
            {
                throw Corrupt("The data holds more than the output has room for.", offset);
            }

            literals.AsSpan(0, literalCount).CopyTo(output[written..]);
            return written + literalCount;
        }

        if (at >= rest.Length)
        {
            throw Corrupt("A sequences header is cut short.", offset);
        }

        int modes = rest[at++];
        if ((modes & 3) != 0)
        {
            throw Corrupt("A sequences header's reserved bits are set.", offset);
        }

        at += Table(rest[at..], modes >> 6, 35, 9, LiteralLengthBase, LiteralLengthBits, LiteralLengthPredefined, 6, literalLengthOwn,
            ref literalLengthTable, ref literalLengthLog, offset);
        at += Table(rest[at..], (modes >> 4) & 3, 31, 8, OffsetBase, OffsetBits, OffsetPredefined, 5, offsetOwn, ref offsetTable,
            ref offsetLog, offset);
        at += Table(rest[at..], (modes >> 2) & 3, 52, 9, MatchLengthBase, MatchLengthBits, MatchLengthPredefined, 6, matchLengthOwn,
            ref matchLengthTable, ref matchLengthLog, offset);
        return Sequences(rest[at..], sequences, literalCount, output, written, frameStart, window, offset);
    }

    /// <summary>Reads the literals section into <see cref="literals"/>; returns its size in the block.</summary>
    private int Literals(ReadOnlySpan<byte> block, int blockMax, out int count, long offset)
    {
        if (block.IsEmpty)
        {
            throw Corrupt("A block has no literals section.", offset);
        }

        int first = block[0];
        int type = first & 3;
        int format = (first >> 2) & 3;
        if (type <= 1)
        {
            // Raw or RLE: a header of one, two or three bytes holding the size.
            int header = format switch
            {
                1 => 2,
                3 => 3,
                _ => 1,
            };
            if (block.Length < header)
            {
                throw Corrupt("A literals header is cut short.", offset);
            }

            count = format switch
            {
                1 => (first >> 4) + (block[1] << 4),
                3 => (first >> 4) + (block[1] << 4) + (block[2] << 12),
                _ => first >> 3,
            };
            if (count > blockMax)
            {
                throw Corrupt($"A block has {count} literals, over the block size of {blockMax}.", offset);
            }

            if (type == 0)
            {
                if (block.Length - header < count)
                {
                    throw Corrupt("Raw literals run past the end of the block.", offset);
                }

                block.Slice(header, count).CopyTo(literals);
                return header + count;
            }

            if (block.Length - header < 1)
            {
                throw Corrupt("RLE literals are cut short.", offset);
            }

            literals.AsSpan(0, count).Fill(block[header]);
            return header + 1;
        }

        // Huffman-coded, with a new tree or the last one: three to five bytes for both sizes.
        int size = format switch
        {
            2 => 4,
            3 => 5,
            _ => 3,
        };
        if (block.Length < size)
        {
            throw Corrupt("A literals header is cut short.", offset);
        }

        ulong value = ReadLittleEndian(block[..size]);
        int bits = format switch
        {
            2 => 14,
            3 => 18,
            _ => 10,
        };
        int mask = (1 << bits) - 1;
        count = (int)(value >> 4) & mask;
        int compressed = (int)(value >> (4 + bits)) & mask;
        if (count > blockMax)
        {
            throw Corrupt($"A block has {count} literals, over the block size of {blockMax}.", offset);
        }

        if (block.Length - size < compressed)
        {
            throw Corrupt("Compressed literals run past the end of the block.", offset);
        }

        ReadOnlySpan<byte> source = block.Slice(size, compressed);
        if (type == 2)
        {
            int tree = ZstdHuffman.ReadTree(source, huffman, out huffmanBits);
            source = source[tree..];
        }
        else if (huffmanBits == 0)
        {
            throw Corrupt("Literals reuse a Huffman tree before the frame has one.", offset);
        }

        Span<byte> target = literals.AsSpan(0, count);
        if (format == 0)
        {
            ZstdHuffman.DecodeOne(source, target, huffman, huffmanBits);
        }
        else
        {
            ZstdHuffman.DecodeFour(source, target, huffman, huffmanBits);
        }

        return size + compressed;
    }

    /// <summary>Sets up one of the three sequence tables from its mode; returns the bytes it takes.</summary>
    private static int Table(ReadOnlySpan<byte> source, int mode, int maxSymbol, int maxLog, uint[] baseValues, byte[] extraBits,
        ZstdFseEntry[] predefined, int predefinedLog, ZstdFseEntry[] own, ref ZstdFseEntry[]? table, ref int log, long offset)
    {
        switch (mode)
        {
            case 0:
                table = predefined;
                log = predefinedLog;
                return 0;
            case 1:
                if (source.IsEmpty)
                {
                    throw Corrupt("An RLE sequence table is cut short.", offset);
                }

                int symbol = source[0];
                if (symbol > maxSymbol)
                {
                    throw Corrupt("An RLE sequence table's symbol is out of range.", offset);
                }

                own[0] = new ZstdFseEntry { BaseValue = baseValues[symbol], ExtraBits = extraBits[symbol] };
                table = own;
                log = 0;
                return 1;
            case 2:
                Span<short> counts = stackalloc short[64];
                int used = ZstdFse.ReadCounts(source, counts, maxSymbol, maxLog, out log, out int symbols);
                ZstdFse.Build(counts, symbols, log, baseValues, extraBits, own);
                table = own;
                return used;
            default:
                if (table is null)
                {
                    throw Corrupt("A sequence table repeats before the frame has one.", offset);
                }

                return 0;
        }
    }

    private int Sequences(ReadOnlySpan<byte> stream, int count, int literalCount, Span<byte> output, int written, int frameStart, long window,
        long offset)
    {
        if (!ZstdBitReader.TryStart(stream, out ZstdBitReader reader))
        {
            throw Corrupt("A sequences bit stream has no end mark.", offset);
        }

        ref byte start = ref MemoryMarshal.GetReference(stream);
        ref ZstdFseEntry lengths = ref MemoryMarshal.GetArrayDataReference(literalLengthTable!);
        ref ZstdFseEntry offsets = ref MemoryMarshal.GetArrayDataReference(offsetTable!);
        ref ZstdFseEntry matches = ref MemoryMarshal.GetArrayDataReference(matchLengthTable!);
        uint lengthState = reader.ReadBits(literalLengthLog);
        uint offsetState = reader.ReadBits(offsetLog);
        uint matchState = reader.ReadBits(matchLengthLog);
        reader.Reload(ref start);

        ref byte target = ref MemoryMarshal.GetReference(output);
        ref byte source = ref MemoryMarshal.GetArrayDataReference(literals);
        int outputLength = output.Length;
        int literal = 0;
        uint rep1 = repeat1;
        uint rep2 = repeat2;
        uint rep3 = repeat3;
        for (int left = count; left > 0; left--)
        {
            ZstdFseEntry o = Unsafe.Add(ref offsets, offsetState);
            ZstdFseEntry m = Unsafe.Add(ref matches, matchState);
            ZstdFseEntry l = Unsafe.Add(ref lengths, lengthState);

            // Offset, match length, then literal length bits; the states update in the other order.
            uint offsetValue = o.BaseValue + reader.ReadBits(o.ExtraBits);
            int matchLength = (int)(m.BaseValue + reader.ReadBits(m.ExtraBits));
            if (reader.Consumed > 22)
            {
                reader.Reload(ref start);
            }

            int literalLength = (int)(l.BaseValue + reader.ReadBits(l.ExtraBits));
            if (left > 1)
            {
                lengthState = l.NextState + reader.ReadBits(l.StateBits);
                matchState = m.NextState + reader.ReadBits(m.StateBits);
                offsetState = o.NextState + reader.ReadBits(o.StateBits);
            }

            reader.Reload(ref start);

            uint distance;
            if (offsetValue > 3)
            {
                distance = offsetValue - 3;
                rep3 = rep2;
                rep2 = rep1;
                rep1 = distance;
            }
            else
            {
                // A repeat: with no literals before it, each code means the next older offset, and 3 means the newest less one.
                uint index = offsetValue - (literalLength == 0 ? 0u : 1u);
                if (index == 0)
                {
                    distance = rep1;
                }
                else
                {
                    distance = index switch
                    {
                        1 => rep2,
                        2 => rep3,
                        _ => rep1 - 1,
                    };
                    if (index != 1)
                    {
                        rep3 = rep2;
                    }

                    rep2 = rep1;
                    rep1 = distance;
                }
            }

            if (literalLength > literalCount - literal)
            {
                throw Corrupt("A sequence takes more literals than the block has.", offset);
            }

            long end = (long)written + literalLength + matchLength;
            if (end > outputLength)
            {
                throw Corrupt("The data holds more than the output has room for.", offset);
            }

            bool roomy = end + CopySlack <= outputLength;
            if (roomy)
            {
                WildCopy(ref Unsafe.Add(ref target, written), ref Unsafe.Add(ref source, literal), literalLength);
            }
            else
            {
                Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref target, written), ref Unsafe.Add(ref source, literal), (uint)literalLength);
            }

            written += literalLength;
            literal += literalLength;
            if (distance == 0 || distance > (uint)(written - frameStart) || distance > window)
            {
                throw Corrupt("A match reaches back before the frame or its window.", offset);
            }

            Match(ref Unsafe.Add(ref target, written), (int)distance, matchLength, roomy);
            written += matchLength;
        }

        if (!reader.Finished)
        {
            throw Corrupt("A sequences bit stream's length does not match its sequences.", offset);
        }

        repeat1 = rep1;
        repeat2 = rep2;
        repeat3 = rep3;
        int tail = literalCount - literal;
        if (tail > outputLength - written)
        {
            throw Corrupt("The data holds more than the output has room for.", offset);
        }

        Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref target, written), ref Unsafe.Add(ref source, literal), (uint)tail);
        return written + tail;
    }

    /// <summary>Copies 16 bytes at a time, so up to 15 bytes past <paramref name="length"/> are read and written.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WildCopy(ref byte target, ref byte source, int length)
    {
        Unsafe.WriteUnaligned(ref target, Unsafe.ReadUnaligned<Vector128<byte>>(ref source));
        for (int i = 16; i < length; i += 16)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, i), Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref source, i)));
        }
    }

    /// <summary>Copies a match <paramref name="distance"/> bytes back to <paramref name="target"/>, overlapping or not.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Match(ref byte target, int distance, int length, bool roomy)
    {
        ref byte source = ref Unsafe.Subtract(ref target, distance);
        if (roomy)
        {
            if (distance >= 16)
            {
                WildCopy(ref target, ref source, length);
                return;
            }

            if (distance < 8)
            {
                // Four bytes one by one, then four from where the pattern repeats; after that the source lies eight to fourteen
                // bytes back, a whole number of periods, so eight-byte copies keep the pattern.
                target = source;
                Unsafe.Add(ref target, 1) = Unsafe.Add(ref source, 1);
                Unsafe.Add(ref target, 2) = Unsafe.Add(ref source, 2);
                Unsafe.Add(ref target, 3) = Unsafe.Add(ref source, 3);
                source = ref Unsafe.Add(ref source, ShortDistanceForward[distance]);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, 4), Unsafe.ReadUnaligned<uint>(ref source));
                source = ref Unsafe.Subtract(ref source, ShortDistanceBack[distance]);
            }
            else
            {
                Unsafe.WriteUnaligned(ref target, Unsafe.ReadUnaligned<ulong>(ref source));
            }

            for (int i = 8; i < length; i += 8)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, i), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, i)));
            }
        }
        else if (distance >= length)
        {
            Unsafe.CopyBlockUnaligned(ref target, ref source, (uint)length);
        }
        else if (distance == 1)
        {
            Unsafe.InitBlockUnaligned(ref target, source, (uint)length);
        }
        else
        {
            // The bytes from the source on repeat with the distance's period, so each copy can take twice as many as the last.
            int done = 0;
            int chunk = distance;
            while (done < length)
            {
                int n = Math.Min(chunk, length - done);
                Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref target, done), ref source, (uint)n);
                done += n;
                chunk = done + distance;
            }
        }
    }
}
