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
