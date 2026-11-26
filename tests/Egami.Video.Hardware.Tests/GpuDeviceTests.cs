using Egami.Video.Hardware.Vulkan;

using Vortice.Vulkan;

namespace Egami.Video.Hardware.Tests;

/// <summary>
/// The device layer on a real GPU: buffers, transfers, fences, NV12 video images and video sessions. Skipped where no GPU with
/// hardware video is found, as in CI; run them on a machine with one through <c>python tools/test.py video-hardware</c>.
/// </summary>
[Trait("Category", "Gpu")]
public sealed unsafe class GpuDeviceTests : IDisposable
{
    private readonly VulkanInstance? instance;
    private readonly VulkanPhysicalDevice? physical;

    public GpuDeviceTests()
    {
        if (HardwareVideoCodecs.GetDevices().Count == 0)
        {
            return;
        }

        instance = VulkanInstance.TryCreate(out _);
        physical = instance?.EnumeratePhysicalDevices().FirstOrDefault(p => VulkanProbe.Describe(p, out _).Profiles.Count > 0);
    }

    public void Dispose() => instance?.Dispose();

    [Fact]
    public void CopiesBetweenBuffersOnAVideoQueue()
    {
        using VulkanDevice device = OpenDevice();
        VulkanQueue queue = device.DecodeQueue ?? device.EncodeQueue!;
        using VulkanBuffer source = device.CreateBuffer(4096, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible);
        using VulkanBuffer target = device.CreateBuffer(4096, VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.HostVisible);
        for (int i = 0; i < source.Span.Length; i++)
        {
            source.Span[i] = (byte)(i * 7);
        }

        target.Span.Clear();
        using VulkanCommandPool pool = device.CreateCommandPool(queue);
        using VulkanObjectHandle fence = device.CreateFence();
        nint commands = pool.Allocate();
        for (int round = 0; round < 2; round++)
        {
            pool.Begin(commands);
            VkBufferCopy region = new() { srcOffset = (ulong)(round * 1024), dstOffset = 0, size = 1024 };
            device.Commands.vkCmdCopyBuffer(commands, source.Handle, target.Handle, 1, &region);
            pool.End(commands);
            device.Submit(queue, commands, fence);
            device.WaitAndReset(fence);
            Assert.True(source.Span.Slice(round * 1024, 1024).SequenceEqual(target.Span[..1024]));
        }
    }

    [Fact]
    public void MakesVideoImagesAndSessions()
    {
        using VulkanDevice device = OpenDevice();
        int made = 0;
        foreach ((VkVideoCodecOperationFlagsKHR operation, int profileIdc, VulkanQueue? queue) in new[]
        {
            (VkVideoCodecOperationFlagsKHR.DecodeH264, (int)StdVideoH264ProfileIdc.High, device.DecodeQueue),
            (VkVideoCodecOperationFlagsKHR.EncodeH264, (int)StdVideoH264ProfileIdc.High, device.EncodeQueue),
            (VkVideoCodecOperationFlagsKHR.DecodeH265, (int)StdVideoH265ProfileIdc.Main, device.DecodeQueue),
            (VkVideoCodecOperationFlagsKHR.EncodeH265, (int)StdVideoH265ProfileIdc.Main, device.EncodeQueue),
        })
        {
            if (queue is null || (physical!.QueueFamilies[queue.Family].VideoOperations & operation) == 0)
            {
                continue;
            }

            VulkanProbe.ProfileChain chain;
            VulkanProbe.ProfileChain.Fill(&chain, operation, profileIdc, 8);
            VkVideoCapabilitiesKHR caps = Capabilities(&chain, operation);
            bool decode = operation is VkVideoCodecOperationFlagsKHR.DecodeH264 or VkVideoCodecOperationFlagsKHR.DecodeH265;
            uint width = Math.Min(1920u, caps.maxCodedExtent.width), height = Math.Min(1088u, caps.maxCodedExtent.height);

            VkVideoProfileListInfoKHR list = new() { profileCount = 1, pProfiles = &chain.Profile };
            VkImageCreateInfo imageInfo = new()
            {
                pNext = &list,
                imageType = VkImageType.Image2D,
                format = VkFormat.G8B8R82Plane420Unorm,
                extent = new VkExtent3D { width = width, height = height, depth = 1 },
                mipLevels = 1,
                arrayLayers = 1,
                samples = VkSampleCountFlags.Count1,
                tiling = VkImageTiling.Optimal,
                usage = decode
                    ? VkImageUsageFlags.VideoDecodeDstKHR | VkImageUsageFlags.VideoDecodeDpbKHR | VkImageUsageFlags.TransferSrc
                    : VkImageUsageFlags.VideoEncodeSrcKHR | VkImageUsageFlags.TransferDst,
                sharingMode = VkSharingMode.Exclusive,
                initialLayout = VkImageLayout.Undefined,
            };
            using VulkanImage image = device.CreateImage(imageInfo);
            Assert.NotEqual(0ul, image.Handle);
            Assert.NotEqual(0ul, image.View);

            VkExtensionProperties header = caps.stdHeaderVersion;
            VkVideoSessionCreateInfoKHR sessionInfo = new()
            {
                queueFamilyIndex = (uint)queue.Family,
                pVideoProfile = &chain.Profile,
                pictureFormat = VkFormat.G8B8R82Plane420Unorm,
                maxCodedExtent = new VkExtent2D { width = width, height = height },
                referencePictureFormat = VkFormat.G8B8R82Plane420Unorm,
                maxDpbSlots = Math.Min(caps.maxDpbSlots, 4u),
                maxActiveReferencePictures = Math.Min(caps.maxActiveReferencePictures, 3u),
                pStdHeaderVersion = &header,
            };
            using VulkanVideoSession session = device.CreateVideoSession(sessionInfo);
            Assert.NotEqual(0ul, session.Handle);
            made++;
        }

        Assert.True(made > 0);
    }

    [Fact]
    public void ObjectsKeepTheirDeviceAliveUntilDisposed()
    {
        VulkanDevice device = OpenDevice();
        VulkanBuffer buffer = device.CreateBuffer(256, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible);
        // The device is closed for new use, but vkDestroyDevice waits until the buffer, which holds a reference, is released.
        device.Dispose();
        Assert.False(device.Handle.IsClosed);
        buffer.Span.Fill(1);
        Assert.Equal(1, buffer.Span[255]);
        buffer.Dispose();
        Assert.True(device.Handle.IsClosed);
        Assert.Throws<InvalidOperationException>(() => buffer.Span.Length);
    }

    private VulkanDevice OpenDevice()
    {
        Assert.SkipWhen(physical is null, "No GPU with Vulkan Video: " + HardwareVideoCodecs.UnavailableReason);
        const VkVideoCodecOperationFlagsKHR decode = VkVideoCodecOperationFlagsKHR.DecodeH264 | VkVideoCodecOperationFlagsKHR.DecodeH265;
        const VkVideoCodecOperationFlagsKHR encode = VkVideoCodecOperationFlagsKHR.EncodeH264 | VkVideoCodecOperationFlagsKHR.EncodeH265;
        return VulkanDevice.Create(physical!, decode, encode);
    }

    private VkVideoCapabilitiesKHR Capabilities(VulkanProbe.ProfileChain* chain, VkVideoCodecOperationFlagsKHR operation)
    {
        VkVideoDecodeH264CapabilitiesKHR decodeH264 = new();
        VkVideoDecodeH265CapabilitiesKHR decodeH265 = new();
        VkVideoEncodeH264CapabilitiesKHR encodeH264 = new();
        VkVideoEncodeH265CapabilitiesKHR encodeH265 = new();
        VkVideoDecodeCapabilitiesKHR decode = new();
        VkVideoEncodeCapabilitiesKHR encode = new();
        VkVideoCapabilitiesKHR caps = new();
        switch (operation)
        {
            case VkVideoCodecOperationFlagsKHR.DecodeH264:
                caps.pNext = &decode;
                decode.pNext = &decodeH264;
                break;
            case VkVideoCodecOperationFlagsKHR.DecodeH265:
                caps.pNext = &decode;
                decode.pNext = &decodeH265;
                break;
            case VkVideoCodecOperationFlagsKHR.EncodeH264:
                caps.pNext = &encode;
                encode.pNext = &encodeH264;
                break;
            default:
                caps.pNext = &encode;
                encode.pNext = &encodeH265;
                break;
        }

        VkCheck.ThrowIfFailed(instance!.Commands.vkGetPhysicalDeviceVideoCapabilitiesKHR(physical!.Handle, &chain->Profile, &caps), "vkGetPhysicalDeviceVideoCapabilitiesKHR");
        caps.pNext = null;
        return caps;
    }
}
