namespace Egami.Cli.Hosting;

/// <summary>How a batch runs.</summary>
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
    public static int Run(CliHostSession session, IReadOnlyList<InputFile> inputs, BatchOptions options, Func<InputFile, IReadOnlyList<string>> work)
    {
        int jobs = options.Jobs > 0 ? options.Jobs : Environment.ProcessorCount;
        int done = 0, failed = 0, limited = 0, finished = 0;
        bool usage = false;
        UsageException? usageError = null;

        void One(InputFile file, ParallelLoopState? state)
        {
            if (session.Cancellation.IsCancellationRequested)
            {
                state?.Stop();
                return;
            }

            try
            {
                work(file);
                Interlocked.Increment(ref done);
            }
            catch (UsageException error)
            {
                usage = true;
                usageError ??= error;
                state?.Stop();
            }
            catch (Exception error) when (session.Failure(error) is { } code)
            {
                Interlocked.Increment(ref failed);
                if (code == ExitCodes.Limit)
                {
                    Interlocked.Increment(ref limited);
                }

                session.Error($"{file.Display}: {session.Message(error)}");
                if (options.FailFast)
                {
                    state?.Stop();
                }
            }
            finally
            {
                session.Progress?.Invoke(Interlocked.Increment(ref finished), inputs.Count);
            }
        }

        if (inputs.Count == 1 || jobs == 1)
        {
            foreach (InputFile file in inputs)
            {
                One(file, null);
                if (usage || (options.FailFast && failed > 0) || session.Cancellation.IsCancellationRequested)
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

        session.Cancellation.ThrowIfCancellationRequested();

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

    /// <summary>Whether two paths name the same file.</summary>
    public static bool SamePath(string a, string b)
    {
        return a != "-" && b != "-" && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// <summary>Writes outputs: to standard output, or to a file through a temporary name that replaces the target only once complete.</summary>
internal static class Output
{
    private static readonly Lock StdoutLock = new();

    /// <summary>Writes an output; <paramref name="path"/> is a full path or <c>-</c>.</summary>
    public static void Write(CliHostSession session, string path, Action<Stream> write)
    {
        if (path == "-")
        {
            lock (StdoutLock)
            {
                using MemoryStream buffer = new();
                write(buffer);
                Stream stdout = session.Console.Out();
                buffer.Position = 0;
                buffer.CopyTo(stdout);
                stdout.Flush();
            }

            return;
        }

        session.Paths?.Check(Path.GetFullPath(path, session.WorkingDirectory));
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temporary = Path.Combine(folder ?? string.Empty, $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.{session.ToolName}-tmp");
        try
        {
            using (FileStream file = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                write(file);
            }

            File.Move(temporary, path, overwrite: true);
            session.Written.Enqueue(path);
            session.Info($"wrote {path}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
