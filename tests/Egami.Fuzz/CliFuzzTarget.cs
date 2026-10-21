using System.Text;

using Egami.Cli.Commands;
using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;

namespace Egami.Fuzz;

/// <summary>
/// The CLI's parsers (Roadmap 2.0): cli-size reads its input as the size syntax (and uses what parses on a few sizes),
/// cli-template as an output template (resolved for an input), cli-workflow as a JSON workflow file, and cli-run as the
/// arguments of egami run, one per line. Each may only fail with a usage error; any other exception is a crash.
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
}
