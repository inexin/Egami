namespace Egami.Video;

/// <summary>
/// Matroska codec ids (from the Matroska codec mapping) for the codecs Egami.Video names. <see cref="TrackInfo.CodecId"/>
/// uses these for every container.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-decode"/>
/// </example>
public static class CodecIds
{
    /// <summary>Motion JPEG: every frame a complete JPEG image.</summary>
    public const string MotionJpeg = "V_MJPEG";

    /// <summary>Any Video for Windows codec; <see cref="TrackInfo.CodecPrivate"/> holds its BITMAPINFOHEADER.</summary>
    public const string VfwFourCC = "V_MS/VFW/FOURCC";

    /// <summary>Uncompressed video; the Matroska track's ColourSpace holds the FourCC.</summary>
    public const string Uncompressed = "V_UNCOMPRESSED";

    /// <summary>VP8.</summary>
    public const string Vp8 = "V_VP8";

    /// <summary>VP9.</summary>
    public const string Vp9 = "V_VP9";

    /// <summary>AV1.</summary>
    public const string Av1 = "V_AV1";

    /// <summary>FFV1 (RFC 9043); for version 3 <see cref="TrackInfo.CodecPrivate"/> holds its configuration record.</summary>
    public const string Ffv1 = "V_FFV1";

    /// <summary>H.264 (MPEG-4 AVC); <see cref="TrackInfo.CodecPrivate"/> holds its decoder configuration record (avcC). Passed through, not decoded.</summary>
    public const string Avc = "V_MPEG4/ISO/AVC";

    /// <summary>HEVC (H.265); <see cref="TrackInfo.CodecPrivate"/> holds its decoder configuration record (hvcC). Passed through, not decoded.</summary>
    public const string Hevc = "V_MPEGH/ISO/HEVC";

    /// <summary>MPEG-4 Part 2 video; <see cref="TrackInfo.CodecPrivate"/> holds its decoder specific info. Passed through, not decoded.</summary>
    public const string Mpeg4Visual = "V_MPEG4/ISO/ASP";

    /// <summary>
    /// Any other MP4 or QuickTime video: <see cref="TrackInfo.CodecPrivate"/> holds the whole sample description (stsd entry),
    /// which an MP4 writer stores back unchanged. Matroska's V_QUICKTIME.
    /// </summary>
    public const string QuickTimeVideo = "V_QUICKTIME";

    /// <summary>Any other MP4 or QuickTime audio, its whole sample description in <see cref="TrackInfo.CodecPrivate"/>. Matroska's A_QUICKTIME.</summary>
    public const string QuickTimeAudio = "A_QUICKTIME";

    /// <summary>Any other MP4 or QuickTime track (timecode, data, other subtitles), its whole sample description in <see cref="TrackInfo.CodecPrivate"/>.</summary>
    public const string QuickTimeOther = "D_QUICKTIME";

    /// <summary>
    /// 3GPP timed text (mov_text, tx3g): MP4's subtitles. <see cref="TrackInfo.CodecPrivate"/> holds the text sample entry after
    /// its 8-byte header; <see cref="MovTextSample"/> reads and writes the samples.
    /// </summary>
    public const string MovText = "S_TX3G";

    /// <summary>
    /// WebVTT in MP4 (wvtt, ISO/IEC 14496-30): <see cref="TrackInfo.CodecPrivate"/> holds the sample entry after its 8-byte header
    /// (the vttC box with the file header); each sample holds the cues shown over its time. <see cref="Subtitles.SubtitleDocument.FromPackets"/>
    /// reads the track and <see cref="Subtitles.SubtitleTrackWriter"/> writes one.
    /// </summary>
    public const string Mp4WebVtt = "S_WVTT";

    /// <summary>SRT-style text subtitles (SubRip): each packet one cue's text, with <c>&lt;i&gt;</c>-style tags.</summary>
    public const string SubRip = "S_TEXT/UTF8";

    /// <summary>
    /// WebVTT in Matroska: <see cref="TrackInfo.CodecPrivate"/> holds the file header, each packet a cue's text, and a block
    /// addition the cue's settings and identifier.
    /// </summary>
    public const string WebVtt = "S_TEXT/WEBVTT";

    /// <summary>WebVTT in WebM: each packet holds the cue's identifier line, settings line and text.</summary>
    public const string WebMWebVtt = "D_WEBVTT/SUBTITLES";

    /// <summary>
    /// ASS in Matroska: <see cref="TrackInfo.CodecPrivate"/> holds the script header up to the [Events] format line; each packet
    /// is an event without its times (ReadOrder, Layer, Style, Name, MarginL, MarginR, MarginV, Effect, Text).
    /// </summary>
    public const string Ass = "S_TEXT/ASS";

    /// <summary>SSA in Matroska, laid out as <see cref="Ass"/> with Marked in place of Layer.</summary>
    public const string Ssa = "S_TEXT/SSA";

    /// <summary>Integer PCM, little-endian.</summary>
    public const string PcmLittleEndian = "A_PCM/INT/LIT";

    /// <summary>Integer PCM, big-endian.</summary>
    public const string PcmBigEndian = "A_PCM/INT/BIG";

    /// <summary>IEEE float PCM.</summary>
    public const string PcmFloat = "A_PCM/FLOAT/IEEE";

    /// <summary>MPEG audio layer III (MP3).</summary>
    public const string Mp3 = "A_MPEG/L3";

    /// <summary>MPEG audio layer II.</summary>
    public const string Mp2 = "A_MPEG/L2";

    /// <summary>MPEG audio layer I.</summary>
    public const string Mp1 = "A_MPEG/L1";

    /// <summary>Dolby AC-3 (ATSC A/52).</summary>
    public const string Ac3 = "A_AC3";

    /// <summary>
    /// Dolby E-AC-3 (ATSC A/52 Annex E), each packet an independent frame and its substreams; from MP4, <see cref="TrackInfo.CodecPrivate"/>
    /// holds the dec3 box body. Passed through, not decoded.
    /// </summary>
    public const string EAc3 = "A_EAC3";

    /// <summary>AAC.</summary>
    public const string Aac = "A_AAC";

    /// <summary>Opus.</summary>
    public const string Opus = "A_OPUS";

    /// <summary>Vorbis.</summary>
    public const string Vorbis = "A_VORBIS";

    /// <summary>FLAC.</summary>
    public const string Flac = "A_FLAC";

    /// <summary>
    /// Apple Lossless (ALAC); <see cref="TrackInfo.CodecPrivate"/> holds its 24-byte ALACSpecificConfig (frame length, bit depth,
    /// Rice parameters, channels, rate), as Matroska stores it.
    /// </summary>
    public const string Alac = "A_ALAC";

    /// <summary>
    /// G.711 μ-law (ITU-T G.711), one byte per sample. Matroska has no id of its own for it, so this one is Egami.Video's and
    /// Matroska writers refuse it.
    /// </summary>
    public const string G711MuLaw = "X_G711/MULAW";

    /// <summary>G.711 A-law (ITU-T G.711), one byte per sample; like <see cref="G711MuLaw"/>, an Egami.Video id.</summary>
    public const string G711ALaw = "X_G711/ALAW";

    /// <summary>Any Audio Compression Manager codec; <see cref="TrackInfo.CodecPrivate"/> holds its WAVEFORMATEX.</summary>
    public const string AcmAudio = "A_MS/ACM";
}
