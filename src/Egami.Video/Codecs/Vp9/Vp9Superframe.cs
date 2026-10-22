namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// VP9 superframes (VP9 bitstream specification annex B): several frames in one packet with an index after them, the way WebM
/// stores a hidden frame (an alternate reference) with the shown frame that follows it.
/// </summary>
internal static class Vp9Superframe
{
    /// <summary>
    /// Gets whether a packet is one hidden frame without a superframe index: shown neither now nor as an existing frame. A packet
    /// with a broken index is not one.
    /// </summary>
    public static bool IsLoneHiddenFrame(ReadOnlySpan<byte> packet)
    {
        Span<int> sizes = stackalloc int[8];
        if (packet.Length < 2 || packet[0] >> 6 != 2)
        {
            return false;
        }

        try
        {
            if (Vp9StreamDecoder.ParseSuperframeIndex(packet, sizes) > 0)
            {
                return false;
            }
        }
        catch (CorruptDataException)
        {
            return false;
        }

        int bits = (packet[0] << 8) | packet[1];
        int profile = ((bits >> 13) & 1) | (((bits >> 12) & 1) << 1);
        int at = profile == 3 ? 10 : 11;
        bool showExisting = ((bits >> at) & 1) != 0;
        bool show = ((bits >> (at - 2)) & 1) != 0;
        return !showExisting && !show;
    }

    /// <summary>Gets a packet's frames: the frames its superframe index lists, or the whole packet as one frame.</summary>
    public static List<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> packet)
    {
        Span<int> sizes = stackalloc int[8];
        int count = Vp9StreamDecoder.ParseSuperframeIndex(packet.Span, sizes);
        if (count == 0)
        {
            return [packet];
        }

        List<ReadOnlyMemory<byte>> frames = [];
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            if (sizes[i] > packet.Length - at)
            {
                throw new CorruptDataException("VP9", at, "A frame size in the superframe index runs past the packet.");
            }

            frames.Add(packet.Slice(at, sizes[i]));
            at += sizes[i];
        }

        return frames;
    }

    /// <summary>
    /// Joins up to eight frames into one superframe: the frames, then the index (a marker byte 0b110mmfff, each size in mm+1
    /// little-endian bytes, the marker again), with sizes in as few bytes as the largest needs, as libvpx's encoder writes it.
    /// </summary>
    public static byte[] Merge(IReadOnlyList<ReadOnlyMemory<byte>> frames)
    {
        if (frames.Count is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), frames.Count, "A superframe holds 1 to 8 frames.");
        }

        int max = 0, total = 0;
        foreach (ReadOnlyMemory<byte> f in frames)
        {
            max = Math.Max(max, f.Length);
            total += f.Length;
        }

        int mag = max <= 0xff ? 1 : max <= 0xffff ? 2 : max <= 0xffffff ? 3 : 4;
        byte marker = (byte)(0xc0 | ((mag - 1) << 3) | (frames.Count - 1));
        byte[] result = new byte[total + 2 + (mag * frames.Count)];
        int at = 0;
        foreach (ReadOnlyMemory<byte> f in frames)
        {
            f.Span.CopyTo(result.AsSpan(at));
            at += f.Length;
        }

        result[at++] = marker;
        foreach (ReadOnlyMemory<byte> f in frames)
        {
            for (int j = 0; j < mag; j++)
            {
                result[at++] = (byte)(f.Length >> (8 * j));
            }
        }

        result[at] = marker;
        return result;
    }
}
