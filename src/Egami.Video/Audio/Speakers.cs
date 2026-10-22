namespace Egami.Video.Audio;

/// <summary>
/// Speaker positions, with the bit values of WAVE_FORMAT_EXTENSIBLE's channel mask. Channels in a <see cref="ChannelLayout"/>
/// come in the order of these bits, lowest first, which is the order WAV and FLAC store them in.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-layouts"/>
/// </example>
[Flags]
public enum Speakers : uint
{
    /// <summary>No speaker.</summary>
    None = 0,

    /// <summary>Front left.</summary>
    FrontLeft = 0x1,

    /// <summary>Front right.</summary>
    FrontRight = 0x2,

    /// <summary>Front center.</summary>
    FrontCenter = 0x4,

    /// <summary>Low-frequency effects (the subwoofer, the ".1").</summary>
    LowFrequency = 0x8,

    /// <summary>Back left (the surround left of 5.1 in WAV order).</summary>
    BackLeft = 0x10,

    /// <summary>Back right (the surround right of 5.1 in WAV order).</summary>
    BackRight = 0x20,

    /// <summary>Front left of center.</summary>
    FrontLeftOfCenter = 0x40,

    /// <summary>Front right of center.</summary>
    FrontRightOfCenter = 0x80,

    /// <summary>Back center.</summary>
    BackCenter = 0x100,

    /// <summary>Side left.</summary>
    SideLeft = 0x200,

    /// <summary>Side right.</summary>
    SideRight = 0x400,

    /// <summary>Top center, above the listener.</summary>
    TopCenter = 0x800,

    /// <summary>Top front left.</summary>
    TopFrontLeft = 0x1000,

    /// <summary>Top front center.</summary>
    TopFrontCenter = 0x2000,

    /// <summary>Top front right.</summary>
    TopFrontRight = 0x4000,

    /// <summary>Top back left.</summary>
    TopBackLeft = 0x8000,

    /// <summary>Top back center.</summary>
    TopBackCenter = 0x10000,

    /// <summary>Top back right.</summary>
    TopBackRight = 0x20000,
}
