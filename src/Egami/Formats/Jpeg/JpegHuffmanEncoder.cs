using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

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
    internal const int BufferSize = 64 * 1024;

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

    /// <summary>
    /// Starts a run of <see cref="Flush(ref byte, ref int, ref ulong, ref int)"/> calls in the caller's registers: makes room for
    /// <paramref name="bytes"/> more bytes plus the 8 a flush stores past its end, writes out whole bytes so fewer than 8 bits stay
    /// buffered, and returns the buffer with the write position and the bit buffer; <see cref="End"/> puts them back.
    /// </summary>
    internal ref byte Begin(int bytes, out int length, out ulong bits, out int count)
    {
        Reserve(bytes + 8);
        if (_count >= 8)
        {
            Drain();
        }

        length = _length;
        bits = _bits;
        count = _count;
        return ref MemoryMarshal.GetArrayDataReference(_buffer);
    }

    /// <summary>Puts back the write position and bit buffer of a run started with <see cref="Begin"/>.</summary>
    internal void End(int length, ulong bits, int count)
    {
        _length = length;
        _bits = bits;
        _count = count;
    }

    /// <summary>
    /// Writes out every whole byte of the bit buffer (it holds at most 63 bits), leaving fewer than 8: when none of them is 0xFF,
    /// as one unaligned 8-byte store with the write position moved by the bytes written, else a byte at a time with the stuffed
    /// zeros. The bytes the store puts past the new position are overwritten later.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Flush(ref byte target, ref int length, ref ulong bits, ref int count)
    {
        int whole = count >> 3;

        // The buffered bits at the top of the word; shifted in two steps so that an empty buffer gives 0.
        ulong word = (bits << (63 - count)) << 1;
        ulong inverted = ~word;
        ulong stuffed = (inverted - 0x0101010101010101UL) & ~inverted & 0x8080808080808080UL & ~(ulong.MaxValue >> (8 * whole));
        if (stuffed != 0)
        {
            length = FlushStuffed(ref target, length, word, whole);
        }
        else
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, length), BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(word) : word);
            length += whole;
        }

        count &= 7;
    }

    /// <summary>Writes the top <paramref name="whole"/> bytes of <paramref name="word"/> one at a time with the stuffed zeros; returns the new position.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int FlushStuffed(ref byte target, int length, ulong word, int whole)
    {
        for (int i = 0; i < whole; i++)
        {
            byte b = (byte)(word >> (56 - (8 * i)));
            Unsafe.Add(ref target, length++) = b;
            if (b == 0xFF)
            {
                Unsafe.Add(ref target, length++) = 0;
            }
        }

        return length;
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
    /// The most bytes one 8-bit block can take: a 27-bit DC symbol, 63 AC symbols of at most 26 bits and the end of block, every
    /// byte 0xFF and stuffed.
    /// </summary>
    private const int MaxBlockBytes = 2 * ((27 + (63 * 26) + 16 + 7) / 8);

    /// <summary>
    /// Writes one block of an 8-bit frame, stored in natural order, updating the DC predictor. A bit mask of the nonzero
    /// coefficients gives each zero run directly, so zeros are never visited one by one. Each symbol goes into the 64-bit buffer
    /// with its value bits in one step and without a test: a symbol has at most 27 bits, so after every second one the buffer
    /// (under 8 bits after a flush) is flushed whole, with one test for 0xFF bytes.
    /// </summary>
    internal static void Encode(JpegBitWriter writer, ReadOnlySpan<short> block, ref int predictor, JpegHuffmanCodes dc, JpegHuffmanCodes ac)
    {
        ref byte target = ref writer.Begin(MaxBlockBytes, out int length, out ulong bits, out int count);
        EncodeBlock(ref target, ref length, ref bits, ref count, block, ref predictor, dc, ac);
        writer.End(length, bits, count);
    }

    /// <summary>
    /// <see cref="Encode"/> for <paramref name="mcus"/> whole MCUs of an 8-bit frame in one call, the bit buffer in registers
    /// throughout: component i has <paramref name="blocksPerMcu"/>[i] blocks in each MCU, coded with the tables numbered
    /// <paramref name="tables"/>[i]. No restart may fall inside.
    /// </summary>
    internal static void EncodeMcus(
        JpegBitWriter writer, ReadOnlySpan<short> blocks, int mcus, ReadOnlySpan<int> blocksPerMcu, ReadOnlySpan<int> tables, Span<int> predictors,
        JpegHuffmanCodes[] dc, JpegHuffmanCodes[] ac)
    {
        ref byte target = ref writer.Begin(MaxBlockBytes, out int length, out ulong bits, out int count);
        int b = 0;
        for (int m = 0; m < mcus; m++)
        {
            for (int i = 0; i < blocksPerMcu.Length; i++)
            {
                JpegHuffmanCodes dcCodes = dc[tables[i]], acCodes = ac[tables[i]];
                for (int n = 0; n < blocksPerMcu[i]; n++, b++)
                {
                    if (length > JpegBitWriter.BufferSize - MaxBlockBytes - 8)
                    {
                        writer.End(length, bits, count);
                        target = ref writer.Begin(MaxBlockBytes, out length, out bits, out count);
                    }

                    EncodeBlock(ref target, ref length, ref bits, ref count, blocks.Slice(b * 64, 64), ref predictors[i], dcCodes, acCodes);
                }
            }
        }

        writer.End(length, bits, count);
    }

    /// <summary>Codes one block into a bit buffer that holds under 8 bits, with room for <see cref="MaxBlockBytes"/> at <paramref name="length"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeBlock(
        ref byte target, ref int length, ref ulong bits, ref int count, ReadOnlySpan<short> block, ref int predictor, JpegHuffmanCodes dc, JpegHuffmanCodes ac)
    {
        block = block[..64];
        ref short coefficients = ref MemoryMarshal.GetReference(block);
        ref byte zigzag = ref MemoryMarshal.GetReference(JpegTables.ZigZag);
        ref int acCodes = ref MemoryMarshal.GetArrayDataReference(ac.Entries);
        ulong nonzero = JpegBlock.NonZeroZigZag(block) & ~1UL;

        // The DC symbol is the first of the first pair.
        int diff = coefficients - predictor;
        predictor = coefficients;
        int size = Size(diff);
        Add(ref bits, ref count, dc.Entries[size], size, diff);
        int last = 0;
        while (nonzero != 0)
        {
            AddAc(ref target, ref length, ref bits, ref count, ref coefficients, ref zigzag, ref acCodes, ref nonzero, ref last);
            JpegBitWriter.Flush(ref target, ref length, ref bits, ref count);
            if (nonzero == 0)
            {
                break;
            }

            AddAc(ref target, ref length, ref bits, ref count, ref coefficients, ref zigzag, ref acCodes, ref nonzero, ref last);
        }

        if (last != 63)
        {
            Add(ref bits, ref count, acCodes, 0, 0);
        }

        JpegBitWriter.Flush(ref target, ref length, ref bits, ref count);
    }

    /// <summary>
    /// Adds the symbol of the next nonzero AC coefficient, after the ZRL codes of a run over 15 zeros; those are rare and each is
    /// flushed alone, so the pair rule holds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddAc(
        ref byte target, ref int length, ref ulong bits, ref int count, ref short coefficients, ref byte zigzag, ref int acCodes, ref ulong nonzero, ref int last)
    {
        int k = BitOperations.TrailingZeroCount(nonzero);
        int run = k - last - 1;
        if (run > 15)
        {
            do
            {
                JpegBitWriter.Flush(ref target, ref length, ref bits, ref count);
                Add(ref bits, ref count, Unsafe.Add(ref acCodes, 0xF0), 0, 0);
                run -= 16;
            }
            while (run > 15);

            JpegBitWriter.Flush(ref target, ref length, ref bits, ref count);
        }

        int value = Unsafe.Add(ref coefficients, Unsafe.Add(ref zigzag, k));
        int size = Size(value);
        Add(ref bits, ref count, Unsafe.Add(ref acCodes, (run << 4) | size), size, value);
        last = k;
        nonzero &= nonzero - 1;
    }

    /// <summary>Adds a code (from <see cref="JpegHuffmanCodes.Entries"/>) and the low <paramref name="size"/> bits of a value, untested.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add(ref ulong bits, ref int count, int entry, int size, int value)
    {
        // Negative values are written as value - 1 in their low bits (T.81 F.1.2.1).
        int length = (entry >> 16) + size;
        uint low = (uint)(value - (int)((uint)value >> 31));
        low = Bmi2.IsSupported ? Bmi2.ZeroHighBits(low, (uint)size) : low & ((1u << size) - 1);
        bits = (bits << length) | ((uint)(entry & 0xFFFF) << size) | low;
        count += length;
    }

    /// <summary>
    /// <see cref="Encode"/> for 12-bit frames, whose symbols take up to 31 bits: each added with a test, the buffer drained 32 bits
    /// at a time. The bit buffer stays in locals for the block.
    /// </summary>
    internal static void EncodeWide(JpegBitWriter writer, ReadOnlySpan<short> block, ref int predictor, JpegHuffmanCodes dc, JpegHuffmanCodes ac)
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
        CountDc(block[0], ref predictor, dc);
        CountAc(block, ac);
    }

    /// <summary>Counts the DC symbol of a block whose DC is <paramref name="value"/>, updating the predictor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CountDc(int value, ref int predictor, Span<long> dc)
    {
        int diff = value - predictor;
        predictor = value;
        dc[Size(diff)]++;
    }

    /// <summary>Counts the AC symbols of one block into the 256 counts of <paramref name="ac"/>.</summary>
    internal static void CountAc(ReadOnlySpan<short> block, Span<long> ac)
    {
        block = block[..64];
        ac = ac[..256];
        ref short coefficients = ref MemoryMarshal.GetReference(block);
        ref byte zigzag = ref MemoryMarshal.GetReference(JpegTables.ZigZag);
        ref long counts = ref MemoryMarshal.GetReference(ac);
        ulong nonzero = JpegBlock.NonZeroZigZag(block) & ~1UL;
        int last = 0;
        while (nonzero != 0)
        {
            int k = BitOperations.TrailingZeroCount(nonzero);
            int run = k - last - 1;
            while (run > 15)
            {
                Unsafe.Add(ref counts, 0xF0)++;
                run -= 16;
            }

            // Run and size are at most 15 each, so the symbol is one of the 256 counts.
            Unsafe.Add(ref counts, (run << 4) | Size(Unsafe.Add(ref coefficients, Unsafe.Add(ref zigzag, k))))++;
            last = k;
            nonzero &= nonzero - 1;
        }

        if (last != 63)
        {
            counts++;
        }
    }

    /// <summary>Returns the number of bits of a value's magnitude, its size category (T.81 tables F.1 and F.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Size(int value)
    {
        // Without a branch on the sign, which is as good as random.
        int sign = value >> 31;
        return 32 - BitOperations.LeadingZeroCount((uint)((value ^ sign) - sign));
    }
}
