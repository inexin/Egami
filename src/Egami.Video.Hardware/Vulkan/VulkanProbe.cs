using Egami.Video.Hardware.Vulkan.Native;

namespace Egami.Video.Hardware.Vulkan;

/// <summary>Asks every Vulkan device for its video decode and encode capabilities.</summary>
internal static unsafe class VulkanProbe
{
    /// <summary>A codec profile the probe asks about.</summary>
    private readonly record struct Candidate(
        VkVideoCodecOperationFlagBitsKHR Operation, string Extension, string CodecId, HardwareDirection Direction, string Profile,
        int ProfileIdc, int BitDepth);

    private static readonly Candidate[] Candidates =
    [
        new(VkVideoCodecOperationFlagBitsKHR.DECODE_H264_BIT_KHR, "VK_KHR_video_decode_h264", CodecIds.Avc, HardwareDirection.Decode, "High", (int)StdVideoH264ProfileIdc.HIGH, 8),
        new(VkVideoCodecOperationFlagBitsKHR.DECODE_H265_BIT_KHR, "VK_KHR_video_decode_h265", CodecIds.Hevc, HardwareDirection.Decode, "Main", (int)StdVideoH265ProfileIdc.MAIN, 8),
        new(VkVideoCodecOperationFlagBitsKHR.DECODE_H265_BIT_KHR, "VK_KHR_video_decode_h265", CodecIds.Hevc, HardwareDirection.Decode, "Main 10", (int)StdVideoH265ProfileIdc.MAIN_10, 10),
        new(VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR, "VK_KHR_video_encode_h264", CodecIds.Avc, HardwareDirection.Encode, "High", (int)StdVideoH264ProfileIdc.HIGH, 8),
        new(VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR, "VK_KHR_video_encode_h264", CodecIds.Avc, HardwareDirection.Encode, "Main", (int)StdVideoH264ProfileIdc.MAIN, 8),
        new(VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR, "VK_KHR_video_encode_h264", CodecIds.Avc, HardwareDirection.Encode, "Baseline", (int)StdVideoH264ProfileIdc.BASELINE, 8),
        new(VkVideoCodecOperationFlagBitsKHR.ENCODE_H265_BIT_KHR, "VK_KHR_video_encode_h265", CodecIds.Hevc, HardwareDirection.Encode, "Main", (int)StdVideoH265ProfileIdc.MAIN, 8),
        new(VkVideoCodecOperationFlagBitsKHR.ENCODE_H265_BIT_KHR, "VK_KHR_video_encode_h265", CodecIds.Hevc, HardwareDirection.Encode, "Main 10", (int)StdVideoH265ProfileIdc.MAIN_10, 10),
    ];

    /// <summary>Probes every device; never throws.</summary>
    /// <returns>The devices with at least one profile, and why there are none when the list is empty.</returns>
    public static (IReadOnlyList<HardwareVideoDevice> Devices, string? Reason) Run()
    {
        try
        {
            using VulkanInstance? instance = VulkanInstance.TryCreate(out string? reason);
            if (instance is null)
            {
                return ([], reason);
            }

            List<HardwareVideoDevice> found = [];
            List<string> skipped = [];
            foreach (VulkanPhysicalDevice physical in instance.EnumeratePhysicalDevices())
            {
                HardwareVideoDevice device = Describe(physical, out string? why);
                if (device.Profiles.Count > 0)
                {
                    found.Add(device);
                }
                else
                {
                    skipped.Add($"{physical.Name}: {why}");
                }
            }

            return found.Count > 0
                ? (found, null)
                : ([], skipped.Count == 0 ? "Vulkan reports no GPU." : "No GPU has hardware video Egami can use. " + string.Join(" ", skipped));
        }
        catch (Exception e) when (e is HardwareVideoException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return ([], e.Message);
        }
    }

    /// <summary>Describes one device and its video profiles; <paramref name="why"/> says why it has none.</summary>
    internal static HardwareVideoDevice Describe(VulkanPhysicalDevice physical, out string? why)
    {
        List<HardwareVideoProfile> profiles = [];
        why = null;
        if (physical.ApiVersion < VulkanInstance.RequiredApiVersion)
        {
            why = $"Vulkan {VulkanInstance.ToVersion(physical.ApiVersion)}; hardware video needs 1.3.";
        }
        else if (!physical.HasExtension("VK_KHR_video_queue"))
        {
            why = "the driver has no Vulkan Video (VK_KHR_video_queue).";
        }
        else
        {
            foreach (Candidate candidate in Candidates)
            {
                if (physical.HasExtension(candidate.Extension)
                    && physical.HasExtension(candidate.Direction == HardwareDirection.Decode ? "VK_KHR_video_decode_queue" : "VK_KHR_video_encode_queue")
                    && physical.FindVideoQueueFamily(candidate.Operation) >= 0
                    && Query(physical, candidate) is { } profile)
                {
                    profiles.Add(profile);
                }
            }

            if (profiles.Count == 0)
            {
                why = "no H.264 or HEVC profile is supported in 4:2:0.";
            }
        }

        HardwareDeviceKind kind = physical.Type switch
        {
            VkPhysicalDeviceType.INTEGRATED_GPU => HardwareDeviceKind.Integrated,
            VkPhysicalDeviceType.DISCRETE_GPU => HardwareDeviceKind.Discrete,
            VkPhysicalDeviceType.VIRTUAL_GPU => HardwareDeviceKind.Virtual,
            VkPhysicalDeviceType.CPU => HardwareDeviceKind.Cpu,
            _ => HardwareDeviceKind.Other,
        };
        return new HardwareVideoDevice(
            physical.Name, kind, (int)physical.VendorId, (int)physical.DeviceId, "Vulkan", VulkanInstance.ToVersion(physical.ApiVersion),
            physical.DriverName, physical.DriverInfo, profiles);
    }

    /// <summary>
    /// The profile chain for a codec profile: the codec-specific profile info chained to a <see cref="VkVideoProfileInfoKHR"/>, kept
    /// together so the pointers stay valid while the struct lives on the stack.
    /// </summary>
    internal struct ProfileChain
    {
        public VkVideoProfileInfoKHR Profile;
        public VkVideoDecodeH264ProfileInfoKHR DecodeH264;
        public VkVideoDecodeH265ProfileInfoKHR DecodeH265;
        public VkVideoEncodeH264ProfileInfoKHR EncodeH264;
        public VkVideoEncodeH265ProfileInfoKHR EncodeH265;

        /// <summary>Fills the chain for <paramref name="operation"/>; <paramref name="self"/> must point at this struct, pinned.</summary>
        public static void Fill(ProfileChain* self, VkVideoCodecOperationFlagBitsKHR operation, int profileIdc, int bitDepth)
        {
            *self = default;
            VkVideoComponentBitDepthFlagBitsKHR depth = bitDepth == 10 ? VkVideoComponentBitDepthFlagBitsKHR._10_BIT_KHR : VkVideoComponentBitDepthFlagBitsKHR._8_BIT_KHR;
            void* codec;
            switch (operation)
            {
                case VkVideoCodecOperationFlagBitsKHR.DECODE_H264_BIT_KHR:
                    self->DecodeH264 = new() { stdProfileIdc = (StdVideoH264ProfileIdc)profileIdc, pictureLayout = VkVideoDecodeH264PictureLayoutFlagBitsKHR.PROGRESSIVE_KHR };
                    codec = &self->DecodeH264;
                    break;
                case VkVideoCodecOperationFlagBitsKHR.DECODE_H265_BIT_KHR:
                    self->DecodeH265 = new() { stdProfileIdc = (StdVideoH265ProfileIdc)profileIdc };
                    codec = &self->DecodeH265;
                    break;
                case VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR:
                    self->EncodeH264 = new() { stdProfileIdc = (StdVideoH264ProfileIdc)profileIdc };
                    codec = &self->EncodeH264;
                    break;
                case VkVideoCodecOperationFlagBitsKHR.ENCODE_H265_BIT_KHR:
                    self->EncodeH265 = new() { stdProfileIdc = (StdVideoH265ProfileIdc)profileIdc };
                    codec = &self->EncodeH265;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }

            self->Profile = new()
            {
                pNext = codec,
                videoCodecOperation = operation,
                chromaSubsampling = VkVideoChromaSubsamplingFlagBitsKHR._420_BIT_KHR,
                lumaBitDepth = depth,
                chromaBitDepth = depth,
            };
        }
    }

    private static HardwareVideoProfile? Query(VulkanPhysicalDevice physical, Candidate candidate)
    {
        VkInstanceCommands vk = physical.Instance.Commands;
        ProfileChain chain;
        ProfileChain.Fill(&chain, candidate.Operation, candidate.ProfileIdc, candidate.BitDepth);

        VkVideoDecodeH264CapabilitiesKHR decodeH264 = new();
        VkVideoDecodeH265CapabilitiesKHR decodeH265 = new();
        VkVideoEncodeH264CapabilitiesKHR encodeH264 = new();
        VkVideoEncodeH265CapabilitiesKHR encodeH265 = new();
        VkVideoDecodeCapabilitiesKHR decode = new();
        VkVideoEncodeCapabilitiesKHR encode = new();
        VkVideoCapabilitiesKHR caps = new();
        bool isDecode = candidate.Direction == HardwareDirection.Decode;
        caps.pNext = isDecode ? &decode : &encode;
        switch (candidate.Operation)
        {
            case VkVideoCodecOperationFlagBitsKHR.DECODE_H264_BIT_KHR:
                decode.pNext = &decodeH264;
                break;
            case VkVideoCodecOperationFlagBitsKHR.DECODE_H265_BIT_KHR:
                decode.pNext = &decodeH265;
                break;
            case VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR:
                encode.pNext = &encodeH264;
                break;
            default:
                encode.pNext = &encodeH265;
                break;
        }

        if (vk.vkGetPhysicalDeviceVideoCapabilitiesKHR(physical.Handle, &chain.Profile, &caps) < 0)
        {
            return null;
        }

        // The picture format the codecs will use must be offered: NV12 for 8-bit, P010 for 10-bit.
        VkFormat format = candidate.BitDepth == 10 ? VkFormat.G10X6_B10X6R10X6_2PLANE_420_UNORM_3PACK16 : VkFormat.G8_B8R8_2PLANE_420_UNORM;
        VkImageUsageFlagBits usage = isDecode ? VkImageUsageFlagBits.VIDEO_DECODE_DST_BIT_KHR : VkImageUsageFlagBits.VIDEO_ENCODE_SRC_BIT_KHR;
        if (!HasFormat(physical, &chain.Profile, usage, format))
        {
            return null;
        }

        string level = candidate.Operation switch
        {
            VkVideoCodecOperationFlagBitsKHR.DECODE_H264_BIT_KHR => Level(decodeH264.maxLevelIdc.ToString()),
            VkVideoCodecOperationFlagBitsKHR.DECODE_H265_BIT_KHR => Level(decodeH265.maxLevelIdc.ToString()),
            VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR => Level(encodeH264.maxLevelIdc.ToString()),
            _ => Level(encodeH265.maxLevelIdc.ToString()),
        };
        bool h264 = candidate.Operation == VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR;
        HardwareRateControl rateControl = HardwareRateControl.None;
        if (!isDecode)
        {
            rateControl |= (encode.rateControlModes & VkVideoEncodeRateControlModeFlagBitsKHR.DISABLED_BIT_KHR) != 0 ? HardwareRateControl.ConstantQp : 0;
            rateControl |= (encode.rateControlModes & VkVideoEncodeRateControlModeFlagBitsKHR.CBR_BIT_KHR) != 0 ? HardwareRateControl.ConstantBitrate : 0;
            rateControl |= (encode.rateControlModes & VkVideoEncodeRateControlModeFlagBitsKHR.VBR_BIT_KHR) != 0 ? HardwareRateControl.VariableBitrate : 0;
        }

        return new HardwareVideoProfile(candidate.CodecId, candidate.Direction, candidate.Profile, candidate.BitDepth)
        {
            MinWidth = (int)caps.minCodedExtent.width,
            MinHeight = (int)caps.minCodedExtent.height,
            MaxWidth = (int)caps.maxCodedExtent.width,
            MaxHeight = (int)caps.maxCodedExtent.height,
            MaxLevel = level,
            MaxReferences = (int)caps.maxActiveReferencePictures,
            RateControl = rateControl,
            QualityLevels = isDecode ? 0 : (int)encode.maxQualityLevels,
            MaxBitrate = isDecode ? 0 : (long)Math.Min(encode.maxBitrate, long.MaxValue),
            MaxForwardReferences = isDecode ? 0 : (int)(h264 ? encodeH264.maxPPictureL0ReferenceCount : encodeH265.maxPPictureL0ReferenceCount),
            MaxBackwardReferences = isDecode ? 0 : (int)(h264 ? encodeH264.maxL1ReferenceCount : encodeH265.maxL1ReferenceCount),
        };
    }

    /// <summary>Whether the device offers <paramref name="format"/> for pictures of the profile with <paramref name="usage"/>.</summary>
    internal static bool HasFormat(VulkanPhysicalDevice physical, VkVideoProfileInfoKHR* profile, VkImageUsageFlagBits usage, VkFormat format)
    {
        VkInstanceCommands vk = physical.Instance.Commands;
        VkVideoProfileListInfoKHR list = new() { profileCount = 1, pProfiles = profile };
        VkPhysicalDeviceVideoFormatInfoKHR info = new() { pNext = &list, imageUsage = usage };
        uint count = 0;
        if (vk.vkGetPhysicalDeviceVideoFormatPropertiesKHR(physical.Handle, &info, &count, null) < 0 || count == 0)
        {
            return false;
        }

        VkVideoFormatPropertiesKHR[] formats = new VkVideoFormatPropertiesKHR[count];
        for (int i = 0; i < count; i++)
        {
            formats[i] = new VkVideoFormatPropertiesKHR();
        }

        fixed (VkVideoFormatPropertiesKHR* p = formats)
        {
            if (vk.vkGetPhysicalDeviceVideoFormatPropertiesKHR(physical.Handle, &info, &count, p) < 0)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (p[i].format == format)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Turns a level enum member name such as <c>_5_2</c> into "5.2".</summary>
    private static string Level(string member) => member.TrimStart('_').Replace('_', '.');
}
