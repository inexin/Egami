namespace Egami.Video.Audio.Codecs;

/// <summary>
/// How the Opus encoder codes: the bitrate and how strictly it is kept, what the audio is, the frame duration, how hard the
/// encoder searches, the widest band and the loss protection. Every one has a default that suits music and most speech.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-opus-encode"/>
/// </example>
public sealed record OpusEncoderOptions
{
    private readonly int? _bitrate;
    private readonly TimeSpan _frameDuration = TimeSpan.FromMilliseconds(20);
    private readonly int _effort = 10;
    private readonly int _expectedLoss;

    /// <summary>Gets the defaults.</summary>
    public static OpusEncoderOptions Default { get; } = new();

    /// <summary>
    /// Gets the bitrate in bits per second for all channels together, 500 to 2400000, or <see langword="null"/> (the default)
    /// for 64 kbit/s per channel. Opus codes 500 bit/s to 300 kbit/s per channel; a value outside that is clamped.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 500 to 2400000.</exception>
    public int? Bitrate
    {
        get => _bitrate;
        init => _bitrate = value is int v ? InRange(v, 500, 2_400_000) : null;
    }

    /// <summary>Gets how strictly the bitrate is kept. Default <see cref="OpusRateMode.Vbr"/>.</summary>
    public OpusRateMode RateMode { get; init; }

    /// <summary>Gets what the audio is, which steers the choice between the speech and music coders. Default <see cref="OpusApplication.Audio"/>.</summary>
    public OpusApplication Application { get; init; }

    /// <summary>
    /// Gets the audio in each packet: 2.5, 5, 10, 20, 40, 60, 80, 100 or 120 milliseconds. Default 20 ms. Shorter frames lower
    /// the delay and cost bits; longer ones save a little on speech.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not one of the durations Opus codes.</exception>
    public TimeSpan FrameDuration
    {
        get => _frameDuration;
        init => _frameDuration = value.Ticks is 25_000 or 50_000 or 100_000 or 200_000 or 400_000 or 600_000 or 800_000 or 1_000_000 or 1_200_000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Opus frames last 2.5, 5, 10, 20, 40, 60, 80, 100 or 120 ms.");
    }

    /// <summary>
    /// Gets how hard the encoder searches, 0 (fastest) to 10 (best, the default); libopus calls this the complexity. Below 10
    /// the encoder skips the tonality analysis and simplifies its searches.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 10.</exception>
    public int Effort
    {
        get => _effort;
        init => _effort = InRange(value, 0, 10);
    }

    /// <summary>
    /// Gets the widest band the encoder may code, or <see langword="null"/> (the default) to let it choose from the bitrate.
    /// It never codes wider than half the sample rate.
    /// </summary>
    public OpusBandwidth? Bandwidth { get; init; }

    /// <summary>
    /// Gets whether speech packets also carry a coarse copy of the packet before, which a decoder uses when that one is lost.
    /// Off by default; it costs bits and only helps with <see cref="ExpectedLoss"/> above 0.
    /// </summary>
    public bool InbandFec { get; init; }

    /// <summary>Gets the share of packets expected to be lost, 0 (the default) to 100 percent, which the encoder protects against.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int ExpectedLoss
    {
        get => _expectedLoss;
        init => _expectedLoss = InRange(value, 0, 100);
    }

    /// <summary>
    /// Gets whether silence in speech is sent as one tiny packet every 400 ms instead of a packet every frame (discontinuous
    /// transmission). Off by default.
    /// </summary>
    public bool Dtx { get; init; }

    private static int InRange(int value, int min, int max, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"The value must be {min} to {max}.") : value;
}

/// <summary>How strictly the Opus encoder keeps its bitrate.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-opus-encode"/>
/// </example>
public enum OpusRateMode
{
    /// <summary>Variable: the bitrate is the average, and packets grow where the audio is harder. The best quality per bit.</summary>
    Vbr,

    /// <summary>Constrained variable: packets vary, but the rate over a short window stays near the bitrate, for streaming.</summary>
    ConstrainedVbr,

    /// <summary>Constant: every packet of a frame duration has the same size, for links that need that.</summary>
    Cbr,
}

/// <summary>What the audio given to the Opus encoder is.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-opus-encode"/>
/// </example>
public enum OpusApplication
{
    /// <summary>Music or mixed content, coded for fidelity to the input.</summary>
    Audio,

    /// <summary>Speech, such as calls and voice recordings, coded for intelligibility.</summary>
    Voice,

    /// <summary>The lowest delay: the speech coder is off and the encoder adds 2.5 ms instead of 6.5 ms.</summary>
    LowDelay,
}

/// <summary>The audio bands Opus codes, named by their upper edge.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-opus-encode"/>
/// </example>
public enum OpusBandwidth
{
    /// <summary>Up to 4 kHz.</summary>
    Narrowband = 1101,

    /// <summary>Up to 6 kHz.</summary>
    Mediumband = 1102,

    /// <summary>Up to 8 kHz.</summary>
    Wideband = 1103,

    /// <summary>Up to 12 kHz.</summary>
    Superwideband = 1104,

    /// <summary>Up to 20 kHz.</summary>
    Fullband = 1105,
}
