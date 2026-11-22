using Egami.Video.Codecs;

namespace Egami.Video.Hardware.Encoding;

/// <summary>
/// H.264 on the GPU's video engine: <see cref="CreateEncoder"/> encodes 8-bit 4:2:0 progressive streams in the Constrained
/// Baseline, Main and High profiles through Vulkan Video. Decoding comes in a later preview.
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
        return false;
    }

    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context) =>
        throw new NotSupportedException("Hardware H.264 decoding comes in a later preview of Egami.Video.Hardware.");

    public override IVideoEncoder CreateEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return new H264HardwareEncoder(options, context);
    }
}
