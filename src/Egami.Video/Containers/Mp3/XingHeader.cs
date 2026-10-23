using System.Buffers.Binary;
using System.Text;

using Egami.Video.Audio.Codecs.Mp3;

namespace Egami.Video.Containers.Mp3;

/// <summary>
/// The information frame at the start of a VBR (or CBR "Info") MP3: Xing's header with the frame and byte counts and a 100-entry
/// seek table, Fraunhofer's VBRI header, and the LAME tag after Xing's fields with the encoder delay and padding that make
/// gapless playback exact. The frame decodes to silence and is not audio, so the reader leaves it out.
/// </summary>
internal sealed class XingHeader
{
    /// <summary>The MP3 decoder's own delay in samples, added to the LAME tag's encoder delay (the classic 528 plus 1).</summary>
    public const int DecoderDelay = 529;

    /// <summary>The bytes the writer's Xing fields and LAME tag take after the side information.</summary>
    public const int WrittenBytes = 4 + 4 + 4 + 4 + 100 + 4 + 36;

    private XingHeader()
    {
    }

    /// <summary>Gets the audio frame count (the information frame not counted), or null.</summary>
    public long? Frames { get; private init; }

    /// <summary>Gets the stream's byte count from the information frame on, or null.</summary>
    public long? Bytes { get; private init; }

    /// <summary>Gets Xing's seek table: 100 entries, entry i the byte position at i percent of the time in 256ths of <see cref="Bytes"/>.</summary>
    public byte[]? Toc { get; private init; }

    /// <summary>Gets VBRI's seek table: the byte position of every entry's first frame, counted from the information frame, with that frame's number.</summary>
    public IReadOnlyList<(long Frame, long Offset)>? Points { get; private init; }

    /// <summary>Gets the LAME tag's encoder delay in samples, or null when there is no LAME tag.</summary>
    public int? EncoderDelay { get; private init; }

    /// <summary>Gets the LAME tag's padding at the end in samples, or null when there is no LAME tag.</summary>
    public int? EncoderPadding { get; private init; }

    /// <summary>Gets the encoder named in the LAME tag, such as "LAME3.100", or null.</summary>
    public string? Encoder { get; private init; }

    /// <summary>Reads the information header in <paramref name="frame"/> (a whole first frame), or returns null when it has none.</summary>
    public static XingHeader? Read(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < MpegAudioHeader.Size || !MpegAudioHeader.IsValid(frame))
        {
            return null;
        }

        MpegAudioHeader header = new(frame);
        if (header.Layer != 3)
        {
            return null;
        }

        int at = MpegAudioHeader.Size + header.SideInfoBytes;
        foreach (int offset in header.HasCrc ? [at + 2, at] : (ReadOnlySpan<int>)[at])
        {
            if (frame.Length >= offset + 8 && (frame[offset..].StartsWith("Xing"u8) || frame[offset..].StartsWith("Info"u8)))
            {
                return ReadXing(frame, offset);
            }
        }

        const int VbriAt = MpegAudioHeader.Size + 32;
        return frame.Length >= VbriAt + 26 && frame[VbriAt..].StartsWith("VBRI"u8) ? ReadVbri(frame[VbriAt..]) : null;
    }

    /// <summary>
    /// Writes a Xing (or, for constant bit rate, "Info") header, and a LAME-layout tag when the delay is known, into <paramref name="frame"/>, a silent frame
    /// whose header is already written. The tag names the encoder <paramref name="encoder"/> and holds the delay and padding when known.
    /// </summary>
    public static void Write(Span<byte> frame, bool constant, long frames, long bytes, ReadOnlySpan<byte> toc, int? delay, int? padding, string encoder)
    {
        MpegAudioHeader header = new(frame);
        int p = MpegAudioHeader.Size + (header.HasCrc ? 2 : 0) + header.SideInfoBytes;
        (constant ? "Info"u8 : "Xing"u8).CopyTo(frame[p..]);
        BinaryPrimitives.WriteUInt32BigEndian(frame[(p + 4)..], 0x0F);
        BinaryPrimitives.WriteUInt32BigEndian(frame[(p + 8)..], (uint)Math.Min(frames, uint.MaxValue));
        BinaryPrimitives.WriteUInt32BigEndian(frame[(p + 12)..], (uint)Math.Min(bytes, uint.MaxValue));
        toc.CopyTo(frame[(p + 16)..]);
        BinaryPrimitives.WriteUInt32BigEndian(frame[(p + 116)..], 0);
        if (delay is not { } d || padding is not { } pad)
        {
            // Without a known delay there is no LAME tag: readers would take its zero delay as real and cut 529 samples.
            return;
        }

        int q = p + 120;
        Span<byte> name = frame.Slice(q, 9);
        name.Fill((byte)' ');
        Encoding.ASCII.GetBytes(encoder.AsSpan(0, Math.Min(encoder.Length, 9)), name);
        int dd = Math.Clamp(d, 0, 4095);
        int pp = Math.Clamp(pad, 0, 4095);
        frame[q + 21] = (byte)(dd >> 4);
        frame[q + 22] = (byte)((dd << 4) | (pp >> 8));
        frame[q + 23] = (byte)pp;

        // The tag's own CRC-16 covers the frame up to it; the music CRC after it is left zero, as it would need the whole stream.
        BinaryPrimitives.WriteUInt16BigEndian(frame[(q + 34)..], Crc16(frame[..(q + 34)]));
    }

    /// <summary>The LAME tag's CRC-16: polynomial 0x8005 reflected, starting at 0.</summary>
    private static ushort Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
            }
        }

        return (ushort)crc;
    }

    private static XingHeader ReadXing(ReadOnlySpan<byte> frame, int offset)
    {
        uint flags = BinaryPrimitives.ReadUInt32BigEndian(frame[(offset + 4)..]);
        int p = offset + 8;
        long? frames = null;
        long? bytes = null;
        byte[]? toc = null;
        if ((flags & 1) != 0 && p + 4 <= frame.Length)
        {
            frames = BinaryPrimitives.ReadUInt32BigEndian(frame[p..]);
            p += 4;
        }

        if ((flags & 2) != 0 && p + 4 <= frame.Length)
        {
            bytes = BinaryPrimitives.ReadUInt32BigEndian(frame[p..]);
            p += 4;
        }

        if ((flags & 4) != 0 && p + 100 <= frame.Length)
        {
            toc = frame.Slice(p, 100).ToArray();
            p += 100;
        }

        if ((flags & 8) != 0)
        {
            p += 4;
        }

        // The LAME tag: a 9-character encoder name, then fields ending with the delay and padding 21 bytes in and the tag's CRC 34 bytes in.
        int? delay = null;
        int? padding = null;
        string? encoder = null;
        if (p + 36 <= frame.Length && IsName(frame.Slice(p, 4)))
        {
            ReadOnlySpan<byte> tag = frame[p..];
            bool known = tag.StartsWith("LAME"u8) || tag.StartsWith("Lavf"u8) || tag.StartsWith("Lavc"u8) || tag.StartsWith("GOGO"u8) || tag.StartsWith("L3.9"u8);
            if (known || BinaryPrimitives.ReadUInt16BigEndian(tag[34..]) == Crc16(frame[..(p + 34)]))
            {
                encoder = Encoding.ASCII.GetString(tag[..9]).TrimEnd(' ', '\0');
                delay = (tag[21] << 4) | (tag[22] >> 4);
                padding = ((tag[22] & 0xF) << 8) | tag[23];
            }
        }

        if (frames == 0)
        {
            frames = null;
        }

        if (toc is not null && (bytes is null or 0 || !IsMonotonic(toc)))
        {
            toc = null;
        }

        return new XingHeader { Frames = frames, Bytes = bytes, Toc = toc, EncoderDelay = delay, EncoderPadding = padding, Encoder = encoder };
    }

    private static XingHeader? ReadVbri(ReadOnlySpan<byte> v)
    {
        uint bytes = BinaryPrimitives.ReadUInt32BigEndian(v[10..]);
        uint frames = BinaryPrimitives.ReadUInt32BigEndian(v[14..]);
        int entries = BinaryPrimitives.ReadUInt16BigEndian(v[18..]);
        int scale = BinaryPrimitives.ReadUInt16BigEndian(v[20..]);
        int entrySize = BinaryPrimitives.ReadUInt16BigEndian(v[22..]);
        int framesPerEntry = BinaryPrimitives.ReadUInt16BigEndian(v[24..]);
        List<(long, long)>? points = null;
        if (entrySize is >= 1 and <= 4 && framesPerEntry > 0 && 26 + (entries * entrySize) <= v.Length)
        {
            points = [(0, 0)];
            long offset = 0;
            for (int i = 0; i < entries; i++)
            {
                long size = 0;
                for (int b = 0; b < entrySize; b++)
                {
                    size = (size << 8) | v[26 + (i * entrySize) + b];
                }

                offset += size * scale;
                points.Add(((long)(i + 1) * framesPerEntry, offset));
            }
        }

        return new XingHeader { Frames = frames > 0 ? frames : null, Bytes = bytes > 0 ? bytes : null, Points = points };
    }

    private static bool IsName(ReadOnlySpan<byte> b)
    {
        foreach (byte c in b)
        {
            if (c is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMonotonic(ReadOnlySpan<byte> toc)
    {
        for (int i = 1; i < toc.Length; i++)
        {
            if (toc[i] < toc[i - 1])
            {
                return false;
            }
        }

        return true;
    }
}
