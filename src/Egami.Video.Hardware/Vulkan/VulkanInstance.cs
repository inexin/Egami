using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Vortice.Vulkan;

namespace Egami.Video.Hardware.Vulkan;

/// <summary>Checks Vulkan results.</summary>
internal static class VkCheck
{
    /// <summary>Throws a <see cref="HardwareVideoException"/> when <paramref name="result"/> is an error code.</summary>
    public static void ThrowIfFailed(VkResult result, string command)
    {
        if (result < 0)
        {
            throw new HardwareVideoException($"{command} failed: {Name(result)} ({(int)result}).", (int)result);
        }
    }

    /// <summary>Returns the C name of <paramref name="result"/>, such as <c>VK_ERROR_DEVICE_LOST</c>, or its number.</summary>
    public static string Name(VkResult result)
    {
        string name = result.ToString();
        return char.IsAsciiLetter(name[0]) ? "VK_" + Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", "_").ToUpperInvariant() : name;
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

    private VulkanInstance(VulkanInstanceHandle handle, VkInstanceApi commands, uint loaderVersion, bool validation)
    {
        Handle = handle;
        Commands = commands;
        LoaderVersion = loaderVersion;
        ValidationEnabled = validation;
    }

    /// <summary>Gets the instance handle.</summary>
    public VulkanInstanceHandle Handle { get; }

    /// <summary>Gets the instance-level commands.</summary>
    public VkInstanceApi Commands { get; }

    /// <summary>Gets the Vulkan version the loader supports.</summary>
    public uint LoaderVersion { get; }

    /// <summary>Gets a value indicating whether the validation layer is on.</summary>
    public bool ValidationEnabled { get; }

    /// <summary>Formats a packed Vulkan version as major.minor.patch.</summary>
    public static Version ToVersion(uint packed) => new((int)(packed >> 22) & 0x7F, (int)(packed >> 12) & 0x3FF, (int)(packed & 0xFFF));

    /// <summary>Creates an instance, or explains in <paramref name="reason"/> why Vulkan is not usable here.</summary>
    public static VulkanInstance? TryCreate(out string? reason)
    {
        if (!VulkanLoader.IsLoaded)
        {
            reason = VulkanLoader.LibraryName is { } name
                ? $"The Vulkan loader ({name}) was not found; it comes with a GPU driver that supports Vulkan."
                : "Hardware video through Vulkan needs Windows or Linux.";
            if (!Environment.Is64BitProcess)
            {
                reason = "Hardware video needs a 64-bit process.";
            }

            return null;
        }

        uint loaderVersion = global::Vortice.Vulkan.Vulkan.vkEnumerateInstanceVersion();

        if (loaderVersion < RequiredApiVersion)
        {
            reason = $"The Vulkan loader supports Vulkan {ToVersion(loaderVersion)}; hardware video needs 1.3 or later.";
            return null;
        }

        bool validation = Environment.GetEnvironmentVariable("EGAMI_VULKAN_VALIDATION") == "1" && HasLayer(ValidationLayer);
        VulkanInstanceHandle handle = new();
        nint layerName = validation ? Marshal.StringToHGlobalAnsi(ValidationLayer) : 0;
        try
        {
            fixed (byte* appName = "Egami.Video.Hardware"u8)
            {
                VkApplicationInfo app = new() { pApplicationName = appName, pEngineName = appName, apiVersion = new VkVersion(RequiredApiVersion) };
                VkInstanceCreateInfo info = new()
                {
                    pApplicationInfo = &app,
                    enabledLayerCount = validation ? 1u : 0u,
                    ppEnabledLayerNames = (byte**)&layerName,
                };
                VkInstance instance;
                VkResult result = global::Vortice.Vulkan.Vulkan.vkCreateInstance(&info, null, &instance);
                if (result < 0)
                {
                    reason = $"vkCreateInstance failed: {VkCheck.Name(result)} ({(int)result}).";
                    handle.Dispose();
                    return null;
                }

                VkInstanceApi commands = new(instance);
                handle.Attach(instance.Handle, (delegate* unmanaged<nint, void*, void>)commands.vkDestroyInstance_ptr.Value);
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
        uint count = 0;
        VkCheck.ThrowIfFailed(Commands.vkEnumeratePhysicalDevices(&count, null), "vkEnumeratePhysicalDevices");
        VkPhysicalDevice[] handles = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* p = handles)
        {
            VkCheck.ThrowIfFailed(Commands.vkEnumeratePhysicalDevices(&count, p), "vkEnumeratePhysicalDevices");
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

    private static bool HasLayer(string name)
    {
        uint count = 0;
        if (global::Vortice.Vulkan.Vulkan.vkEnumerateInstanceLayerProperties(&count, null) < 0 || count == 0)
        {
            return false;
        }

        VkLayerProperties[] layers = new VkLayerProperties[count];
        fixed (VkLayerProperties* p = layers)
        {
            if (global::Vortice.Vulkan.Vulkan.vkEnumerateInstanceLayerProperties(&count, p) < 0)
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

/// <summary>
/// Loads the Vulkan loader the graphics driver installs (<c>vulkan-1.dll</c> on Windows, <c>libvulkan.so.1</c> on Linux) through
/// Vortice.Vulkan, once per process. Every Vulkan command is a function pointer the loader or driver hands back. Egami ships
/// no native code: when the driver has no loader, hardware video is not available.
/// </summary>
internal static class VulkanLoader
{
    private static readonly Lazy<bool> Loaded = new(Load);

    /// <summary>Gets the loader's file name on this operating system, or <see langword="null"/> where Egami does not look for one.</summary>
    public static string? LibraryName =>
        OperatingSystem.IsWindows() ? "vulkan-1.dll" : OperatingSystem.IsLinux() ? "libvulkan.so.1" : null;

    /// <summary>Gets a value indicating whether the loader was found and its global commands loaded.</summary>
    public static bool IsLoaded => Loaded.Value;

    private static bool Load()
    {
        // 32-bit processes are left out: non-dispatchable Vulkan handles are 64-bit and Egami keeps them in native ints.
        if (LibraryName is not { } name || !Environment.Is64BitProcess)
        {
            return false;
        }

        try
        {
            // The library stays loaded for the life of the process, as the loader expects.
            return global::Vortice.Vulkan.Vulkan.vkInitialize(name) == VkResult.Success;
        }
        catch (EntryPointNotFoundException)
        {
            // A library of that name without vkGetInstanceProcAddr is not a Vulkan loader.
            return false;
        }
    }
}

/// <summary>Converts to the one-bit <c>uint</c> fields of the Vulkan Video <c>StdVideo*Flags</c> structs.</summary>
internal static class VulkanBits
{
    /// <summary>Returns 1 for <see langword="true"/> and 0 for <see langword="false"/>.</summary>
    public static uint Of(bool value) => value ? 1u : 0u;
}
