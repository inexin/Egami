using System.Buffers.Binary;

namespace Egami.Video.Containers.Avi;

/// <summary>
/// Maps AVI stream formats (BITMAPINFOHEADER and WAVEFORMATEX, from the Microsoft AVI RIFF reference) to Matroska codec ids
/// and back. Formats without their own id use Matroska's compatibility ids, which carry the Windows header unchanged.
/// </summary>
internal static class AviCodecs
{
    public const int BitmapInfoHeaderSize = 40;
    public const int WaveFormatExSize = 18;

    private const ushort WavePcm = 0x0001;
    private const ushort WaveFloat = 0x0003;
    private const ushort WaveALaw = 0x0006;
    private const ushort WaveMuLaw = 0x0007;
    private const ushort WaveMpeg = 0x0050;
    private const ushort WaveMp3 = 0x0055;
    private const ushort WaveAac = 0x00FF;
    private const ushort WaveAc3 = 0x2000;
    private const ushort WaveExtensible = 0xFFFE;

    private static readonly uint[] MotionJpegFourCCs =
        [FourCC.Mjpg, FourCC.FromString("mjpg"), FourCC.FromString("JPEG"), FourCC.FromString("jpeg"), FourCC.FromString("AVRn"),
         FourCC.FromString("AVDJ"), FourCC.FromString("dmb1"), FourCC.FromString("MJPA")];

    public static bool IsMotionJpeg(uint compression) => Array.IndexOf(MotionJpegFourCCs, compression) >= 0;

    /// <summary>Uncompressed layouts AVI names by FourCC, with their bits per pixel.</summary>
    private static readonly Dictionary<string, int> UncompressedBits = new()
    {
        ["I420"] = 12,
        ["IYUV"] = 12,
        ["YV12"] = 12,
        ["NV12"] = 12,
        ["NV21"] = 12,
        ["YUY2"] = 16,
        ["UYVY"] = 16,
        ["Y42B"] = 16,
        ["444P"] = 24,
        ["Y800"] = 8,
        ["v210"] = 20,
    };

    /// <summary>Returns the uncompressed layout FourCC a compression code names, or null.</summary>
    public static string? UncompressedFourCC(uint compression)
    {
        string text = FourCC.ToString(compression);
        return UncompressedBits.ContainsKey(text) ? text : null;
    }

    /// <summary>Reads a video stream format. Returns the codec id, codec private bytes, size and compression FourCC.</summary>
    public static (string CodecId, byte[] Private, int Width, int Height, uint Compression) ReadBitmapInfo(ReadOnlySpan<byte> strf, uint handler)
    {
        if (strf.Length < BitmapInfoHeaderSize)
        {
            return (CodecIds.VfwFourCC, strf.ToArray(), 0, 0, handler);
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(strf[4..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(strf[8..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(strf[16..]);
        height = height == int.MinValue ? 0 : Math.Abs(height);
        width = Math.Max(0, width);
        if (IsMotionJpeg(compression) || (compression == 0 && IsMotionJpeg(handler)))
        {
            return (CodecIds.MotionJpeg, [], width, height, compression);
        }

        return UncompressedFourCC(compression) is not null
            ? (CodecIds.Uncompressed, [], width, height, compression)
            : (CodecIds.VfwFourCC, strf.ToArray(), width, height, compression);
    }

    /// <summary>Builds the BITMAPINFOHEADER an AVI writer stores for a video track.</summary>
    public static byte[] WriteBitmapInfo(VideoTrackInfo track, out uint handler)
    {
        if (track.CodecId == CodecIds.VfwFourCC && track.CodecPrivate.Length >= BitmapInfoHeaderSize)
        {
            byte[] copy = track.CodecPrivate.ToArray();
            handler = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(16));
            return copy;
        }

        if (track.CodecId == CodecIds.Uncompressed && track.FourCC is { } layout && UncompressedBits.TryGetValue(layout, out int bits))
        {
            uint code = FourCC.FromString(layout);
            handler = code;
            byte[] raw = new byte[BitmapInfoHeaderSize];
            BinaryPrimitives.WriteInt32LittleEndian(raw, BitmapInfoHeaderSize);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), track.Width);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8), track.Height);
            BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(12), 1);
            BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(14), (short)bits);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(16), code);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(20), checked((int)(Codecs.Raw.RawLayout.FromFourCC(layout)!.FrameSize(track.Width, track.Height))));
            return raw;
        }

        if (track.CodecId != CodecIds.MotionJpeg)
        {
            throw new NotSupportedException($"AVI cannot hold video codec '{track.CodecId}'{(track.CodecId == CodecIds.Uncompressed ? " in this layout" : "")}.");
        }

        handler = FourCC.Mjpg;
        byte[] header = new byte[BitmapInfoHeaderSize];
        Span<byte> h = header;
        BinaryPrimitives.WriteInt32LittleEndian(h, BitmapInfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], track.Width);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], track.Height);
        BinaryPrimitives.WriteInt16LittleEndian(h[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(h[14..], 24);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], FourCC.Mjpg);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], checked(track.Width * track.Height * 3));
        return header;
    }

    /// <summary>Reads an audio stream format.</summary>
    public static (string CodecId, byte[] Private, int SampleRate, int Channels, int Bits) ReadWaveFormat(ReadOnlySpan<byte> strf)
    {
        if (strf.Length < 16)
        {
            return (CodecIds.AcmAudio, strf.ToArray(), 0, 0, 0);
        }

        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(strf);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(strf[2..]);
        int rate = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(strf[4..]));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(strf[14..]);
        int extraSize = strf.Length >= WaveFormatExSize ? Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(strf[16..]), strf.Length - WaveFormatExSize) : 0;
        ReadOnlySpan<byte> extra = strf.Slice(Math.Min(WaveFormatExSize, strf.Length), extraSize);
        if (tag == WaveExtensible && extra.Length >= 22)
        {
            // WAVEFORMATEXTENSIBLE: the sub-format GUID's first two bytes are the real format tag.
            tag = BinaryPrimitives.ReadUInt16LittleEndian(extra[6..]);
            if (tag is not (WavePcm or WaveFloat))
            {
                return (CodecIds.AcmAudio, strf.ToArray(), rate, channels, bits);
            }
        }

        return tag switch
        {
            WavePcm => (CodecIds.PcmLittleEndian, [], rate, channels, bits),
            WaveFloat => (CodecIds.PcmFloat, [], rate, channels, bits),
            WaveALaw => (CodecIds.G711ALaw, [], rate, channels, 8),
            WaveMuLaw => (CodecIds.G711MuLaw, [], rate, channels, 8),
            WaveMp3 => (CodecIds.Mp3, [], rate, channels, 0),
            WaveMpeg => (CodecIds.Mp2, [], rate, channels, 0),
            WaveAc3 => (CodecIds.Ac3, [], rate, channels, 0),
            WaveAac => (CodecIds.Aac, extra.ToArray(), rate, channels, 0),
            _ => (CodecIds.AcmAudio, strf.ToArray(), rate, channels, bits),
        };
    }

    /// <summary>Builds the WAVEFORMATEX an AVI writer stores for an audio track.</summary>
    public static byte[] WriteWaveFormat(AudioTrackInfo track)
    {
        if (track.CodecId == CodecIds.AcmAudio && track.CodecPrivate.Length >= 16)
        {
            return track.CodecPrivate.ToArray();
        }

        (ushort tag, int bits, int blockAlign, int bytesPerSecond) = track.CodecId switch
        {
            CodecIds.PcmLittleEndian or CodecIds.PcmFloat => (
                track.CodecId == CodecIds.PcmFloat ? WaveFloat : WavePcm,
                track.BitsPerSample,
                track.Channels * ((track.BitsPerSample + 7) / 8),
                track.SampleRate * track.Channels * ((track.BitsPerSample + 7) / 8)),
            CodecIds.Mp3 => (WaveMp3, 0, 1, 0),
            CodecIds.Mp2 => (WaveMpeg, 0, 1, 0),
            CodecIds.Ac3 => (WaveAc3, 0, 1, 0),
            CodecIds.Aac => (WaveAac, 0, 1, 0),
            _ => throw new NotSupportedException($"AVI cannot hold audio codec '{track.CodecId}'."),
        };

        ReadOnlySpan<byte> extra = track.CodecId == CodecIds.Aac ? track.CodecPrivate.Span : [];
        byte[] format = new byte[WaveFormatExSize + extra.Length];
        Span<byte> f = format;
        BinaryPrimitives.WriteUInt16LittleEndian(f, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(f[2..], (ushort)track.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(f[4..], track.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(f[8..], bytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(f[12..], (ushort)Math.Max(1, blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(f[14..], (ushort)bits);
        BinaryPrimitives.WriteUInt16LittleEndian(f[16..], (ushort)extra.Length);
        extra.CopyTo(f[WaveFormatExSize..]);
        return format;
    }
}
