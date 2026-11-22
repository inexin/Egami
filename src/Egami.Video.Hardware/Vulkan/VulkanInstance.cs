using System.Runtime.InteropServices;

using Egami.Video.Hardware.Vulkan.Native;

namespace Egami.Video.Hardware.Vulkan;

/// <summary>Checks Vulkan results.</summary>
internal static class VkCheck
{
    /// <summary>Throws a <see cref="HardwareVideoException"/> when <paramref name="result"/> is an error code.</summary>
    public static void ThrowIfFailed(VkResult result, string command)
    {
        if (result < 0)
        {
            throw new HardwareVideoException($"{command} failed: VK_{result} ({(int)result}).", (int)result);
        }
    }
}

/// <summary>A Vulkan instance: the loader's view of every Vulkan device on the machine.</summary>
/// <remarks>
/// Set the environment variable <c>EGAMI_VULKAN_VALIDATION</c> to 1 to turn on the Khronos validation layer when it is installed
/// (the Vulkan SDK installs it); it reports misuse of the API on the console or the debugger output.
/// </remarks>
internal sealed unsafe class VulkanInstance : IDisposable
{
    /// <summary>The Vulkan version Egami asks for, and the least a device must offer: 1.3, for synchronization2 in core.</summary>
    public const uint RequiredApiVersion = (1u << 22) | (3u << 12);

    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    private VulkanInstance(VulkanInstanceHandle handle, VkInstanceCommands commands, uint loaderVersion, bool validation)
    {
        Handle = handle;
        Commands = commands;
        LoaderVersion = loaderVersion;
        ValidationEnabled = validation;
    }

    /// <summary>Gets the instance handle.</summary>
    public VulkanInstanceHandle Handle { get; }

    /// <summary>Gets the instance-level commands.</summary>
    public VkInstanceCommands Commands { get; }

    /// <summary>Gets the Vulkan version the loader supports.</summary>
    public uint LoaderVersion { get; }

    /// <summary>Gets a value indicating whether the validation layer is on.</summary>
    public bool ValidationEnabled { get; }

    /// <summary>Formats a packed Vulkan version as major.minor.patch.</summary>
    public static Version ToVersion(uint packed) => new((int)(packed >> 22) & 0x7F, (int)(packed >> 12) & 0x3FF, (int)(packed & 0xFFF));

    /// <summary>Creates an instance, or explains in <paramref name="reason"/> why Vulkan is not usable here.</summary>
    public static VulkanInstance? TryCreate(out string? reason)
    {
        if (!VkLoader.IsLoaded)
        {
            reason = VkLoader.LibraryName is { } name
                ? $"The Vulkan loader ({name}) was not found; it comes with a GPU driver that supports Vulkan."
                : "Hardware video through Vulkan needs Windows or Linux.";
            if (!Environment.Is64BitProcess)
            {
                reason = "Hardware video needs a 64-bit process.";
            }

            return null;
        }

        VkGlobalCommands global = new(VkLoader.GetInstanceProcAddr, 0);
        uint loaderVersion = 1u << 22;
        if (global.vkEnumerateInstanceVersion != null)
        {
            global.vkEnumerateInstanceVersion(&loaderVersion);
        }

        if (loaderVersion < RequiredApiVersion)
        {
            reason = $"The Vulkan loader supports Vulkan {ToVersion(loaderVersion)}; hardware video needs 1.3 or later.";
            return null;
        }

        bool validation = Environment.GetEnvironmentVariable("EGAMI_VULKAN_VALIDATION") == "1" && HasLayer(global, ValidationLayer);
        VulkanInstanceHandle handle = new();
        nint layerName = validation ? Marshal.StringToHGlobalAnsi(ValidationLayer) : 0;
        try
        {
            fixed (byte* appName = "Egami.Video.Hardware"u8)
            {
                VkApplicationInfo app = new() { pApplicationName = appName, pEngineName = appName, apiVersion = RequiredApiVersion };
                VkInstanceCreateInfo info = new()
                {
                    pApplicationInfo = &app,
                    enabledLayerCount = validation ? 1u : 0u,
                    ppEnabledLayerNames = (byte**)&layerName,
                };
                nint instance;
                VkResult result = global.vkCreateInstance(&info, null, &instance);
                if (result < 0)
                {
                    reason = $"vkCreateInstance failed: VK_{result} ({(int)result}).";
                    handle.Dispose();
                    return null;
                }

                VkInstanceCommands commands = new(VkLoader.GetInstanceProcAddr, instance);
                handle.Attach(instance, (delegate* unmanaged<nint, void*, void>)VkLoader.Load(VkLoader.GetInstanceProcAddr, instance, "vkDestroyInstance"u8));
                reason = null;
                return new VulkanInstance(handle, commands, loaderVersion, validation);
            }
        }
        finally
        {
            if (layerName != 0)
            {
                Marshal.FreeHGlobal(layerName);
            }
        }
    }

    /// <summary>Lists the physical devices; devices the driver reports but Egami cannot query are left out.</summary>
    public VulkanPhysicalDevice[] EnumeratePhysicalDevices()
    {
        nint instance = Handle.DangerousGetHandle();
        uint count = 0;
        VkCheck.ThrowIfFailed(Commands.vkEnumeratePhysicalDevices(instance, &count, null), "vkEnumeratePhysicalDevices");
        nint[] handles = new nint[count];
        fixed (nint* p = handles)
        {
            VkCheck.ThrowIfFailed(Commands.vkEnumeratePhysicalDevices(instance, &count, p), "vkEnumeratePhysicalDevices");
        }

        VulkanPhysicalDevice[] devices = new VulkanPhysicalDevice[count];
        for (int i = 0; i < count; i++)
        {
            devices[i] = new VulkanPhysicalDevice(this, handles[i]);
        }

        return devices;
    }

    /// <inheritdoc/>
    public void Dispose() => Handle.Dispose();

    private static bool HasLayer(VkGlobalCommands global, string name)
    {
        if (global.vkEnumerateInstanceLayerProperties == null)
        {
            return false;
        }

        uint count = 0;
        if (global.vkEnumerateInstanceLayerProperties(&count, null) < 0 || count == 0)
        {
            return false;
        }

        VkLayerProperties[] layers = new VkLayerProperties[count];
        fixed (VkLayerProperties* p = layers)
        {
            if (global.vkEnumerateInstanceLayerProperties(&count, p) < 0)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (VulkanText.Read(p[i].layerName, 256) == name)
                {
                    return true;
                }
            }
        }

        return false;
    }
}

/// <summary>Reads the null-terminated strings in Vulkan structs.</summary>
internal static unsafe class VulkanText
{
    /// <summary>Reads UTF-8 text up to the first null byte or <paramref name="capacity"/> bytes.</summary>
    public static string Read(byte* text, int capacity)
    {
        int length = new ReadOnlySpan<byte>(text, capacity).IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(text, length < 0 ? capacity : length);
    }
}
