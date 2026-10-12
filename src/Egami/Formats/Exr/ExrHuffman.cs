using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Exr;

/// <summary>
/// OpenEXR's Huffman code for 16-bit words (internal_huf.c, BSD-3-Clause; see THIRD-PARTY-NOTICES): a canonical code over the
/// symbols im to iM, code lengths packed in 6 bits with runs of zero lengths shortened, and symbol iM meaning "repeat the last
/// word as many more times as the next 8 bits say". Used by PIZ and DWA.
/// </summary>
internal static class ExrHuffman
{
    private const int EncSize = (1 << 16) + 1;
    private const int ShortZeroCodeRun = 59;
    private const int LongZeroCodeRun = 63;
    private const int ShortestLongRun = 2 + LongZeroCodeRun - ShortZeroCodeRun;
    private const int TableBits = 14;
    private const int ShortFlag = 1 << 30;

    /// <summary>Decodes a compressed block into exactly <paramref name="output"/>.Length words; false when damaged.</summary>
    public static bool Decode(ReadOnlySpan<byte> compressed, Span<ushort> output)
    {
        if (compressed.Length < 20)
        {
            return output.Length == 0;
        }

        uint im = BinaryPrimitives.ReadUInt32LittleEndian(compressed);
        uint iM = BinaryPrimitives.ReadUInt32LittleEndian(compressed[4..]);
        uint nBits = BinaryPrimitives.ReadUInt32LittleEndian(compressed[12..]);
        if (im >= EncSize || iM >= EncSize || im > iM)
        {
            return false;
        }

        ulong[] hcode = ArrayPool<ulong>.Shared.Rent(EncSize);
        int[] symbols = ArrayPool<int>.Shared.Rent((int)(iM - im + 1));
        try
        {
            int pos = 20;
            if (!UnpackTable(compressed, ref pos, (int)im, (int)iM, hcode, symbols, out int count))
            {
                return false;
            }

            long dataBytes = ((long)nBits + 7) / 8;
            if (dataBytes > compressed.Length - pos)
            {
                return false;
            }

            return DecodeBits(hcode, symbols.AsSpan(0, count), (int)iM, compressed.Slice(pos, (int)dataBytes), nBits, output);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(hcode);
            ArrayPool<int>.Shared.Return(symbols);
        }
    }

    /// <summary>
    /// Encodes <paramref name="data"/> as OpenEXR's Huffman block: header, packed code lengths, then the codes, a run of one
    /// value sent as the value, the run symbol and an 8-bit count when that is shorter. Returns the bytes written, or -1 when
    /// <paramref name="output"/> is too small.
    /// </summary>
    public static int Encode(ReadOnlySpan<ushort> data, Span<byte> output)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        if (output.Length < 20)
        {
            return -1;
        }

        long[] frequency = new long[EncSize];
        foreach (ushort v in data)
        {
            frequency[v]++;
        }

        int im = 0;
        while (frequency[im] == 0)
        {
            im++;
        }

        int iM = 0;
        for (int i = im; i < EncSize; i++)
        {
            if (frequency[i] != 0)
            {
                iM = i;
            }
        }

        // The run-length pseudo-symbol, one past the largest value.
        iM++;
        frequency[iM] = 1;
        ulong[] hcode = new ulong[EncSize];
        if (!CodeLengths(frequency, im, iM, hcode))
        {
            return -1;
        }

        Canonical(hcode);
        BitWriter writer = new(output, 20);
        for (int i = im; i <= iM; i++)
        {
            int l = (int)(hcode[i] & 63);
            if (l == 0)
            {
                int run = 1;
                while (i < iM && run < 255 + ShortestLongRun && (hcode[i + 1] & 63) == 0)
                {
                    i++;
                    run++;
                }

                if (run >= 2)
                {
                    bool ok = run >= ShortestLongRun
                        ? writer.Put(6, LongZeroCodeRun) && writer.Put(8, (ulong)(run - ShortestLongRun))
                        : writer.Put(6, (ulong)(ShortZeroCodeRun + run - 2));
                    if (!ok)
                    {
                        return -1;
                    }

                    continue;
                }
            }

            if (!writer.Put(6, (ulong)l))
            {
                return -1;
            }
        }

        if (!writer.Flush())
        {
            return -1;
        }

        int tableEnd = writer.Position;
        BitWriter codes = new(output, tableEnd);
        ulong runCode = hcode[iM];
        bool Send(ref BitWriter w, ulong code, int runCount)
        {
            int length = (int)(code & 63);
            if (length + (int)(runCode & 63) + 8 < length * runCount)
            {
                return w.Put(length, code >> 6) && w.Put((int)(runCode & 63), runCode >> 6) && w.Put(8, (ulong)runCount);
            }

            for (; runCount >= 0; runCount--)
            {
                if (!w.Put(length, code >> 6))
                {
                    return false;
                }
            }

            return true;
        }

        int s = data[0], cs = 0;
        for (int i = 1; i < data.Length; i++)
        {
            int ns = data[i];
            if (cs == 255 || s != ns)
            {
                if (!Send(ref codes, hcode[s], cs))
                {
                    return -1;
                }

                cs = 0;
                s = ns;
            }
            else
            {
                cs++;
            }
        }

        if (!Send(ref codes, hcode[s], cs))
        {
            return -1;
        }

        long nBits = ((long)(codes.Position - tableEnd) * 8) + codes.Pending;
        if (!codes.Flush() || nBits > uint.MaxValue)
        {
            return -1;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)im);
        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], (uint)iM);
        BinaryPrimitives.WriteUInt32LittleEndian(output[8..], (uint)(tableEnd - 20));
        BinaryPrimitives.WriteUInt32LittleEndian(output[12..], (uint)nBits);
        BinaryPrimitives.WriteUInt32LittleEndian(output[16..], 0);
        return codes.Position;
    }

    /// <summary>Huffman code lengths for the symbols im to iM with nonzero frequency, merging the two rarest each time.</summary>
    private static bool CodeLengths(long[] frequency, int im, int iM, ulong[] lengths)
    {
        // Each node is the list of the symbols beneath it, linked through next, with its last symbol in tail.
        PriorityQueue<int, (long Frequency, int Symbol)> heap = new();
        int[] next = new int[EncSize];
        int[] tail = new int[EncSize];
        for (int i = im; i <= iM; i++)
        {
            if (frequency[i] != 0)
            {
                heap.Enqueue(i, (frequency[i], i));
                next[i] = -1;
                tail[i] = i;
            }
        }

        while (heap.Count > 1)
        {
            heap.TryDequeue(out int a, out (long Frequency, int Symbol) fa);
            heap.TryDequeue(out int b, out (long Frequency, int Symbol) fb);
            for (int j = a; j >= 0; j = next[j])
            {
                lengths[j]++;
            }

            for (int j = b; j >= 0; j = next[j])
            {
                lengths[j]++;
            }

            next[tail[b]] = a;
            tail[b] = tail[a];
            heap.Enqueue(b, (fa.Frequency + fb.Frequency, Math.Min(fa.Symbol, fb.Symbol)));
        }

        for (int i = im; i <= iM; i++)
        {
            if (lengths[i] > 58)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Assigns canonical codes to code lengths, as OpenEXR's hufCanonicalCodeTable does.</summary>
    private static void Canonical(ulong[] hcode)
    {
        Span<ulong> n = stackalloc ulong[59];
        n.Clear();
        for (int i = 0; i < EncSize; i++)
        {
            n[(int)hcode[i]]++;
        }

        ulong code = 0;
        for (int i = 58; i > 0; i--)
        {
            ulong nc = (code + n[i]) >> 1;
            n[i] = code;
            code = nc;
        }

        for (int i = 0; i < EncSize; i++)
        {
            ulong l = hcode[i];
            if (l > 0)
            {
                hcode[i] = l | (n[(int)l]++ << 6);
            }
        }
    }

    /// <summary>Writes bits most significant first.</summary>
    private ref struct BitWriter(Span<byte> output, int position)
    {
        private readonly Span<byte> _output = output;
        private ulong _bits;
        private int _count;

        public int Position { get; private set; } = position;

        public readonly int Pending => _count;

        public bool Put(int bits, ulong value)
        {
            _bits = (_bits << bits) | value;
            _count += bits;
            while (_count >= 8)
            {
                if (Position >= _output.Length)
                {
                    return false;
                }

                _count -= 8;
                _output[Position++] = (byte)(_bits >> _count);
            }

            return true;
        }

        public bool Flush()
        {
            if (_count > 0)
            {
                if (Position >= _output.Length)
                {
                    return false;
                }

                _output[Position++] = (byte)(_bits << (8 - _count));
                _count = 0;
            }

            return true;
        }
    }

    /// <summary>
    /// Reads the 6-bit code lengths of symbols im to iM, then assigns the canonical codes. Only the symbols that have a code are
    /// set in <paramref name="hcode"/>; they are listed, in order, in <paramref name="symbols"/>.
    /// </summary>
    private static bool UnpackTable(ReadOnlySpan<byte> data, ref int pos, int im, int iM, ulong[] hcode, int[] symbols, out int count)
    {
        count = 0;
        uint c = 0;
        int lc = 0;
        int length = data.Length;
        for (; im <= iM; im++)
        {
            // Six bits, most significant first; one more byte always covers them.
            if (lc < 6)
            {
                if (pos >= length)
                {
                    return false;
                }

                c = (c << 8) | data[pos++];
                lc += 8;
            }

            lc -= 6;
            ulong l = (c >> lc) & 63;
            if (l is > 0 and < ShortZeroCodeRun)
            {
                hcode[im] = l;
                symbols[count++] = im;
            }
            else if (l == LongZeroCodeRun)
            {
                if (lc < 8)
                {
                    if (pos >= length)
                    {
                        return false;
                    }

                    c = (c << 8) | data[pos++];
                    lc += 8;
                }

                lc -= 8;
                int run = (int)((c >> lc) & 0xFF) + ShortestLongRun;
                if (im + run > iM + 1)
                {
                    return false;
                }

                im += run - 1;
            }
            else if (l >= ShortZeroCodeRun)
            {
                int run = (int)l - ShortZeroCodeRun + 2;
                if (im + run > iM + 1)
                {
                    return false;
                }

                im += run - 1;
            }
        }

        // Canonical codes: count each length, give each length its first code, then number the symbols in order.
        Span<ulong> n = stackalloc ulong[59];
        n.Clear();
        ReadOnlySpan<int> coded = symbols.AsSpan(0, count);
        foreach (int i in coded)
        {
            n[(int)hcode[i]]++;
        }

        ulong code = 0;
        for (int i = 58; i > 0; i--)
        {
            ulong next = (code + n[i]) >> 1;
            n[i] = code;
            code = next;
        }

        foreach (int i in coded)
        {
            ulong l = hcode[i];
            hcode[i] = l | (n[(int)l]++ << 6);
        }

        return true;
    }

    /// <summary>
    /// Decodes <paramref name="nBits"/> bits with a two-level table: the first 12 bits give a short code at once, or, for a
    /// prefix of longer codes, a second table over the bits after it. Codes too long for a 12-bit second table are found
    /// by length, each length's codes being consecutive in the canonical code.
    /// </summary>
    private static bool DecodeBits(ulong[] hcode, ReadOnlySpan<int> symbols, int iM, ReadOnlySpan<byte> input, uint nBits, Span<ushort> output)
    {
        // Entries: symbol << 8 | length; a prefix of long codes is Long | subBits << 24 | offset into the second level;
        // a prefix whose codes are too long for that is Search. 0 is no code.
        int[] table = ArrayPool<int>.Shared.Rent(1 << PrimaryBits);
        int[] sorted = ArrayPool<int>.Shared.Rent(Math.Max(1, symbols.Length));
        int[]? second = null;
        try
        {
            Array.Clear(table, 0, 1 << PrimaryBits);
            Span<int> count = stackalloc int[59];
            Span<ulong> firstCode = stackalloc ulong[59];
            Span<int> start = stackalloc int[59];
            Span<int> fill = stackalloc int[59];
            count.Clear();
            firstCode.Fill(ulong.MaxValue);
            int maxLength = 0;
            Span<byte> longest = stackalloc byte[1 << PrimaryBits];
            longest.Clear();
            foreach (int i in symbols)
            {
                int l = (int)(hcode[i] & 63);
                ulong code = hcode[i] >> 6;
                if ((code >> l) != 0)
                {
                    return false;
                }

                count[l]++;
                firstCode[l] = Math.Min(firstCode[l], code);
                maxLength = Math.Max(maxLength, l);
                if (l <= PrimaryBits)
                {
                    int at = (int)(code << (PrimaryBits - l));
                    int span = 1 << (PrimaryBits - l);
                    for (int k = 0; k < span; k++)
                    {
                        if (table[at + k] != 0)
                        {
                            return false;
                        }

                        table[at + k] = (i << 8) | l | (i == iM ? RunFlag : 0);
                    }
                }
                else
                {
                    int prefix = (int)(code >> (l - PrimaryBits));
                    if (table[prefix] != 0)
                    {
                        return false;
                    }

                    longest[prefix] = (byte)Math.Max(longest[prefix], l);
                }
            }

            // The second level: one table per prefix of long codes, as wide as its longest code needs.
            int secondSize = 0;
            for (int p = 0; p < 1 << PrimaryBits; p++)
            {
                if (longest[p] != 0)
                {
                    int sub = longest[p] - PrimaryBits;
                    if (sub <= SecondBits)
                    {
                        table[p] = Long | (sub << 24) | secondSize;
                        secondSize += 1 << sub;
                    }
                    else
                    {
                        table[p] = Search;
                    }
                }
            }

            if (secondSize > 0)
            {
                second = ArrayPool<int>.Shared.Rent(secondSize);
                Array.Clear(second, 0, secondSize);
            }

            int next = 0;
            for (int l = PrimaryBits + 1; l <= maxLength; l++)
            {
                start[l] = next;
                next += count[l];
            }

            start.CopyTo(fill);
            foreach (int i in symbols)
            {
                int l = (int)(hcode[i] & 63);
                if (l <= PrimaryBits)
                {
                    continue;
                }

                sorted[fill[l]++] = i;
                ulong code = hcode[i] >> 6;
                int entry = table[(int)(code >> (l - PrimaryBits))];
                if (entry == Search)
                {
                    continue;
                }

                int sub = (entry >> 24) & 63;
                int offset = entry & 0xFFFFFF;
                int extra = l - PrimaryBits;
                int suffix = (int)(code & ((1UL << extra) - 1));
                int at = offset + (suffix << (sub - extra));
                for (int k = 0, span = 1 << (sub - extra); k < span; k++)
                {
                    second![at + k] = (i << 8) | l | (i == iM ? RunFlag : 0);
                }
            }

            // The input copied with zero bytes after it, so the bit buffer can always refill with one 8-byte read.
            byte[] padded = ArrayPool<byte>.Shared.Rent(input.Length + 24);
            try
            {
                input.CopyTo(padded);
                padded.AsSpan(input.Length, 24).Clear();
                Tables tables = new(table, second ?? [], sorted, count, firstCode, start, maxLength);
                return Run(padded, input.Length, nBits, tables, iM, output);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(padded);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(table);
            ArrayPool<int>.Shared.Return(sorted);
            if (second is not null)
            {
                ArrayPool<int>.Shared.Return(second);
            }
        }
    }

    private const int PrimaryBits = 12;
    private const int SecondBits = 12;
    private const int Long = 1 << 30;
    private const int Search = int.MinValue;

    /// <summary>Marks the entries of the run symbol, so the fast loop leaves them to the general one.</summary>
    private const int RunFlag = 1 << 29;

    /// <summary>
    /// The decoding loop: a 64-bit buffer, left-aligned, refilled from the zero-padded input whenever it holds fewer than 32 bits,
    /// which covers any code the two tables hold. Codes of the first or second table other than the run symbol take the short way.
    /// </summary>
    private static bool Run(byte[] input, int length, uint nBits, scoped in Tables tables, int iM, Span<ushort> output)
    {
        ref byte inRef = ref MemoryMarshal.GetArrayDataReference(input);
        ref int primary = ref MemoryMarshal.GetArrayDataReference(tables.Primary);
        ref int second = ref MemoryMarshal.GetArrayDataReference(tables.Second);
        ref ushort outRef = ref MemoryMarshal.GetReference(output);
        int outLength = output.Length;
        int outPos = 0;
        ulong buffer = 0;
        int buffered = 0;
        int inPos = 0;
        while (outPos < outLength)
        {
            // The fast loop: a refill makes at least 56 bits, enough for four codes of the first table, which take the short way
            // until a code needs more (a long code or the run symbol) or the block nears its end.
            while (outPos < outLength - 4 && inPos <= length)
            {
                buffer |= BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, inPos))) >> buffered;
                inPos += (63 - buffered) >> 3;
                buffered |= 56;
                int e0 = Unsafe.Add(ref primary, (nint)(buffer >> (64 - PrimaryBits)));
                if ((uint)(e0 - 1) >= RunFlag - 1)
                {
                    goto General;
                }

                Unsafe.Add(ref outRef, outPos) = (ushort)(e0 >> 8);
                buffer <<= e0 & 0xFF;
                buffered -= e0 & 0xFF;
                int e1 = Unsafe.Add(ref primary, (nint)(buffer >> (64 - PrimaryBits)));
                if ((uint)(e1 - 1) >= RunFlag - 1)
                {
                    outPos++;
                    goto General;
                }

                Unsafe.Add(ref outRef, outPos + 1) = (ushort)(e1 >> 8);
                buffer <<= e1 & 0xFF;
                buffered -= e1 & 0xFF;
                int e2 = Unsafe.Add(ref primary, (nint)(buffer >> (64 - PrimaryBits)));
                if ((uint)(e2 - 1) >= RunFlag - 1)
                {
                    outPos += 2;
                    goto General;
                }

                Unsafe.Add(ref outRef, outPos + 2) = (ushort)(e2 >> 8);
                buffer <<= e2 & 0xFF;
                buffered -= e2 & 0xFF;
                int e3 = Unsafe.Add(ref primary, (nint)(buffer >> (64 - PrimaryBits)));
                if ((uint)(e3 - 1) >= RunFlag - 1)
                {
                    outPos += 3;
                    goto General;
                }

                Unsafe.Add(ref outRef, outPos + 3) = (ushort)(e3 >> 8);
                buffer <<= e3 & 0xFF;
                buffered -= e3 & 0xFF;
                outPos += 4;
            }

        General:
            if (buffered < 32)
            {
                if (inPos > length + 8)
                {
                    // Past the data and its zero padding: the codes run on beyond the block.
                    return false;
                }

                buffer |= BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, inPos))) >> buffered;
                inPos += (63 - buffered) >> 3;
                buffered |= 56;
            }

            int entry = Unsafe.Add(ref primary, (nint)(buffer >> (64 - PrimaryBits)));
            if ((uint)(entry - 1) >= RunFlag - 1)
            {
                if (entry > 0 && (entry & Long) != 0)
                {
                    entry = Unsafe.Add(ref second, (nint)((entry & 0xFFFFFF) + (int)((buffer << PrimaryBits) >> (64 - ((entry >> 24) & 63)))));
                }
                else if (entry == Search)
                {
                    // A code longer than the two tables: make sure all of it is in the buffer.
                    if (inPos > length + 8)
                    {
                        return false;
                    }

                    buffer |= BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, inPos))) >> buffered;
                    inPos += (63 - buffered) >> 3;
                    buffered |= 56;
                    entry = FindLong(buffer, tables);
                    entry |= entry != 0 && (entry >> 8) == iM ? RunFlag : 0;
                }

                if (entry == 0)
                {
                    return false;
                }
            }

            int codeLength = entry & 0xFF;
            buffer <<= codeLength;
            buffered -= codeLength;
            if ((entry & RunFlag) == 0)
            {
                Unsafe.Add(ref outRef, outPos++) = (ushort)(entry >> 8);
                continue;
            }

            // A run: the next 8 bits repeat the last word.
            if (buffered < 8)
            {
                buffer |= BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref inRef, inPos))) >> buffered;
                inPos += (63 - buffered) >> 3;
                buffered |= 56;
            }

            int run = (int)(buffer >> 56);
            buffer <<= 8;
            buffered -= 8;
            if (outPos == 0 || run > outLength - outPos)
            {
                return false;
            }

            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref outRef, outPos), run).Fill(Unsafe.Add(ref outRef, outPos - 1));
            outPos += run;
        }

        // Every code must lie within the bits the block says it holds.
        long consumed = ((long)inPos * 8) - buffered;
        return consumed <= nBits;
    }

    /// <summary>Finds a code longer than the two tables cover, by length; returns its entry, or 0.</summary>
    private static int FindLong(ulong buffer, scoped in Tables tables)
    {
        for (int l = PrimaryBits + 1; l <= tables.MaxLength; l++)
        {
            ulong v = buffer >> (64 - l);
            int n = tables.Count[l];
            if (n > 0 && v >= tables.FirstCode[l] && v - tables.FirstCode[l] < (ulong)n)
            {
                return (tables.Sorted[tables.Start[l] + (int)(v - tables.FirstCode[l])] << 8) | l;
            }
        }

        return 0;
    }

    /// <summary>The decoding tables of one block.</summary>
    private readonly ref struct Tables(int[] primary, int[] second, int[] sorted, ReadOnlySpan<int> count, ReadOnlySpan<ulong> firstCode, ReadOnlySpan<int> start, int maxLength)
    {
        public int[] Primary { get; } = primary;

        public int[] Second { get; } = second;

        public int[] Sorted { get; } = sorted;

        public ReadOnlySpan<int> Count { get; } = count;

        public ReadOnlySpan<ulong> FirstCode { get; } = firstCode;

        public ReadOnlySpan<int> Start { get; } = start;

        public int MaxLength { get; } = maxLength;
    }
}
