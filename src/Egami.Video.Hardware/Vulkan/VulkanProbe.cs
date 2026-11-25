using System.Globalization;

using Vortice.Vulkan;

namespace Egami.Video.Hardware.Vulkan;

/// <summary>Asks every Vulkan device for its video decode and encode capabilities.</summary>
internal static unsafe class VulkanProbe
{
    /// <summary>The levels in the order of <c>StdVideoH264LevelIdc</c>.</summary>
    internal static readonly string[] H264Levels =
        ["1.0", "1.1", "1.2", "1.3", "2.0", "2.1", "2.2", "3.0", "3.1", "3.2", "4.0", "4.1", "4.2", "5.0", "5.1", "5.2", "6.0", "6.1", "6.2"];

    /// <summary>The levels in the order of <c>StdVideoH265LevelIdc</c>.</summary>
    internal static readonly string[] H265Levels = ["1.0", "2.0", "2.1", "3.0", "3.1", "4.0", "4.1", "5.0", "5.1", "5.2", "6.0", "6.1", "6.2"];

    /// <summary>A codec profile the probe asks about.</summary>
    private readonly record struct Candidate(
        VkVideoCodecOperationFlagsKHR Operation, string Extension, string CodecId, HardwareDirection Direction, string Profile,
        int ProfileIdc, int BitDepth);

    private static readonly Candidate[] Candidates =
    [
        new(VkVideoCodecOperationFlagsKHR.DecodeH264, "VK_KHR_video_decode_h264", CodecIds.Avc, HardwareDirection.Decode, "High", (int)StdVideoH264ProfileIdc.High, 8),
        new(VkVideoCodecOperationFlagsKHR.DecodeH265, "VK_KHR_video_decode_h265", CodecIds.Hevc, HardwareDirection.Decode, "Main", (int)StdVideoH265ProfileIdc.Main, 8),
        new(VkVideoCodecOperationFlagsKHR.DecodeH265, "VK_KHR_video_decode_h265", CodecIds.Hevc, HardwareDirection.Decode, "Main 10", (int)StdVideoH265ProfileIdc.Main10, 10),
        new(VkVideoCodecOperationFlagsKHR.EncodeH264, "VK_KHR_video_encode_h264", CodecIds.Avc, HardwareDirection.Encode, "High", (int)StdVideoH264ProfileIdc.High, 8),
        new(VkVideoCodecOperationFlagsKHR.EncodeH264, "VK_KHR_video_encode_h264", CodecIds.Avc, HardwareDirection.Encode, "Main", (int)StdVideoH264ProfileIdc.Main, 8),
        new(VkVideoCodecOperationFlagsKHR.EncodeH264, "VK_KHR_video_encode_h264", CodecIds.Avc, HardwareDirection.Encode, "Baseline", (int)StdVideoH264ProfileIdc.Baseline, 8),
        new(VkVideoCodecOperationFlagsKHR.EncodeH265, "VK_KHR_video_encode_h265", CodecIds.Hevc, HardwareDirection.Encode, "Main", (int)StdVideoH265ProfileIdc.Main, 8),
        new(VkVideoCodecOperationFlagsKHR.EncodeH265, "VK_KHR_video_encode_h265", CodecIds.Hevc, HardwareDirection.Encode, "Main 10", (int)StdVideoH265ProfileIdc.Main10, 10),
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
            VkPhysicalDeviceType.IntegratedGpu => HardwareDeviceKind.Integrated,
            VkPhysicalDeviceType.DiscreteGpu => HardwareDeviceKind.Discrete,
            VkPhysicalDeviceType.VirtualGpu => HardwareDeviceKind.Virtual,
            VkPhysicalDeviceType.Cpu => HardwareDeviceKind.Cpu,
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
        public static void Fill(ProfileChain* self, VkVideoCodecOperationFlagsKHR operation, int profileIdc, int bitDepth)
        {
            *self = default;
            VkVideoComponentBitDepthFlagsKHR depth = bitDepth == 10 ? VkVideoComponentBitDepthFlagsKHR.Depth10 : VkVideoComponentBitDepthFlagsKHR.Depth8;
            void* codec;
            switch (operation)
            {
                case VkVideoCodecOperationFlagsKHR.DecodeH264:
                    self->DecodeH264 = new() { stdProfileIdc = (StdVideoH264ProfileIdc)profileIdc, pictureLayout = VkVideoDecodeH264PictureLayoutFlagsKHR.Progressive };
                    codec = &self->DecodeH264;
                    break;
                case VkVideoCodecOperationFlagsKHR.DecodeH265:
                    self->DecodeH265 = new() { stdProfileIdc = (StdVideoH265ProfileIdc)profileIdc };
                    codec = &self->DecodeH265;
                    break;
                case VkVideoCodecOperationFlagsKHR.EncodeH264:
                    self->EncodeH264 = new() { stdProfileIdc = (StdVideoH264ProfileIdc)profileIdc };
                    codec = &self->EncodeH264;
                    break;
                case VkVideoCodecOperationFlagsKHR.EncodeH265:
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
                chromaSubsampling = VkVideoChromaSubsamplingFlagsKHR.Subsampling420,
                lumaBitDepth = depth,
                chromaBitDepth = depth,
            };
        }
    }

    private static HardwareVideoProfile? Query(VulkanPhysicalDevice physical, Candidate candidate)
    {
        VkInstanceApi vk = physical.Instance.Commands;
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
            case VkVideoCodecOperationFlagsKHR.DecodeH264:
                decode.pNext = &decodeH264;
                break;
            case VkVideoCodecOperationFlagsKHR.DecodeH265:
                decode.pNext = &decodeH265;
                break;
            case VkVideoCodecOperationFlagsKHR.EncodeH264:
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
        VkFormat format = candidate.BitDepth == 10 ? VkFormat.G10X6B10X6R10X62Plane420Unorm3Pack16 : VkFormat.G8B8R82Plane420Unorm;
        VkImageUsageFlags usage = isDecode ? VkImageUsageFlags.VideoDecodeDstKHR : VkImageUsageFlags.VideoEncodeSrcKHR;
        if (!HasFormat(physical, &chain.Profile, usage, format))
        {
            return null;
        }

        string level = candidate.Operation switch
        {
            VkVideoCodecOperationFlagsKHR.DecodeH264 => Level(H264Levels, (int)decodeH264.maxLevelIdc),
            VkVideoCodecOperationFlagsKHR.DecodeH265 => Level(H265Levels, (int)decodeH265.maxLevelIdc),
            VkVideoCodecOperationFlagsKHR.EncodeH264 => Level(H264Levels, (int)encodeH264.maxLevelIdc),
            _ => Level(H265Levels, (int)encodeH265.maxLevelIdc),
        };
        bool h264 = candidate.Operation == VkVideoCodecOperationFlagsKHR.EncodeH264;
        HardwareRateControl rateControl = HardwareRateControl.None;
        if (!isDecode)
        {
            rateControl |= (encode.rateControlModes & VkVideoEncodeRateControlModeFlagsKHR.Disabled) != 0 ? HardwareRateControl.ConstantQp : 0;
            rateControl |= (encode.rateControlModes & VkVideoEncodeRateControlModeFlagsKHR.Cbr) != 0 ? HardwareRateControl.ConstantBitrate : 0;
            rateControl |= (encode.rateControlModes & VkVideoEncodeRateControlModeFlagsKHR.Vbr) != 0 ? HardwareRateControl.VariableBitrate : 0;
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
    internal static bool HasFormat(VulkanPhysicalDevice physical, VkVideoProfileInfoKHR* profile, VkImageUsageFlags usage, VkFormat format)
    {
        VkInstanceApi vk = physical.Instance.Commands;
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
    private static string Level(string[] levels, int index) =>
        (uint)index < (uint)levels.Length ? levels[index] : index.ToString(CultureInfo.InvariantCulture);
}
