using Vortice.Vulkan;

namespace Egami.Video.Hardware.Vulkan;

/// <summary>One queue family of a physical device.</summary>
/// <param name="Index">The family's index.</param>
/// <param name="Flags">What its queues do.</param>
/// <param name="QueueCount">How many queues it has.</param>
/// <param name="VideoOperations">The video codec operations its queues run; none for a family without video.</param>
/// <param name="ResultStatusQueries">Whether its queues report a video operation's result status through a query.</param>
internal readonly record struct VulkanQueueFamily(
    int Index, VkQueueFlags Flags, int QueueCount, VkVideoCodecOperationFlagsKHR VideoOperations, bool ResultStatusQueries);

/// <summary>A GPU (or a software device) as Vulkan reports it, before a logical device is made on it.</summary>
internal sealed unsafe class VulkanPhysicalDevice
{
    private readonly HashSet<string> extensions = new(StringComparer.Ordinal);
    private VkPhysicalDeviceMemoryProperties memory;

    internal VulkanPhysicalDevice(VulkanInstance instance, VkPhysicalDevice handle)
    {
        Instance = instance;
        Handle = handle;
        VkInstanceApi vk = instance.Commands;

        VkPhysicalDeviceDriverProperties driver = new();
        VkPhysicalDeviceProperties2 properties = new() { pNext = &driver };
        vk.vkGetPhysicalDeviceProperties2(handle, &properties);
        ApiVersion = properties.properties.apiVersion;
        DriverVersion = properties.properties.driverVersion;
        VendorId = properties.properties.vendorID;
        DeviceId = properties.properties.deviceID;
        Type = properties.properties.deviceType;
        Name = VulkanText.Read(properties.properties.deviceName, 256);
        DriverName = VulkanText.Read(driver.driverName, 256);
        DriverInfo = VulkanText.Read(driver.driverInfo, 256);

        uint familyCount = 0;
        vk.vkGetPhysicalDeviceQueueFamilyProperties2(handle, &familyCount, null);
        VkQueueFamilyProperties2[] families = new VkQueueFamilyProperties2[familyCount];
        VkQueueFamilyVideoPropertiesKHR[] video = new VkQueueFamilyVideoPropertiesKHR[familyCount];
        VkQueueFamilyQueryResultStatusPropertiesKHR[] status = new VkQueueFamilyQueryResultStatusPropertiesKHR[familyCount];
        fixed (VkQueueFamilyProperties2* f = families)
        fixed (VkQueueFamilyVideoPropertiesKHR* v = video)
        fixed (VkQueueFamilyQueryResultStatusPropertiesKHR* s = status)
        {
            for (int i = 0; i < familyCount; i++)
            {
                // The video structs are chained only where the driver has the video queue extension; others may reject them.
                s[i] = new VkQueueFamilyQueryResultStatusPropertiesKHR();
                v[i] = new VkQueueFamilyVideoPropertiesKHR { pNext = &s[i] };
                f[i] = new VkQueueFamilyProperties2();
            }

            ReadExtensions(vk);
            bool hasVideo = HasExtension("VK_KHR_video_queue");
            for (int i = 0; i < familyCount; i++)
            {
                f[i].pNext = hasVideo ? &v[i] : null;
            }

            vk.vkGetPhysicalDeviceQueueFamilyProperties2(handle, &familyCount, f);
            QueueFamilies = new VulkanQueueFamily[familyCount];
            for (int i = 0; i < familyCount; i++)
            {
                VkQueueFamilyProperties p = f[i].queueFamilyProperties;
                QueueFamilies[i] = new VulkanQueueFamily(
                    i, p.queueFlags, (int)p.queueCount, hasVideo ? v[i].videoCodecOperations : 0, hasVideo && s[i].queryResultStatusSupport);
            }
        }

        VkPhysicalDeviceMemoryProperties props;
        vk.vkGetPhysicalDeviceMemoryProperties(handle, &props);
        memory = props;
    }

    /// <summary>Gets the instance the device belongs to.</summary>
    public VulkanInstance Instance { get; }

    /// <summary>Gets the <c>VkPhysicalDevice</c> handle, valid as long as the instance.</summary>
    public VkPhysicalDevice Handle { get; }

    /// <summary>Gets the device's name.</summary>
    public string Name { get; }

    /// <summary>Gets the Vulkan version the device supports, packed.</summary>
    public uint ApiVersion { get; }

    /// <summary>Gets the driver's own version number, packed in the vendor's way.</summary>
    public uint DriverVersion { get; }

    /// <summary>Gets the PCI vendor id.</summary>
    public uint VendorId { get; }

    /// <summary>Gets the vendor's device id.</summary>
    public uint DeviceId { get; }

    /// <summary>Gets whether the device is integrated, discrete, virtual or the CPU.</summary>
    public VkPhysicalDeviceType Type { get; }

    /// <summary>Gets the driver's name.</summary>
    public string DriverName { get; }

    /// <summary>Gets the driver's own description of its version.</summary>
    public string DriverInfo { get; }

    /// <summary>Gets the queue families.</summary>
    public VulkanQueueFamily[] QueueFamilies { get; }

    /// <summary>Gets the device's memory types and heaps.</summary>
    public ref readonly VkPhysicalDeviceMemoryProperties Memory => ref memory;

    /// <summary>Gets a value indicating whether the device has the extension.</summary>
    public bool HasExtension(string name) => extensions.Contains(name);

    /// <summary>Returns the first queue family whose queues run <paramref name="operation"/>, or -1.</summary>
    public int FindVideoQueueFamily(VkVideoCodecOperationFlagsKHR operation)
    {
        foreach (VulkanQueueFamily family in QueueFamilies)
        {
            if ((family.VideoOperations & operation) != 0 && family.QueueCount > 0)
            {
                return family.Index;
            }
        }

        return -1;
    }

    /// <summary>Returns the index of a memory type in <paramref name="typeBits"/> with all of <paramref name="required"/>, or -1.</summary>
    public int FindMemoryType(uint typeBits, VkMemoryPropertyFlags required)
    {
        for (int i = 0; i < memory.memoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) != 0 && (memory.memoryTypes[i].propertyFlags & required) == required)
            {
                return i;
            }
        }

        return -1;
    }

    private void ReadExtensions(VkInstanceApi vk)
    {
        if (extensions.Count > 0)
        {
            return;
        }

        uint count = 0;
        if (vk.vkEnumerateDeviceExtensionProperties(Handle, null, &count, null) < 0)
        {
            return;
        }

        VkExtensionProperties[] list = new VkExtensionProperties[count];
        fixed (VkExtensionProperties* p = list)
        {
            if (vk.vkEnumerateDeviceExtensionProperties(Handle, null, &count, p) < 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                extensions.Add(VulkanText.Read(p[i].extensionName, 256));
            }
        }
    }
}
