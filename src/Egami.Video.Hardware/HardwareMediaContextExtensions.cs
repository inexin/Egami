using Egami.Video.Codecs;
using Egami.Video.Hardware.Integration;

namespace Egami.Video.Hardware;

/// <summary>Registers the GPU's video codecs with a <see cref="MediaContext"/>, with the software codecs as the fallback.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="withhardware"/>
/// </example>
public static class HardwareMediaContextExtensions
{
    /// <summary>
    /// Returns a copy of <paramref name="context"/> that decodes H.264 and HEVC on the GPU, and encodes them there when
    /// <paramref name="use"/> includes <see cref="HardwareUse.Encode"/>, keeping the context's software codecs for whatever
    /// the GPU cannot do. Call it after <c>WithH264</c> and <c>WithHevc</c>: it wraps the codecs registered for those ids,
    /// and a later registration replaces it. Without a GPU with Vulkan Video the context is returned as it is.
    /// </summary>
    /// <remarks>
    /// Decoding moves to the software codec for tracks the hardware turns down up front (a bit depth or chroma format it does
    /// not decode), for scaled decoding, and for streams it refuses at their first picture: the packets sent until then are
    /// sent to the software decoder again. Frames are the same either way. Encoding, when asked for, moves to the software
    /// codec for options the hardware does not take (<see cref="VideoEncoderOptions.FirstPassStats"/>) and when the GPU
    /// cannot code the profile, size, level or rate control; hardware output differs from the software encoders' and is
    /// bigger at the same quality, so encoding stays in software unless asked.
    /// </remarks>
    /// <param name="context">The context to add the hardware codecs to.</param>
    /// <param name="use">What to use the GPU for: <see cref="HardwareUse.Decode"/>, the default, or also encoding.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static MediaContext WithHardwareCodecs(this MediaContext context, HardwareUse use = HardwareUse.Decode)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (VideoCodec hardware in (ReadOnlySpan<VideoCodec>)[HardwareVideoCodecs.H264, HardwareVideoCodecs.Hevc])
        {
            bool decode = use.HasFlag(HardwareUse.Decode) && HardwareVideoCodecs.IsAvailable(hardware.CodecId, HardwareDirection.Decode);
            bool encode = use.HasFlag(HardwareUse.Encode) && HardwareVideoCodecs.IsAvailable(hardware.CodecId, HardwareDirection.Encode);
            if (!decode && !encode)
            {
                continue;
            }

            VideoCodec? software = context.Codecs.FirstOrDefault(c => string.Equals(c.CodecId, hardware.CodecId, StringComparison.Ordinal));
            if (software is HardwareFallbackCodec earlier)
            {
                software = earlier.Software;
            }

            context = context.WithCodec(new HardwareFallbackCodec(hardware, software, (decode ? HardwareUse.Decode : 0) | (encode ? HardwareUse.Encode : 0)));
        }

        return context;
    }
}
