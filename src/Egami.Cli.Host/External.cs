using System.Diagnostics;

namespace Egami.Cli.Hosting;

/// <summary>Runs <c>&lt;tool&gt; &lt;name&gt;</c> as the program <c>&lt;tool&gt;-&lt;name&gt;</c> from the PATH, as git and dotnet do.</summary>
internal static class External
{
    /// <summary>Finds <c>&lt;tool&gt;-&lt;name&gt;</c> on the PATH, or returns <see langword="null"/>.</summary>
    public static string? Find(string tool, string name, Func<string, string?> environment)
    {
        if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':', '.']) >= 0 || Globber.HasWildcard(name))
        {
            return null;
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? (environment("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (string folder in (environment("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(folder.Trim('"'), $"{tool}-{name}{extension.ToLowerInvariant()}");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Runs the program with the rest of the arguments and the same standard streams, and returns its exit code.</summary>
    public static int Run(string program, string[] args, CliConsole console)
    {
        bool script = OperatingSystem.IsWindows() && Path.GetExtension(program).ToLowerInvariant() is ".cmd" or ".bat";
        ProcessStartInfo start = new(script ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = !ReferenceEquals(console, CliConsole.System),
            RedirectStandardError = !ReferenceEquals(console, CliConsole.System),
        };
        if (script)
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(program);
        }

        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start) ?? throw new IOException($"Could not start {program}.");
        if (start.RedirectStandardOutput)
        {
            Task<string> error = process.StandardError.ReadToEndAsync();
            console.Text.Write(process.StandardOutput.ReadToEnd());
            console.Error.Write(error.GetAwaiter().GetResult());
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
