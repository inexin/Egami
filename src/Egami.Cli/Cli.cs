using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;

using Egami.Cli.Commands;
using Egami.Cli.Host;
using Egami.Cli.Steps;

namespace Egami.Cli;

/// <summary>
/// The tool's entry point, shared by the executable and the tests: builds the commands, parses, maps errors to exit codes
/// (0 success, 1 a file failed, 2 usage, 3 a safety limit) and runs unknown commands as <c>egami-&lt;name&gt;</c> programs from
/// the PATH (CLI design Q13, Q14).
/// </summary>
internal static class Cli
{
    /// <summary>Runs the tool in this process.</summary>
    public static int Run(string[] args, CliConsole console, string workingDirectory, Func<string, string?> environment)
    {
        CliSession session = new(console, workingDirectory, environment);
        ValueParsers.Session = session;
        try
        {
            Command root = CreateRoot(session);
            if (args.Length > 0 && !args[0].StartsWith('-') && !root.Subcommands.Any(c => c.Name == args[0] || c.Aliases.Contains(args[0]))
                && External.Find(args[0], environment) is { } program)
            {
                return External.Run(program, args[1..], console);
            }

            InvocationConfiguration invocation = new() { Output = console.Text, Error = console.Error, EnableDefaultExceptionHandler = false };
            if (args.Length == 0)
            {
                root.Parse(["--help"]).Invoke(invocation);
                return ExitCodes.Usage;
            }

            // run parses its own arguments, since each step carries options of its own; its help comes from the parser.
            if (args[0] == "run" && !args.Skip(1).Any(a => a is "-h" or "--help" or "-?"))
            {
                return RunCommand.Execute(session, args[1..]);
            }

            ParseResult result = root.Parse(args, new ParserConfiguration { EnablePosixBundling = false });
            if (result.Errors.Count > 0 && result.Action is not System.CommandLine.Help.HelpAction)
            {
                bool atRoot = result.CommandResult.Command == root;
                if (atRoot && !args[0].StartsWith('-'))
                {
                    session.Error($"'{args[0]}' is not an egami command, and no egami-{args[0]} program is on the PATH.");
                }
                else
                {
                    foreach (ParseError error in result.Errors)
                    {
                        session.Error(error.Message);
                    }
                }

                console.Error.WriteLine($"Run '{(atRoot ? "egami" : $"egami {result.CommandResult.Command.Name}")} --help' for usage.");
                return ExitCodes.Usage;
            }

            GlobalOptions.Configure(session, result);
            return result.Invoke(invocation);
        }
        catch (UsageException error)
        {
            session.Error(error.Message);
            return ExitCodes.Usage;
        }
        catch (Exception error) when (Pipeline.Batch.Failure(error) is { } code)
        {
            session.Error(Pipeline.Batch.Message(error));
            return code;
        }
        finally
        {
            ValueParsers.Session = null;
        }
    }

    /// <summary>Builds the command tree; the root is named egami whatever the executable is called.</summary>
    public static Command CreateRoot(CliSession session)
    {
        Command root = new("egami", "Convert, inspect, resize and edit images with Egami, a fully managed imaging library.");
        root.Options.Add(new System.CommandLine.Help.HelpOption());
        root.Options.Add(new VersionOption());
        GlobalOptions.AddTo(root);
        root.Subcommands.Add(InfoCommands.Info(session));
        root.Subcommands.Add(EverydayCommands.Convert(session));
        root.Subcommands.Add(EverydayCommands.Resize(session));
        root.Subcommands.Add(EverydayCommands.Thumbnail(session));
        root.Subcommands.Add(EverydayCommands.Crop(session));
        root.Subcommands.Add(EverydayCommands.Rotate(session));
        root.Subcommands.Add(EverydayCommands.Flip(session));
        root.Subcommands.Add(EverydayCommands.AutoOrient(session));
        root.Subcommands.Add(RunCommand.Create(session));
        root.Subcommands.Add(InfoCommands.Formats(session));
        return root;
    }
}

/// <summary>Runs <c>egami &lt;name&gt;</c> as the program <c>egami-&lt;name&gt;</c> from the PATH, as git and dotnet do.</summary>
internal static class External
{
    /// <summary>Finds <c>egami-&lt;name&gt;</c> on the PATH, or returns <see langword="null"/>.</summary>
    public static string? Find(string name, Func<string, string?> environment)
    {
        if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':', '.']) >= 0 || Pipeline.Globber.HasWildcard(name))
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
                string candidate = Path.Combine(folder.Trim('"'), $"egami-{name}{extension.ToLowerInvariant()}");
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
