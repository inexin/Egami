namespace Egami.Video.Audio.Codecs.Mp3;

/// <summary>
/// Finds MPEG audio frames in a byte stream: a candidate header counts only when the headers at the following frame
/// boundaries match it (three in a row by default), so junk, tags and false syncs inside audio data are skipped. Free-format
/// frames get their length from the distance to the next matching header. Modeled on minimp3's frame search (CC0).
/// </summary>
internal static class MpegAudioSync
{
    /// <summary>How many headers in a row lock the stream: the candidate and two more.</summary>
    public const int LockHeaders = 3;

    /// <summary>
    /// Finds the first locked frame in <paramref name="data"/>. Returns its offset and sets <paramref name="frameBytes"/> to its
    /// whole length with padding; returns -1 when there is none yet (more data may complete one). With <paramref name="atEnd"/>
    /// the data is the rest of the stream, so a frame that runs to the end, or is followed by fewer matching headers than
    /// <see cref="LockHeaders"/> needs, still counts.
    /// </summary>
    /// <param name="data">The bytes to search.</param>
    /// <param name="freeFormatBytes">The free-format frame length found so far (without padding), updated when a free-format stream is found.</param>
    /// <param name="frameBytes">The frame's length with padding.</param>
    /// <param name="atEnd">Whether the data runs to the end of the stream.</param>
    /// <returns>The offset of the frame, or -1.</returns>
    public static int Find(ReadOnlySpan<byte> data, ref int freeFormatBytes, out int frameBytes, bool atEnd)
    {
        for (int i = 0; i + MpegAudioHeader.Size <= data.Length; i++)
        {
            if (data[i] != 0xFF)
            {
                int next = data[(i + 1)..].IndexOf((byte)0xFF);
                if (next < 0)
                {
                    break;
                }

                i += next;
                if (i + MpegAudioHeader.Size > data.Length)
                {
                    break;
                }
            }

            ReadOnlySpan<byte> h = data[i..];
            if (!MpegAudioHeader.IsValid(h))
            {
                continue;
            }

            MpegAudioHeader header = new(h);
            int bytes = header.FrameBytes(freeFormatBytes);
            int total = bytes + header.PaddingBytes;
            if (bytes == 0)
            {
                // Free format: the length is the distance to the next matching header, confirmed by the one after it.
                for (int k = MpegAudioHeader.MinFreeFormatBytes + header.PaddingBytes; k < MpegAudioHeader.MaxFreeFormatBytes && i + (2 * k) < data.Length - MpegAudioHeader.Size; k++)
                {
                    if (MpegAudioHeader.Matches(h, h[k..]))
                    {
                        int fb = k - header.PaddingBytes;
                        int nextTotal = fb + new MpegAudioHeader(h[k..]).PaddingBytes;
                        if (i + k + nextTotal + MpegAudioHeader.Size > data.Length || !MpegAudioHeader.Matches(h, h[(k + nextTotal)..]))
                        {
                            continue;
                        }

                        total = k;
                        bytes = fb;
                        freeFormatBytes = fb;
                        break;
                    }
                }
            }

            if (bytes > 0 && i + total <= data.Length && IsLocked(h, data.Length - i, bytes, atEnd) is { } locked)
            {
                if (!locked)
                {
                    // Not enough data to see the following headers yet.
                    frameBytes = 0;
                    return -1;
                }

                frameBytes = total;
                return i;
            }

            if (bytes > 0 && i + total > data.Length && !atEnd)
            {
                frameBytes = 0;
                return -1;
            }

            if (atEnd && i == 0 && bytes > 0 && total == data.Length)
            {
                frameBytes = total;
                return i;
            }

            if (header.IsFreeFormat)
            {
                freeFormatBytes = 0;
            }
        }

        frameBytes = 0;
        return -1;
    }

    /// <summary>
    /// Returns whether the frame at the start of <paramref name="h"/> is followed by matching headers: <see langword="true"/>
    /// when locked, <see langword="false"/> when the data ends before that can be told, <see langword="null"/> when a following
    /// header does not match.
    /// </summary>
    private static bool? IsLocked(ReadOnlySpan<byte> h, int length, int freeFormatBytes, bool atEnd)
    {
        int offset = 0;
        for (int matched = 1; matched < LockHeaders; matched++)
        {
            MpegAudioHeader current = new(h[offset..]);
            offset += current.FrameBytes(freeFormatBytes) + current.PaddingBytes;
            if (offset + MpegAudioHeader.Size > length)
            {
                // The stream ends here (or the buffer does): at the end one frame is enough, as minimp3 accepts.
                return atEnd ? offset <= length : false;
            }

            if (!MpegAudioHeader.Matches(h, h[offset..]))
            {
                // A tag after the frames ends the stream as well as the data's end does.
                return IsTag(h[offset..length]) ? true : null;
            }
        }

        return true;
    }

    /// <summary>Returns whether the bytes start an ID3v1, ID3v2, APEv2 or Lyrics3 tag.</summary>
    public static bool IsTag(ReadOnlySpan<byte> data) =>
        data.StartsWith("TAG"u8) || data.StartsWith("ID3"u8) || data.StartsWith("APETAGEX"u8) || data.StartsWith("LYRICSBEGIN"u8);
}
