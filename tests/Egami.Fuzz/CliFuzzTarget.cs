using System.Text;
using System.Text.Json;

using Egami.Cli.Commands;
using Egami.Cli.Host;
using Egami.Cli.Mcp;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;

namespace Egami.Fuzz;

/// <summary>
/// The CLI's parsers (Roadmap 2.0): cli-size reads its input as the size syntax (and uses what parses on a few sizes),
/// cli-template as an output template (resolved for an input), cli-workflow as a JSON workflow file, cli-run as the
/// arguments of egami run, one per line, and cli-mcp as an egami mcp tool call, { "tool": name, "arguments": { ... } }, turned
/// into command-line words. Each may only fail with a usage error; any other exception is a crash.
/// Egami.Cli.Tests compiles this file too and feeds it its seeds and random mutations.
/// </summary>
internal static class CliFuzzTarget
{
    public static void Size(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        try
        {
            SizeSpec spec = SizeSpec.Parse(text);
            _ = SizeSpec.Parse(spec.ToString());
            foreach (Size source in (ReadOnlySpan<Size>)[new(1, 1), new(4000, 3000), new(7, 65535)])
            {
                foreach (FitMode fit in Enum.GetValues<FitMode>())
                {
                    Size target = spec.Target(source, fit);
                    if (target.Width < 1 || target.Height < 1)
                    {
                        throw new InvalidOperationException($"'{text}' gave {target.Width}x{target.Height}.");
                    }
                }
            }
        }
        catch (UsageException)
        {
        }
    }

    public static void Template(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        try
        {
            OutputTemplate template = OutputTemplate.Parse(text);
            InputFile input = new("photos/a.jpg", "photos/a.jpg", "sub", 3);
            _ = template.Resolve(input, "png", 12);
            _ = template.Resolve(input with { Directory = string.Empty }, "webp");
        }
        catch (UsageException)
        {
        }
    }

    public static void Workflow(byte[] data)
    {
        try
        {
            List<PipelineItem> items = Egami.Cli.Commands.Workflow.Parse(data, "fuzz.json");
            using StringWriter written = new();
            Egami.Cli.Commands.Workflow.Write(written, items);
            List<PipelineItem> again = Egami.Cli.Commands.Workflow.Parse(Encoding.UTF8.GetBytes(written.ToString()), "again.json");
            if (again.Count != items.Count)
            {
                throw new InvalidOperationException("A written workflow read back with a different number of steps.");
            }
        }
        catch (UsageException)
        {
        }
    }

    public static void Run(byte[] data)
    {
        string[] args = Encoding.UTF8.GetString(data).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            RunCommand.Parsed parsed = RunCommand.Parse(args);
            foreach (RunCommand.ParsedStep step in parsed.Steps)
            {
                _ = RunCommand.Bind(step);
            }
        }
        catch (UsageException)
        {
        }
    }

    public static void Mcp(byte[] data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            ToolInfo tool = root.TryGetProperty("tool", out JsonElement name) && name.ValueKind == JsonValueKind.String && ToolCatalog.Tools.FirstOrDefault(t => name.ValueEquals(t.Name)) is { } named
                ? named
                : ToolCatalog.Tools[data.Length % ToolCatalog.Tools.Count];
            Dictionary<string, JsonElement> arguments = [];
            if (root.TryGetProperty("arguments", out JsonElement given) && given.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in given.EnumerateObject())
                {
                    // The SDK reads argument names into strings, refusing a message whose names are not valid UTF-8.
                    string key;
                    try
                    {
                        key = property.Name;
                    }
                    catch (InvalidOperationException)
                    {
                        return;
                    }

                    arguments[key] = property.Value;
                }
            }

            try
            {
                (string[] words, _, _) = ToolCall.Words(tool, arguments, null);
                if (words.Length == 0 || words.Any(w => w is null))
                {
                    throw new InvalidOperationException("A call gave no words.");
                }
            }
            catch (UsageException)
            {
            }
        }
    }
}
