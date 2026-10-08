using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Jpeg;

/// <summary>A Huffman table for encoding: the code and length of each symbol (T.81 annex C), built from counts and symbols.</summary>
internal sealed class JpegHuffmanCodes
{
    private readonly ushort[] _codes = new ushort[256];
    private readonly byte[] _lengths = new byte[256];

    internal JpegHuffmanCodes(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> symbols)
    {
        Counts = counts.ToArray();
        Symbols = symbols.ToArray();
        int code = 0;
        int k = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < counts[length - 1]; i++, k++, code++)
            {
                _codes[symbols[k]] = (ushort)code;
                _lengths[symbols[k]] = (byte)length;
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
    internal int Code(int symbol) => _codes[symbol];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Length(int symbol) => _lengths[symbol];

    /// <summary>
    /// Builds the optimal table for symbol frequencies, with no code longer than 16 bits and no code of all ones (T.81 annex K.2,
    /// figures K.1 to K.4). Symbols that never occur get no code.
    /// </summary>
    internal static JpegHuffmanCodes Optimal(ReadOnlySpan<long> frequencies)
    {
        // One extra symbol with frequency 1 takes the all-ones code, which is then dropped.
        long[] freq = new long[257];
        frequencies[..256].CopyTo(freq);
        freq[256] = 1;
        int[] codeSize = new int[257];
        int[] others = new int[257];
        Array.Fill(others, -1);
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
        int[] bits = new int[33];
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
        byte[] counts = new byte[16];
        for (int i = 1; i <= 16; i++)
        {
            counts[i - 1] = (byte)bits[i];
        }

        // Symbols in order of code length, then number (figure K.4).
        List<byte> symbols = [];
        for (int length = 1; length <= 32; length++)
        {
            for (int s = 0; s < 256; s++)
            {
                if (codeSize[s] == length)
                {
                    symbols.Add((byte)s);
                }
            }
        }

        return new JpegHuffmanCodes(counts, [.. symbols]);
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

    /// <summary>Adds the low <paramref name="length"/> bits of <paramref name="value"/> (0 to 26 bits).</summary>
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
    /// <summary>Writes one block in zigzag order, updating the DC predictor.</summary>
    internal static void Encode(JpegBitWriter writer, ReadOnlySpan<short> block, ref int predictor, JpegHuffmanCodes dc, JpegHuffmanCodes ac)
    {
        int diff = block[0] - predictor;
        predictor = block[0];
        int size = Size(diff);
        writer.Put(dc.Code(size), dc.Length(size));
        if (size > 0)
        {
            writer.Put(diff < 0 ? diff - 1 : diff, size);
        }

        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int value = block[k];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                writer.Put(ac.Code(0xF0), ac.Length(0xF0));
                run -= 16;
            }

            size = Size(value);
            int symbol = (run << 4) | size;
            writer.Put(ac.Code(symbol), ac.Length(symbol));
            writer.Put(value < 0 ? value - 1 : value, size);
            run = 0;
        }

        if (run > 0)
        {
            writer.Put(ac.Code(0x00), ac.Length(0x00));
        }
    }

    /// <summary>Counts the symbols one block would use, updating the DC predictor.</summary>
    internal static void Count(ReadOnlySpan<short> block, ref int predictor, Span<long> dc, Span<long> ac)
    {
        int diff = block[0] - predictor;
        predictor = block[0];
        dc[Size(diff)]++;
        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int value = block[k];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                ac[0xF0]++;
                run -= 16;
            }

            ac[(run << 4) | Size(value)]++;
            run = 0;
        }

        if (run > 0)
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
