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
    /// bitrate, keyframe interval, B-frames and Annex B packets. Decoding comes in a later preview.
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
    /// </remarks>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="encode"/>
    /// </example>
    public static VideoCodec H264 => HardwareH264Codec.Instance;

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
