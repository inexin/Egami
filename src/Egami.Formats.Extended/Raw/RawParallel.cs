namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Row loops of a develop: <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>, or a plain loop when one thread
/// is asked for, which skips the task machinery (and its allocations, which grow with the loop's length).
/// </summary>
internal static class RawParallel
{
    public static void For(int from, int to, ParallelOptions options, Action<int> body)
    {
        if (options.MaxDegreeOfParallelism != 1)
        {
            Parallel.For(from, to, options, body);
            return;
        }

        for (int i = from; i < to; i++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            body(i);
        }
    }
}
