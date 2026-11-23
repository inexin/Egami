using Egami.Video.Codecs;

namespace Egami.Video.Hardware.Integration;

/// <summary>
/// A codec that decodes, and encodes when asked to, on the GPU, and hands everything the GPU cannot do to the software codec it
/// replaced in the context: tracks the hardware turns down, streams it refuses at their first picture, scaled decoding, and
/// encoder options or sizes it cannot code.
/// </summary>
internal sealed class HardwareFallbackCodec : VideoCodec
{
    public HardwareFallbackCodec(VideoCodec hardware, VideoCodec? software, HardwareUse use)
    {
        Hardware = hardware;
        Software = software;
        Use = use;
    }

    /// <summary>Gets the hardware codec.</summary>
    public VideoCodec Hardware { get; }

    /// <summary>Gets the software codec the context had for the codec id, if any.</summary>
    public VideoCodec? Software { get; }

    /// <summary>Gets what the hardware is used for.</summary>
    public HardwareUse Use { get; }

    public override string Name => Software is null ? Hardware.Name : $"{Hardware.Name}, else {Software.Name}";

    public override string CodecId => Hardware.CodecId;

    public override bool CanEncode => (Use.HasFlag(HardwareUse.Encode) && Hardware.CanEncode) || Software?.CanEncode == true;

    public override bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return (Use.HasFlag(HardwareUse.Decode) && Hardware.CanDecode(track)) || Software?.CanDecode(track) == true;
    }

    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        bool hardware = Use.HasFlag(HardwareUse.Decode) && Hardware.CanDecode(track) && (options?.ScaleDenominator ?? 1) == 1;
        if (!hardware)
        {
            return (Software ?? Hardware).CreateDecoder(track, options, context);
        }

        if (Software is null || !Software.CanDecode(track))
        {
            return Hardware.CreateDecoder(track, options, context);
        }

        try
        {
            return new FallbackVideoDecoder(Hardware.CreateDecoder(track, options, context), () => Software.CreateDecoder(track, options, context));
        }
        catch (Exception e) when (e is HardwareVideoException or NotSupportedException or UnsupportedFormatException)
        {
            return Software.CreateDecoder(track, options, context);
        }
    }

    public override IVideoEncoder CreateEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        bool software = Software?.CanEncode == true;
        bool hardware = Use.HasFlag(HardwareUse.Encode) && Hardware.CanEncode && !(software && options.FirstPassStats is not null);
        if (!hardware)
        {
            return (software ? Software! : Hardware).CreateEncoder(options, context);
        }

        if (!software)
        {
            return Hardware.CreateEncoder(options, context);
        }

        try
        {
            return Hardware.CreateEncoder(options, context);
        }
        catch (Exception e) when (e is HardwareVideoException or NotSupportedException)
        {
            return Software!.CreateEncoder(options, context);
        }
    }
}
