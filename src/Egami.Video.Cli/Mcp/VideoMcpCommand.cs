using System.CommandLine;

using Egami.Cli.Hosting.Mcp;
using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Mcp;

/// <summary>
/// <c>egami-video mcp</c> (Video CLI design Q10, Build Plan task 24): the commands as tools for an AI assistant over the Model
/// Context Protocol, on the shared host egami mcp runs on: one tool per command, the folders the assistant opened or
/// <c>--allow</c> names, inputs never replaced, progress notifications and cancellation for long jobs.
/// </summary>
internal static class VideoMcpCommand
{
    /// <summary>The most inputs one call takes unless <c>--max-inputs</c> says otherwise.</summary>
    public const int DefaultMaxInputs = 1000;

    public static Command Create(VideoSession session)
    {
        Option<string[]> allow = new("--allow")
        {
            Description = "A folder the tools may read and write, besides the folders the assistant opened; give it once per folder. With neither, the working folder.",
            HelpName = "folder",
            AllowMultipleArgumentsPerToken = false,
        };
        Option<int> maxInputs = new("--max-inputs")
        {
            Description = $"The most files one tool call may take; {DefaultMaxInputs} by default.",
            HelpName = "count",
            DefaultValueFactory = _ => DefaultMaxInputs,
        };
        Command command = new(
            "mcp",
            "Serve egami-video's commands as tools to an AI assistant over the Model Context Protocol, on standard input and output. "
            + "Tools read and write only inside the folders the assistant opened and those given with --allow, and never replace an input unless asked.");
        command.Options.Add(allow);
        command.Options.Add(maxInputs);
        command.SetAction(result =>
        {
            string? limit = session.Globals is { } globals ? result.GetValue(globals.Limit) : null;
            return McpServerHost.Run(session, VideoTools.Catalog, result.GetValue(allow) ?? [], result.GetValue(maxInputs), limit);
        });
        return command;
    }
}
