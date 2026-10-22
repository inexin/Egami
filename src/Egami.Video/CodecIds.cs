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

    /// <summary>Dolby AC-3.</summary>
    public const string Ac3 = "A_AC3";

    /// <summary>AAC.</summary>
    public const string Aac = "A_AAC";

    /// <summary>Opus.</summary>
    public const string Opus = "A_OPUS";

    /// <summary>Vorbis.</summary>
    public const string Vorbis = "A_VORBIS";

    /// <summary>FLAC.</summary>
    public const string Flac = "A_FLAC";

    /// <summary>Any Audio Compression Manager codec; <see cref="TrackInfo.CodecPrivate"/> holds its WAVEFORMATEX.</summary>
    public const string AcmAudio = "A_MS/ACM";
}
