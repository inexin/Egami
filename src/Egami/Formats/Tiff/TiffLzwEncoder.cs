using System.Buffers;

namespace Egami.Formats.Tiff;

/// <summary>
/// TIFF's LZW (TIFF 6.0 section 13) for the writer: codes most significant bit first, starting at 9 bits, widening one code early
/// as TIFF does, a Clear first and whenever the table fills, and an EndOfInformation last. Strings are found through an
/// open-addressed table of (prefix code, byte) pairs.
/// </summary>
internal static class TiffLzwEncoder
{
    private const int Clear = 256;
    private const int End = 257;
    private const int First = 258;
    private const int Limit = 4094;
    private const int HashSize = 1 << 13;

    /// <summary>Returns the most bytes <see cref="Encode"/> writes for <paramref name="length"/> input bytes.</summary>
    public static int MaxEncodedLength(int length) => ((length + 2) * 12 / 8) + 16 + (length / 2000 * 2);

    /// <summary>Encodes <paramref name="data"/> into <paramref name="output"/>, which holds at least <see cref="MaxEncodedLength"/> bytes.</summary>
    public static int Encode(ReadOnlySpan<byte> data, Span<byte> output)
    {
        int[] keys = ArrayPool<int>.Shared.Rent(HashSize);
        short[] codes = ArrayPool<short>.Shared.Rent(HashSize);
        try
        {
            Span<int> key = keys.AsSpan(0, HashSize);
            Span<short> code = codes.AsSpan(0, HashSize);
            key.Fill(-1);
            int written = 0;
            ulong buffer = 0;
            int bits = 0;
            int width = 9;
            int next = First;

            void Put(int value, Span<byte> output)
            {
                buffer = (buffer << width) | (uint)value;
                bits += width;
                while (bits >= 8)
                {
                    bits -= 8;
                    output[written++] = (byte)(buffer >> bits);
                }
            }

            Put(Clear, output);
            if (data.IsEmpty)
            {
                Put(End, output);
                if (bits > 0)
                {
                    output[written++] = (byte)(buffer << (8 - bits));
                }

                return written;
            }

            int prefix = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                byte b = data[i];
                int pair = (prefix << 8) | b;
                int slot = ((pair * 40503) >> 3) & (HashSize - 1);
                while (true)
                {
                    int found = key[slot];
                    if (found == pair)
                    {
                        prefix = code[slot];
                        goto Next;
                    }

                    if (found < 0)
                    {
                        break;
                    }

                    slot = (slot + 1) & (HashSize - 1);
                }

                Put(prefix, output);
                key[slot] = pair;
                code[slot] = (short)next;
                next++;
                if (next == Limit)
                {
                    // The table is full: Clear and start again.
                    Put(Clear, output);
                    key.Fill(-1);
                    next = First;
                    width = 9;
                }
                else if (next > (1 << width) - 1 && width < 12)
                {
                    // Widen once the next entry would not fit; the decoder, a code behind, widens when its table reaches
                    // 511, 1023 and 2047, which is TIFF's early change.
                    width++;
                }

                prefix = b;
            Next:;
            }

            Put(prefix, output);
            next++;
            if (next == Limit)
            {
                Put(Clear, output);
                width = 9;
            }
            else if (next > (1 << width) - 1 && width < 12)
            {
                // The decoder adds an entry for the last code too, so EndOfInformation goes at the width it then reads.
                width++;
            }

            Put(End, output);
            if (bits > 0)
            {
                output[written++] = (byte)(buffer << (8 - bits));
            }

            return written;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(keys);
            ArrayPool<short>.Shared.Return(codes);
        }
    }
}
