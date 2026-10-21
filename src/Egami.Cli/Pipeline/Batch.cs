using Egami.Cli.Host;

namespace Egami.Cli.Pipeline;

/// <summary>How a batch runs (CLI design Q11).</summary>
/// <param name="Jobs">How many files run at once; 0 for the processor count.</param>
/// <param name="FailFast">Whether the first failed file stops the batch.</param>
/// <param name="InPlace">Whether an output may replace its input.</param>
internal sealed record BatchOptions(int Jobs, bool FailFast, bool InPlace)
{
    /// <summary>Gets one file at a time, carrying on after failures, never writing over an input.</summary>
    public static BatchOptions Default { get; } = new(0, false, false);
}

/// <summary>
/// Runs a command over every input: several files at once (each image single-threaded while several run, so the output never
/// depends on <c>--jobs</c>), a failed file reported and the rest carried on unless <c>--fail-fast</c>, and a summary at the end.
/// </summary>
internal static class Batch
{
    /// <summary>Runs <paramref name="work"/> for every input and returns the exit code.</summary>
    public static int Run(CliSession session, IReadOnlyList<InputFile> inputs, BatchOptions options, Func<InputSource, IReadOnlyList<string>> work)
    {
        int jobs = options.Jobs > 0 ? options.Jobs : Environment.ProcessorCount;
        int done = 0, failed = 0, limited = 0;
        bool usage = false;
        UsageException? usageError = null;

        void One(InputFile file, ParallelLoopState? state)
        {
            try
            {
                work(new InputSource(file));
                Interlocked.Increment(ref done);
            }
            catch (UsageException error)
            {
                usage = true;
                usageError ??= error;
                state?.Stop();
            }
            catch (Exception error) when (Failure(error) is { } code)
            {
                Interlocked.Increment(ref failed);
                if (code == ExitCodes.Limit)
                {
                    Interlocked.Increment(ref limited);
                }

                session.Error($"{file.Display}: {Message(error)}");
                if (options.FailFast)
                {
                    state?.Stop();
                }
            }
        }

        if (inputs.Count == 1 || jobs == 1)
        {
            foreach (InputFile file in inputs)
            {
                One(file, null);
                if (usage || (options.FailFast && failed > 0))
                {
                    break;
                }
            }
        }
        else
        {
            Parallel.ForEach(inputs, new ParallelOptions { MaxDegreeOfParallelism = jobs }, (file, state) => One(file, state));
        }

        if (usageError is not null)
        {
            throw usageError;
        }

        if (inputs.Count > 1)
        {
            int skipped = inputs.Count - done - failed;
            string summary = $"{inputs.Count} files: {done} done, {failed} failed" + (skipped > 0 ? $", {skipped} not run" : string.Empty);
            if (failed > 0)
            {
                session.Error(summary);
            }
            else
            {
                session.Info(summary);
            }
        }

        return limited > 0 ? ExitCodes.Limit : failed > 0 ? ExitCodes.Failed : ExitCodes.Success;
    }

    /// <summary>The exit code an exception from one file maps to, or <see langword="null"/> for a bug, which is not caught.</summary>
    public static int? Failure(Exception error)
    {
        return error switch
        {
            LimitExceededException => ExitCodes.Limit,
            EgamiException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException => ExitCodes.Failed,
            _ => null,
        };
    }

    /// <summary>A one-line message for a failed file.</summary>
    public static string Message(Exception error)
    {
        return error switch
        {
            LimitExceededException limit => $"exceeds a safety limit: {limit.Message} Raise it with --limit if you trust the file.",
            FileNotFoundException missing => missing.Message,
            _ => error.Message,
        };
    }

    /// <summary>Whether two paths name the same file.</summary>
    public static bool SamePath(string a, string b)
    {
        return a != "-" && b != "-" && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
