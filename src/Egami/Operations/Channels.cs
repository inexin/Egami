namespace Egami.Operations;

/// <summary>One channel of a raster's working RGBA, or its luminance, for <see cref="Raster.ExtractChannel(ColorChannel)"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Channels.cs" region="extract-combine"/>
/// </example>
public enum ColorChannel
{
    /// <summary>Red; a gray raster's gray value.</summary>
    Red = 0,

    /// <summary>Green; a gray raster's gray value.</summary>
    Green = 1,

    /// <summary>Blue; a gray raster's gray value.</summary>
    Blue = 2,

    /// <summary>Alpha; opaque on a raster without alpha.</summary>
    Alpha = 3,

    /// <summary>The Rec. 709 luminance of the stored values, 0.2126 R + 0.7152 G + 0.0722 B, as Grayscale computes it.</summary>
    Luminance = 4,
}

/// <summary>A set of a raster's working RGBA channels, for <see cref="RasterOps.OnChannels(ColorChannels, Action{RasterOps})"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Channels.cs" region="on-channels"/>
/// </example>
[Flags]
public enum ColorChannels
{
    /// <summary>No channel.</summary>
    None = 0,

    /// <summary>Red.</summary>
    Red = 1,

    /// <summary>Green.</summary>
    Green = 2,

    /// <summary>Blue.</summary>
    Blue = 4,

    /// <summary>Alpha.</summary>
    Alpha = 8,

    /// <summary>Red, green and blue.</summary>
    Color = Red | Green | Blue,

    /// <summary>Every channel.</summary>
    All = Color | Alpha,
}

/// <summary>Where an output channel of <see cref="RasterOps.ShuffleChannels"/> takes its value from.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Channels.cs" region="shuffle"/>
/// </example>
public enum ChannelSource
{
    /// <summary>The input's red.</summary>
    Red = 0,

    /// <summary>The input's green.</summary>
    Green = 1,

    /// <summary>The input's blue.</summary>
    Blue = 2,

    /// <summary>The input's alpha.</summary>
    Alpha = 3,

    /// <summary>Always 0.</summary>
    Zero = 4,

    /// <summary>Always full (1).</summary>
    One = 5,
}
