namespace Egami.Video.Hardware;

/// <summary>Whether a hardware codec decodes or encodes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="available"/>
/// </example>
public enum HardwareDirection
{
    /// <summary>Decodes compressed video into frames.</summary>
    Decode,

    /// <summary>Encodes frames into compressed video.</summary>
    Encode,
}

/// <summary>The rate control modes a hardware encoder offers.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="devices"/>
/// </example>
[Flags]
public enum HardwareRateControl
{
    /// <summary>No rate control mode is reported (decoders).</summary>
    None = 0,

    /// <summary>A constant quantiser for each picture type; the bit rate follows the content.</summary>
    ConstantQp = 1,

    /// <summary>A constant bit rate, as streaming over a fixed link needs.</summary>
    ConstantBitrate = 2,

    /// <summary>A variable bit rate around a target, with a peak.</summary>
    VariableBitrate = 4,
}

/// <summary>What kind of device a GPU is.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="devices"/>
/// </example>
public enum HardwareDeviceKind
{
    /// <summary>A device the driver does not classify.</summary>
    Other,

    /// <summary>A GPU built into the CPU, sharing its memory.</summary>
    Integrated,

    /// <summary>A separate graphics card with its own memory.</summary>
    Discrete,

    /// <summary>A GPU of a virtual machine.</summary>
    Virtual,

    /// <summary>A software device running on the CPU.</summary>
    Cpu,
}

/// <summary>A GPU with hardware video codecs, as the driver reports it.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="devices"/>
/// </example>
public sealed class HardwareVideoDevice
{
    internal HardwareVideoDevice(
        string name, HardwareDeviceKind kind, int vendorId, int deviceId, string api, Version apiVersion, string driverName,
        string driverInfo, IReadOnlyList<HardwareVideoProfile> profiles)
    {
        Name = name;
        Kind = kind;
        VendorId = vendorId;
        DeviceId = deviceId;
        Api = api;
        ApiVersion = apiVersion;
        DriverName = driverName;
        DriverInfo = driverInfo;
        Profiles = profiles;
    }

    /// <summary>Gets the device's name, such as "NVIDIA GeForce GTX 1070".</summary>
    public string Name { get; }

    /// <summary>Gets what kind of device it is.</summary>
    public HardwareDeviceKind Kind { get; }

    /// <summary>Gets the PCI vendor id: 0x10DE for NVIDIA, 0x1002 for AMD, 0x8086 for Intel.</summary>
    public int VendorId { get; }

    /// <summary>Gets the vendor's id of the device model.</summary>
    public int DeviceId { get; }

    /// <summary>Gets the API the codecs are reached through: "Vulkan".</summary>
    public string Api { get; }

    /// <summary>Gets the version of <see cref="Api"/> the driver supports on this device.</summary>
    public Version ApiVersion { get; }

    /// <summary>Gets the driver's name.</summary>
    public string DriverName { get; }

    /// <summary>Gets the driver's own description of its version.</summary>
    public string DriverInfo { get; }

    /// <summary>Gets the codec profiles the device decodes or encodes; empty when it has no video hardware Egami can use.</summary>
    public IReadOnlyList<HardwareVideoProfile> Profiles { get; }

    /// <summary>Returns the name, the API version and the driver.</summary>
    /// <returns>A one-line description.</returns>
    public override string ToString() => $"{Name} ({Api} {ApiVersion}, {DriverName} {DriverInfo})";
}

/// <summary>One codec profile a GPU decodes or encodes in hardware, with its limits.</summary>
/// <remarks>Every profile is 4:2:0. The encode-only members are zero, or <see cref="HardwareRateControl.None"/>, for decoders.</remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHardware.cs" region="devices"/>
/// </example>
public sealed class HardwareVideoProfile
{
    internal HardwareVideoProfile(string codecId, HardwareDirection direction, string profile, int bitDepth)
    {
        CodecId = codecId;
        Direction = direction;
        Profile = profile;
        BitDepth = bitDepth;
    }

    /// <summary>Gets the codec, as an <see cref="CodecIds"/> value such as <see cref="CodecIds.Avc"/>.</summary>
    public string CodecId { get; }

    /// <summary>Gets whether this profile decodes or encodes.</summary>
    public HardwareDirection Direction { get; }

    /// <summary>Gets the codec profile, such as "High" or "Main 10". A decoder of a profile also decodes the profiles below it.</summary>
    public string Profile { get; }

    /// <summary>Gets the bit depth of the samples: 8 or 10.</summary>
    public int BitDepth { get; }

    /// <summary>Gets the smallest width in pixels.</summary>
    public int MinWidth { get; internal init; }

    /// <summary>Gets the smallest height in pixels.</summary>
    public int MinHeight { get; internal init; }

    /// <summary>Gets the largest width in pixels.</summary>
    public int MaxWidth { get; internal init; }

    /// <summary>Gets the largest height in pixels.</summary>
    public int MaxHeight { get; internal init; }

    /// <summary>Gets the highest codec level, such as "5.2".</summary>
    public string MaxLevel { get; internal init; } = "";

    /// <summary>Gets how many reference pictures one picture can use at most.</summary>
    public int MaxReferences { get; internal init; }

    /// <summary>Gets the rate control modes the encoder offers.</summary>
    public HardwareRateControl RateControl { get; internal init; }

    /// <summary>Gets how many quality levels the encoder offers, from fastest to best; 0 for decoders.</summary>
    public int QualityLevels { get; internal init; }

    /// <summary>Gets the highest bit rate the encoder supports, in bits per second; 0 for decoders.</summary>
    public long MaxBitrate { get; internal init; }

    /// <summary>Gets how many earlier pictures a P picture can reference; 0 for decoders.</summary>
    public int MaxForwardReferences { get; internal init; }

    /// <summary>Gets how many later pictures a B picture can reference: 0 when the encoder writes no B pictures, and for decoders.</summary>
    public int MaxBackwardReferences { get; internal init; }

    /// <summary>Returns the codec, direction, profile and limits on one line.</summary>
    /// <returns>A one-line description.</returns>
    public override string ToString()
    {
        string codec = CodecId == CodecIds.Avc ? "H.264" : CodecId == CodecIds.Hevc ? "HEVC" : CodecId;
        string text = $"{codec} {Direction.ToString().ToLowerInvariant()} {Profile} {BitDepth}-bit, {MinWidth}x{MinHeight} to {MaxWidth}x{MaxHeight}, level {MaxLevel}, {MaxReferences} references";
        return Direction == HardwareDirection.Encode
            ? $"{text}, rate control {RateControl}, {QualityLevels} quality levels, P {MaxForwardReferences} / B {MaxBackwardReferences}"
            : text;
    }
}
