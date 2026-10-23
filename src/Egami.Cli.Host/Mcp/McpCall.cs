using System.Globalization;
using System.Text;
using System.Text.Json;

using ModelContextProtocol.Protocol;

namespace Egami.Cli.Hosting.Mcp;

/// <summary>What a call may touch and how it reports back, set by the server for every call.</summary>
/// <param name="Paths">The folders the call may read and write.</param>
/// <param name="WorkingDirectory">The folder relative paths start from.</param>
/// <param name="Environment">The environment variable reader (for the tool's limits variable).</param>
/// <param name="MaxInputs">The most inputs one call may take.</param>
/// <param name="Limit">The server's <c>--limit</c>, passed on to every call.</param>
/// <param name="Progress">Hears of each finished file of a batch, or <see langword="null"/>.</param>
internal sealed record CallScope(PathPolicy Paths, string WorkingDirectory, Func<string, string?> Environment, int MaxInputs, string? Limit, Action<int, int>? Progress)
{
    /// <summary>Gets what hears progress within a file, for tools whose jobs report it (see <see cref="CliHostSession.ProgressNote"/>).</summary>
    public Action<double, double, string>? Note { get; init; }
}

/// <summary>The outcome of one call, before it becomes a protocol result.</summary>
/// <param name="ExitCode">The command line's exit code.</param>
/// <param name="Written">The files written.</param>
/// <param name="Output">What the command printed: its JSON, its text, or nothing.</param>
/// <param name="Messages">Warnings and errors, without the tool's name in front.</param>
/// <param name="IsError">Whether the call failed.</param>
internal sealed record CallOutcome(int ExitCode, IReadOnlyList<string> Written, JsonElement? Output, IReadOnlyList<string> Messages, bool IsError);

/// <summary>
/// Runs one tool call: its JSON arguments become the same words the command line takes, run in this process through the tool
/// set's runner with the console captured, so a call does exactly what the command does.
/// </summary>
internal static class McpCall
{
    private static readonly JsonWriterOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Runs a call and returns the protocol's result; mistakes in the arguments come back as a failed call, so the assistant can fix them.</summary>
    public static async Task<CallToolResult> InvokeAsync(McpToolSet set, ToolInfo tool, IDictionary<string, JsonElement>? arguments, CallScope scope, CancellationToken cancellationToken)
    {
        string[] words;
        object? run;
        bool preview;
        try
        {
            (words, run, preview) = Words(set, tool, arguments, scope.Limit);
        }
        catch (UsageException error)
        {
            return Result(new CallOutcome(ExitCodes.Usage, [], null, [$"error: {error.Message}"], true), null);
        }

        CallOutcome outcome = await Task.Run(() => Run(set, tool, words, run, scope, cancellationToken), cancellationToken).ConfigureAwait(false);
        ImageContentBlock? picture = null;
        List<string> messages = [.. outcome.Messages];
        if (preview && set.Preview is { } makePreview && !outcome.IsError && outcome.Written.Count > 0)
        {
            try
            {
                scope.Paths.Check(outcome.Written[0]);
                picture = makePreview(outcome.Written[0]);
            }
            catch (Exception error) when (set.Failure(error) is not null || error is UsageException)
            {
                messages.Add($"warning: no preview of {outcome.Written[0]}: {error.Message}");
            }
        }

        return Result(outcome with { Messages = messages }, picture);
    }

    /// <summary>The words a call stands for, the inputs and steps of a <c>run</c>, and whether a preview was asked for.</summary>
    internal static (string[] Words, object? Run, bool Preview) Words(McpToolSet set, ToolInfo tool, IDictionary<string, JsonElement>? arguments, string? limit)
    {
        arguments ??= new Dictionary<string, JsonElement>();
        bool preview = false;
        List<string> options = [];
        List<string> positional = [];
        List<string>? inputs = null;
        JsonElement? workflow = null;
        foreach ((string name, JsonElement value) in arguments)
        {
            if (name == "preview" && tool.Writes && set.Preview is not null)
            {
                preview = Flag(name, value);
                continue;
            }

            ToolParameter parameter = tool.Parameters.FirstOrDefault(p => p.Name == name)
                ?? throw new UsageException($"{tool.Name} has no argument '{name}'. It takes {string.Join(", ", tool.Parameters.Select(p => p.Name))}{(tool.Writes && set.Preview is not null ? " and preview" : string.Empty)}.");
            if (value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (tool.Name == "run" && set.Workflow is not null && parameter.Token is null)
            {
                if (name == "workflow")
                {
                    workflow = value.ValueKind == JsonValueKind.Object ? value : throw new UsageException($"workflow must be an object: {{ \"{set.Name}\": 1, \"steps\": [ ... ] }}.");
                }
                else
                {
                    inputs = List(name, value);
                }

                continue;
            }

            switch (parameter.Kind)
            {
                case ParameterKind.Flag:
                    if (Flag(name, value))
                    {
                        options.Add(parameter.Token!);
                    }

                    break;
                case ParameterKind.Boolean:
                    options.Add(parameter.Token!);
                    options.Add(Flag(name, value) ? "true" : "false");
                    break;
                case ParameterKind.List:
                    foreach (string item in List(name, value))
                    {
                        if (parameter.Token is null)
                        {
                            positional.Add(item);
                        }
                        else
                        {
                            options.Add(parameter.Token);
                            options.Add(item);
                        }
                    }

                    break;
                default:
                    string text = Scalar(name, value, parameter.Kind);
                    if (parameter.Token is null)
                    {
                        positional.Add(text);
                    }
                    else
                    {
                        options.Add(parameter.Token);
                        options.Add(text);
                    }

                    break;
            }
        }

        foreach (ToolParameter parameter in tool.Parameters.Where(p => p.Required && !arguments.ContainsKey(p.Name)))
        {
            throw new UsageException($"{tool.Name} needs '{parameter.Name}': {parameter.Description}");
        }

        if (limit is not null)
        {
            options.Add("--limit");
            options.Add(limit);
        }

        if (tool.Name == "run" && set.Workflow is { } workflowTool)
        {
            if (options.Contains("--list"))
            {
                return (["run", "--list"], null, false);
            }

            if (workflow is not { } steps)
            {
                throw new UsageException($"run needs a workflow: {workflowTool.Example}.");
            }

            // The raw bytes, as a workflow file would give them: the workflow reader deals with text that is not valid UTF-8.
            byte[] json = System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(steps).ToArray();
            return (["run", .. options], workflowTool.Parse(inputs ?? [], json), preview);
        }

        if (tool.Json)
        {
            options.Add("--json");
        }

        // Every positional value comes after --, so a file named -x.png is never taken for an option.
        return ([.. tool.Words, .. options, "--", .. positional], null, preview);
    }

    private static CallOutcome Run(McpToolSet set, ToolInfo tool, string[] words, object? run, CallScope scope, CancellationToken cancellationToken)
    {
        StringWriter text = new() { NewLine = "\n" };
        StringWriter error = new() { NewLine = "\n" };
        static Stream NoStream() => throw new UsageException("Standard input and output carry the protocol here; give files instead of -.");
        CliConsole console = new(NoStream, NoStream, text, TextWriter.Synchronized(error));
        CliHostSession? session = null;
        int code;
        try
        {
            code = set.Run(words, console, scope.WorkingDirectory, scope.Environment, s =>
            {
                session = s;
                s.Paths = scope.Paths;
                s.MaxInputs = scope.MaxInputs;
                s.Cancellation = cancellationToken;
                s.Progress = scope.Progress;
                s.ProgressNote = scope.Note;
                if (run is not null)
                {
                    set.Workflow?.Attach(s, run);
                }
            });
        }
        catch (OperationCanceledException)
        {
            return new CallOutcome(ExitCodes.Failed, [.. session?.Written ?? []], null, [.. Lines(set, error.ToString()), "error: cancelled"], true);
        }

        List<string> messages = Lines(set, error.ToString());
        bool failed = code is ExitCodes.Usage or ExitCodes.Limit
            || (code == ExitCodes.Failed && (!set.Checks.Contains(tool.Name) || messages.Any(m => m.StartsWith("error:", StringComparison.Ordinal))));
        return new CallOutcome(code, [.. session?.Written ?? []], Output(text.ToString()), messages, failed);
    }

    private static CallToolResult Result(CallOutcome outcome, ImageContentBlock? picture)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer, Compact))
        {
            json.WriteStartObject();
            json.WriteNumber("exitCode", outcome.ExitCode);
            json.WriteStartArray("written");
            foreach (string path in outcome.Written)
            {
                json.WriteStringValue(path);
            }

            json.WriteEndArray();
            if (outcome.Output is { } output)
            {
                json.WritePropertyName("output");
                output.WriteTo(json);
            }

            json.WriteStartArray("messages");
            foreach (string message in outcome.Messages)
            {
                json.WriteStringValue(message);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        JsonElement structured = JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
        List<ContentBlock> content = [new TextContentBlock { Text = Encoding.UTF8.GetString(buffer.ToArray()) }];
        if (picture is not null)
        {
            content.Add(picture);
        }

        return new CallToolResult { Content = content, StructuredContent = structured, IsError = outcome.IsError };
    }

    private static JsonElement? Output(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] is '{' or '[')
        {
            try
            {
                return JsonDocument.Parse(trimmed).RootElement.Clone();
            }
            catch (JsonException)
            {
            }
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStringValue(text.TrimEnd());
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private static List<string> Lines(McpToolSet set, string text)
    {
        string prefix = set.Name + ": ";
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line)];
    }

    private static bool Flag(string name, JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(Text(name, value), out bool parsed) => parsed,
            _ => throw new UsageException($"{name} must be true or false."),
        };
    }

    private static string Scalar(string name, JsonElement value, ParameterKind kind)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when kind == ParameterKind.Integer:
                return value.TryGetInt64(out long whole) ? whole.ToString(CultureInfo.InvariantCulture) : throw new UsageException($"{name} must be a whole number.");
            case JsonValueKind.Number:
                return value.GetRawText();
            case JsonValueKind.String when kind is ParameterKind.Integer or ParameterKind.Number:
                string number = Text(name, value);
                return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? number : throw new UsageException($"{name} must be a number.");
            case JsonValueKind.String:
                return Text(name, value);
            case JsonValueKind.True or JsonValueKind.False when kind == ParameterKind.Text:
                return value.ValueKind == JsonValueKind.True ? "true" : "false";
            default:
                throw new UsageException($"{name} must be {(kind == ParameterKind.Text ? "text" : "a number")}.");
        }
    }

    private static List<string> List(string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return [Text(name, value)];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new UsageException($"{name} must be a list of text.");
        }

        List<string> items = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            items.Add(item.ValueKind switch
            {
                JsonValueKind.String => Text(name, item),
                JsonValueKind.Number => item.GetRawText(),
                _ => throw new UsageException($"{name} must be a list of text."),
            });
        }

        return items;
    }

    /// <summary>A string value; JSON that is not valid UTF-8 is a usage error, not a crash.</summary>
    private static string Text(string name, JsonElement value)
    {
        try
        {
            return value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            throw new UsageException($"{name} is not valid UTF-8 text.");
        }
    }
}
