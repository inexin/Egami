using System.Reflection;
using System.Text.Json;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Egami.Cli.Hosting.Mcp;

/// <summary>
/// A document a tool's server serves as an MCP resource: a fixed address, what it holds and how to read it. Resources are
/// static: the server sends no list-changed notices and takes no subscriptions.
/// </summary>
/// <param name="Uri">The address, such as <c>egami://formats</c>.</param>
/// <param name="Name">A short name.</param>
/// <param name="Title">The title an assistant shows.</param>
/// <param name="Description">What the resource holds.</param>
/// <param name="MimeType">The media type of its text.</param>
/// <param name="Read">Reads the text.</param>
internal sealed record McpResourceEntry(string Uri, string Name, string Title, string Description, string MimeType, Func<string> Read)
{
    /// <summary>A resource built into an assembly, read as UTF-8 text exactly as it was built in.</summary>
    public static Func<string> Embedded(Assembly assembly, string name) => () =>
    {
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not built into the tool.");
        using StreamReader reader = new(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    };

    /// <summary>What a command line writes to standard output, such as <c>formats --json</c>.</summary>
    public static Func<string> Output(McpRunner run, params string[] words) => () =>
    {
        StringWriter text = new() { NewLine = "\n" };
        static Stream NoStream() => Stream.Null;
        int code = run(words, new CliConsole(NoStream, NoStream, text, TextWriter.Null), Environment.CurrentDirectory, _ => null, _ => { });
        if (code != ExitCodes.Success)
        {
            throw new InvalidOperationException($"'{string.Join(' ', words)}' failed with exit code {code}.");
        }

        return text.ToString();
    };
}

/// <summary>
/// A task a tool's server offers as an MCP prompt: its arguments (all text) and the one user message it stands for, which names
/// the tools to call.
/// </summary>
/// <param name="Name">The name, in snake case.</param>
/// <param name="Title">The title an assistant shows.</param>
/// <param name="Description">What the prompt does.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Tools">The tools the message names, which must all be tools of the server.</param>
/// <param name="Message">Writes the message's task from the arguments; the server adds the tools and the folder rules.</param>
internal sealed record McpPromptEntry(
    string Name,
    string Title,
    string Description,
    IReadOnlyList<McpPromptArgument> Arguments,
    IReadOnlyList<string> Tools,
    Func<IReadOnlyDictionary<string, string>, string> Message)
{
    /// <summary>The rules every prompt's message ends with.</summary>
    public const string FolderRules = "Use only paths inside the folders the user allowed; write results to a new file or folder, "
        + "never over an input (leave in_place unset), and report each file that failed from the result's messages.";

    /// <summary>The message for these arguments: the task, the tools to call and the folder rules.</summary>
    public string Text(IReadOnlyDictionary<string, string> arguments)
        => $"{Message(arguments)}\n\nTools to call: {string.Join(", ", Tools)}. {FolderRules}";
}

/// <summary>An argument of an <see cref="McpPromptEntry"/>.</summary>
/// <param name="Name">The name, in snake case.</param>
/// <param name="Description">What it takes.</param>
/// <param name="Required">Whether the prompt needs it.</param>
internal sealed record McpPromptArgument(string Name, string Description, bool Required = true);

/// <summary>The answers to resources/list, resources/read, prompts/list and prompts/get for a tool set.</summary>
internal static class McpContentServer
{
    public static ListResourcesResult ListResources(McpToolSet set) => new()
    {
        Resources = [.. set.Resources.Select(r => new Resource { Uri = r.Uri, Name = r.Name, Title = r.Title, Description = r.Description, MimeType = r.MimeType })],
    };

    public static ReadResourceResult ReadResource(McpToolSet set, ReadResourceRequestParams? request)
    {
        string uri = request?.Uri ?? throw new McpProtocolException("resources/read needs a uri.", McpErrorCode.InvalidParams);
        McpResourceEntry resource = set.Resources.FirstOrDefault(r => string.Equals(r.Uri, uri, StringComparison.Ordinal))
            ?? throw new McpProtocolException($"{set.Name} has no resource '{uri}'.", McpErrorCode.InvalidParams);
        return new ReadResourceResult { Contents = [new TextResourceContents { Uri = resource.Uri, MimeType = resource.MimeType, Text = resource.Read() }] };
    }

    public static ListPromptsResult ListPrompts(McpToolSet set) => new()
    {
        Prompts =
        [
            .. set.Prompts.Select(p => new Prompt
            {
                Name = p.Name,
                Title = p.Title,
                Description = p.Description,
                Arguments = [.. p.Arguments.Select(a => new PromptArgument { Name = a.Name, Description = a.Description, Required = a.Required })],
            }),
        ],
    };

    public static GetPromptResult GetPrompt(McpToolSet set, GetPromptRequestParams? request)
    {
        string name = request?.Name ?? throw new McpProtocolException("prompts/get needs a name.", McpErrorCode.InvalidParams);
        McpPromptEntry prompt = set.Prompts.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal))
            ?? throw new McpProtocolException($"{set.Name} has no prompt '{name}'.", McpErrorCode.InvalidParams);
        Dictionary<string, string> arguments = new(StringComparer.Ordinal);
        foreach ((string key, JsonElement value) in request.Arguments ?? new Dictionary<string, JsonElement>())
        {
            if (!prompt.Arguments.Any(a => a.Name == key))
            {
                throw new McpProtocolException($"The prompt {name} has no argument '{key}'.", McpErrorCode.InvalidParams);
            }

            arguments[key] = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        }

        foreach (McpPromptArgument argument in prompt.Arguments)
        {
            if (argument.Required && (!arguments.TryGetValue(argument.Name, out string? given) || string.IsNullOrWhiteSpace(given)))
            {
                throw new McpProtocolException($"The prompt {name} needs the argument '{argument.Name}'.", McpErrorCode.InvalidParams);
            }
        }

        return new GetPromptResult
        {
            Description = prompt.Description,
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = prompt.Text(arguments) } }],
        };
    }
}
