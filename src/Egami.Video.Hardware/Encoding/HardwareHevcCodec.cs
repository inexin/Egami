using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Hardware.Decoding;

namespace Egami.Video.Hardware.Encoding;

/// <summary>
/// HEVC on the GPU's video engine through Vulkan Video: <see cref="CreateEncoder"/> encodes and <see cref="CreateDecoder"/>
/// decodes 4:2:0 progressive streams in the Main and Main 10 profiles.
/// </summary>
internal sealed class HardwareHevcCodec : VideoCodec
{
    private HardwareHevcCodec()
    {
    }

    public static HardwareHevcCodec Instance { get; } = new();

    public override string Name => "HEVC (hardware)";

    public override string CodecId => CodecIds.Hevc;

    public override bool CanEncode => HardwareVideoCodecs.IsAvailable(CodecIds.Hevc, HardwareDirection.Encode);

    public override bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return (track.CodecId == CodecIds.Hevc
                || (track.CodecId == CodecIds.QuickTimeVideo && !HevcHardwareDecoder.DolbyVisionConfiguration(track.CodecPrivate).IsEmpty))
            && (track is not VideoTrackInfo video || ((video.BitDepth is 0 or 8 or 10) && video.ChromaFormat is null or ChromaFormat.Yuv420))
            && HardwareVideoCodecs.IsAvailable(CodecIds.Hevc, HardwareDirection.Decode);
    }

    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        options ??= new VideoDecoderOptions();
        if (options.ScaleDenominator != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ScaleDenominator, "HEVC decodes at full size only; the scale denominator must be 1.");
        }

        return new HevcHardwareDecoder(track, options, context);
    }

    public override IVideoEncoder CreateEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return new HevcHardwareEncoder(options, context);
    }
}
