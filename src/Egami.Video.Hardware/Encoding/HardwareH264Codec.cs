using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Hardware.Decoding;

namespace Egami.Video.Hardware.Encoding;

/// <summary>
/// H.264 on the GPU's video engine: <see cref="CreateEncoder"/> encodes 8-bit 4:2:0 progressive streams in the Constrained
/// Baseline, Main and High profiles through Vulkan Video, and <see cref="CreateDecoder"/> decodes 8-bit 4:2:0 progressive streams.
/// </summary>
internal sealed class HardwareH264Codec : VideoCodec
{
    private HardwareH264Codec()
    {
    }

    public static HardwareH264Codec Instance { get; } = new();

    public override string Name => "H.264 (hardware)";

    public override string CodecId => CodecIds.Avc;

    public override bool CanEncode => HardwareVideoCodecs.IsAvailable(CodecIds.Avc, HardwareDirection.Encode);

    public override bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.CodecId == CodecIds.Avc
            && (track is not VideoTrackInfo video || ((video.BitDepth is 0 or 8) && video.ChromaFormat is null or ChromaFormat.Yuv420))
            && HardwareVideoCodecs.IsAvailable(CodecIds.Avc, HardwareDirection.Decode);
    }

    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        return new H264HardwareDecoder(track, options ?? new VideoDecoderOptions(), context);
    }

    public override IVideoEncoder CreateEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return new H264HardwareEncoder(options, context);
    }
}
