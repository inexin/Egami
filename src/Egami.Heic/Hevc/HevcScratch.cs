namespace Egami.Heic.Hevc;

/// <summary>
/// The working buffers of a slice decoder, one set per thread and used again for every picture, so decoding a picture allocates
/// none of them. A slice decoder takes them when it is made; no two decoders on one thread run at once.
/// </summary>
internal sealed class HevcScratch
{
    [ThreadStatic]
    private static HevcScratch? t_current;

    /// <summary>Gets this thread's buffers.</summary>
    internal static HevcScratch Current => t_current ??= new HevcScratch();

    internal HevcIntra Intra { get; } = new();

    internal HevcInter Inter { get; } = new();

    /// <summary>Gets the coefficient levels of a transform block: all zeros between blocks (Reconstruct clears what it used).</summary>
    internal int[] Coefficients { get; } = new int[32 * 32];

    internal int[] Residual { get; } = new int[32 * 32];

    internal int[] LumaResidual { get; } = new int[32 * 32];

    internal short[] Scratch { get; } = new short[32 * 32];
}
