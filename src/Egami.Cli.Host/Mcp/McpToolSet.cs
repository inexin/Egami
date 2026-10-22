using System.CommandLine;
using System.Reflection;
using System.Text.Json;

using ModelContextProtocol.Protocol;

namespace Egami.Cli.Hosting.Mcp;

/// <summary>Runs a tool's command line in this process: words, console, working folder, environment, and a session set-up.</summary>
internal delegate int McpRunner(string[] args, CliConsole console, string workingDirectory, Func<string, string?> environment, Action<CliHostSession> setup);

/// <summary>
/// What a tool's <c>mcp</c> server serves: its name and instructions, its command tree (one MCP tool per command), how a call
/// runs, and the choices only the tool can make: which options and commands stay out, which commands only read, and so on.
/// </summary>
internal sealed class McpToolSet
{
    /// <summary>Gets the options a server leaves out unless a tool set says otherwise.</summary>
    public static IReadOnlySet<string> DefaultExcluded { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "--help", "--version", "--verbose", "--debug", "--quiet", "--limit", "--json", "--workflow", "--list", "--jobs", "--fail-fast",
    };

    /// <summary>Gets the command's name, such as <c>egami</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the server's title, such as <c>Egami</c>.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the project's web site.</summary>
    public required string WebsiteUrl { get; init; }

    /// <summary>Gets the instructions the server gives the assistant.</summary>
    public required string Instructions { get; init; }

    /// <summary>Gets the command tree the tools come from; built once, with a session that writes nowhere.</summary>
    public required Func<Command> CreateRoot { get; init; }

    /// <summary>Gets how a call runs the command line.</summary>
    public required McpRunner Run { get; init; }

    /// <summary>Gets how an exception from a preview maps to an exit code, or <see langword="null"/> for a bug.</summary>
    public required Func<Exception, int?> Failure { get; init; }

    /// <summary>
    /// Gets the options that belong to the server, that a call always sets, or that an assistant has no basis to choose: calls
    /// use one job per processor and keep going past a failed file, reporting each.
    /// </summary>
    public IReadOnlySet<string> Excluded { get; init; } = DefaultExcluded;

    /// <summary>Gets the commands that are not tools, such as the server itself.</summary>
    public IReadOnlySet<string> NotTools { get; init; } = new HashSet<string>(StringComparer.Ordinal) { "mcp" };

    /// <summary>Gets the tools (in snake case) that only read.</summary>
    public IReadOnlySet<string> ReadOnly { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the arguments a command can't run without, though its tree calls them optional because the shell takes each either
    /// as a word or as an option.
    /// </summary>
    public IReadOnlySet<(string Tool, string Argument)> Needed { get; init; } = new HashSet<(string, string)>();

    /// <summary>Gets the tools whose exit code 1 reports a finding (a difference, a failed check) rather than an error.</summary>
    public IReadOnlySet<string> Checks { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Gets the <c>run</c> tool's workflow, or <see langword="null"/> when <c>run</c> is a tool like the others.</summary>
    public McpWorkflow? Workflow { get; init; }

    /// <summary>Gets the picture of a written file that a call may ask for, or <see langword="null"/> when there is none.</summary>
    public Func<string, ImageContentBlock>? Preview { get; init; }

    /// <summary>Gets what the preview argument says, when there is a preview.</summary>
    public string PreviewDescription { get; init; } = "Also return the first file written as a picture, fit to 1024 pixels.";

    /// <summary>Gets the tool's version, from the assembly the shared host is built into.</summary>
    public static string Version { get; } = (typeof(McpToolSet).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];
}

/// <summary>
/// How the <c>run</c> tool takes its steps: as a workflow object with the tool's published schema, which the server checks and
/// hands to the session before the command runs.
/// </summary>
/// <param name="InputsDescription">What the <c>inputs</c> argument takes.</param>
/// <param name="WorkflowDescription">What the <c>workflow</c> argument takes, with an example.</param>
/// <param name="ListDescription">What the <c>list_steps</c> argument does.</param>
/// <param name="Example">A short workflow, for messages.</param>
/// <param name="Body">The schema without <c>$id</c>, <c>$schema</c>, <c>title</c> and <c>$defs</c>.</param>
/// <param name="Definitions">The schema's <c>$defs</c>.</param>
/// <param name="Parse">Reads the inputs and the workflow's raw JSON; throws <see cref="UsageException"/> for a wrong workflow.</param>
/// <param name="Attach">Hands what <paramref name="Parse"/> returned to the call's session.</param>
internal sealed record McpWorkflow(
    string InputsDescription,
    string WorkflowDescription,
    string ListDescription,
    string Example,
    JsonElement Body,
    JsonElement Definitions,
    Func<IReadOnlyList<string>, byte[], object> Parse,
    Action<CliHostSession, object> Attach)
{
    /// <summary>Splits a published workflow schema into its body and its definitions.</summary>
    public static (JsonElement Body, JsonElement Definitions) SplitSchema(Stream schema)
    {
        using JsonDocument document = JsonDocument.Parse(schema);
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("$id" or "$schema" or "title" or "$defs"))
                {
                    property.WriteTo(json);
                }
            }

            json.WriteEndObject();
        }

        return (JsonDocument.Parse(buffer.ToArray()).RootElement.Clone(), document.RootElement.GetProperty("$defs").Clone());
    }
}
