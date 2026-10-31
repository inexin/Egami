using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Compression.Zstd;

/// <summary>
/// Zstandard's Huffman-coded literals (RFC 8878 sections 3.1.1.3.1 and 4.2): the tree description, and the one or four
/// backward bit streams. The decoding table has an entry for every value of the next <c>maxBits</c> bits, holding the symbol in
/// its low byte and its code length in its high byte.
/// </summary>
internal static class ZstdHuffman
{
    /// <summary>The longest code RFC 8878 allows.</summary>
    public const int MaxBits = 11;

    /// <summary>Reads a tree description at the start of <paramref name="source"/> into <paramref name="table"/>; returns the bytes it takes.</summary>
    public static int ReadTree(ReadOnlySpan<byte> source, Span<ushort> table, out int maxBits)
    {
        if (source.IsEmpty)
        {
            throw ZstdDecoder.Corrupt("A Huffman tree description is missing.");
        }

        Span<byte> weights = stackalloc byte[256];
        int header = source[0];
        int count;
        int used;
        if (header >= 128)
        {
            // Direct: four bits a weight, the first in the high half.
            count = header - 127;
            used = 1 + ((count + 1) >> 1);
            if (used > source.Length)
            {
                throw ZstdDecoder.Corrupt("A Huffman tree description is cut short.");
            }

            for (int i = 0; i < count; i++)
            {
                byte pair = source[1 + (i >> 1)];
                weights[i] = (byte)((i & 1) == 0 ? pair >> 4 : pair & 15);
            }
        }
        else
        {
            used = 1 + header;
            if (used > source.Length)
            {
                throw ZstdDecoder.Corrupt("A Huffman tree description is cut short.");
            }

            count = DecodeWeights(source.Slice(1, header), weights);
        }

        // The last symbol's weight is what brings the total to the next power of two.
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            if (weights[i] > MaxBits)
            {
                throw ZstdDecoder.Corrupt("A Huffman weight is over 11.");
            }

            total += (1 << weights[i]) >> 1;
        }

        if (total == 0)
        {
            throw ZstdDecoder.Corrupt("A Huffman tree has no weights.");
        }

        maxBits = BitOperations.Log2((uint)total) + 1;
        int rest = (1 << maxBits) - total;
        if (maxBits > MaxBits || !BitOperations.IsPow2(rest))
        {
            throw ZstdDecoder.Corrupt("A Huffman tree's weights do not add up.");
        }

        weights[count++] = (byte)(BitOperations.Log2((uint)rest) + 1);

        // Codes go out by weight, lightest (longest) first, and by symbol within a weight.
        Span<int> start = stackalloc int[MaxBits + 2];
        for (int i = 0; i < count; i++)
        {
            start[weights[i]]++;
        }

        if (start[1] < 2 || (start[1] & 1) != 0)
        {
            throw ZstdDecoder.Corrupt("A Huffman tree has an odd number of the longest codes.");
        }

        int position = 0;
        for (int w = 1; w <= maxBits; w++)
        {
            int n = start[w];
            start[w] = position;
            position += n << (w - 1);
        }

        for (int s = 0; s < count; s++)
        {
            int w = weights[s];
            if (w == 0)
            {
                continue;
            }

            int length = 1 << (w - 1);
            ushort entry = (ushort)(s | ((maxBits + 1 - w) << 8));
            table.Slice(start[w], length).Fill(entry);
            start[w] += length;
        }

        return used;
    }

    /// <summary>Decodes one stream of <paramref name="output"/>.Length symbols.</summary>
    public static void DecodeOne(ReadOnlySpan<byte> stream, Span<byte> output, ReadOnlySpan<ushort> table, int maxBits)
    {
        if (!ZstdBitReader.TryStart(stream, out ZstdBitReader reader))
        {
            throw ZstdDecoder.Corrupt("A Huffman stream has no end mark.");
        }

        ref byte start = ref MemoryMarshal.GetReference(stream);
        ref ushort codes = ref MemoryMarshal.GetReference(table);
        ref byte target = ref MemoryMarshal.GetReference(output);
        int at = 0;
        int end = output.Length;
        while (reader.Position >= 8 && at + 4 <= end)
        {
            reader.Reload(ref start);
            Unsafe.Add(ref target, at) = Symbol(ref reader, ref codes, maxBits);
            Unsafe.Add(ref target, at + 1) = Symbol(ref reader, ref codes, maxBits);
            Unsafe.Add(ref target, at + 2) = Symbol(ref reader, ref codes, maxBits);
            Unsafe.Add(ref target, at + 3) = Symbol(ref reader, ref codes, maxBits);
            at += 4;
        }

        for (; at < end; at++)
        {
            reader.Reload(ref start);
            Unsafe.Add(ref target, at) = Symbol(ref reader, ref codes, maxBits);
        }

        reader.Reload(ref start);
        if (!reader.Finished)
        {
            throw ZstdDecoder.Corrupt("A Huffman stream's length does not match its literals.");
        }
    }

    /// <summary>Decodes four streams behind a six-byte jump table into the quarters of <paramref name="output"/>.</summary>
    public static void DecodeFour(ReadOnlySpan<byte> source, Span<byte> output, ReadOnlySpan<ushort> table, int maxBits)
    {
        if (source.Length < 6)
        {
            throw ZstdDecoder.Corrupt("A Huffman jump table is cut short.");
        }

        int size1 = BinaryPrimitives.ReadUInt16LittleEndian(source);
        int size2 = BinaryPrimitives.ReadUInt16LittleEndian(source[2..]);
        int size3 = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        int size4 = source.Length - 6 - size1 - size2 - size3;
        int segment = (output.Length + 3) >> 2;
        int last = output.Length - (3 * segment);
        if (size4 < 0 || last < 0)
        {
            throw ZstdDecoder.Corrupt("A Huffman jump table does not fit its literals.");
        }

        ReadOnlySpan<byte> s1 = source.Slice(6, size1);
        ReadOnlySpan<byte> s2 = source.Slice(6 + size1, size2);
        ReadOnlySpan<byte> s3 = source.Slice(6 + size1 + size2, size3);
        ReadOnlySpan<byte> s4 = source[(6 + size1 + size2 + size3)..];
        if (!ZstdBitReader.TryStart(s1, out ZstdBitReader r1) || !ZstdBitReader.TryStart(s2, out ZstdBitReader r2)
            || !ZstdBitReader.TryStart(s3, out ZstdBitReader r3) || !ZstdBitReader.TryStart(s4, out ZstdBitReader r4))
        {
            throw ZstdDecoder.Corrupt("A Huffman stream has no end mark.");
        }

        ref byte b1 = ref MemoryMarshal.GetReference(s1);
        ref byte b2 = ref MemoryMarshal.GetReference(s2);
        ref byte b3 = ref MemoryMarshal.GetReference(s3);
        ref byte b4 = ref MemoryMarshal.GetReference(s4);
        ref ushort codes = ref MemoryMarshal.GetReference(table);
        ref byte o1 = ref MemoryMarshal.GetReference(output);
        ref byte o2 = ref Unsafe.Add(ref o1, segment);
        ref byte o3 = ref Unsafe.Add(ref o2, segment);
        ref byte o4 = ref Unsafe.Add(ref o3, segment);

        // The four streams side by side while every one has eight bytes to go and the last quarter (the shortest) room for four,
        // with each reader's fields in locals so they stay in registers.
        int at = 0;
        int shift = 64 - maxBits;
        ulong c1 = r1.Container, c2 = r2.Container, c3 = r3.Container, c4 = r4.Container;
        int n1 = r1.Consumed, n2 = r2.Consumed, n3 = r3.Consumed, n4 = r4.Consumed;
        int p1 = r1.Position, p2 = r2.Position, p3 = r3.Position, p4 = r4.Position;
        while (p1 >= 8 && p2 >= 8 && p3 >= 8 && p4 >= 8 && at + 4 <= last)
        {
            p1 -= n1 >> 3;
            n1 &= 7;
            c1 = Load(ref b1, p1);
            p2 -= n2 >> 3;
            n2 &= 7;
            c2 = Load(ref b2, p2);
            p3 -= n3 >> 3;
            n3 &= 7;
            c3 = Load(ref b3, p3);
            p4 -= n4 >> 3;
            n4 &= 7;
            c4 = Load(ref b4, p4);
            for (int k = 0; k < 4; k++)
            {
                ushort e1 = Unsafe.Add(ref codes, (nint)((c1 << n1) >> shift));
                ushort e2 = Unsafe.Add(ref codes, (nint)((c2 << n2) >> shift));
                ushort e3 = Unsafe.Add(ref codes, (nint)((c3 << n3) >> shift));
                ushort e4 = Unsafe.Add(ref codes, (nint)((c4 << n4) >> shift));
                n1 += e1 >> 8;
                n2 += e2 >> 8;
                n3 += e3 >> 8;
                n4 += e4 >> 8;
                Unsafe.Add(ref o1, at + k) = (byte)e1;
                Unsafe.Add(ref o2, at + k) = (byte)e2;
                Unsafe.Add(ref o3, at + k) = (byte)e3;
                Unsafe.Add(ref o4, at + k) = (byte)e4;
            }

            at += 4;
        }

        r1 = new ZstdBitReader { Container = c1, Consumed = n1, Position = p1 };
        r2 = new ZstdBitReader { Container = c2, Consumed = n2, Position = p2 };
        r3 = new ZstdBitReader { Container = c3, Consumed = n3, Position = p3 };
        r4 = new ZstdBitReader { Container = c4, Consumed = n4, Position = p4 };
        Tail(ref r1, ref b1, ref o1, at, segment, ref codes, maxBits);
        Tail(ref r2, ref b2, ref o2, at, segment, ref codes, maxBits);
        Tail(ref r3, ref b3, ref o3, at, segment, ref codes, maxBits);
        Tail(ref r4, ref b4, ref o4, at, last, ref codes, maxBits);
    }

    private static void Tail(ref ZstdBitReader reader, ref byte start, ref byte target, int at, int end, ref ushort codes, int maxBits)
    {
        for (; at < end; at++)
        {
            reader.Reload(ref start);
            Unsafe.Add(ref target, at) = Symbol(ref reader, ref codes, maxBits);
        }

        reader.Reload(ref start);
        if (!reader.Finished)
        {
            throw ZstdDecoder.Corrupt("A Huffman stream's length does not match its literals.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Load(ref byte start, int position)
    {
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, position));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Symbol(ref ZstdBitReader reader, ref ushort codes, int maxBits)
    {
        ushort entry = Unsafe.Add(ref codes, reader.Peek(maxBits));
        reader.Consumed += entry >> 8;
        return (byte)entry;
    }

    /// <summary>Decodes FSE-compressed Huffman weights (two interleaved states, accuracy log up to 6); returns how many.</summary>
    private static int DecodeWeights(ReadOnlySpan<byte> source, Span<byte> weights)
    {
        Span<short> counts = stackalloc short[16];
        int used = ZstdFse.ReadCounts(source, counts, 15, 6, out int log, out int symbols);
        Span<ZstdFseEntry> table = stackalloc ZstdFseEntry[64];
        ZstdFse.Build(counts, symbols, log, default, default, table);
        ReadOnlySpan<byte> stream = source[used..];
        if (!ZstdBitReader.TryStart(stream, out ZstdBitReader reader))
        {
            throw ZstdDecoder.Corrupt("A Huffman weight stream has no end mark.");
        }

        ref byte start = ref MemoryMarshal.GetReference(stream);
        int state1 = (int)reader.ReadBits(log);
        int state2 = (int)reader.ReadBits(log);
        reader.Reload(ref start);
        int count = 0;
        while (true)
        {
            // Each state gives a symbol, then reads its next state; when that runs past the start, the other state's symbol
            // is the last.
            if (count > 253)
            {
                throw ZstdDecoder.Corrupt("A Huffman tree has more than 255 weights.");
            }

            weights[count++] = (byte)table[state1].BaseValue;
            state1 = table[state1].NextState + (int)reader.ReadBits(table[state1].StateBits);
            reader.Reload(ref start);
            if (reader.Overflowed)
            {
                weights[count++] = (byte)table[state2].BaseValue;
                break;
            }

            weights[count++] = (byte)table[state2].BaseValue;
            state2 = table[state2].NextState + (int)reader.ReadBits(table[state2].StateBits);
            reader.Reload(ref start);
            if (reader.Overflowed)
            {
                if (count > 254)
                {
                    throw ZstdDecoder.Corrupt("A Huffman tree has more than 255 weights.");
                }

                weights[count++] = (byte)table[state1].BaseValue;
                break;
            }
        }

        return count;
    }
}
