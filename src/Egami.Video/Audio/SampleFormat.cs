namespace Egami.Video.Audio;

/// <summary>
/// How an <see cref="AudioFrame"/> stores its samples: the type of one sample, and whether the channels are interleaved (one
/// buffer, a sample of every channel in turn) or planar (one buffer per channel). Integer samples are signed except
/// <see cref="U8"/>, whose silence is 128; float samples run from -1 to 1.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-convert"/>
/// </example>
public enum SampleFormat
{
    /// <summary>Unsigned 8-bit, interleaved.</summary>
    U8,

    /// <summary>Signed 16-bit, interleaved.</summary>
    S16,

    /// <summary>Signed 32-bit, interleaved; also holds 24-bit and other depths, with <see cref="AudioFrame.ValidBits"/> saying how many bits are used.</summary>
    S32,

    /// <summary>Signed 64-bit, interleaved.</summary>
    S64,

    /// <summary>32-bit float, interleaved.</summary>
    F32,

    /// <summary>64-bit float, interleaved.</summary>
    F64,

    /// <summary>Unsigned 8-bit, planar.</summary>
    U8Planar,

    /// <summary>Signed 16-bit, planar.</summary>
    S16Planar,

    /// <summary>Signed 32-bit, planar. FLAC and integer PCM decode to this or <see cref="S16Planar"/>.</summary>
    S32Planar,

    /// <summary>Signed 64-bit, planar.</summary>
    S64Planar,

    /// <summary>32-bit float, planar: what lossy decoders hand out, and what the resampler and mixer work in.</summary>
    F32Planar,

    /// <summary>64-bit float, planar.</summary>
    F64Planar,
}

/// <summary>Facts about a <see cref="SampleFormat"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-convert"/>
/// </example>
public static class SampleFormatExtensions
{
    /// <summary>Returns the size of one sample in bytes: 1, 2, 4 or 8.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The size in bytes.</returns>
    public static int BytesPerSample(this SampleFormat format) => format.ToInterleaved() switch
    {
        SampleFormat.U8 => 1,
        SampleFormat.S16 => 2,
        SampleFormat.S32 or SampleFormat.F32 => 4,
        SampleFormat.S64 or SampleFormat.F64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Returns the number of bits in one sample: 8, 16, 32 or 64.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The size in bits.</returns>
    public static int BitsPerSample(this SampleFormat format) => format.BytesPerSample() * 8;

    /// <summary>Returns whether each channel has a buffer of its own.</summary>
    /// <param name="format">The format.</param>
    /// <returns>Whether the format is planar.</returns>
    public static bool IsPlanar(this SampleFormat format) => format >= SampleFormat.U8Planar;

    /// <summary>Returns whether samples are floating point.</summary>
    /// <param name="format">The format.</param>
    /// <returns>Whether the format is float.</returns>
    public static bool IsFloat(this SampleFormat format) => format.ToInterleaved() is SampleFormat.F32 or SampleFormat.F64;

    /// <summary>Returns the planar format with the same sample type.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The planar format.</returns>
    public static SampleFormat ToPlanar(this SampleFormat format) => format.IsPlanar() ? format : format + (int)SampleFormat.U8Planar;

    /// <summary>Returns the interleaved format with the same sample type.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The interleaved format.</returns>
    public static SampleFormat ToInterleaved(this SampleFormat format) => format.IsPlanar() ? format - (int)SampleFormat.U8Planar : format;
}
