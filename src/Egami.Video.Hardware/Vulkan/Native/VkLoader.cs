using System.Runtime.InteropServices;

namespace Egami.Video.Hardware.Vulkan.Native;

/// <summary>
/// Finds the Vulkan loader the graphics driver installs (<c>vulkan-1.dll</c> on Windows, <c>libvulkan.so.1</c> on Linux) and
/// takes its one exported entry point, <c>vkGetInstanceProcAddr</c>. Every other Vulkan command is a function pointer that
/// <c>vkGetInstanceProcAddr</c> or <c>vkGetDeviceProcAddr</c> hands back. Egami ships no native code: when the driver has no
/// loader, hardware video is not available.
/// </summary>
internal static unsafe class VkLoader
{
    private static readonly Lazy<nint> GetInstanceProcAddrPointer = new(Find);

    /// <summary>Gets the loader's file name on this operating system, or <see langword="null"/> where Egami does not look for one.</summary>
    public static string? LibraryName =>
        OperatingSystem.IsWindows() ? "vulkan-1.dll" : OperatingSystem.IsLinux() ? "libvulkan.so.1" : null;

    /// <summary>Gets <c>vkGetInstanceProcAddr</c>, or null when no loader was found.</summary>
    public static delegate* unmanaged<nint, byte*, nint> GetInstanceProcAddr => (delegate* unmanaged<nint, byte*, nint>)GetInstanceProcAddrPointer.Value;

    /// <summary>Gets a value indicating whether the loader was found.</summary>
    public static bool IsLoaded => GetInstanceProcAddrPointer.Value != 0;

    /// <summary>Looks up a command by its null-terminated name; null when the loader or driver does not have it.</summary>
    public static nint Load(delegate* unmanaged<nint, byte*, nint> getProcAddr, nint handle, ReadOnlySpan<byte> name)
    {
        if (getProcAddr == null)
        {
            return 0;
        }

        // The u8 literals the generated tables pass end in a null byte the span does not include.
        fixed (byte* p = name)
        {
            return getProcAddr(handle, p);
        }
    }

    private static nint Find()
    {
        // 32-bit processes are left out: non-dispatchable Vulkan handles are 64-bit and the bindings keep them in native ints.
        if (LibraryName is not { } name || !Environment.Is64BitProcess)
        {
            return 0;
        }

        // The library stays loaded for the life of the process, as the loader expects.
        return NativeLibrary.TryLoad(name, out nint library) && NativeLibrary.TryGetExport(library, "vkGetInstanceProcAddr", out nint export) ? export : 0;
    }
}
