namespace Egami.Nal;

/// <summary>
/// Finds the NAL units of H.264 and HEVC data: an Annex B byte stream split at its start codes (H.264 and H.265 Annex B), sample or
/// item data split at big-endian length fields (ISO/IEC 14496-15), and the emulation prevention bytes removed from a unit
/// (H.264 7.4.1, H.265 7.4.2). Both standards frame NAL units the same way; only the unit header differs.
/// </summary>
internal static class NalFraming
{
    /// <summary>Removes emulation_prevention_three_byte (the 03 in 00 00 03), noting where each was.</summary>
    internal static byte[] Unescape(ReadOnlySpan<byte> nal, List<int>? removed = null)
    {
        ReadOnlySpan<byte> pattern = [0, 0, 3];
        int first = nal.IndexOf(pattern);
        if (first < 0)
        {
            return nal.ToArray();
        }

        byte[] output = new byte[nal.Length];
        int written = 0, zeros = 0;
        for (int i = 0; i < nal.Length; i++)
        {
            byte b = nal[i];
            if (zeros >= 2 && b == 3)
            {
                removed?.Add(i);
                zeros = 0;
                continue;
            }

            output[written++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return output[..written];
    }

    /// <summary>Splits data of NAL units, each after a big-endian size field of <paramref name="lengthSize"/> bytes (1 to 4).</summary>
    /// <exception cref="CorruptDataException">A size runs past the end.</exception>
    internal static List<ReadOnlyMemory<byte>> SplitLengthPrefixed(ReadOnlyMemory<byte> data, int lengthSize)
    {
        List<ReadOnlyMemory<byte>> units = [];
        ReadOnlySpan<byte> span = data.Span;
        int at = 0;
        while (at < span.Length)
        {
            if (at + lengthSize > span.Length)
            {
                throw NalErrors.Corrupt("A NAL unit's size field is cut off.");
            }

            long size = 0;
            for (int i = 0; i < lengthSize; i++)
            {
                size = (size << 8) | span[at + i];
            }

            at += lengthSize;
            if (size > span.Length - at)
            {
                throw NalErrors.Corrupt($"A NAL unit of {size} bytes runs past the end of the data's {span.Length} bytes.");
            }

            if (size > 0)
            {
                units.Add(data.Slice(at, (int)size));
            }

            at += (int)size;
        }

        return units;
    }

    /// <summary>
    /// Splits an Annex B byte stream at its start codes (00 00 01, and 00 00 00 01 with its zero_byte), dropping the trailing zero
    /// bytes of each unit. Bytes before the first start code are not a NAL unit and are left out.
    /// </summary>
    internal static List<ReadOnlyMemory<byte>> SplitAnnexB(ReadOnlyMemory<byte> stream)
    {
        List<ReadOnlyMemory<byte>> units = [];
        ReadOnlySpan<byte> span = stream.Span;
        int start = -1;
        for (int i = 0; i + 2 < span.Length; i++)
        {
            if (span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 1)
            {
                if (start >= 0)
                {
                    Add(span, start, i);
                }

                start = i + 3;
                i += 2;
            }
        }

        if (start >= 0)
        {
            Add(span, start, span.Length);
        }

        return units;

        void Add(ReadOnlySpan<byte> bytes, int from, int to)
        {
            while (to > from && bytes[to - 1] == 0)
            {
                to--;
            }

            if (to > from)
            {
                units.Add(stream[from..to]);
            }
        }
    }

    /// <summary>
    /// Finds the next three-byte start code prefix (00 00 01) at or after <paramref name="from"/>, for reading an Annex B stream a
    /// piece at a time.
    /// </summary>
    /// <returns>The position of the prefix's first byte, or -1 when the data holds none.</returns>
    internal static int IndexOfStartCode(ReadOnlySpan<byte> data, int from = 0)
    {
        ReadOnlySpan<byte> prefix = [0, 0, 1];
        int found = data[from..].IndexOf(prefix);
        return found < 0 ? -1 : from + found;
    }

    /// <summary>Gets whether the data starts with a start code (00 00 01 or 00 00 00 01): Annex B rather than length-prefixed.</summary>
    internal static bool StartsWithStartCode(ReadOnlySpan<byte> data) =>
        data is [0, 0, 1, ..] or [0, 0, 0, 1, ..];
}
