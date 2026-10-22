using System.CommandLine;
using System.Text.Json;

using Egami.Cli.Host;

using ModelContextProtocol.Protocol;

namespace Egami.Cli.Mcp;

/// <summary>How a tool argument's JSON value becomes command-line words.</summary>
internal enum ParameterKind
{
    /// <summary>One word.</summary>
    Text,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A flag: present when true.</summary>
    Flag,

    /// <summary>A flag that also says false (<c>--lossless false</c>).</summary>
    Boolean,

    /// <summary>Several words.</summary>
    List,
}

/// <summary>One argument of a tool and the command-line option or argument it stands for.</summary>
/// <param name="Name">The argument's name in snake case.</param>
/// <param name="Token">The option, such as <c>--quality</c>, or <see langword="null"/> for a positional argument.</param>
/// <param name="Kind">How its value is written.</param>
/// <param name="Description">The help text.</param>
/// <param name="Required">Whether the call must give it.</param>
internal sealed record ToolParameter(string Name, string? Token, ParameterKind Kind, string Description, bool Required);

/// <summary>One tool: the command it runs and its arguments.</summary>
/// <param name="Name">The tool's name: the command's words in snake case, such as <c>frames_split</c>.</param>
/// <param name="Words">The words that start the command, such as <c>frames split</c>.</param>
/// <param name="Description">The command's help text.</param>
/// <param name="Parameters">The arguments, positional ones first in their order.</param>
/// <param name="Json">Whether the command takes <c>--json</c>, which every call then passes.</param>
/// <param name="Writes">Whether the command writes files.</param>
internal sealed record ToolInfo(string Name, string[] Words, string Description, IReadOnlyList<ToolParameter> Parameters, bool Json, bool Writes);

/// <summary>
/// The tools of <c>egami mcp</c> (MCP design Q2, Q3): one per command, with the arguments and help text of the same command tree
/// the command line parses, so a new command or option shows up in both without glue. <c>run</c> takes its steps as a workflow
/// object with the published schema instead of words.
/// </summary>
internal static class ToolCatalog
{
    /// <summary>Options that belong to the server, or that a call always sets, so no tool takes them.</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "--help", "--version", "--verbose", "--debug", "--quiet", "--limit", "--json", "--workflow", "--list",
    };

    /// <summary>Commands that are not tools: the server itself.</summary>
    private static readonly HashSet<string> NotTools = new(StringComparer.Ordinal) { "mcp" };

    /// <summary>Commands that only read.</summary>
    private static readonly HashSet<string> ReadOnly = new(StringComparer.Ordinal) { "info", "formats", "stats", "metadata_show" };

    private static readonly Lazy<List<ToolInfo>> All = new(Build);

    /// <summary>Gets every tool, in the order of the command tree.</summary>
    public static IReadOnlyList<ToolInfo> Tools => All.Value;

    /// <summary>Finds a tool by name.</summary>
    public static ToolInfo? Find(string name) => Tools.FirstOrDefault(t => t.Name == name);

    /// <summary>The protocol's description of a tool.</summary>
    public static Tool Describe(ToolInfo tool)
    {
        return new Tool
        {
            Name = tool.Name,
            Title = "egami " + string.Join(' ', tool.Words),
            Description = tool.Description,
            InputSchema = InputSchema(tool),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = !tool.Writes,
                DestructiveHint = tool.Writes ? tool.Parameters.Any(p => p.Name == "in_place") : null,
                IdempotentHint = true,
                OpenWorldHint = false,
            },
        };
    }

    private static List<ToolInfo> Build()
    {
        CliSession session = new(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), ".", _ => null);
        Command root = Cli.CreateRoot(session);
        List<ToolInfo> tools = [];
        void Walk(Command command, string[] words)
        {
            foreach (Command child in command.Subcommands)
            {
                if (NotTools.Contains(child.Name))
                {
                    continue;
                }

                string[] path = [.. words, child.Name];
                if (child.Subcommands.Count > 0)
                {
                    Walk(child, path);
                }
                else
                {
                    tools.Add(child.Name == "run" ? Run(child) : FromCommand(child, path));
                }
            }
        }

        Walk(root, []);
        return tools;
    }

    private static ToolInfo FromCommand(Command command, string[] words)
    {
        string name = string.Join('_', words.Select(Snake));
        List<ToolParameter> parameters = [];
        foreach (System.CommandLine.Argument argument in command.Arguments)
        {
            ParameterKind kind = argument.Arity.MaximumNumberOfValues > 1 ? ParameterKind.List : Kind(argument.ValueType);
            parameters.Add(new ToolParameter(Snake(argument.Name), null, kind, argument.Description ?? string.Empty, argument.Arity.MinimumNumberOfValues > 0));
        }

        bool json = false;
        foreach (Option option in command.Options)
        {
            json |= option.Name == "--json";
            if (!Excluded.Contains(option.Name))
            {
                parameters.Add(Parameter(option));
            }
        }

        bool writes = !ReadOnly.Contains(name);
        return new ToolInfo(name, words, command.Description ?? string.Empty, Unique(name, parameters), json, writes);
    }

    /// <summary><c>run</c>: inputs as a list and the steps as a workflow object, plus run's own options.</summary>
    private static ToolInfo Run(Command command)
    {
        List<ToolParameter> parameters =
        [
            new("inputs", null, ParameterKind.List, "Image files, globs (*, ?, **) or folders with recursive.", false),
            new("workflow", null, ParameterKind.Text, "The steps, as a workflow object: { \"egami\": 1, \"steps\": [ { \"op\": \"resize\", \"size\": \"1200x\" }, { \"op\": \"save\", \"path\": \"out/{name}.webp\", \"quality\": 82 } ] }. list_steps lists the steps and their options.", false),
            new("list_steps", "--list", ParameterKind.Flag, "List every step with its options, instead of running anything.", false),
        ];
        foreach (Option option in command.Options)
        {
            if (!Excluded.Contains(option.Name))
            {
                parameters.Add(Parameter(option));
            }
        }

        return new ToolInfo("run", ["run"], command.Description ?? string.Empty, Unique("run", parameters), false, true);
    }

    private static ToolParameter Parameter(Option option)
    {
        Type type = Nullable.GetUnderlyingType(option.ValueType) ?? option.ValueType;
        ParameterKind kind = type == typeof(bool)
            ? (Nullable.GetUnderlyingType(option.ValueType) is null ? ParameterKind.Flag : ParameterKind.Boolean)
            : Kind(option.ValueType);
        return new ToolParameter(Snake(option.Name.TrimStart('-')), option.Name, kind, option.Description ?? string.Empty, option.Required);
    }

    private static ParameterKind Kind(Type valueType)
    {
        Type type = Nullable.GetUnderlyingType(valueType) ?? valueType;
        return type == typeof(int) || type == typeof(long) ? ParameterKind.Integer
            : type == typeof(float) || type == typeof(double) ? ParameterKind.Number
            : type == typeof(bool) ? ParameterKind.Boolean
            : type.IsArray || (type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) ? ParameterKind.List
            : ParameterKind.Text;
    }

    private static List<ToolParameter> Unique(string tool, List<ToolParameter> parameters)
    {
        string? twice = parameters.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1)?.Key;
        return twice is null ? parameters : throw new InvalidOperationException($"The tool {tool} has two arguments named {twice}.");
    }

    private static JsonElement InputSchema(ToolInfo tool)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "object");
            json.WriteStartObject("properties");
            foreach (ToolParameter parameter in tool.Parameters)
            {
                json.WritePropertyName(parameter.Name);
                if (tool.Name == "run" && parameter.Name == "workflow")
                {
                    WorkflowSchema.Body.WriteTo(json);
                    continue;
                }

                json.WriteStartObject();
                switch (parameter.Kind)
                {
                    case ParameterKind.Integer: json.WriteString("type", "integer"); break;
                    case ParameterKind.Number: json.WriteString("type", "number"); break;
                    case ParameterKind.Flag or ParameterKind.Boolean: json.WriteString("type", "boolean"); break;
                    case ParameterKind.List:
                        json.WriteString("type", "array");
                        json.WriteStartObject("items");
                        json.WriteString("type", "string");
                        json.WriteEndObject();
                        break;
                    default: json.WriteString("type", "string"); break;
                }

                json.WriteString("description", parameter.Description);
                json.WriteEndObject();
            }

            if (tool.Writes)
            {
                json.WriteStartObject("preview");
                json.WriteString("type", "boolean");
                json.WriteString("description", "Also return the first file written as a picture, fit to 1024 pixels.");
                json.WriteEndObject();
            }

            json.WriteEndObject();
            string[] required = [.. tool.Parameters.Where(p => p.Required).Select(p => p.Name)];
            if (required.Length > 0)
            {
                json.WriteStartArray("required");
                foreach (string name in required)
                {
                    json.WriteStringValue(name);
                }

                json.WriteEndArray();
            }

            json.WriteBoolean("additionalProperties", false);
            if (tool.Name == "run")
            {
                // The workflow schema's references point at #/$defs, so its definitions move to the top.
                json.WritePropertyName("$defs");
                WorkflowSchema.Definitions.WriteTo(json);
            }

            json.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// The workflow schema, from the published file built into the tool: its body without <c>$id</c>, <c>$schema</c>, <c>title</c>
    /// and <c>$defs</c>, and the definitions apart.
    /// </summary>
    private static (JsonElement Body, JsonElement Definitions) WorkflowSchema { get; } = LoadWorkflowSchema();

    private static (JsonElement Body, JsonElement Definitions) LoadWorkflowSchema()
    {
        using Stream stream = typeof(ToolCatalog).Assembly.GetManifestResourceStream("egami-workflow-1.json")
            ?? throw new InvalidOperationException("The workflow schema is not built into the tool.");
        using JsonDocument document = JsonDocument.Parse(stream);
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

    /// <summary><c>frames-split</c> or <c>--bit-depth</c> in snake case.</summary>
    internal static string Snake(string name) => name.TrimStart('-').Replace('-', '_');
}
