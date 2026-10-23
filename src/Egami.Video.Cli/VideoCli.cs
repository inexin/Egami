using System.CommandLine;
using System.CommandLine.Parsing;

using Egami.Video.Cli.Commands;
using Egami.Video.Cli.Host;

namespace Egami.Video.Cli;

/// <summary>
/// The tool's entry point, shared by the executable, the tests and the MCP server: builds the commands, parses, maps errors to
/// exit codes (0 success, 1 a file failed, 2 usage, 3 a safety limit, 130 stopped with Ctrl+C) and runs unknown commands as
/// <c>egami-video-&lt;name&gt;</c> programs from the PATH, as egami does.
/// </summary>
internal static class VideoCli
{
    /// <summary>The commands that parse their own arguments, since their options follow another tool's rules.</summary>
    private static readonly Dictionary<string, Func<VideoSession, string[], int>> RawCommands = new(StringComparer.Ordinal)
    {
        ["run"] = RunVideoCommand.Execute,
        ["ffmpeg"] = FfmpegMode.ExecuteFfmpeg,
        ["ffprobe"] = FfmpegMode.ExecuteFfprobe,
    };

    /// <summary>Runs the tool in this process; <paramref name="setup"/> may set up the session first, as the MCP server does.</summary>
    public static int Run(string[] args, CliConsole console, string workingDirectory, Func<string, string?> environment, Action<VideoSession>? setup = null)
    {
        VideoSession session = new(console, workingDirectory, environment);
        setup?.Invoke(session);
        Egami.Cli.Steps.ValueParsers.Session = session.Images;

        // Numbers on a command line are written the same way everywhere: 0.8, never 0,8.
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Command root = CreateRoot(session);
            if (args.Length > 0 && !args[0].StartsWith('-') && !root.Subcommands.Any(c => c.Name == args[0] || c.Aliases.Contains(args[0]))
                && External.Find("egami-video", args[0], environment) is { } program)
            {
                return External.Run(program, args[1..], console);
            }

            InvocationConfiguration invocation = new() { Output = console.Text, Error = console.Error, EnableDefaultExceptionHandler = false };
            if (args.Length == 0)
            {
                root.Parse(["--help"]).Invoke(invocation);
                return ExitCodes.Usage;
            }

            if (RawCommands.TryGetValue(args[0], out Func<VideoSession, string[], int>? raw) && !args.Skip(1).Any(a => a is "-h" or "--help" or "-?"))
            {
                return raw(session, args[1..]);
            }

            ParseResult result = root.Parse(args, new ParserConfiguration { EnablePosixBundling = false });
            if (result.Errors.Count > 0 && result.Action is not System.CommandLine.Help.HelpAction)
            {
                bool atRoot = result.CommandResult.Command == root;
                if (atRoot && !args[0].StartsWith('-'))
                {
                    session.Error($"'{args[0]}' is not an egami-video command, and no egami-video-{args[0]} program is on the PATH.");
                }
                else
                {
                    foreach (ParseError error in result.Errors)
                    {
                        session.Error(error.Message);
                    }
                }

                console.Error.WriteLine($"Run '{(atRoot ? "egami-video" : $"egami-video {result.CommandResult.Command.Name}")} --help' for usage.");
                return ExitCodes.Usage;
            }

            VideoGlobalOptions.Configure(session, result);
            return result.Invoke(invocation);
        }
        catch (UsageException error)
        {
            session.Error(error.Message);
            return ExitCodes.Usage;
        }
        catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
        {
            session.Error("stopped.");
            return ExitCodes.Cancelled;
        }
        catch (Exception error) when (session.Failure(error) is { } code)
        {
            session.Error(session.Message(error));
            return code;
        }
        finally
        {
            session.ProgressSink?.Dispose();
            Egami.Cli.Steps.ValueParsers.Session = null;
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>Builds the command tree; the root is named egami-video whatever the executable is called.</summary>
    public static Command CreateRoot(VideoSession session)
    {
        Command root = new("egami-video", "Inspect, remux, cut, join and convert video and audio files, and export and make frames, with Egami.Video, a fully managed video library.");
        root.Options.Add(new System.CommandLine.Help.HelpOption());
        root.Options.Add(new VersionOption());
        VideoGlobalOptions.AddTo(root, session);

        // Stage A: what Egami.Video does on its own (Video CLI design Q3).
        root.Subcommands.Add(InfoCommand.Create(session));
        root.Subcommands.Add(RemuxCommand.Create(session));
        root.Subcommands.Add(CutCommands.Trim(session));
        root.Subcommands.Add(CutCommands.Concat(session));
        root.Subcommands.Add(CutCommands.Segment(session));
        root.Subcommands.Add(TimestampsCommand.Create(session));
        root.Subcommands.Add(TrackCommands.Extract(session));
        root.Subcommands.Add(TrackCommands.Embed(session));
        root.Subcommands.Add(MetadataCommand.Create(session));
        root.Subcommands.Add(FramesCommand.Create(session));
        root.Subcommands.Add(EncodeCommand.Create(session));

        // Stage B: per-frame work through Egami.
        root.Subcommands.Add(RunVideoCommand.Create(session));
        root.Subcommands.Add(PictureCommands.Thumbnail(session));
        root.Subcommands.Add(PictureCommands.Scenes(session));
        root.Subcommands.Add(AnalyzeCommand.Create(session));
        root.Subcommands.Add(AudioPictureCommands.Waveform(session));
        root.Subcommands.Add(AudioPictureCommands.Spectrum(session));
        root.Subcommands.Add(GenerateCommand.Create(session));
        root.Subcommands.Add(PictureCommands.Sheet(session));
        root.Subcommands.Add(PictureCommands.Animate(session));
        root.Subcommands.Add(CheckCommands.Hash(session));
        root.Subcommands.Add(CheckCommands.Compare(session));

        // Stage C: audio (V5).
        root.Subcommands.Add(AudioCommand.Create(session));

        root.Subcommands.Add(FormatsCommand.Create(session));
        root.Subcommands.Add(FfmpegMode.CreateFfmpeg(session));
        root.Subcommands.Add(FfmpegMode.CreateFfprobe(session));
        root.Subcommands.Add(Mcp.VideoMcpCommand.Create(session));
        return root;
    }
}
