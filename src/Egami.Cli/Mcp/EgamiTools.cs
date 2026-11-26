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
            ReadOnly = new HashSet<string>(StringComparer.Ordinal) { "info", "formats", "stats", "metadata_show", "font_info" },

            // Arguments a command can't run without, though its tree calls them optional (tool review R3).
            Needed = new HashSet<(string, string)> { ("rotate", "angle"), ("flip", "direction"), ("text", "text"), ("overlay", "image") },
            Checks = new HashSet<string>(StringComparer.Ordinal) { "compare" },

            // How an animated SVG input is read: every tool reads inputs the same way.
            Shared = new HashSet<string>(StringComparer.Ordinal) { "--animate", "--at" },
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
            Resources = Resources(),
            Prompts = Prompts(),
        };
    }

    /// <summary>The documents egami mcp serves: the workflow schema, the formats and the agent guide.</summary>
    private static McpResourceEntry[] Resources()
    {
        System.Reflection.Assembly assembly = typeof(EgamiTools).Assembly;
        return
        [
            new("egami://schema/workflow", "workflow-schema", "egami workflow schema", "The JSON schema of a workflow object, as the run tool takes it and egami run --workflow reads it.", "application/schema+json", McpResourceEntry.Embedded(assembly, "egami-workflow-1.json")),
            new("egami://formats", "formats", "Formats egami reads and writes", "Every format with its extensions, media types and whether egami reads and writes it, as egami formats --json prints it.", "application/json", McpResourceEntry.Output((args, console, folder, environment, setup) => Cli.Run(args, console, folder, environment, setup), "formats", "--json")),
            new("egami://guide", "guide", "egami guide for agents", "How to call egami from scripts and through this server: commands, workflow files, exit codes and common mistakes.", "text/markdown", McpResourceEntry.Embedded(assembly, "egami-guide.md")),
        ];
    }

    /// <summary>The tasks egami mcp offers as prompts.</summary>
    private static McpPromptEntry[] Prompts()
    {
        McpPromptArgument folder = new("folder", "The folder that holds the images.");
        return
        [
            new(
                "thumbnails",
                "Make thumbnails",
                "Make a thumbnail of every image in a folder.",
                [folder, new("size", "The box each thumbnail fits in, in egami's size syntax, such as 256x256 (the default) or 320x.", Required: false)],
                ["info", "thumbnail"],
                a => $"Make a thumbnail of every image in {a["folder"]}, fitting {Or(a, "size", "256x256")}. "
                    + $"Call info on the folder first to see what it holds; then call thumbnail with inputs [\"{a["folder"]}\"], size \"{Or(a, "size", "256x256")}\" "
                    + "and output \"{dir}/thumbs/{name}.jpg\", so the thumbnails go to a thumbs folder beside the images."),
            new(
                "convert_folder",
                "Convert a folder",
                "Convert every image in a folder to another format.",
                [folder, new("format", "The format to write, by name or extension, such as png, webp, avif or jxl.")],
                ["formats", "convert"],
                a => $"Convert every image in {a["folder"]} to {a["format"]}. "
                    + $"Call formats with format \"{a["format"]}\" to check egami writes it and see its settings; then call convert with inputs [\"{a["folder"]}\"], "
                    + $"format \"{a["format"]}\" and output \"{{dir}}/{a["format"]}/{{name}}.{{format}}\", so the copies go to a new folder beside the originals."),
            new(
                "web_images",
                "Images for the web",
                "Resize the images in a folder for a web page and write them as WebP or AVIF.",
                [folder, new("max_width", "The widest an image may be, in pixels; 1600 by default.", Required: false)],
                ["info", "resize"],
                a => $"Make web copies of every image in {a["folder"]}, at most {Or(a, "max_width", "1600")} pixels wide. "
                    + $"Call info on the folder to see the sizes; then call resize with inputs [\"{a["folder"]}\"], size \"{Or(a, "max_width", "1600")}x>\" (only shrink), "
                    + "auto_orient true, strip true, quality 80 and output \"{dir}/web/{name}.webp\" (or .avif for smaller files that take longer to write). "
                    + "Tell the user the sizes before and after."),
            new(
                "strip_metadata",
                "Strip location and camera data",
                "Remove location (GPS) and camera data from the images in a folder before sharing them.",
                [folder],
                ["metadata_show", "metadata_strip"],
                a => $"Remove location and camera data from every image in {a["folder"]}. "
                    + $"Call metadata_show on the folder to see which images carry GPS or camera EXIF and XMP; then call metadata_strip with inputs [\"{a["folder"]}\"], "
                    + "keep \"icc\" (so colors stay right) and output \"{dir}/clean/{name}.{ext}\". "
                    + "A lossy image (JPEG, WebP, AVIF, JPEG XL) that keeps its color profile is written again, which needs reencode true and costs a generation: "
                    + "ask the user first, or leave keep out so JPEGs lose everything and are rewritten without decoding. Call metadata_show on the results to confirm."),
            new(
                "make_workflow",
                "Write a workflow",
                "Write an egami workflow for a goal and run it.",
                [new("goal", "What the workflow should do, such as 'auto-orient, resize to 1200 wide and save as WebP'."), new("inputs", "The images, globs or folder to run it on.", Required: false)],
                ["run"],
                a => $"Write an egami workflow that does this: {a["goal"]}. "
                    + "Read the resource egami://schema/workflow for the workflow object's shape, and call run with list_steps true for the steps and their options. "
                    + "Then call run with the workflow object, dry_run true and "
                    + (a.TryGetValue("inputs", out string? inputs) && !string.IsNullOrWhiteSpace(inputs) ? $"inputs [\"{inputs}\"]" : "the inputs the user names")
                    + " to check the plan, and once it is right, again without dry_run. Show the user the workflow so they can keep it as a .json file."),
        ];
    }

    private static string Or(IReadOnlyDictionary<string, string> arguments, string name, string fallback)
        => arguments.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    /// <summary>The first frame of a written file, scaled to fit 1024 pixels, as PNG.</summary>
    private static ImageContentBlock Preview(string path)
    {
        const int Fit = 1024;
        EgamiContext context = Host.CliSession.Base;
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
