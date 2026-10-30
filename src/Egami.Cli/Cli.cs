using System.CommandLine;
using System.CommandLine.Parsing;

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
    /// <summary>Runs the tool in this process; <paramref name="setup"/> may set up the session first, as egami mcp does for each call.</summary>
    public static int Run(string[] args, CliConsole console, string workingDirectory, Func<string, string?> environment, Action<CliSession>? setup = null)
    {
        CliSession session = new(console, workingDirectory, environment);
        setup?.Invoke(session);
        ValueParsers.Session = session;

        // Numbers on a command line are written the same way everywhere: 0.8, never 0,8.
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Command root = CreateRoot(session);
            if (args.Length > 0 && !args[0].StartsWith('-') && !root.Subcommands.Any(c => c.Name == args[0] || c.Aliases.Contains(args[0]))
                && External.Find("egami", args[0], environment) is { } program)
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
        catch (Exception error) when (session.Failure(error) is { } code)
        {
            session.Error(session.Message(error));
            return code;
        }
        finally
        {
            ValueParsers.Session = null;
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>Builds the command tree; the root is named egami whatever the executable is called.</summary>
    public static Command CreateRoot(CliSession session)
    {
        Command root = new("egami", "Convert, inspect, resize and edit images with Egami, a fully managed imaging library.");
        root.Options.Add(new System.CommandLine.Help.HelpOption());
        root.Options.Add(new VersionOption());
        GlobalOptions.AddTo(root, session);
        root.Subcommands.Add(InfoCommands.Info(session));
        root.Subcommands.Add(EverydayCommands.Convert(session));
        root.Subcommands.Add(EverydayCommands.Resize(session));
        root.Subcommands.Add(EverydayCommands.Thumbnail(session));
        root.Subcommands.Add(EverydayCommands.Crop(session));
        root.Subcommands.Add(EverydayCommands.Rotate(session));
        root.Subcommands.Add(EverydayCommands.Flip(session));
        root.Subcommands.Add(EverydayCommands.AutoOrient(session));
        root.Subcommands.Add(RunCommand.Create(session));
        root.Subcommands.Add(AnalysisCommands.Compare(session));
        root.Subcommands.Add(AnalysisCommands.Stats(session));
        root.Subcommands.Add(OcrCommand.Create(session));
        root.Subcommands.Add(FrameCommands.Create(session));
        root.Subcommands.Add(LayersCommand.Create(session));
        root.Subcommands.Add(MetadataCommands.Create(session));
        root.Subcommands.Add(MetadataCommands.Color(session));
        root.Subcommands.Add(DrawingCommands.Text(session));
        root.Subcommands.Add(DrawingCommands.Overlay(session));
        root.Subcommands.Add(DrawingCommands.Montage(session));
        root.Subcommands.Add(InfoCommands.Formats(session));
        root.Subcommands.Add(Mcp.McpCommand.Create(session));
        return root;
    }
}
