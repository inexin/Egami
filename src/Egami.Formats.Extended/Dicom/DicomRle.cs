using System.Buffers.Binary;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// The DICOM RLE Lossless decoder (PS3.5 annex G): a 64-byte header of up to 15 segment offsets, then one byte-wise PackBits
/// segment per byte of each sample, most significant byte first, component after component. Each segment decodes to one byte
/// of every pixel.
/// </summary>
internal static class DicomRle
{
    /// <summary>Decodes a frame into <paramref name="samples"/>, interleaved, with the stored bits taken as native data's are.</summary>
    public static void Decode(ReadOnlySpan<byte> data, DicomImageInfo info, int[] samples, Action<string, string> damaged)
    {
        int bytes = info.BitsAllocated / 8;
        if (info.BitsAllocated is not (8 or 16 or 32))
        {
            throw new NotSupportedException($"RLE frames of {info.BitsAllocated} bits allocated are not decoded.");
        }

        int components = info.SamplesPerPixel;
        int expected = bytes * components;
        if (data.Length < 64)
        {
            damaged("RleHeader", "The RLE frame is shorter than its 64-byte header; it is left 0.");
            return;
        }

        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count != expected)
        {
            damaged("RleSegments", $"The RLE frame has {count} segments; {expected} were expected. The ones there were decoded.");
        }

        int pixels = info.Rows * info.Columns;
        uint[] raw = new uint[samples.Length];
        byte[] segment = new byte[pixels];
        int segments = Math.Min(Math.Min(count, 15), expected);
        for (int s = 0; s < segments; s++)
        {
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(data[(4 + (s * 4))..]);
            uint end = s + 1 < Math.Min(count, 15) ? BinaryPrimitives.ReadUInt32LittleEndian(data[(8 + (s * 4))..]) : (uint)data.Length;
            if (start < 64 || start > data.Length || end < start || end > data.Length)
            {
                damaged("RleSegments", $"RLE segment {s} lies at bytes {start} to {end} of a {data.Length}-byte frame; it is left 0.");
                continue;
            }

            Array.Clear(segment);
            int written = Unpack(data[(int)start..(int)end], segment);
            if (written < pixels)
            {
                damaged("RleShort", $"RLE segment {s} decodes to {written} bytes of {pixels}; the rest is left 0.");
            }

            int component = s / bytes;
            int shift = 8 * (bytes - 1 - (s % bytes));
            for (int p = 0, at = component; p < pixels; p++, at += components)
            {
                raw[at] |= (uint)segment[p] << shift;
            }
        }

        int high = info.HighBit + 1 - info.BitsStored;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = DicomPixelDecoder.Stored(raw[i], high, info.BitsStored, info.IsSigned);
        }
    }

    /// <summary>
    /// Encodes a frame of interleaved stored values: <paramref name="components"/> times <paramref name="bitsAllocated"/> / 8
    /// segments, each a byte plane coded with PackBits row by row (no run crosses a row, as G.3.1 asks) and padded to an even
    /// length, after the 64-byte header of their offsets. Values are put under the high bit, signed ones as their two's complement.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<int> samples, int components, int rows, int columns, int bitsAllocated, int bitsStored, int highBit)
    {
        if (bitsAllocated is not (8 or 16 or 32))
        {
            throw new NotSupportedException($"RLE frames of {bitsAllocated} bits allocated are not written; RLE codes 8, 16 and 32-bit samples.");
        }

        int bytes = bitsAllocated / 8;
        int segments = components * bytes;
        if (segments > 15)
        {
            throw new NotSupportedException($"An RLE frame holds at most 15 segments; {components} samples of {bytes} bytes need {segments}.");
        }

        int shift = highBit + 1 - bitsStored;
        uint mask = bitsStored >= 32 ? uint.MaxValue : (1u << bitsStored) - 1;
        long worst = 64 + (segments * (((long)rows * (columns + ((columns + 127) / 128))) + 1));
        if (worst > Array.MaxLength)
        {
            throw new NotSupportedException("The frame is too large for one RLE fragment.");
        }

        byte[] output = new byte[worst];
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)segments);
        byte[] plane = new byte[columns];
        int at = 64;
        int stride = columns * components;
        for (int s = 0; s < segments; s++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4 + (s * 4)), (uint)at);
            int component = s / bytes;
            int byteShift = 8 * (bytes - 1 - (s % bytes));
            for (int y = 0; y < rows; y++)
            {
                ReadOnlySpan<int> row = samples.Slice(y * stride, stride);
                for (int x = 0, i = component; x < columns; x++, i += components)
                {
                    plane[x] = (byte)(((uint)row[i] & mask) << shift >> byteShift);
                }

                at += Pack(plane, output.AsSpan(at));
            }

            if ((at & 1) != 0)
            {
                output[at++] = 0;
            }
        }

        Array.Resize(ref output, at);
        return output;
    }

    /// <summary>Packs one row of a byte plane with PackBits (G.3.1): runs of three or more repeated, the rest literal; returns the bytes written.</summary>
    internal static int Pack(ReadOnlySpan<byte> source, Span<byte> target)
    {
        int written = 0;
        int i = 0;
        int n = source.Length;
        while (i < n)
        {
            byte value = source[i];
            int run = 1;
            while (i + run < n && run < 128 && source[i + run] == value)
            {
                run++;
            }

            if (run >= 3 || (run == 2 && i + run == n))
            {
                target[written++] = (byte)(257 - run);
                target[written++] = value;
                i += run;
                continue;
            }

            // A literal stretch, up to the next run of three or 128 bytes.
            int start = i;
            int length = 0;
            while (i < n && length < 128)
            {
                if (i + 2 < n && source[i] == source[i + 1] && source[i] == source[i + 2])
                {
                    break;
                }

                i++;
                length++;
            }

            target[written++] = (byte)(length - 1);
            source.Slice(start, length).CopyTo(target[written..]);
            written += length;
        }

        return written;
    }

    /// <summary>Unpacks one PackBits segment (G.3.2) into <paramref name="target"/>; returns the bytes written. Extra bytes are dropped.</summary>
    internal static int Unpack(ReadOnlySpan<byte> source, Span<byte> target)
    {
        int at = 0;
        int written = 0;
        while (at < source.Length && written < target.Length)
        {
            int n = (sbyte)source[at++];
            if (n >= 0)
            {
                int take = Math.Min(Math.Min(n + 1, source.Length - at), target.Length - written);
                source.Slice(at, take).CopyTo(target[written..]);
                at += n + 1;
                written += take;
            }
            else if (n != -128)
            {
                if (at >= source.Length)
                {
                    break;
                }

                int take = Math.Min(1 - n, target.Length - written);
                target.Slice(written, take).Fill(source[at++]);
                written += take;
            }
        }

        return written;
    }
}
