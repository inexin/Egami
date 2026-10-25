namespace Egami.Video.Cli.Host;

/// <summary>Short names for Matroska codec ids: the name the tool prints, and ffprobe's codec_name for <c>info --format ffprobe</c>.</summary>
internal static class CodecNames
{
    private static readonly Dictionary<string, (string Name, string Short, string LongName)> Known = new(StringComparer.Ordinal)
    {
        [CodecIds.MotionJpeg] = ("Motion JPEG", "mjpeg", "Motion JPEG"),
        [CodecIds.Uncompressed] = ("raw video", "rawvideo", "raw video"),
        [CodecIds.Vp8] = ("VP8", "vp8", "On2 VP8"),
        [CodecIds.Vp9] = ("VP9", "vp9", "Google VP9"),
        [CodecIds.Av1] = ("AV1", "av1", "Alliance for Open Media AV1"),
        [CodecIds.Avc] = ("H.264", "h264", "H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10"),
        [CodecIds.Hevc] = ("H.265", "hevc", "H.265 / HEVC (High Efficiency Video Coding)"),
        [CodecIds.Mpeg4Visual] = ("MPEG-4 Visual", "mpeg4", "MPEG-4 part 2"),
        [CodecIds.MovText] = ("MP4 timed text", "mov_text", "MOV text"),
        [CodecIds.PcmLittleEndian] = ("PCM", "pcm", "PCM"),
        [CodecIds.PcmBigEndian] = ("PCM big-endian", "pcm", "PCM"),
        [CodecIds.PcmFloat] = ("PCM float", "pcm", "PCM"),
        [CodecIds.Mp3] = ("MP3", "mp3", "MP3 (MPEG audio layer 3)"),
        [CodecIds.Mp2] = ("MP2", "mp2", "MP2 (MPEG audio layer 2)"),
        [CodecIds.Mp1] = ("MP1", "mp1", "MP1 (MPEG audio layer 1)"),
        [CodecIds.Ac3] = ("AC-3", "ac3", "ATSC A/52A (AC-3)"),
        [CodecIds.EAc3] = ("E-AC-3", "eac3", "ATSC A/52B (AC-3, E-AC-3)"),
        [CodecIds.Alac] = ("ALAC", "alac", "ALAC (Apple Lossless Audio Codec)"),
        [CodecIds.G711MuLaw] = ("G.711 μ-law", "pcm_mulaw", "PCM mu-law / G.711 mu-law"),
        [CodecIds.G711ALaw] = ("G.711 A-law", "pcm_alaw", "PCM A-law / G.711 A-law"),
        [CodecIds.Aac] = ("AAC", "aac", "AAC (Advanced Audio Coding)"),
        [CodecIds.Opus] = ("Opus", "opus", "Opus (Opus Interactive Audio Codec)"),
        [CodecIds.Vorbis] = ("Vorbis", "vorbis", "Vorbis"),
        [CodecIds.Flac] = ("FLAC", "flac", "FLAC (Free Lossless Audio Codec)"),
        ["S_TEXT/UTF8"] = ("SubRip", "subrip", "SubRip subtitle"),
        ["S_TEXT/ASS"] = ("ASS", "ass", "ASS (Advanced SSA) subtitle"),
        ["S_TEXT/SSA"] = ("SSA", "ssa", "SSA (SubStation Alpha) subtitle"),
        ["S_TEXT/WEBVTT"] = ("WebVTT", "webvtt", "WebVTT subtitle"),
        ["D_WEBVTT/SUBTITLES"] = ("WebVTT", "webvtt", "WebVTT subtitle"),
        ["S_VOBSUB"] = ("VobSub", "dvd_subtitle", "DVD subtitles"),
        ["S_HDMV/PGS"] = ("PGS", "hdmv_pgs_subtitle", "HDMV Presentation Graphic Stream subtitles"),
    };

    /// <summary>The name the tool prints, such as VP9; the codec id itself when it is not a known one.</summary>
    public static string Name(string codecId) => Known.TryGetValue(codecId, out var known) ? known.Name : codecId;

    /// <summary>
    /// ffprobe's codec_name for a track, such as vp9 or pcm_s16le; <see langword="null"/> when there is no known name.
    /// PCM names carry the sample type, size and byte order, as ffprobe's do.
    /// </summary>
    public static string? FfprobeName(TrackInfo track)
    {
        if (track is AudioTrackInfo audio && track.CodecId is CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat)
        {
            int bits = audio.BitsPerSample > 0 ? audio.BitsPerSample : 16;
            string order = track.CodecId == CodecIds.PcmBigEndian ? "be" : "le";
            return track.CodecId == CodecIds.PcmFloat ? $"pcm_f{bits}{order}"
                : bits == 8 ? "pcm_u8"
                : $"pcm_s{bits}{order}";
        }

        return Known.TryGetValue(track.CodecId, out var known) ? known.Short : null;
    }

    /// <summary>ffprobe's codec_long_name, or <see langword="null"/>.</summary>
    public static string? FfprobeLongName(TrackInfo track)
    {
        return Known.TryGetValue(track.CodecId, out var known) ? known.LongName : null;
    }
}
