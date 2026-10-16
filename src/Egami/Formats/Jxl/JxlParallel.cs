using System.Runtime.ExceptionServices;

namespace Egami.Formats.Jxl;

/// <summary>
/// Runs the decoder's independent pieces (groups, rows) on several threads, bounded by <see cref="EgamiContext.MaxParallelism"/>
/// (design question 10). Every piece writes only its own samples, so the output is the same at every thread count.
/// </summary>
internal readonly record struct JxlParallel(int Threads, CancellationToken CancellationToken)
{
    /// <summary>Gets a single thread, for callers without a context.</summary>
    internal static JxlParallel Single { get; } = new(1, CancellationToken.None);

    /// <summary>Calls <paramref name="body"/> for 0 to <paramref name="count"/> − 1, on one thread when there is little to share.</summary>
    internal void For(int count, Action<int> body, int minimumPerThread = 1)
    {
        int threads = Math.Min(Threads, count / Math.Max(1, minimumPerThread));
        if (threads <= 1)
        {
            for (int i = 0; i < count; i++)
            {
                body(i);
            }

            return;
        }

        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = CancellationToken }, body);
        }
        catch (AggregateException e) when (e.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
        }
    }

    /// <summary>Calls <paramref name="body"/> with bands of rows covering 0 to <paramref name="rows"/>, a band per piece of work.</summary>
    internal void Rows(int rows, Action<int, int> body)
    {
        const int Band = 32;
        int bands = (rows + Band - 1) / Band;
        For(bands, b => body(b * Band, Math.Min(rows, (b + 1) * Band)), minimumPerThread: 2);
    }
}
