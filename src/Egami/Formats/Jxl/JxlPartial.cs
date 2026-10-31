using System.Buffers.Binary;

namespace Egami.Formats.Jxl;

/// <summary>The codestream of a partly downloaded JPEG XL file (PR D): what has arrived of it, from a bare codestream or the container's boxes.</summary>
internal static class JxlPartial
{
    /// <summary>
    /// Gets the codestream bytes among the first <paramref name="length"/> of <paramref name="data"/>: all of a bare codestream,
    /// else the jxlc box's payload or the jxlp boxes' in order, the last cut where the bytes end. <see langword="null"/> while
    /// none has arrived or the file is neither.
    /// </summary>
    internal static byte[]? Codestream(byte[] data, int length)
    {
        ReadOnlySpan<byte> bytes = data.AsSpan(0, length);
        if (bytes is [0xFF, 0x0A, ..])
        {
            return bytes.ToArray();
        }

        if (bytes.Length < 12 || !bytes[..12].SequenceEqual(JxlContainer.Signature))
        {
            return null;
        }

        List<byte> codestream = [];
        long at = 12;
        while (at + 8 <= bytes.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes[(int)at..]);
            string type = System.Text.Encoding.ASCII.GetString(bytes.Slice((int)at + 4, 4));
            int header = 8;
            if (size == 1)
            {
                if (at + 16 > bytes.Length)
                {
                    break;
                }

                size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(bytes[((int)at + 8)..]), long.MaxValue);
                header = 16;
            }

            long end = size == 0 ? long.MaxValue : at + size;
            if (size != 0 && size < header)
            {
                return null;
            }

            int start = (int)at + header + (type == "jxlp" ? 4 : 0);
            if (type is "jxlc" or "jxlp" && start <= bytes.Length)
            {
                int stop = (int)Math.Min(end, bytes.Length);
                codestream.AddRange(bytes[start..stop]);
            }

            if (end > bytes.Length)
            {
                break;
            }

            at = end;
        }

        return codestream.Count >= 2 ? [.. codestream] : null;
    }
}
