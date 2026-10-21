using System.Globalization;
using System.Text.Json;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;

namespace Egami.Cli.Commands;

/// <summary>
/// JSON workflow files (CLI design Q6): the steps of <c>run</c>, one object per step with the step's options as properties,
/// under a version number. Read and written with <see cref="JsonDocument"/> and <see cref="Utf8JsonWriter"/>, so no reflection.
/// </summary>
/// <example>
/// <code>
/// { "egami": 1, "steps": [ { "op": "auto-orient" }, { "op": "resize", "size": "1200x" }, { "op": "save", "format": "webp", "quality": 82 } ] }
/// </code>
/// </example>
internal static class Workflow
{
    /// <summary>The workflow format's version.</summary>
    public const int Version = 1;

    /// <summary>The address of the published schema.</summary>
    public const string SchemaUrl = "https://raw.githubusercontent.com/inexin/Egami/main/docs/schemas/egami-workflow-1.json";

    /// <summary>The largest workflow file read, as a guard against reading something that is not one.</summary>
    private const int MaxBytes = 4 << 20;

    /// <summary>Reads a workflow file; throws <see cref="UsageException"/> saying where a file is wrong.</summary>
    public static List<PipelineItem> Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new UsageException($"The workflow '{path}' does not exist.");
        }

        if (new FileInfo(path).Length > MaxBytes)
        {
            throw new UsageException($"The workflow '{path}' is larger than {MaxBytes >> 20} MB; it is probably not a workflow.");
        }

        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    /// <summary>Reads a workflow from bytes; <paramref name="name"/> names it in messages.</summary>
    public static List<PipelineItem> Parse(ReadOnlyMemory<byte> json, string name)
    {
        if (!System.Text.Unicode.Utf8.IsValid(json.Span))
        {
            throw new UsageException($"{name} is not UTF-8 text.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
        }
        catch (JsonException error)
        {
            throw new UsageException($"{name} is not valid JSON: {error.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Error(name, "the file must be a JSON object with \"egami\" and \"steps\"");
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name is not ("$schema" or "egami" or "steps" or "description"))
                {
                    throw Error(name, $"unknown property \"{property.Name}\"; a workflow has \"egami\", \"steps\" and optionally \"description\"");
                }
            }

            if (!root.TryGetProperty("egami", out JsonElement version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number))
            {
                throw Error(name, "\"egami\" must give the workflow version, 1");
            }

            if (number != Version)
            {
                throw Error(name, $"version {number} is not one this egami reads (it reads {Version})");
            }

            if (!root.TryGetProperty("steps", out JsonElement steps) || steps.ValueKind != JsonValueKind.Array)
            {
                throw Error(name, "\"steps\" must be an array of steps");
            }

            List<PipelineItem> items = [];
            int index = 0;
            foreach (JsonElement step in steps.EnumerateArray())
            {
                index++;
                string where = $"step {index}";
                if (step.ValueKind != JsonValueKind.Object)
                {
                    throw Error(name, $"{where} must be an object such as {{ \"op\": \"blur\", \"sigma\": 2 }}");
                }

                if (!step.TryGetProperty("op", out JsonElement op) || op.ValueKind != JsonValueKind.String)
                {
                    throw Error(name, $"{where} has no \"op\" naming the step");
                }

                RunCommand.ParsedStep parsed = new(op.GetString()!.ToLowerInvariant());
                foreach (JsonProperty property in step.EnumerateObject())
                {
                    if (property.Name == "op")
                    {
                        continue;
                    }

                    string key = ValueParsers.Kebab(property.Name);
                    if (parsed.Name == "save" && key == "set")
                    {
                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            throw Error(name, $"{where}: \"set\" must be an object of writer settings, such as {{ \"png.compression-level\": 9 }}");
                        }

                        foreach (JsonProperty setting in property.Value.EnumerateObject())
                        {
                            parsed.Named.Add(new("set", $"{setting.Name}={Text(name, where, setting)}"));
                        }

                        continue;
                    }

                    if (parsed.Name == "save" && key == "path")
                    {
                        parsed.Positional.Add(Text(name, where, property));
                        continue;
                    }

                    parsed.Named.Add(new(key, Text(name, where, property)));
                }

                try
                {
                    items.Add(RunCommand.Bind(parsed));
                }
                catch (UsageException error)
                {
                    throw Error(name, $"{where}: {error.Message}");
                }
            }

            if (items.Count == 0)
            {
                throw Error(name, "\"steps\" is empty");
            }

            return items;
        }
    }

    /// <summary>Writes steps as a workflow file.</summary>
    public static void Save(string path, IReadOnlyList<PipelineItem> items)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        using StreamWriter writer = new(path, append: false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
        Write(writer, items);
    }

    /// <summary>Writes steps as workflow JSON.</summary>
    public static void Write(TextWriter output, IReadOnlyList<PipelineItem> items)
    {
        JsonOut.Write(output, json =>
        {
            json.WriteStartObject();
            json.WriteString("$schema", SchemaUrl);
            json.WriteNumber("egami", Version);
            json.WriteStartArray("steps");
            foreach (PipelineItem item in items)
            {
                json.WriteStartObject();
                switch (item)
                {
                    case StepItem { Step: var step }:
                        json.WriteString("op", step.Info.Name);
                        ParamInfo[] parameters = step.Info.Overloads[step.Overload].Parameters;
                        foreach ((string key, string value) in step.Values)
                        {
                            ParamInfo parameter = parameters.First(p => p.Name == key);
                            WriteValue(json, key, value, parameter.Type);
                        }

                        break;
                    case SaveItem { Save: var save }:
                        json.WriteString("op", "save");
                        if (save.Path is not null)
                        {
                            json.WriteString("path", save.Path);
                        }

                        if (save.Format is not null)
                        {
                            json.WriteString("format", save.Format);
                        }

                        foreach ((string key, string value) in save.Common)
                        {
                            WriteValue(json, key == "strip-metadata" ? "strip" : key, value, key is "lossless" or "strip-metadata" ? "bool" : "int");
                        }

                        if (save.Settings.Count > 0)
                        {
                            json.WriteStartObject("set");
                            foreach ((string key, string value) in save.Settings)
                            {
                                WriteValue(json, key, value, "text");
                            }

                            json.WriteEndObject();
                        }

                        break;
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        });
    }

    private static void WriteValue(Utf8JsonWriter json, string key, string value, string type)
    {
        if (type is "number" or "int" or "integer" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            json.WriteNumber(key, number);
        }
        else if (type == "bool" && ValueParsers.Bool(value) is bool flag)
        {
            json.WriteBoolean(key, flag);
        }
        else
        {
            json.WriteString(key, value);
        }
    }

    private static string Text(string name, string where, JsonProperty property)
    {
        JsonElement value = property.Value;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array when value.EnumerateArray().All(e => e.ValueKind is JsonValueKind.Number or JsonValueKind.String) =>
                string.Join(',', value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
            _ => throw Error(name, $"{where}: \"{property.Name}\" must be a string, number, true or false"),
        };
    }

    private static UsageException Error(string name, string problem) => new($"{name}: {problem.TrimEnd('.')}.");
}
