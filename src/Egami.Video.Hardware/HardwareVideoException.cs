namespace Egami.Video.Hardware;

/// <summary>Thrown when the GPU driver refuses or fails a hardware video operation.</summary>
/// <remarks>
/// <see cref="ResultCode"/> holds the driver's result: for Vulkan Video a negative <c>VkResult</c>, such as -4
/// (<c>VK_ERROR_DEVICE_LOST</c>) when the driver reset the GPU. Mistakes in arguments throw the standard
/// <see cref="ArgumentException"/> types instead.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="available"/>
/// </example>
public sealed class HardwareVideoException : EgamiException
{
    /// <summary>Initializes a new instance of the <see cref="HardwareVideoException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="resultCode">The driver's result code, or 0 when the driver gave none.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public HardwareVideoException(string message, int resultCode = 0, Exception? innerException = null)
        : base(message, innerException)
    {
        ResultCode = resultCode;
    }

    /// <summary>Gets the driver's result code: for Vulkan Video a negative <c>VkResult</c>, or 0 when the driver gave none.</summary>
    public int ResultCode { get; }
}
