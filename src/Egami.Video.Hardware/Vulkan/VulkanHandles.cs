using System.Runtime.InteropServices;

using Egami.Video.Hardware.Vulkan.Native;

namespace Egami.Video.Hardware.Vulkan;

/// <summary>A <c>VkInstance</c>, destroyed when released.</summary>
internal sealed unsafe class VulkanInstanceHandle : SafeHandle
{
    private delegate* unmanaged<nint, void*, void> destroy;

    public VulkanInstanceHandle()
        : base(0, ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == 0;

    /// <summary>Takes ownership of a created instance and the command that destroys it.</summary>
    public void Attach(nint instance, delegate* unmanaged<nint, void*, void> destroyInstance)
    {
        destroy = destroyInstance;
        SetHandle(instance);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        if (destroy != null)
        {
            destroy(handle, null);
        }

        return true;
    }
}

/// <summary>A <c>VkDevice</c>, destroyed when released. It keeps its instance alive until then.</summary>
internal sealed unsafe class VulkanDeviceHandle : SafeHandle
{
    private delegate* unmanaged<nint, void*, void> destroy;
    private VulkanInstanceHandle? instance;

    public VulkanDeviceHandle()
        : base(0, ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == 0;

    /// <summary>Takes ownership of a created device and the command that destroys it, and holds a reference on its instance.</summary>
    public void Attach(nint device, delegate* unmanaged<nint, void*, void> destroyDevice, VulkanInstanceHandle owner)
    {
        bool added = false;
        owner.DangerousAddRef(ref added);
        instance = owner;
        destroy = destroyDevice;
        SetHandle(device);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        if (destroy != null)
        {
            destroy(handle, null);
        }

        instance?.DangerousRelease();
        return true;
    }
}

/// <summary>
/// A non-dispatchable Vulkan object a device owns (a buffer, image, image view, device memory, command pool, fence, semaphore,
/// query pool, video session or video session parameters), destroyed with its <c>vkDestroy*</c> or <c>vkFreeMemory</c> command
/// when released. It keeps its device alive until then.
/// </summary>
/// <remarks>
/// The 64-bit handle is kept in the native-sized handle field; <see cref="VkLoader"/> loads Vulkan only in 64-bit processes.
/// </remarks>
internal sealed unsafe class VulkanObjectHandle : SafeHandle
{
    private readonly delegate* unmanaged<nint, ulong, void*, void> destroy;
    private readonly VulkanDeviceHandle device;

    /// <summary>Takes ownership of <paramref name="value"/>, which <paramref name="destroyObject"/> destroys on <paramref name="owner"/>.</summary>
    public VulkanObjectHandle(VulkanDeviceHandle owner, ulong value, delegate* unmanaged<nint, ulong, void*, void> destroyObject)
        : base(0, ownsHandle: true)
    {
        bool added = false;
        owner.DangerousAddRef(ref added);
        device = owner;
        destroy = destroyObject;
        SetHandle((nint)value);
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == 0;

    /// <summary>Gets the Vulkan handle, or 0 once released.</summary>
    public ulong Value => IsClosed ? 0 : (ulong)handle;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        destroy(device.DangerousGetHandle(), (ulong)handle, null);
        device.DangerousRelease();
        return true;
    }
}
