using Egami.Video.Containers.Hevc;

namespace Egami.Video.Containers;

/// <summary>
/// A raw HEVC stream (.h265, .265, .hevc): the Annex B byte stream that encoders such as x265 write, NAL units after start
/// codes with no header, index or timestamps. Readers group the NAL units into access units, one picture each, and give each as
/// a packet; access unit n is at n frames of the frame rate. The track's size, bit depth and chroma format come from the first
/// sequence parameter set. Reading only: to store HEVC, use MP4 or Matroska.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoHevc.cs" region="raw"/>
/// </example>
public sealed class HevcFormat : ContainerFormat
{
    private readonly Rational? _frameRate;

    /// <summary>Gets the format, timed by the stream's own frame rate or 25 frames a second.</summary>
    public static HevcFormat Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "HEVC Annex B";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".h265", ".265", ".hevc"];

    /// <inheritdoc/>
    public override int SignatureLength => 6;

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
    /// The file must start with a start code and then a video, sequence or picture parameter set, an access unit delimiter or a
    /// prefix SEI message of the base layer, as encoders write them; the two-byte NAL unit header tells these apart from an H.264
    /// stream's first unit.
    /// </remarks>
    public override bool IsMatch(ReadOnlySpan<byte> header)
    {
        int at = header is [0, 0, 1, ..] ? 3 : header is [0, 0, 0, 1, ..] ? 4 : -1;
        if (at < 0 || header.Length <= at + 1)
        {
            return false;
        }

        // forbidden_zero_bit 0, nal_unit_type 32 to 35 or 39, nuh_layer_id 0, nuh_temporal_id_plus1 1.
        return header[at] is 0x40 or 0x42 or 0x44 or 0x46 or 0x4E && header[at + 1] == 0x01;
    }

    /// <inheritdoc/>
    /// <exception cref="UnsupportedFormatException">The stream has no sequence parameter set Egami.Video.Hevc can read.</exception>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new HevcDemuxer(stream, context, leaveOpen, FrameRate, Name);
    }
}
