using Egami.Pixels;

namespace Egami.Operations;

/// <summary>Processes a band of rows: rows <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive).</summary>
/// <param name="start">The first row.</param>
/// <param name="end">The row just past the last one.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="row-band-operation"/>
/// </example>
public delegate void RowBandAction(int start, int end);

/// <summary>Everything an operation gets for one frame.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="custom-operation"/>
/// </example>
public sealed class OperationInput<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    internal OperationInput(PixelPlane<TPixel> source, PixelPlane<TPixel> destination, EgamiContext context, int frameIndex, CancellationToken cancellationToken)
    {
        Source = source;
        Destination = destination;
        Context = context;
        FrameIndex = frameIndex;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets the pixels to read. Do not change them unless <see cref="IsInPlace"/>.</summary>
    public PixelPlane<TPixel> Source { get; }

    /// <summary>Gets the plane to write, sized by <see cref="IRasterOperation.OutputSize"/>. Every pixel must be written.</summary>
    public PixelPlane<TPixel> Destination { get; }

    /// <summary>Gets a value indicating whether <see cref="Source"/> and <see cref="Destination"/> are the same plane.</summary>
    public bool IsInPlace => ReferenceEquals(Source, Destination);

    /// <summary>Gets the context, for memory, limits and <see cref="EgamiContext.MaxParallelism"/>.</summary>
    public EgamiContext Context { get; }

    /// <summary>Gets the index of the frame being processed.</summary>
    public int FrameIndex { get; }

    /// <summary>Gets the token that cancels the chain. <see cref="ForEachBand"/> checks it between bands.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Runs <paramref name="action"/> over the destination's rows in bands, in parallel up to <see cref="EgamiContext.MaxParallelism"/>,
    /// the way built-in operations do. Bands are at least 16 rows; small images run on the calling thread.
    /// </summary>
    /// <param name="action">The work for one band. Bands may run at the same time on different threads.</param>
    /// <exception cref="OperationCanceledException">The chain was cancelled.</exception>
    public void ForEachBand(RowBandAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RowRunner.Run(Destination.Height, (long)Destination.Width * PixelSize, Context.MaxParallelism, action, CancellationToken);
    }

    private static int PixelSize => System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>();
}
