using Egami.Video.Hardware.Vulkan.Native;

namespace Egami.Video.Hardware.Vulkan;

/// <summary>
/// The Vulkan instance and devices the hardware codecs share for the life of the process: making a device takes tens of
/// milliseconds, and one device serves every decoder and encoder on its GPU (each with its own command pool and session).
/// </summary>
internal static class VulkanVideoContext
{
    private const VkVideoCodecOperationFlagBitsKHR DecodeOperations =
        VkVideoCodecOperationFlagBitsKHR.DECODE_H264_BIT_KHR | VkVideoCodecOperationFlagBitsKHR.DECODE_H265_BIT_KHR;

    private const VkVideoCodecOperationFlagBitsKHR EncodeOperations =
        VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR | VkVideoCodecOperationFlagBitsKHR.ENCODE_H265_BIT_KHR;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<nint, VulkanDevice> Devices = [];
    private static VulkanInstance? _instance;
    private static string? _failure;

    /// <summary>
    /// Returns the device for a codec operation: on the first GPU, discrete ones first, with a queue for it and the codec's
    /// profile, made on first use and kept. The device has queues for every video operation its GPU offers.
    /// </summary>
    /// <param name="operation">The decode or encode operation.</param>
    /// <param name="profile">The codec profile the device must support, as the probe names it ("High", "Main 10").</param>
    /// <exception cref="HardwareVideoException">No GPU has the operation, or the driver refused the device.</exception>
    public static VulkanDevice GetDevice(VkVideoCodecOperationFlagBitsKHR operation, string profile)
    {
        lock (Gate)
        {
            if (_instance is null)
            {
                if (_failure is not null)
                {
                    throw new HardwareVideoException(_failure);
                }

                _instance = VulkanInstance.TryCreate(out string? reason);
                if (_instance is null)
                {
                    _failure = reason ?? "Vulkan is not available.";
                    throw new HardwareVideoException(_failure);
                }
            }

            (string codecId, HardwareDirection direction) = operation switch
            {
                VkVideoCodecOperationFlagBitsKHR.DECODE_H264_BIT_KHR => (CodecIds.Avc, HardwareDirection.Decode),
                VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR => (CodecIds.Avc, HardwareDirection.Encode),
                VkVideoCodecOperationFlagBitsKHR.DECODE_H265_BIT_KHR => (CodecIds.Hevc, HardwareDirection.Decode),
                _ => (CodecIds.Hevc, HardwareDirection.Encode),
            };
            VulkanPhysicalDevice[] physicals = _instance.EnumeratePhysicalDevices();
            foreach (VulkanPhysicalDevice physical in physicals.OrderBy(p => p.Type == VkPhysicalDeviceType.DISCRETE_GPU ? 0 : 1))
            {
                if (physical.FindVideoQueueFamily(operation) < 0
                    || !VulkanProbe.Describe(physical, out _).Profiles.Any(p => p.CodecId == codecId && p.Direction == direction && p.Profile == profile))
                {
                    continue;
                }

                if (!Devices.TryGetValue(physical.Handle, out VulkanDevice? device))
                {
                    device = VulkanDevice.Create(physical, DecodeOperations, EncodeOperations);
                    Devices.Add(physical.Handle, device);
                }

                return device;
            }

            throw new HardwareVideoException(
                $"No GPU {(direction == HardwareDirection.Decode ? "decodes" : "encodes")} {(codecId == CodecIds.Avc ? "H.264" : "HEVC")} {profile} in hardware. "
                + (HardwareVideoCodecs.UnavailableReason ?? string.Empty));
        }
    }
}
