using System.Buffers.Binary;
using System.Text;

using Egami.Video.Audio;
using Egami.Video.Containers.Avi;

namespace Egami.Video.Containers.Wav;

/// <summary>A wave format chunk (WAVEFORMATEX, with the WAVE_FORMAT_EXTENSIBLE fields when present).</summary>
internal sealed record WaveFormat(ushort Tag, int Channels, int SampleRate, int BlockAlign, int Bits, int ValidBits, uint ChannelMask, byte[] Raw);

/// <summary>Chunk ids, GUIDs and the shared chunk readers and writers of WAV, RF64 and Wave64.</summary>
internal static class WavChunks
{
    public const ushort WavePcm = 1;
    public const ushort WaveFloat = 3;
    public const ushort WaveExtensible = 0xFFFE;

    /// <summary>Wave64's RIFF GUID, 66666972-912E-11CF-A5D6-28DB04C10000, as stored.</summary>
    public static ReadOnlySpan<byte> RiffGuid => [0x72, 0x69, 0x66, 0x66, 0x2E, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00];

    /// <summary>Wave64's LIST GUID, 7473696C-912F-11CF-A5D6-28DB04C10000.</summary>
    public static ReadOnlySpan<byte> ListGuid => [0x6C, 0x69, 0x73, 0x74, 0x2F, 0x91, 0xCF, 0x11, 0xA5, 0xD6, 0x28, 0xDB, 0x04, 0xC1, 0x00, 0x00];

    /// <summary>Wave64's WAVE GUID, 65766177-ACF3-11D3-8CD1-00C04F8EDB8A.</summary>
    public static ReadOnlySpan<byte> WaveGuid => [0x77, 0x61, 0x76, 0x65, 0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A];

    /// <summary>The last 12 bytes of Wave64's GUIDs for chunks named by a FourCC (fmt, data, fact, cue and so on).</summary>
    public static ReadOnlySpan<byte> FourCCGuidTail => [0xF3, 0xAC, 0xD3, 0x11, 0x8C, 0xD1, 0x00, 0xC0, 0x4F, 0x8E, 0xDB, 0x8A];

    /// <summary>The KSDATAFORMAT_SUBTYPE GUID tail after the 2-byte format tag (and 2 zero bytes) in WAVE_FORMAT_EXTENSIBLE.</summary>
    public static ReadOnlySpan<byte> SubFormatTail => [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];

    /// <summary>Returns the Wave64 GUID of a chunk, or null for one not named by a FourCC.</summary>
    public static string? Wave64Name(ReadOnlySpan<byte> guid)
    {
        if (guid.SequenceEqual(ListGuid))
        {
            return "LIST";
        }

        return guid[4..].SequenceEqual(FourCCGuidTail) ? Encoding.ASCII.GetString(guid[..4]) : null;
    }

    /// <summary>Writes the Wave64 GUID of a chunk named by a FourCC.</summary>
    public static void Wave64Guid(string fourCC, Span<byte> destination)
    {
        if (fourCC == "LIST")
        {
            ListGuid.CopyTo(destination);
            return;
        }

        Encoding.ASCII.GetBytes(fourCC, destination[..4]);
        FourCCGuidTail.CopyTo(destination[4..]);
    }

    public static WaveFormat? ReadFormat(ReadOnlySpan<byte> fmt)
    {
        if (fmt.Length < 16)
        {
            return null;
        }

        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
        int rate = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]));
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        int validBits = bits;
        uint mask = 0;
        if (tag == WaveExtensible && fmt.Length >= 18 + 22 && BinaryPrimitives.ReadUInt16LittleEndian(fmt[16..]) >= 22)
        {
            int valid = BinaryPrimitives.ReadUInt16LittleEndian(fmt[18..]);
            validBits = valid is > 0 && valid <= bits ? valid : bits;
            mask = BinaryPrimitives.ReadUInt32LittleEndian(fmt[20..]);
            tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
        }

        return new WaveFormat(tag, channels, rate, blockAlign, bits, validBits, mask, fmt.ToArray());
    }

    /// <summary>Turns a format chunk into a track: PCM and float as Egami.Video decodes them, others as AVI names them.</summary>
    public static AudioTrackInfo Track(WaveFormat format, MediaTime? duration)
    {
        string codecId;
        byte[] codecPrivate = [];
        int bits = format.Bits;
        if (format.Tag is WavePcm)
        {
            codecId = CodecIds.PcmLittleEndian;
            bits = format.BlockAlign > 0 && format.Channels > 0 ? Math.Min(bits, format.BlockAlign / format.Channels * 8) : bits;
            bits = (bits + 7) / 8 * 8;
        }
        else if (format.Tag is WaveFloat)
        {
            codecId = CodecIds.PcmFloat;
        }
        else
        {
            (codecId, codecPrivate, _, _, bits) = AviCodecs.ReadWaveFormat(format.Raw);
        }

        return new AudioTrackInfo
        {
            Number = 1,
            Kind = TrackKind.Audio,
            CodecId = codecId,
            CodecPrivate = codecPrivate,
            TimeBase = new Rational(1, Math.Max(format.SampleRate, 1)),
            Duration = duration,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = bits,
            Layout = LayoutFromMask(format.ChannelMask, format.Channels),
        };
    }

    /// <summary>Returns the layout a channel mask gives, or null for no mask.</summary>
    public static ChannelLayout? LayoutFromMask(uint mask, int channels)
    {
        mask &= 0x3FFFF;
        if (mask == 0 || channels is < 1 or > ChannelLayout.MaxChannels)
        {
            return null;
        }

        // A mask with more speakers than channels names the first ones, lowest bit first.
        while (System.Numerics.BitOperations.PopCount(mask) > channels)
        {
            mask &= ~(1u << (31 - System.Numerics.BitOperations.LeadingZeroCount(mask)));
        }

        return new ChannelLayout((Speakers)mask, channels);
    }

    /// <summary>Builds a format chunk for a PCM or float track, extensible when it has more than two channels or more than 16 bits.</summary>
    public static byte[] WriteFormat(AudioTrackInfo track, int bits, bool isFloat)
    {
        ChannelLayout layout = track.EffectiveLayout;
        bool extensible = track.Channels > 2 || (!isFloat && bits > 16) || layout != ChannelLayout.ForChannelCount(track.Channels);
        byte[] fmt = new byte[extensible ? 40 : isFloat ? 18 : 16];
        Span<byte> f = fmt;
        int blockAlign = bits / 8 * track.Channels;
        BinaryPrimitives.WriteUInt16LittleEndian(f, extensible ? WaveExtensible : isFloat ? WaveFloat : WavePcm);
        BinaryPrimitives.WriteUInt16LittleEndian(f[2..], (ushort)track.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(f[4..], (uint)track.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(f[8..], (uint)Math.Min((long)track.SampleRate * blockAlign, uint.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(f[12..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(f[14..], (ushort)bits);
        if (extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(f[16..], 22);
            BinaryPrimitives.WriteUInt16LittleEndian(f[18..], (ushort)bits);
            BinaryPrimitives.WriteUInt32LittleEndian(f[20..], layout.IsPositioned || layout.Speakers != Speakers.None ? (uint)layout.Speakers : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(f[24..], isFloat ? WaveFloat : WavePcm);
            SubFormatTail.CopyTo(f[26..]);
        }

        return fmt;
    }

    /// <summary>Reads a LIST/INFO body (after the "INFO" type) into tags.</summary>
    public static void ReadInfo(ReadOnlySpan<byte> body, Dictionary<string, string> tags)
    {
        int p = 0;
        while (p + 8 <= body.Length)
        {
            string id = Encoding.ASCII.GetString(body.Slice(p, 4));
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(body[(p + 4)..]), (uint)(body.Length - p - 8));
            ReadOnlySpan<byte> value = body.Slice(p + 8, size);
            int end = value.IndexOf((byte)0);
            string text = Encoding.UTF8.GetString(end < 0 ? value : value[..end]).Trim();
            if (text.Length > 0)
            {
                tags[Array.Find(FourCC.InfoTags, t => t.Chunk == id).Tag ?? id] = text;
            }

            p += 8 + size + (size & 1);
        }
    }

    /// <summary>Builds a LIST/INFO body ("INFO" and its sub-chunks) from tags with an INFO chunk id.</summary>
    public static byte[]? WriteInfo(IReadOnlyDictionary<string, string> tags)
    {
        using MemoryStream body = new();
        body.Write("INFO"u8);
        Span<byte> header = stackalloc byte[8];
        foreach ((string tag, string value) in tags)
        {
            string? chunk = Array.Find(FourCC.InfoTags, t => t.Tag == tag).Chunk ?? (tag.Length == 4 && tag[0] == 'I' && tag.All(char.IsAsciiLetterUpper) ? tag : null);
            if (chunk is null || value.Length == 0)
            {
                continue;
            }

            byte[] text = [.. Encoding.UTF8.GetBytes(value), 0];
            Encoding.ASCII.GetBytes(chunk, header);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)text.Length);
            body.Write(header);
            body.Write(text);
            if ((text.Length & 1) != 0)
            {
                body.WriteByte(0);
            }
        }

        return body.Length > 4 ? body.ToArray() : null;
    }

    /// <summary>Reads a cue chunk: point id to sample offset.</summary>
    public static List<(uint Id, long Sample)> ReadCue(ReadOnlySpan<byte> body)
    {
        List<(uint, long)> points = [];
        if (body.Length < 4)
        {
            return points;
        }

        long count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(body), (body.Length - 4) / 24);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> p = body.Slice(4 + (i * 24), 24);
            points.Add((BinaryPrimitives.ReadUInt32LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p[20..])));
        }

        return points;
    }

    /// <summary>Reads the labels of a LIST/adtl body (after the "adtl" type).</summary>
    public static void ReadLabels(ReadOnlySpan<byte> body, Dictionary<uint, string> labels)
    {
        int p = 0;
        while (p + 8 <= body.Length)
        {
            string id = Encoding.ASCII.GetString(body.Slice(p, 4));
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(body[(p + 4)..]), (uint)(body.Length - p - 8));
            ReadOnlySpan<byte> sub = body.Slice(p + 8, size);
            if (id == "labl" && sub.Length >= 4)
            {
                ReadOnlySpan<byte> text = sub[4..];
                int end = text.IndexOf((byte)0);
                labels[BinaryPrimitives.ReadUInt32LittleEndian(sub)] = Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
            }

            p += 8 + size + (size & 1);
        }
    }

    /// <summary>Builds a cue chunk body and a LIST/adtl body for chapters, with sample offsets at the track's rate.</summary>
    public static (byte[] Cue, byte[]? Labels) WriteCue(IReadOnlyList<ChapterInfo> chapters, int sampleRate)
    {
        byte[] cue = new byte[4 + (24 * chapters.Count)];
        BinaryPrimitives.WriteUInt32LittleEndian(cue, (uint)chapters.Count);
        using MemoryStream adtl = new();
        adtl.Write("adtl"u8);
        Span<byte> header = stackalloc byte[12];
        for (int i = 0; i < chapters.Count; i++)
        {
            Span<byte> p = cue.AsSpan(4 + (24 * i), 24);
            uint sample = (uint)Math.Clamp(chapters[i].Start.Rescale(new Rational(1, sampleRate)).Ticks, 0, uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)(i + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(p[4..], sample);
            "data"u8.CopyTo(p[8..]);
            BinaryPrimitives.WriteUInt32LittleEndian(p[20..], sample);
            if (chapters[i].Title is { Length: > 0 } title)
            {
                byte[] text = [.. Encoding.UTF8.GetBytes(title), 0];
                "labl"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(4 + text.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)(i + 1));
                adtl.Write(header);
                adtl.Write(text);
                if ((text.Length & 1) != 0)
                {
                    adtl.WriteByte(0);
                }
            }
        }

        return (cue, adtl.Length > 4 ? adtl.ToArray() : null);
    }

    /// <summary>Reads the text frames of an ID3v2 tag into tags.</summary>
    public static void ReadId3(ReadOnlySpan<byte> tag, Dictionary<string, string> tags) => Mp3.Id3Tag.Read(tag, tags);
}
