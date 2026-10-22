using System.CommandLine;

using Egami.Cli.Host;

namespace Egami.Cli.Mcp;

/// <summary><c>egami mcp</c>: the commands as tools for an AI assistant, over the Model Context Protocol (MCP design).</summary>
internal static class McpCommand
{
    /// <summary>The most inputs one call takes unless <c>--max-inputs</c> says otherwise (MCP design Q6).</summary>
    public const int DefaultMaxInputs = 1000;

    public static Command Create(CliSession session)
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
            "Serve egami's commands as tools to an AI assistant over the Model Context Protocol, on standard input and output. "
            + "Tools read and write only inside the folders the assistant opened and those given with --allow, and never replace an input unless asked.");
        command.Options.Add(allow);
        command.Options.Add(maxInputs);
        command.SetAction(result =>
        {
            GlobalOptions.Configure(session, result);
            string? limit = session.Globals is { } globals ? result.GetValue(globals.Limit) : null;
            return McpHost.Run(session, result.GetValue(allow) ?? [], result.GetValue(maxInputs), limit);
        });
        return command;
    }
}
