using Egami.Video.Containers.H264;

namespace Egami.Video.Containers;

/// <summary>
/// A raw H.264 stream (.h264, .264, .avc): the Annex B byte stream that encoders such as x264 write, NAL units after start codes
/// with no header, index or timestamps. Readers group the NAL units into access units, one picture each, and give each as a
/// packet; access unit n is at n frames of the frame rate. The track's size, bit depth and chroma format come from the first
/// sequence parameter set. Reading only: to store H.264, use MP4 or Matroska.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoH264.cs" region="raw"/>
/// </example>
public sealed class H264Format : ContainerFormat
{
    private readonly Rational? _frameRate;

    /// <summary>Gets the format, timed by the stream's own frame rate or 25 frames a second.</summary>
    public static H264Format Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "H.264 Annex B";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".h264", ".264", ".avc"];

    /// <inheritdoc/>
    public override int SignatureLength => 5;

    /// <summary>
    /// Gets the frame rate readers time the access units with, as the file has no timestamps, or <see langword="null"/> for the
    /// rate the sequence parameter set's timing information gives, and 25 frames a second when it gives none (as FFmpeg reads
    /// such files). Register a format with another rate through <see cref="MediaContext.WithContainer"/>, or open it directly.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive.</exception>
    public Rational? FrameRate
    {
        get => _frameRate;
        init
        {
            if (value is { } rate && (rate.Numerator <= 0 || rate.Denominator <= 0))
            {
                throw new ArgumentOutOfRangeException(nameof(value), rate, "The frame rate must be positive.");
            }

            _frameRate = value;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The file must start with a start code and then a sequence parameter set, an access unit delimiter or an SEI message, as
    /// encoders write them; the NAL unit header rules tell these apart from an HEVC stream's first unit.
    /// </remarks>
    public override bool IsMatch(ReadOnlySpan<byte> header)
    {
        int at = header is [0, 0, 1, ..] ? 3 : header is [0, 0, 0, 1, ..] ? 4 : -1;
        if (at < 0 || header.Length <= at)
        {
            return false;
        }

        byte nal = header[at];
        return (nal & 0x80) == 0 && ((nal & 31) == 7 || nal is 0x09 or 0x06);
    }

    /// <inheritdoc/>
    /// <exception cref="UnsupportedFormatException">The stream has no sequence parameter set Egami.Video.H264 can read.</exception>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new H264Demuxer(stream, context, leaveOpen, FrameRate, Name);
    }
}
