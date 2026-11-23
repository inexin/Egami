using Egami.Video.Codecs;
using Egami.Video.Hardware.Encoding;
using Egami.Video.Hardware.Vulkan;

namespace Egami.Video.Hardware;

/// <summary>
/// Finds the hardware video codecs of the machine's GPUs. The first call asks the GPU driver through Vulkan Video and keeps the
/// answer for the life of the process; it never throws, and on a machine without a usable GPU or driver it finds nothing.
/// </summary>
/// <remarks>
/// <para>
/// Hardware decoding and encoding go through the Vulkan loader the GPU driver installs (<c>vulkan-1.dll</c> on Windows,
/// <c>libvulkan.so.1</c> on Linux). Egami ships no native code. The driver needs Vulkan 1.3 and the <c>VK_KHR_video_queue</c>
/// extensions; current NVIDIA, AMD and Intel drivers have them. Egami.Video's software codecs stay the default.
/// </para>
/// <para>
/// The probe lists H.264 (High to decode; High, Main and Baseline to encode) and HEVC (Main and Main 10), all
/// in 4:2:0.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="available"/>
/// </example>
public static class HardwareVideoCodecs
{
    private static readonly Lazy<(IReadOnlyList<HardwareVideoDevice> Devices, string? Reason)> Probed =
        new(VulkanProbe.Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the hardware H.264 codec: its <see cref="VideoCodec.CreateEncoder"/> encodes on the GPU through Vulkan Video, 8-bit
    /// 4:2:0 in the Constrained Baseline, Main and High profiles. Hand it frames as Egami's software encoder takes them, with the
    /// same <see cref="VideoEncoderOptions"/> and <see cref="H264EncoderOptions"/>: quality, quantizer, constant and variable
    /// bitrate, keyframe interval, B-frames and Annex B packets. Its <see cref="VideoCodec.CreateDecoder"/> decodes progressive
    /// 8-bit 4:2:0 streams on the GPU to the same samples as Egami's software decoder; add it to a context with
    /// <see cref="MediaContext.WithCodec(VideoCodec)"/> to have <see cref="VideoReader"/> use it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="VideoEncoderOptions.Effort"/> picks the driver's quality level: from effort 4 (the default) its best, lower
    /// efforts its faster levels. Quality maps to the quantizer on the software encoder's scale (Quality 60 is QP 17), with I
    /// and B pictures spaced from P pictures as the driver prefers (2 finer and 2 coarser where it doesn't say). B-frames are
    /// off unless <see cref="H264EncoderOptions.BFrames"/> asks for them, since some GPUs predict B pictures from the earlier
    /// reference only and they then cost more than P pictures; they need <see cref="LatencyMode.Quality"/>.
    /// <see cref="LatencyMode.Realtime"/> tunes the driver for low latency.
    /// The driver writes the SPS and PPS from Egami's values: the level, the VUI with the frames' color and frame rate, and the
    /// entropy coder, which drivers may change.
    /// </para>
    /// <para>
    /// Scene cuts do not start keyframes (keyframes come at <see cref="VideoEncoderOptions.KeyframeInterval"/> and where
    /// <see cref="EncodeFrameOptions.ForceKeyframe"/> asks), adaptive quantization and first-pass statistics are not used, and
    /// HDR SEI messages are not written (the track carries the HDR metadata). The output differs between GPUs and drivers.
    /// Creating an encoder throws <see cref="HardwareVideoException"/> when no GPU encodes H.264, and
    /// <see cref="NotSupportedException"/> when the GPU cannot code the size, level or rate control asked for.
    /// </para>
    /// <para>
    /// The decoder reads the stream with Egami's own H.264 parser, keeps the decoded picture buffer, and has the GPU decode each
    /// picture and copy it back; frames come out in output order as <see cref="YuvRaster{T}"/> of bytes, cropped, with the
    /// color the software decoder gives. Field pictures, MBAFF frames, monochrome, 4:2:2, streams above 8 bits and streams whose
    /// size, level or reference pictures go beyond the GPU throw <see cref="UnsupportedFormatException"/> when their first
    /// picture comes; <see cref="VideoCodec.CanDecode"/> turns down
    /// tracks whose bit depth or chroma format says so up front.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="encode"/>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="decode"/>
    /// </example>
    public static VideoCodec H264 => HardwareH264Codec.Instance;

    /// <summary>
    /// Gets the hardware HEVC codec: its <see cref="VideoCodec.CreateEncoder"/> encodes 4:2:0 HEVC in the Main and Main 10
    /// profiles on the GPU with the options of Egami's software HEVC encoder, and its <see cref="VideoCodec.CreateDecoder"/>
    /// decodes 4:2:0 Main and Main 10 streams to the same samples as Egami's software decoder; add it to a context with
    /// <see cref="MediaContext.WithCodec(VideoCodec)"/> to have <see cref="VideoReader"/> use it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The encoder takes <see cref="VideoEncoderOptions"/> and <see cref="HevcEncoderOptions"/> as the software encoder does:
    /// <see cref="VideoEncoderOptions.Quality"/> or <see cref="VideoEncoderOptions.Quantizer"/> give a constant QP on the
    /// software encoder's scale, the bitrate modes use the driver's rate control, and <see cref="VideoEncoderOptions.Effort"/>
    /// picks the driver's quality level (4, the default, is its best). YUV frames are coded as they are and RGB frames
    /// converted to BT.601. The driver writes the VPS, SPS and PPS from Egami's values. B-frames are written only where
    /// <see cref="HevcEncoderOptions.BFrames"/> asks and the GPU codes B pictures. Without a
    /// <see cref="HevcEncoderOptions.Profile"/>, frames of 16-bit planes are coded as Main 10 and the rest as Main, as by the
    /// software encoder. First-pass statistics throw; scene cuts do not start keyframes, and HDR SEI messages are not written
    /// (the track carries the HDR metadata). The output differs between GPUs and drivers. Creating an encoder throws
    /// <see cref="HardwareVideoException"/> when no GPU encodes HEVC, and <see cref="NotSupportedException"/> when the GPU
    /// cannot code the profile, size, level or rate control asked for.
    /// </para>
    /// <para>
    /// The decoder reads the stream with Egami's own HEVC parser, keeps the decoded picture buffer with its reference picture
    /// sets, and has the GPU decode each picture and copy it back. Frames come out in output order at the conformance window,
    /// with the color the software decoder gives: <see cref="YuvRaster{T}"/> of bytes for 8-bit streams and of
    /// <see cref="ushort"/> holding 10 bits for Main 10. Monochrome, 4:2:2, 4:4:4, 12-bit streams and the coding tools of the
    /// format range extensions throw <see cref="UnsupportedFormatException"/> when their first picture comes, as do streams
    /// whose size, level or reference pictures go beyond the GPU;
    /// <see cref="VideoCodec.CanDecode"/> turns down tracks whose bit depth or chroma format already rules them out. As in the
    /// software decoder, decoding starts at an IRAP picture, the leading pictures a CRA picture skips at the start are not
    /// output, and layers above the base (Dolby Vision enhancement layers) are skipped.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="encodehevc"/>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="decodehevc"/>
    /// </example>
    public static VideoCodec Hevc => HardwareHevcCodec.Instance;

    /// <summary>
    /// Gets why <see cref="GetDevices"/> found no device with hardware video, such as a missing Vulkan loader or a driver without
    /// Vulkan Video; <see langword="null"/> when a device was found.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="devices"/>
    /// </example>
    public static string? UnavailableReason => Probed.Value.Reason;

    /// <summary>Returns the GPUs with hardware video codecs Egami can use, and their profiles.</summary>
    /// <returns>The devices, in the driver's order; empty when there are none.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="devices"/>
    /// </example>
    public static IReadOnlyList<HardwareVideoDevice> GetDevices() => Probed.Value.Devices;

    /// <summary>Returns whether some GPU on this machine decodes or encodes <paramref name="codecId"/> in hardware.</summary>
    /// <param name="codecId">The codec, as a <see cref="CodecIds"/> value: <see cref="CodecIds.Avc"/> or <see cref="CodecIds.Hevc"/>.</param>
    /// <param name="direction">Decode or encode.</param>
    /// <returns><see langword="true"/> when a device has a profile of the codec in that direction.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="codecId"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="available"/>
    /// </example>
    public static bool IsAvailable(string codecId, HardwareDirection direction)
    {
        ArgumentNullException.ThrowIfNull(codecId);
        foreach (HardwareVideoDevice device in GetDevices())
        {
            foreach (HardwareVideoProfile profile in device.Profiles)
            {
                if (profile.Direction == direction && string.Equals(profile.CodecId, codecId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
