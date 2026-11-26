using Egami.Fonts.Parsing;

namespace Egami.Fonts.Woff;

/// <summary>
/// Decompresses LZCOMP (W3C MicroType Express submission, section 3 and Appendix C): an LZ77 variant whose literals, copy
/// commands, lengths and distances are adaptive Huffman symbols, over a window preloaded with 7168 bytes, with an optional
/// run-length layer on the output.
/// </summary>
internal static class Lzcomp
{
    /// <summary>The bytes the window starts with: 32 × 96 pairs (k, j), then every byte value four times.</summary>
    internal const int PreloadSize = (2 * 32 * 96) + (4 * 256);

    /// <summary>From this distance on, a copy is at least 3 bytes long, so its coded length is one short.</summary>
    internal const int MaxTwoByteDistance = 512;

    /// <summary>Returns the preloaded window head that both ends of LZCOMP share.</summary>
    internal static void Preload(Span<byte> window)
    {
        int i = 0;
        for (int k = 0; k < 32; k++)
        {
            for (int j = 0; j < 96; j++)
            {
                window[i++] = (byte)k;
                window[i++] = (byte)j;
            }
        }

        for (int j = 0; j < 256; j++)
        {
            window[i++] = (byte)j;
            window[i++] = (byte)j;
            window[i++] = (byte)j;
            window[i++] = (byte)j;
        }
    }

    /// <summary>Returns how many 3-bit distance symbols a stream of <paramref name="length"/> bytes needs at most.</summary>
    internal static int DistanceRanges(long length)
    {
        int ranges = 1;
        while ((1L << (3 * ranges)) < length)
        {
            ranges++;
        }

        return ranges;
    }

    /// <summary>Decompresses one LZCOMP block.</summary>
    /// <param name="data">The block.</param>
    /// <param name="readRunLengthFlag">Whether the block starts with the run-length bit (every MicroType Express version but 1).</param>
    /// <param name="limit">The most bytes the output may take.</param>
    /// <param name="offset">The block's offset in the file, for errors.</param>
    public static byte[] Decompress(ReadOnlySpan<byte> data, bool readRunLengthFlag, long limit, long offset)
    {
        var bits = new BitReader(data, offset);
        bool runLength = readRunLengthFlag && bits.Bit();
        var distances = new AdaptiveHuffman(8);
        var lengths = new AdaptiveHuffman(8);
        int outLength = bits.Value(24);
        WebFontDecoder.CheckLimit(outLength, limit);
        int ranges = DistanceRanges(outLength);
        int dup2 = 256 + (8 * ranges);
        var symbols = new AdaptiveHuffman(dup2 + 3);

        byte[] window = new byte[PreloadSize + outLength];
        Preload(window);
        int pos = PreloadSize, end = window.Length;
        while (pos < end)
        {
            int symbol = symbols.Read(ref bits);
            if (symbol < 256)
            {
                window[pos++] = (byte)symbol;
                continue;
            }

            if (symbol >= dup2)
            {
                // DUP2, DUP4 and DUP6: one byte from 2, 4 or 6 back.
                window[pos] = window[pos - (2 * (symbol - dup2 + 1))];
                pos++;
                continue;
            }

            // A copy: the symbol gives the number of distance symbols and the first length digits, a stop bit and two value bits.
            int code = symbol - 256;
            int distanceSymbols = (code / 8) + 1;
            int digits = code % 8;
            long length = 0;
            while (true)
            {
                length = (length << 2) | (uint)(digits & 3);
                if ((digits & 4) == 0)
                {
                    break;
                }

                if (length > end)
                {
                    throw FontErrors.Corrupt(offset + bits.BytePosition, "An LZCOMP copy is longer than the block.");
                }

                digits = lengths.Read(ref bits);
            }

            length += 2;
            long distance = 0;
            for (int i = 0; i < distanceSymbols; i++)
            {
                distance = (distance << 3) | (uint)distances.Read(ref bits);
            }

            distance++;
            if (distance >= MaxTwoByteDistance)
            {
                length++;
            }

            long start = pos - distance - length + 1;
            if (start < 0 || length > end - pos)
            {
                throw FontErrors.Corrupt(offset + bits.BytePosition, "An LZCOMP copy reaches outside the window.");
            }

            for (int i = 0; i < length; i++)
            {
                window[pos++] = window[start + i];
            }
        }

        return runLength ? RunLengthDecode(window.AsSpan(PreloadSize), limit, offset) : window.AsSpan(PreloadSize).ToArray();
    }

    /// <summary>Undoes the run-length layer: the first byte is the escape; escape 0 is the escape itself, escape n b is n × b.</summary>
    internal static byte[] RunLengthDecode(ReadOnlySpan<byte> data, long limit, long offset)
    {
        if (data.IsEmpty)
        {
            return [];
        }

        byte escape = data[0];
        long size = 0;
        for (int i = 1; i < data.Length; i++)
        {
            if (data[i] != escape)
            {
                size++;
            }
            else if (i + 1 < data.Length && data[i + 1] == 0)
            {
                size++;
                i++;
            }
            else
            {
                size += i + 1 < data.Length ? data[i + 1] : 0;
                i += 2;
            }
        }

        WebFontDecoder.CheckLimit(size, limit);
        byte[] result = new byte[size];
        int at = 0;
        for (int i = 1; i < data.Length; i++)
        {
            if (data[i] != escape)
            {
                result[at++] = data[i];
            }
            else if (i + 1 >= data.Length)
            {
                throw FontErrors.Corrupt(offset, "The LZCOMP run-length data ends after an escape.");
            }
            else if (data[i + 1] == 0)
            {
                result[at++] = escape;
                i++;
            }
            else
            {
                if (i + 2 >= data.Length)
                {
                    throw FontErrors.Corrupt(offset, "The LZCOMP run-length data ends inside a run.");
                }

                result.AsSpan(at, data[i + 1]).Fill(data[i + 2]);
                at += data[i + 1];
                i += 2;
            }
        }

        return result;
    }

    /// <summary>Reads bits most significant first from each byte.</summary>
    internal ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly long _offset;
        private int _byte;
        private int _bit;

        public BitReader(ReadOnlySpan<byte> data, long offset)
        {
            _data = data;
            _offset = offset;
            _bit = 8;
            _byte = -1;
        }

        public readonly int BytePosition => Math.Max(_byte, 0);

        public bool Bit()
        {
            if (_bit == 8)
            {
                if (++_byte >= _data.Length)
                {
                    throw FontErrors.Corrupt(_offset + _data.Length, "The LZCOMP data ends early.");
                }

                _bit = 0;
            }

            return ((_data[_byte] >> (7 - _bit++)) & 1) != 0;
        }

        public int Value(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                value = (value << 1) | (Bit() ? 1 : 0);
            }

            return value;
        }
    }

    /// <summary>
    /// An adaptive Huffman code over symbols 0 to range - 1: a complete tree kept in sibling order (weights never increase along
    /// the node numbers), so each update swaps a node with the first of its equal-weight run before raising its weight.
    /// </summary>
    internal sealed class AdaptiveHuffman
    {
        private const int Root = 1;

        private readonly int[] _up;
        private readonly int[] _left;
        private readonly int[] _right;
        private readonly int[] _code;
        private readonly int[] _weight;
        private readonly int[] _symbolIndex;

        public AdaptiveHuffman(int range)
        {
            int nodes = 2 * range;
            _up = new int[nodes];
            _left = new int[nodes];
            _right = new int[nodes];
            _code = new int[nodes];
            _weight = new int[nodes];
            _symbolIndex = new int[range];

            // Node 0 is unused; its weight stops the search for an equal-weight run, which the root's larger weight also does.
            _weight[0] = int.MaxValue;
            for (int i = 2; i < nodes; i++)
            {
                _up[i] = i / 2;
                _weight[i] = 1;
            }

            for (int i = 1; i < range; i++)
            {
                _left[i] = 2 * i;
                _right[i] = (2 * i) + 1;
            }

            for (int i = 0; i < range; i++)
            {
                _code[i] = -1;
                _code[range + i] = i;
                _left[range + i] = -1;
                _right[range + i] = -1;
                _symbolIndex[i] = range + i;
            }

            for (int i = range - 1; i >= Root; i--)
            {
                _weight[i] = _weight[_left[i]] + _weight[_right[i]];
            }

            if (range > 256 && range < 512)
            {
                // The LZCOMP symbol alphabet: the first two copy commands and DUP2 and DUP4 start out more likely.
                Update(_symbolIndex[256]);
                Update(_symbolIndex[257]);
                for (int i = 0; i < 12; i++)
                {
                    Update(_symbolIndex[range - 3]);
                }

                for (int i = 0; i < 6; i++)
                {
                    Update(_symbolIndex[range - 2]);
                }
            }
            else
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int i = 0; i < range; i++)
                    {
                        Update(_symbolIndex[i]);
                    }
                }
            }
        }

        public int Read(ref BitReader bits)
        {
            int a = Root;
            do
            {
                a = bits.Bit() ? _right[a] : _left[a];
            }
            while (_code[a] < 0);

            // The update may swap the leaf's node away, so the symbol is taken first.
            int symbol = _code[a];
            Update(a);
            return symbol;
        }

        /// <summary>Writes the code of <paramref name="symbol"/> as bits, root first, and updates the tree.</summary>
        public void Write(int symbol, List<bool> bits)
        {
            int a = _symbolIndex[symbol];
            int start = bits.Count;
            for (int node = a; node != Root; node = _up[node])
            {
                bits.Add(_right[_up[node]] == node);
            }

            bits.Reverse(start, bits.Count - start);
            Update(a);
        }

        private void Update(int a)
        {
            for (; a != Root; a = _up[a])
            {
                int weight = _weight[a];
                int b = a - 1;
                if (_weight[b] == weight)
                {
                    do
                    {
                        b--;
                    }
                    while (_weight[b] == weight);

                    b++;
                    if (b > Root)
                    {
                        Swap(a, b);
                        a = b;
                    }
                }

                _weight[a] = weight + 1;
            }

            _weight[Root]++;
        }

        private void Swap(int a, int b)
        {
            int upA = _up[a], upB = _up[b];
            (_left[a], _left[b]) = (_left[b], _left[a]);
            (_right[a], _right[b]) = (_right[b], _right[a]);
            (_code[a], _code[b]) = (_code[b], _code[a]);
            (_weight[a], _weight[b]) = (_weight[b], _weight[a]);
            _up[a] = upA;
            _up[b] = upB;
            Relink(a);
            Relink(b);
        }

        private void Relink(int node)
        {
            int code = _code[node];
            if (code < 0)
            {
                _up[_left[node]] = node;
                _up[_right[node]] = node;
            }
            else
            {
                _symbolIndex[code] = node;
            }
        }
    }
}
