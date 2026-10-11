using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Tiff;

/// <summary>
/// TIFF's LZW (TIFF 6.0 section 13), and TIFF 5.0's older form: a strip starting with Clear written least significant bit first
/// (00 01) is the old kind, whose codes are bit-reversed and whose width grows at the usual place, not one code early.
/// </summary>
/// <remarks>
/// Every string in the table is the previous string plus the first byte of the next one, and both were just written out side by
/// side, so a table entry is only where its string already stands in the output and how long it is: decoding a code is one copy,
/// with no chain to walk. That needs the whole strip in one buffer, which the reader has anyway.
/// </remarks>
internal static class TiffLzw
{
    private const int Clear = 256;
    private const int End = 257;
    private const int First = 258;

    /// <summary>Decodes a strip or tile into <paramref name="output"/>; returns the bytes written (fewer when the data ends or is damaged).</summary>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length >= 2 && input[0] == 0 && (input[1] & 1) == 1)
        {
            return DecodeOld(input, output);
        }

        Span<int> start = stackalloc int[4096];
        Span<short> length = stackalloc short[4096];
        int next = First;
        int width = 9;
        int previous = -1;
        int written = 0;
        int at = 0;
        ulong buffer = 0;
        int bits = 0;
        while (written < output.Length)
        {
            if (bits < width)
            {
                if (at + 8 <= input.Length)
                {
                    // Top up the window to at least 56 bits with one unaligned read.
                    ulong word = BinaryPrimitives.ReadUInt64BigEndian(input[at..]);
                    int take = (63 - bits) >> 3;
                    buffer |= (word >> (64 - (take * 8))) << (64 - bits - (take * 8));
                    bits += take * 8;
                    at += take;
                }
                else
                {
                    while (bits <= 56 && at < input.Length)
                    {
                        buffer |= (ulong)input[at++] << (56 - bits);
                        bits += 8;
                    }

                    if (bits < width)
                    {
                        break;
                    }
                }
            }

            int code = (int)(buffer >> (64 - width));
            buffer <<= width;
            bits -= width;
            if (code == End)
            {
                break;
            }

            if (code == Clear)
            {
                next = First;
                width = 9;
                previous = -1;
                continue;
            }

            int count;
            if (code < 256)
            {
                output[written] = (byte)code;
                count = 1;
            }
            else if (code < next && previous >= 0)
            {
                count = Math.Min(length[code], output.Length - written);
                Copy(output, start[code], written, count);
            }
            else if (code == next && previous >= 0)
            {
                // KwKwK: the previous string, which was just written, then its own first byte.
                int prevLength = previous < 256 ? 1 : length[previous];
                int from = written - prevLength;
                count = Math.Min(prevLength + 1, output.Length - written);
                Copy(output, from, written, Math.Min(prevLength, count));
                if (count > prevLength)
                {
                    output[written + prevLength] = output[from];
                }
            }
            else
            {
                // A code not yet defined: damage; stop here.
                break;
            }

            if (previous >= 0 && next < 4096)
            {
                int prevLength = previous < 256 ? 1 : length[previous];
                start[next] = written - prevLength;
                length[next] = (short)(prevLength + 1);
                next++;
                if (next == (1 << width) - 1 && width < 12)
                {
                    width++;
                }
            }

            previous = code;
            written += count;
        }

        return written;
    }

    /// <summary>Copies <paramref name="count"/> earlier output bytes forward; the ranges overlap only in the KwKwK case.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy(Span<byte> output, int from, int to, int count)
    {
        if (from + count <= to)
        {
            output.Slice(from, count).CopyTo(output[to..]);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            output[to + i] = output[from + i];
        }
    }

    /// <summary>The old, bit-reversed form: codes least significant bit first, the width growing one code later.</summary>
    private static int DecodeOld(ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<int> start = stackalloc int[4096];
        Span<short> length = stackalloc short[4096];
        int next = First;
        int width = 9;
        int previous = -1;
        int written = 0;
        int at = 0;
        ulong buffer = 0;
        int bits = 0;
        while (written < output.Length)
        {
            while (bits < width && at < input.Length)
            {
                buffer |= (ulong)input[at++] << bits;
                bits += 8;
            }

            if (bits < width)
            {
                break;
            }

            int code = (int)(buffer & ((1UL << width) - 1));
            buffer >>= width;
            bits -= width;
            if (code == End)
            {
                break;
            }

            if (code == Clear)
            {
                next = First;
                width = 9;
                previous = -1;
                continue;
            }

            int count;
            if (code < 256)
            {
                output[written] = (byte)code;
                count = 1;
            }
            else if (code < next && previous >= 0)
            {
                count = Math.Min(length[code], output.Length - written);
                Copy(output, start[code], written, count);
            }
            else if (code == next && previous >= 0)
            {
                int prevLength = previous < 256 ? 1 : length[previous];
                int from = written - prevLength;
                count = Math.Min(prevLength + 1, output.Length - written);
                Copy(output, from, written, Math.Min(prevLength, count));
                if (count > prevLength)
                {
                    output[written + prevLength] = output[from];
                }
            }
            else
            {
                break;
            }

            if (previous >= 0 && next < 4096)
            {
                int prevLength = previous < 256 ? 1 : length[previous];
                start[next] = written - prevLength;
                length[next] = (short)(prevLength + 1);
                next++;
                if (next == 1 << width && width < 12)
                {
                    width++;
                }
            }

            previous = code;
            written += count;
        }

        return written;
    }
}
