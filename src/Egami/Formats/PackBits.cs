namespace Egami.Formats;

/// <summary>
/// Apple's PackBits run-length code (Technical Note 1023, TIFF 6.0 section 9): a flag byte n, then n + 1 literal bytes when n is
/// 0 to 127, or the next byte written 1 − n times when n is −127 to −1; −128 is a no-op. Moved from Lars's ClassicMac
/// (ClassicMac.Core.PackBits, MIT). Shared by TIFF and PSD.
/// </summary>
internal static class PackBits
{
    /// <summary>
    /// Decodes <paramref name="source"/> into <paramref name="destination"/> until either runs out. Returns the bytes written;
    /// a run cut short by the end of either is copied as far as it goes.
    /// </summary>
    internal static int Decode(ReadOnlySpan<byte> source, Span<byte> destination, out int read)
    {
        int at = 0;
        int written = 0;
        while (at < source.Length && written < destination.Length)
        {
            int flag = (sbyte)source[at++];
            if (flag >= 0)
            {
                int count = Math.Min(Math.Min(flag + 1, source.Length - at), destination.Length - written);
                source.Slice(at, count).CopyTo(destination[written..]);
                at += count;
                written += count;
            }
            else if (flag != -128)
            {
                if (at >= source.Length)
                {
                    break;
                }

                int count = Math.Min(1 - flag, destination.Length - written);
                destination.Slice(written, count).Fill(source[at++]);
                written += count;
            }
        }

        read = at;
        return written;
    }

    /// <summary>Returns the most bytes <see cref="Encode"/> writes for <paramref name="length"/> input bytes.</summary>
    internal static int MaxEncodedLength(int length) => length + ((length + 127) / 128);

    /// <summary>
    /// Packs <paramref name="data"/> into <paramref name="destination"/>, which holds at least <see cref="MaxEncodedLength"/>
    /// bytes: runs of 3 or more equal bytes as (1 − count, byte), everything else in literal blocks, at most 128 bytes in either;
    /// −128 is never written. Returns the bytes written.
    /// </summary>
    internal static int Encode(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        int n = data.Length;
        int i = 0;
        int written = 0;
        while (i < n)
        {
            byte value = data[i];
            int run = 1;
            while (i + run < n && run < 128 && data[i + run] == value)
            {
                run++;
            }

            if (run >= 3)
            {
                destination[written++] = (byte)(1 - run);
                destination[written++] = value;
                i += run;
                continue;
            }

            int start = i++;
            while (i < n && i - start < 128 && !(i + 2 < n && data[i] == data[i + 1] && data[i] == data[i + 2]))
            {
                i++;
            }

            destination[written++] = (byte)(i - start - 1);
            data[start..i].CopyTo(destination[written..]);
            written += i - start;
        }

        return written;
    }
}
