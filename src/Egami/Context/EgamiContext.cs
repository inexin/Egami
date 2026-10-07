namespace Egami;

/// <summary>
/// Every setting that is not specific to one call: where memory comes from, how many threads an operation may use, and the safety limits.
/// Contexts are immutable and safe to share. Change one with a <c>with</c> expression.
/// </summary>
/// <example>
/// <code>
/// var context = EgamiContext.Default with
/// {
///     Limits = SafetyLimits.Default with { MaxPixels = 100_000_000 },
///     MaxParallelism = 4,
/// };
/// </code>
/// </example>
public sealed record EgamiContext
{
    private readonly IPixelMemorySource _memory = PooledPixelMemorySource.Shared;
    private readonly int _maxParallelism = Environment.ProcessorCount;
    private readonly SafetyLimits _limits = SafetyLimits.Default;

    /// <summary>Gets the context used when a call passes none.</summary>
    public static EgamiContext Default { get; } = new();

    /// <summary>Gets the source of all pixel memory. Defaults to <see cref="PooledPixelMemorySource.Shared"/>.</summary>
    public IPixelMemorySource Memory { get => _memory; init => _memory = value ?? throw new ArgumentNullException(nameof(value)); }

    /// <summary>Gets the most threads one operation may use. 1 means fully sequential. Defaults to the processor count.</summary>
    public int MaxParallelism
    {
        get => _maxParallelism;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxParallelism = value;
        }
    }

    /// <summary>Gets the safety limits applied when reading. Defaults to <see cref="SafetyLimits.Default"/>.</summary>
    public SafetyLimits Limits { get => _limits; init => _limits = value ?? throw new ArgumentNullException(nameof(value)); }
}
