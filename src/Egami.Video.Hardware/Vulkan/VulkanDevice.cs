using System.Runtime.InteropServices;

using Vortice.Vulkan;

namespace Egami.Video.Hardware.Vulkan;

/// <summary>A device queue. Vulkan requires submissions to one queue to come from one thread at a time: take <see cref="Lock"/>.</summary>
/// <param name="Family">The queue family index.</param>
/// <param name="Handle">The <c>VkQueue</c>.</param>
internal sealed record VulkanQueue(int Family, VkQueue Handle)
{
    /// <summary>Gets the lock that serialises submissions.</summary>
    public Lock Lock { get; } = new();
}

/// <summary>
/// A logical Vulkan device with the queues for the video operations asked for, and the helpers the hardware codecs build on:
/// memory, buffers, images, command pools, fences and video sessions. Every object it makes holds the device alive until disposed.
/// </summary>
internal sealed unsafe class VulkanDevice : IDisposable
{
    /// <summary>How long a fence wait may take before the driver is taken as hung.</summary>
    public static readonly TimeSpan FenceTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] VideoExtensions =
    [
        "VK_KHR_video_queue", "VK_KHR_video_decode_queue", "VK_KHR_video_decode_h264", "VK_KHR_video_decode_h265",
        "VK_KHR_video_encode_queue", "VK_KHR_video_encode_h264", "VK_KHR_video_encode_h265", "VK_KHR_video_maintenance1",
    ];

    private VulkanDevice(VulkanPhysicalDevice physical, VulkanDeviceHandle handle, VkDeviceApi commands, string[] enabled)
    {
        Physical = physical;
        Handle = handle;
        Commands = commands;
        EnabledExtensions = enabled;
    }

    /// <summary>Gets the physical device.</summary>
    public VulkanPhysicalDevice Physical { get; }

    /// <summary>Gets the device handle.</summary>
    public VulkanDeviceHandle Handle { get; }

    /// <summary>Gets the device-level commands.</summary>
    public VkDeviceApi Commands { get; }

    /// <summary>Gets the extensions the device was made with.</summary>
    public IReadOnlyList<string> EnabledExtensions { get; }

    /// <summary>Gets the queue for video decoding, or <see langword="null"/>.</summary>
    public VulkanQueue? DecodeQueue { get; private set; }

    /// <summary>Gets the queue for video encoding, or <see langword="null"/>.</summary>
    public VulkanQueue? EncodeQueue { get; private set; }

    /// <summary>Gets the raw <c>VkDevice</c>; valid while <see cref="Handle"/> is open.</summary>
    public nint Raw => Handle.DangerousGetHandle();

    /// <summary>
    /// Makes a device on <paramref name="physical"/> with one decode queue and one encode queue where the device has queue
    /// families for <paramref name="decode"/> and <paramref name="encode"/> operations, and every video extension it offers.
    /// </summary>
    /// <exception cref="HardwareVideoException">The device has neither queue, or the driver refused the device.</exception>
    public static VulkanDevice Create(
        VulkanPhysicalDevice physical, VkVideoCodecOperationFlagsKHR decode, VkVideoCodecOperationFlagsKHR encode)
    {
        if (physical.ApiVersion < VulkanInstance.RequiredApiVersion)
        {
            throw new HardwareVideoException($"{physical.Name} supports Vulkan {VulkanInstance.ToVersion(physical.ApiVersion)}; hardware video needs 1.3.");
        }

        int decodeFamily = decode != 0 ? physical.FindVideoQueueFamily(decode) : -1;
        int encodeFamily = encode != 0 ? physical.FindVideoQueueFamily(encode) : -1;
        if (decodeFamily < 0 && encodeFamily < 0)
        {
            throw new HardwareVideoException($"{physical.Name} has no video queue for the requested codecs.");
        }

        string[] enabled = [.. VideoExtensions.Where(physical.HasExtension)];
        nint[] names = [.. enabled.Select(Marshal.StringToHGlobalAnsi)];
        try
        {
            float priority = 1f;
            VkDeviceQueueCreateInfo* queues = stackalloc VkDeviceQueueCreateInfo[2];
            uint queueCount = 0;
            foreach (int family in new[] { decodeFamily, encodeFamily }.Where(f => f >= 0).Distinct())
            {
                queues[queueCount++] = new VkDeviceQueueCreateInfo { queueFamilyIndex = (uint)family, queueCount = 1, pQueuePriorities = &priority };
            }

            VkPhysicalDeviceVulkan13Features features13 = new() { synchronization2 = true };
            fixed (nint* pNames = names)
            {
                VkDeviceCreateInfo info = new()
                {
                    pNext = &features13,
                    queueCreateInfoCount = queueCount,
                    pQueueCreateInfos = queues,
                    enabledExtensionCount = (uint)names.Length,
                    ppEnabledExtensionNames = (byte**)pNames,
                };
                VkDevice raw;
                VkCheck.ThrowIfFailed(physical.Instance.Commands.vkCreateDevice(physical.Handle, &info, null, &raw), "vkCreateDevice");

                VkDeviceApi commands = new(physical.Instance.Commands, raw);
                VulkanDeviceHandle handle = new();
                handle.Attach(raw.Handle, (delegate* unmanaged<nint, void*, void>)commands.vkDestroyDevice_ptr.Value, physical.Instance.Handle);
                VulkanDevice device = new(physical, handle, commands, enabled);
                device.DecodeQueue = decodeFamily >= 0 ? device.GetQueue(decodeFamily) : null;
                device.EncodeQueue = encodeFamily >= 0 ? device.GetQueue(encodeFamily) : null;
                return device;
            }
        }
        finally
        {
            foreach (nint name in names)
            {
                Marshal.FreeHGlobal(name);
            }
        }
    }

    /// <summary>
    /// Allocates memory for <paramref name="requirements"/> of a type with all of <paramref name="required"/>, preferring one that
    /// also has <paramref name="preferred"/>.
    /// </summary>
    public VulkanObjectHandle Allocate(in VkMemoryRequirements requirements, VkMemoryPropertyFlags required, VkMemoryPropertyFlags preferred = 0)
    {
        int type = Physical.FindMemoryType(requirements.memoryTypeBits, required | preferred);
        if (type < 0)
        {
            type = Physical.FindMemoryType(requirements.memoryTypeBits, required);
        }

        if (type < 0)
        {
            throw new HardwareVideoException($"{Physical.Name} has no memory type with {required} for this resource.");
        }

        VkMemoryAllocateInfo info = new() { allocationSize = requirements.size, memoryTypeIndex = (uint)type };
        VkDeviceMemory memory;
        VkCheck.ThrowIfFailed(Commands.vkAllocateMemory(&info, null, &memory), "vkAllocateMemory");
        return new VulkanObjectHandle(Handle, memory, Commands.vkFreeMemory_ptr);
    }

    /// <summary>
    /// Makes a buffer with its own memory. Host-visible memory is mapped for the buffer's life and must be coherent, so writes
    /// and reads need no flush.
    /// </summary>
    /// <param name="size">The size in bytes.</param>
    /// <param name="usage">What the buffer is for.</param>
    /// <param name="memory">The memory properties it needs.</param>
    /// <param name="next">A <c>pNext</c> chain for the create info, such as a video profile list.</param>
    public VulkanBuffer CreateBuffer(ulong size, VkBufferUsageFlags usage, VkMemoryPropertyFlags memory, void* next = null)
    {
        VkBufferCreateInfo info = new() { pNext = next, size = size, usage = usage, sharingMode = VkSharingMode.Exclusive };
        VkBuffer raw;
        VkCheck.ThrowIfFailed(Commands.vkCreateBuffer(&info, null, &raw), "vkCreateBuffer");
        VulkanObjectHandle buffer = new(Handle, raw, Commands.vkDestroyBuffer_ptr);
        VulkanObjectHandle? bound = null;
        try
        {
            VkMemoryRequirements requirements;
            Commands.vkGetBufferMemoryRequirements(raw, &requirements);
            bool host = (memory & VkMemoryPropertyFlags.HostVisible) != 0;
            VkMemoryPropertyFlags required = host ? memory | VkMemoryPropertyFlags.HostCoherent : memory;
            bound = Allocate(requirements, required, host ? VkMemoryPropertyFlags.HostCached : 0);
            VkCheck.ThrowIfFailed(Commands.vkBindBufferMemory(raw, bound.Value, 0), "vkBindBufferMemory");
            void* mapped = null;
            if (host)
            {
                VkCheck.ThrowIfFailed(Commands.vkMapMemory(bound.Value, 0, ulong.MaxValue, 0, &mapped), "vkMapMemory");
            }

            return new VulkanBuffer(buffer, bound, size, (byte*)mapped);
        }
        catch
        {
            buffer.Dispose();
            bound?.Dispose();
            throw;
        }
    }

    /// <summary>Makes a 2D image in device-local memory, with a view of all of it.</summary>
    /// <param name="info">The image's create info; its <c>pNext</c> chain carries the video profile list for video images.</param>
    /// <param name="aspect">The view's aspect: color for the multi-planar video formats.</param>
    public VulkanImage CreateImage(in VkImageCreateInfo info, VkImageAspectFlags aspect = VkImageAspectFlags.Color)
    {
        VkImage raw;
        fixed (VkImageCreateInfo* p = &info)
        {
            VkCheck.ThrowIfFailed(Commands.vkCreateImage(p, null, &raw), "vkCreateImage");
        }

        VulkanObjectHandle image = new(Handle, raw, Commands.vkDestroyImage_ptr);
        VulkanObjectHandle? memory = null;
        try
        {
            VkMemoryRequirements requirements;
            Commands.vkGetImageMemoryRequirements(raw, &requirements);
            memory = Allocate(requirements, VkMemoryPropertyFlags.DeviceLocal);
            VkCheck.ThrowIfFailed(Commands.vkBindImageMemory(raw, memory.Value, 0), "vkBindImageMemory");
            VkImageViewCreateInfo view = new()
            {
                image = raw,
                viewType = info.arrayLayers > 1 ? VkImageViewType.Image2DArray : VkImageViewType.Image2D,
                format = info.format,
                subresourceRange = new VkImageSubresourceRange { aspectMask = aspect, levelCount = 1, layerCount = info.arrayLayers },
            };
            VkImageView viewRaw;
            VkCheck.ThrowIfFailed(Commands.vkCreateImageView(&view, null, &viewRaw), "vkCreateImageView");
            return new VulkanImage(image, memory, new VulkanObjectHandle(Handle, viewRaw, Commands.vkDestroyImageView_ptr), info.format, info.extent.width, info.extent.height);
        }
        catch
        {
            image.Dispose();
            memory?.Dispose();
            throw;
        }
    }

    /// <summary>Makes a fence, signaled or not.</summary>
    public VulkanObjectHandle CreateFence(bool signaled = false)
    {
        VkFenceCreateInfo info = new() { flags = signaled ? VkFenceCreateFlags.Signaled : 0 };
        VkFence raw;
        VkCheck.ThrowIfFailed(Commands.vkCreateFence(&info, null, &raw), "vkCreateFence");
        return new VulkanObjectHandle(Handle, raw, Commands.vkDestroyFence_ptr);
    }

    /// <summary>Makes a command pool for <paramref name="queue"/>'s family whose buffers can be reset one by one.</summary>
    public VulkanCommandPool CreateCommandPool(VulkanQueue queue)
    {
        VkCommandPoolCreateInfo info = new() { flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = (uint)queue.Family };
        VkCommandPool raw;
        VkCheck.ThrowIfFailed(Commands.vkCreateCommandPool(&info, null, &raw), "vkCreateCommandPool");
        return new VulkanCommandPool(this, new VulkanObjectHandle(Handle, raw, Commands.vkDestroyCommandPool_ptr));
    }

    /// <summary>Submits one command buffer to <paramref name="queue"/>; <paramref name="fence"/> signals when it is done.</summary>
    public void Submit(VulkanQueue queue, VkCommandBuffer commandBuffer, VulkanObjectHandle fence)
    {
        VkSubmitInfo info = new() { commandBufferCount = 1, pCommandBuffers = &commandBuffer };
        lock (queue.Lock)
        {
            VkCheck.ThrowIfFailed(Commands.vkQueueSubmit(queue.Handle, 1, &info, fence.Value), "vkQueueSubmit");
        }
    }

    /// <summary>Waits for <paramref name="fence"/> and resets it.</summary>
    /// <exception cref="HardwareVideoException">The wait failed or took longer than <see cref="FenceTimeout"/>.</exception>
    public void WaitAndReset(VulkanObjectHandle fence)
    {
        VkFence raw = fence.Value;
        VkResult result = Commands.vkWaitForFences(1, &raw, true, (ulong)FenceTimeout.Ticks * 100);
        if (result == VkResult.Timeout)
        {
            throw new HardwareVideoException($"The GPU did not finish within {FenceTimeout.TotalSeconds} seconds.", (int)result);
        }

        VkCheck.ThrowIfFailed(result, "vkWaitForFences");
        VkCheck.ThrowIfFailed(Commands.vkResetFences(1, &raw), "vkResetFences");
    }

    /// <summary>Makes a video session and binds the memory it asks for.</summary>
    public VulkanVideoSession CreateVideoSession(in VkVideoSessionCreateInfoKHR info)
    {
        VkVideoSessionKHR raw;
        fixed (VkVideoSessionCreateInfoKHR* p = &info)
        {
            VkCheck.ThrowIfFailed(Commands.vkCreateVideoSessionKHR(p, null, &raw), "vkCreateVideoSessionKHR");
        }

        VulkanVideoSession session = new(new VulkanObjectHandle(Handle, raw, Commands.vkDestroyVideoSessionKHR_ptr));
        try
        {
            uint count = 0;
            VkCheck.ThrowIfFailed(Commands.vkGetVideoSessionMemoryRequirementsKHR(raw, &count, null), "vkGetVideoSessionMemoryRequirementsKHR");
            VkVideoSessionMemoryRequirementsKHR[] requirements = new VkVideoSessionMemoryRequirementsKHR[count];
            VkBindVideoSessionMemoryInfoKHR[] binds = new VkBindVideoSessionMemoryInfoKHR[count];
            for (int i = 0; i < count; i++)
            {
                requirements[i] = new VkVideoSessionMemoryRequirementsKHR();
            }

            fixed (VkVideoSessionMemoryRequirementsKHR* r = requirements)
            {
                VkCheck.ThrowIfFailed(Commands.vkGetVideoSessionMemoryRequirementsKHR(raw, &count, r), "vkGetVideoSessionMemoryRequirementsKHR");
            }

            for (int i = 0; i < count; i++)
            {
                // Session memory may be of any type the driver allows; device-local is preferred.
                VulkanObjectHandle memory = Allocate(requirements[i].memoryRequirements, 0, VkMemoryPropertyFlags.DeviceLocal);
                session.Memory.Add(memory);
                binds[i] = new VkBindVideoSessionMemoryInfoKHR
                {
                    memoryBindIndex = requirements[i].memoryBindIndex,
                    memory = memory.Value,
                    memorySize = requirements[i].memoryRequirements.size,
                };
            }

            fixed (VkBindVideoSessionMemoryInfoKHR* b = binds)
            {
                VkCheck.ThrowIfFailed(Commands.vkBindVideoSessionMemoryKHR(raw, count, b), "vkBindVideoSessionMemoryKHR");
            }

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!Handle.IsClosed)
        {
            Commands.vkDeviceWaitIdle();
        }

        Handle.Dispose();
    }

    private VulkanQueue GetQueue(int family)
    {
        VkQueue queue;
        Commands.vkGetDeviceQueue((uint)family, 0, &queue);
        return new VulkanQueue(family, queue);
    }
}

/// <summary>A buffer with its own memory; <see cref="Span"/> is its mapped memory when it is host-visible.</summary>
internal sealed unsafe class VulkanBuffer(VulkanObjectHandle buffer, VulkanObjectHandle memory, ulong size, byte* mapped) : IDisposable
{
    /// <summary>Gets the <c>VkBuffer</c>.</summary>
    public ulong Handle => buffer.Value;

    /// <summary>Gets the buffer's size in bytes.</summary>
    public ulong Size { get; } = size;

    /// <summary>Gets a value indicating whether the buffer's memory is mapped.</summary>
    public bool IsMapped => mapped != null && !buffer.IsClosed;

    /// <summary>Gets the mapped memory.</summary>
    /// <exception cref="InvalidOperationException">The buffer is not host-visible, or is disposed.</exception>
    public Span<byte> Span => IsMapped ? new Span<byte>(mapped, checked((int)Size)) : throw new InvalidOperationException("The buffer is not mapped.");

    /// <inheritdoc/>
    public void Dispose()
    {
        // Freeing the memory unmaps it.
        buffer.Dispose();
        memory.Dispose();
    }
}

/// <summary>An image with its memory and a view.</summary>
internal sealed class VulkanImage(VulkanObjectHandle image, VulkanObjectHandle memory, VulkanObjectHandle view, VkFormat format, uint width, uint height) : IDisposable
{
    /// <summary>Gets the <c>VkImage</c>.</summary>
    public ulong Handle => image.Value;

    /// <summary>Gets the <c>VkImageView</c>.</summary>
    public ulong View => view.Value;

    /// <summary>Gets the image's format.</summary>
    public VkFormat Format { get; } = format;

    /// <summary>Gets the width in pixels.</summary>
    public uint Width { get; } = width;

    /// <summary>Gets the height in pixels.</summary>
    public uint Height { get; } = height;

    /// <inheritdoc/>
    public void Dispose()
    {
        view.Dispose();
        image.Dispose();
        memory.Dispose();
    }
}

/// <summary>A command pool and the primary command buffers allocated from it, which it frees with itself.</summary>
internal sealed unsafe class VulkanCommandPool(VulkanDevice device, VulkanObjectHandle pool) : IDisposable
{
    /// <summary>Allocates a primary command buffer.</summary>
    public VkCommandBuffer Allocate()
    {
        VkCommandBufferAllocateInfo info = new() { commandPool = pool.Value, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
        VkCommandBuffer buffer;
        VkCheck.ThrowIfFailed(device.Commands.vkAllocateCommandBuffers(&info, &buffer), "vkAllocateCommandBuffers");
        return buffer;
    }

    /// <summary>Resets <paramref name="commandBuffer"/> and begins recording it for one submission.</summary>
    public void Begin(VkCommandBuffer commandBuffer)
    {
        VkCheck.ThrowIfFailed(device.Commands.vkResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer");
        VkCommandBufferBeginInfo info = new() { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        VkCheck.ThrowIfFailed(device.Commands.vkBeginCommandBuffer(commandBuffer, &info), "vkBeginCommandBuffer");
    }

    /// <summary>Ends recording <paramref name="commandBuffer"/>.</summary>
    public void End(VkCommandBuffer commandBuffer) => VkCheck.ThrowIfFailed(device.Commands.vkEndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

    /// <inheritdoc/>
    public void Dispose() => pool.Dispose();
}

/// <summary>A video session and the memory bound to it.</summary>
internal sealed class VulkanVideoSession(VulkanObjectHandle session) : IDisposable
{
    /// <summary>Gets the <c>VkVideoSessionKHR</c>.</summary>
    public ulong Handle => session.Value;

    /// <summary>Gets the memory bound to the session, freed after it.</summary>
    public List<VulkanObjectHandle> Memory { get; } = [];

    /// <inheritdoc/>
    public void Dispose()
    {
        session.Dispose();
        foreach (VulkanObjectHandle memory in Memory)
        {
            memory.Dispose();
        }
    }
}
