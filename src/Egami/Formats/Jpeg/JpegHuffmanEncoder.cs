using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Jpeg;

/// <summary>A Huffman table for encoding: the code and length of each symbol (T.81 annex C), built from counts and symbols.</summary>
internal sealed class JpegHuffmanCodes
{
    internal JpegHuffmanCodes(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
    {
        Counts = counts.ToArray();
        Symbols = symbols.ToArray();

        // Only as long as the highest symbol: a DC table needs 12 entries, not 256.
        int highest = 0;
        foreach (byte symbol in symbols)
        {
            highest = Math.Max(highest, symbol);
        }

        Entries = new int[highest + 1];
        int code = 0;
        int k = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < counts[length - 1]; i++, k++, code++)
            {
                Entries[symbols[k]] = (length << 16) | code;
            }

            code <<= 1;
        }
    }

    /// <summary>Gets the number of codes of each length, 1 to 16 bits, as the DHT segment stores them.</summary>
    internal byte[] Counts { get; }

    /// <summary>Gets the symbols in code order, as the DHT segment stores them.</summary>
    internal byte[] Symbols { get; }

    /// <summary>Gets the standard luminance DC table (T.81 table K.3).</summary>
    internal static JpegHuffmanCodes DcLuma { get; } = new(JpegTables.DcLumaCounts, JpegTables.DcSymbols);

    /// <summary>Gets the standard chrominance DC table (T.81 table K.4).</summary>
    internal static JpegHuffmanCodes DcChroma { get; } = new(JpegTables.DcChromaCounts, JpegTables.DcSymbols);

    /// <summary>Gets the standard luminance AC table (T.81 table K.5).</summary>
    internal static JpegHuffmanCodes AcLuma { get; } = new(JpegTables.AcLumaCounts, JpegTables.AcLumaSymbols);

    /// <summary>Gets the standard chrominance AC table (T.81 table K.6).</summary>
    internal static JpegHuffmanCodes AcChroma { get; } = new(JpegTables.AcChromaCounts, JpegTables.AcChromaSymbols);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Code(int symbol) => Entries[symbol] & 0xFFFF;

    /// <summary>Gets each symbol's code in the low 16 bits and its length above them, for one lookup per symbol.</summary>
    internal int[] Entries { get; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Length(int symbol) => Entries[symbol] >> 16;

    /// <summary>
    /// Builds the optimal table for symbol frequencies, with no code longer than 16 bits and no code of all ones (T.81 annex K.2,
    /// figures K.1 to K.4). Symbols that never occur get no code.
    /// </summary>
    internal static JpegHuffmanCodes Optimal(ReadOnlySpan<long> frequencies)
    {
        // One extra symbol with frequency 1 takes the all-ones code, which is then dropped.
        Span<long> freq = stackalloc long[257];
        frequencies[..256].CopyTo(freq);
        freq[256] = 1;
        Span<int> codeSize = stackalloc int[257];
        codeSize.Clear();
        Span<int> others = stackalloc int[257];
        others.Fill(-1);
        while (true)
        {
            // The two least frequent symbols; among equals, the one with the higher number (figure K.1).
            int c1 = -1;
            long v = long.MaxValue;
            for (int i = 0; i <= 256; i++)
            {
                if (freq[i] != 0 && freq[i] <= v)
                {
                    v = freq[i];
                    c1 = i;
                }
            }

            int c2 = -1;
            v = long.MaxValue;
            for (int i = 0; i <= 256; i++)
            {
                if (freq[i] != 0 && freq[i] <= v && i != c1)
                {
                    v = freq[i];
                    c2 = i;
                }
            }

            if (c2 < 0)
            {
                break;
            }

            freq[c1] += freq[c2];
            freq[c2] = 0;
            codeSize[c1]++;
            while (others[c1] >= 0)
            {
                c1 = others[c1];
                codeSize[c1]++;
            }

            others[c1] = c2;
            codeSize[c2]++;
            while (others[c2] >= 0)
            {
                c2 = others[c2];
                codeSize[c2]++;
            }
        }

        // Count the codes of each length, then move codes longer than 16 bits up the tree (figure K.3).
        Span<int> bits = stackalloc int[33];
        bits.Clear();
        for (int i = 0; i <= 256; i++)
        {
            if (codeSize[i] > 0)
            {
                bits[Math.Min(codeSize[i], 32)]++;
            }
        }

        for (int i = 32; i > 16; i--)
        {
            while (bits[i] > 0)
            {
                int j = i - 2;
                while (bits[j] == 0)
                {
                    j--;
                }

                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        }

        // Drop the reserved code: it has the longest length.
        int longest = 16;
        while (bits[longest] == 0)
        {
            longest--;
        }

        bits[longest]--;
        Span<byte> counts = stackalloc byte[16];
        for (int i = 1; i <= 16; i++)
        {
            counts[i - 1] = (byte)bits[i];
        }

        // Symbols in order of code length, then number (figure K.4).
        Span<byte> symbols = stackalloc byte[256];
        int symbolCount = 0;
        for (int length = 1; length <= 32; length++)
        {
            for (int s = 0; s < 256; s++)
            {
                if (codeSize[s] == length)
                {
                    symbols[symbolCount++] = (byte)s;
                }
            }
        }

        return new JpegHuffmanCodes(counts, symbols[..symbolCount]);
    }
}

/// <summary>
/// Writes entropy-coded data: bits gathered in a 64-bit buffer, written out a byte at a time with a zero after every 0xFF
/// (T.81 B.1.1.5), into a pooled buffer that goes to the stream as it fills.
/// </summary>
internal sealed class JpegBitWriter : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly Stream _output;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _length;
    private ulong _bits;
    private int _count;

    internal JpegBitWriter(Stream output)
    {
        _output = output;
    }

    /// <summary>Adds the low <paramref name="length"/> bits of <paramref name="value"/> (0 to 31 bits).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Put(int value, int length)
    {
        _bits = (_bits << length) | ((uint)value & ((1u << length) - 1));
        _count += length;
        if (_count >= 32)
        {
            Drain();
        }
    }

    /// <summary>Copies the bit buffer out, for a hot loop to keep in registers; <see cref="Store"/> puts it back.</summary>
    internal void Load(out ulong bits, out int count)
    {
        bits = _bits;
        count = _count;
    }

    /// <summary>Puts back a bit buffer taken with <see cref="Load"/>.</summary>
    internal void Store(ulong bits, int count)
    {
        _bits = bits;
        _count = count;
    }

    /// <summary>
    /// Writes out the oldest 32 of the buffered bits (there must be at least 32): in one step when none of the four bytes is 0xFF,
    /// else a byte at a time with the stuffed zeros.
    /// </summary>
    internal void Drain32(ref ulong bits, ref int count)
    {
        Reserve(8);
        count -= 32;
        uint word = (uint)(bits >> count);
        uint inverted = ~word;
        if (((inverted - 0x01010101u) & ~inverted & 0x80808080u) == 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length, 4), word);
            _length += 4;
            return;
        }

        for (int shift = 24; shift >= 0; shift -= 8)
        {
            byte b = (byte)(word >> shift);
            _buffer[_length++] = b;
            if (b == 0xFF)
            {
                _buffer[_length++] = 0;
            }
        }
    }

    /// <summary>Pads the last byte with one bits, as T.81 asks before a marker.</summary>
    internal void Align()
    {
        int pad = (8 - (_count & 7)) & 7;
        if (pad > 0)
        {
            Put((1 << pad) - 1, pad);
        }

        Drain();
    }

    /// <summary>Writes bytes as they are, for segments outside the entropy-coded data.</summary>
    internal void Raw(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            Reserve(1);
            int n = Math.Min(bytes.Length, BufferSize - _length);
            bytes[..n].CopyTo(_buffer.AsSpan(_length));
            _length += n;
            bytes = bytes[n..];
        }
    }

    /// <summary>Writes one byte of arithmetic-coded data, with a zero after 0xFF (the bit buffer must be empty).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Byte(int value)
    {
        Reserve(2);
        _buffer[_length++] = (byte)value;
        if (value == 0xFF)
        {
            _buffer[_length++] = 0;
        }
    }

    /// <summary>Writes a marker (after <see cref="Align"/>).</summary>
    internal void Marker(byte marker)
    {
        Reserve(2);
        _buffer[_length++] = 0xFF;
        _buffer[_length++] = marker;
    }

    /// <summary>Writes everything buffered to the stream.</summary>
    internal void Flush()
    {
        _output.Write(_buffer, 0, _length);
        _length = 0;
    }

    public void Dispose()
    {
        byte[] buffer = Interlocked.Exchange(ref _buffer, null!);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Drain()
    {
        Reserve(16);
        while (_count >= 8)
        {
            _count -= 8;
            byte b = (byte)(_bits >> _count);
            _buffer[_length++] = b;
            if (b == 0xFF)
            {
                _buffer[_length++] = 0;
            }
        }
    }

    private void Reserve(int bytes)
    {
        if (_length + bytes > BufferSize)
        {
            Flush();
        }
    }
}

/// <summary>Huffman-codes quantized blocks (T.81 F.1.2), or only counts the symbols they would use, for optimized tables.</summary>
internal static class JpegBlockCoder
{
    /// <summary>
    /// Writes one block, stored in natural order, updating the DC predictor. A bit mask of the nonzero coefficients gives each zero run
    /// directly, so zeros are never visited one by one; each code goes out with its value bits in one write, and the bit buffer
    /// stays in locals for the block.
    /// </summary>
    internal static void Encode(JpegBitWriter writer, ReadOnlySpan<short> block, ref int predictor, JpegHuffmanCodes dc, JpegHuffmanCodes ac)
    {
        block = block[..64];
        ref short coefficients = ref MemoryMarshal.GetReference(block);
        ref byte zigzag = ref MemoryMarshal.GetReference(JpegTables.ZigZag);
        ref int acCodes = ref MemoryMarshal.GetArrayDataReference(ac.Entries);
        writer.Load(out ulong bits, out int count);

        int diff = coefficients - predictor;
        predictor = coefficients;
        int size = Size(diff);
        Emit(writer, ref bits, ref count, dc.Entries[size], size, diff);

        ulong nonzero = JpegBlock.NonZeroZigZag(block) & ~1UL;
        int last = 0;
        while (nonzero != 0)
        {
            int k = BitOperations.TrailingZeroCount(nonzero);
            int run = k - last - 1;
            while (run > 15)
            {
                Emit(writer, ref bits, ref count, Unsafe.Add(ref acCodes, 0xF0), 0, 0);
                run -= 16;
            }

            int value = Unsafe.Add(ref coefficients, Unsafe.Add(ref zigzag, k));
            size = Size(value);
            Emit(writer, ref bits, ref count, Unsafe.Add(ref acCodes, (run << 4) | size), size, value);
            last = k;
            nonzero &= nonzero - 1;
        }

        if (last != 63)
        {
            Emit(writer, ref bits, ref count, acCodes, 0, 0);
        }

        writer.Store(bits, count);
    }

    /// <summary>Adds a code (from <see cref="JpegHuffmanCodes.Entries"/>) and the low <paramref name="size"/> bits of a value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Emit(JpegBitWriter writer, ref ulong bits, ref int count, int entry, int size, int value)
    {
        // Negative values are written as value - 1 in their low bits (T.81 F.1.2.1).
        int length = (entry >> 16) + size;
        uint payload = ((uint)(entry & 0xFFFF) << size) | ((uint)(value - (int)((uint)value >> 31)) & ((1u << size) - 1));
        bits = (bits << length) | payload;
        count += length;
        if (count >= 32)
        {
            writer.Drain32(ref bits, ref count);
        }
    }

    /// <summary>Counts the symbols one block would use, updating the DC predictor.</summary>
    internal static void Count(ReadOnlySpan<short> block, ref int predictor, Span<long> dc, Span<long> ac)
    {
        int diff = block[0] - predictor;
        predictor = block[0];
        dc[Size(diff)]++;
        ulong nonzero = JpegBlock.NonZeroZigZag(block) & ~1UL;
        int last = 0;
        while (nonzero != 0)
        {
            int k = BitOperations.TrailingZeroCount(nonzero);
            int run = k - last - 1;
            while (run > 15)
            {
                ac[0xF0]++;
                run -= 16;
            }

            ac[(run << 4) | Size(block[JpegTables.ZigZag[k]])]++;
            last = k;
            nonzero &= nonzero - 1;
        }

        if (last != 63)
        {
            ac[0x00]++;
        }
    }

    /// <summary>Returns the number of bits of a value's magnitude, its size category (T.81 tables F.1 and F.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Size(int value)
    {
        return 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(value));
    }
}
