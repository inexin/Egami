using System.Text.Json;

using Egami.Cli.Commands;
using Egami.Cli.Host;
using Egami.Cli.Hosting;
using Egami.Cli.Hosting.Mcp;
using Egami.Formats;

using ModelContextProtocol.Protocol;

namespace Egami.Cli.Mcp;

/// <summary>
/// What <c>egami mcp</c> serves (MCP design Q2, Q3): the shared host's server over egami's command tree, with egami's choices
/// of read-only commands, needed arguments, the workflow schema for <c>run</c> and a picture of the first file written.
/// </summary>
internal static class EgamiTools
{
    // ocr runs the program a call names (--engine-command), which an assistant could turn into any command: never a tool.
    private static readonly Lazy<McpCatalog> All = new(() => McpCatalog.Build(Set).Where(tool => tool.Name != "ocr"));

    /// <summary>Gets egami's tool set.</summary>
    public static McpToolSet Set => SetValue.Value;

    /// <summary>Gets every tool.</summary>
    public static McpCatalog Catalog => All.Value;

    private static Lazy<McpToolSet> SetValue { get; } = new(Create);

    private static McpToolSet Create()
    {
        (JsonElement body, JsonElement definitions) = LoadWorkflowSchema();
        return new McpToolSet
        {
            Name = "egami",
            Title = "Egami",
            WebsiteUrl = "https://inexin.github.io/Egami/",
            Instructions = "egami converts, inspects, resizes and edits image files on this computer with Egami, a fully managed .NET imaging library. "
                + "Every tool is an egami command; paths are files, globs or folders inside the folders the user allowed, and outputs never replace inputs unless in_place is true. "
                + "Start with info; formats lists what can be read and written and each format's settings; run takes any chain of steps as a workflow object (list_steps lists them). "
                + "Every result is an object: exitCode (0 success, 1 a file failed or compare found a difference, 2 a usage error, 3 a safety limit), "
                + "written (the files written), output (the command's JSON or text) and messages (warnings and errors).",
            CreateRoot = () => Cli.CreateRoot(new CliSession(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), ".", _ => null)),
            Run = (args, console, workingDirectory, environment, setup) => Cli.Run(args, console, workingDirectory, environment, setup),
            Failure = CliSession.FailureOf,

            // Commands that only read.
            ReadOnly = new HashSet<string>(StringComparer.Ordinal) { "info", "formats", "stats", "metadata_show" },

            // Arguments a command can't run without, though its tree calls them optional (tool review R3).
            Needed = new HashSet<(string, string)> { ("rotate", "angle"), ("flip", "direction"), ("text", "text"), ("overlay", "image") },
            Checks = new HashSet<string>(StringComparer.Ordinal) { "compare" },
            Workflow = new McpWorkflow(
                "Image files, globs (*, ?, **) or folders with recursive.",
                "The steps, as a workflow object: { \"egami\": 1, \"steps\": [ { \"op\": \"resize\", \"size\": \"1200x\" }, { \"op\": \"save\", \"path\": \"out/{name}.webp\", \"quality\": 82 } ] }. list_steps lists the steps and their options.",
                "List every step with its options, instead of running anything.",
                "{ \"egami\": 1, \"steps\": [ { \"op\": \"resize\", \"size\": \"800x\" }, { \"op\": \"save\", \"path\": \"out.webp\" } ] }",
                body,
                definitions,
                (inputs, json) => new RunRequest(inputs, Workflow.Parse(json, "workflow")),
                (session, request) => ((CliSession)session).Run = (RunRequest)request),
            Preview = Preview,
        };
    }

    /// <summary>The first frame of a written file, scaled to fit 1024 pixels, as PNG.</summary>
    private static ImageContentBlock Preview(string path)
    {
        const int Fit = 1024;
        EgamiContext context = EgamiContext.Default.WithFormat(Svg.SvgFormat.Info).WithFormat(Metafiles.MetafileFormat.Info);
        RasterInfo info = Raster.Probe(path, context);
        double scale = Math.Min(1, (double)Fit / Math.Max(info.Width, info.Height));
        int width = Math.Max(1, (int)Math.Round(info.Width * scale)), height = Math.Max(1, (int)Math.Round(info.Height * scale));
        using Raster image = Raster.Load(path, context, new ReaderOptions { Frames = ..1, MinimumSize = new Size(width, height) });
        Raster small = image.Width == width && image.Height == height ? image : image.Derive(ops => ops.Resize(width, height));
        try
        {
            using MemoryStream png = new();
            small.Save(png, "png");
            return ImageContentBlock.FromBytes(png.ToArray(), "image/png");
        }
        finally
        {
            if (!ReferenceEquals(small, image))
            {
                small.Dispose();
            }
        }
    }

    /// <summary>The workflow schema, from the published file built into the tool.</summary>
    private static (JsonElement Body, JsonElement Definitions) LoadWorkflowSchema()
    {
        using Stream stream = typeof(EgamiTools).Assembly.GetManifestResourceStream("egami-workflow-1.json")
            ?? throw new InvalidOperationException("The workflow schema is not built into the tool.");
        return McpWorkflow.SplitSchema(stream);
    }
}

/// <summary>egami's tools, by name.</summary>
internal static class ToolCatalog
{
    /// <summary>Gets every tool, in the order of the command tree.</summary>
    public static IReadOnlyList<ToolInfo> Tools => EgamiTools.Catalog.Tools;

    /// <summary>Finds a tool by name.</summary>
    public static ToolInfo? Find(string name) => EgamiTools.Catalog.Find(name);
}

/// <summary>The words an egami tool call stands for.</summary>
internal static class ToolCall
{
    /// <summary>The words a call stands for, the inputs and steps of a <c>run</c>, and whether a preview was asked for.</summary>
    internal static (string[] Words, object? Run, bool Preview) Words(ToolInfo tool, IDictionary<string, JsonElement>? arguments, string? limit)
        => McpCall.Words(EgamiTools.Set, tool, arguments, limit);
}

/// <summary>egami's server.</summary>
internal static class McpHost
{
    /// <summary>Gets the tool's version, as the server tells the client.</summary>
    internal static string Version => McpToolSet.Version;
}
