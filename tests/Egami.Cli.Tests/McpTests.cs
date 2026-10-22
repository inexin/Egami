using System.IO.Pipes;
using System.Text.Json;

using Egami.Cli.Host;
using Egami.Cli.Mcp;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Egami.Cli.Tests;

/// <summary>
/// egami mcp (Roadmap 2.0.1): the server run in this process through the same entry point as the executable, with the SDK's
/// client on the other end of a pair of pipes. Every call must do what the command line does, and stay inside the allowed folders.
/// </summary>
public sealed class McpTests : IAsyncDisposable
{
    private const string Jpeg = "jpeg/duck-420.jpg";

    private const string Png = "pngsuite/basn6a08.png";

    private static readonly string Snapshots = Path.Combine(Path.GetDirectoryName(Tool.Images)!, "Egami.Cli.Tests", "Snapshots");

    private readonly Tool _tool = new();

    private readonly List<IAsyncDisposable> _servers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable server in _servers)
        {
            await server.DisposeAsync();
        }

        _tool.Dispose();
    }

    [Fact]
    public async Task ListsOneToolPerCommand()
    {
        McpClient client = await Connect();
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: Token);
        string[] expected =
        [
            "info", "convert", "resize", "thumbnail", "crop", "rotate", "flip", "auto_orient", "run", "compare", "stats", "frames_split",
            "frames_join", "metadata_show", "metadata_strip", "metadata_copy", "color", "text", "overlay", "montage", "formats",
        ];
        Assert.Equal(expected, tools.Select(t => t.Name));
        foreach (McpClientTool tool in tools)
        {
            Assert.Equal("object", tool.ProtocolTool.InputSchema.GetProperty("type").GetString());
            Assert.False(tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal(!ToolCatalog.Find(tool.Name)!.Writes, tool.ProtocolTool.Annotations!.ReadOnlyHint);
        }

        Assert.Equal("egami", client.ServerInfo!.Name);
        Assert.Contains("workflow", client.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolListMatchesItsSnapshot()
    {
        McpClient client = await Connect();
        ListToolsResult result = await client.ListToolsAsync(new ListToolsRequestParams(), Token);
        string json = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ListToolsResult)));
        using JsonDocument document = JsonDocument.Parse(json);
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            document.RootElement.WriteTo(writer);
        }

        string text = System.Text.Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        string path = Path.Combine(Snapshots, "mcp", "tools.json");
        if (Environment.GetEnvironmentVariable("EGAMI_UPDATE_SNAPSHOTS") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            Assert.SkipWhen(Environment.GetEnvironmentVariable("EGAMI_UPDATE_SNAPSHOTS") != "1", "Wrote the new snapshot mcp/tools.json; run again.");
            return;
        }

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public async Task InfoReturnsItsJson()
    {
        string jpeg = _tool.Copy(Jpeg);
        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync("info", new Dictionary<string, object?> { ["inputs"] = new[] { jpeg } }, cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        JsonElement structured = result.StructuredContent!.Value;
        Assert.Equal(0, structured.GetProperty("exitCode").GetInt32());
        JsonElement file = structured.GetProperty("output").GetProperty("files")[0];
        Assert.Equal("JPEG", file.GetProperty("format").GetString());
        Assert.Equal(320, file.GetProperty("width").GetInt32());

        // The same JSON the command line prints.
        ToolRun run = _tool.Run("info", jpeg, "--json");
        using JsonDocument cli = JsonDocument.Parse(run.Text);
        Assert.True(JsonElement.DeepEquals(cli.RootElement, structured.GetProperty("output")));
    }

    [Fact]
    public async Task ResizeWritesWhatTheCommandLineWrites()
    {
        string jpeg = _tool.Copy(Jpeg);
        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync(
            "resize",
            new Dictionary<string, object?> { ["inputs"] = new[] { jpeg }, ["size"] = "100x", ["output"] = "mcp/{name}.png", ["strip"] = true },
            cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        string written = Assert.Single(Written(result));
        Assert.Equal(_tool.PathOf("mcp/duck-420.png"), written);
        Assert.Equal(0, _tool.Run("resize", jpeg, "--size", "100x", "-o", "cli/{name}.png", "--strip").Code);
        Assert.Equal(File.ReadAllBytes(_tool.PathOf("cli/duck-420.png")), File.ReadAllBytes(written));
    }

    [Fact]
    public async Task NegativeValuesAndOddNamesAreValuesNotOptions()
    {
        _tool.Copy(Png, "-odd.png");
        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync(
            "rotate",
            new Dictionary<string, object?> { ["inputs"] = Files("-odd.png"), ["angle"] = -90, ["output"] = "turned.png" },
            cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        using Raster turned = Raster.Load(_tool.PathOf("turned.png"));
        Assert.Equal(32, turned.Width);
    }

    [Fact]
    public async Task PreviewIsAPicture()
    {
        string jpeg = _tool.Copy(Jpeg);
        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync(
            "convert",
            new Dictionary<string, object?> { ["inputs"] = new[] { jpeg }, ["output"] = "out.webp", ["preview"] = true },
            cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        ImageContentBlock picture = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", picture.MimeType);
        using Raster image = Raster.Load(picture.DecodedData.ToArray());
        Assert.Equal((320, 240), (image.Width, image.Height));
    }

    [Fact]
    public async Task PathsOutsideTheAllowedFoldersAreRefused()
    {
        using Tool outside = new();
        string png = outside.Copy(Png);
        McpClient client = await Connect();
        CallToolResult read = await client.CallToolAsync("info", new Dictionary<string, object?> { ["inputs"] = new[] { outside.PathOf(png) } }, cancellationToken: Token);
        Assert.True(read.IsError);
        Assert.Contains("outside the folders", Text(read), StringComparison.Ordinal);

        string mine = _tool.Copy(Png);
        CallToolResult write = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = new[] { mine }, ["output"] = outside.PathOf("x.png") }, cancellationToken: Token);
        Assert.True(write.IsError);
        Assert.False(File.Exists(outside.PathOf("x.png")));

        CallToolResult relative = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = new[] { mine }, ["output"] = "../escape.png" }, cancellationToken: Token);
        Assert.True(relative.IsError);
        Assert.Equal(ExitCodes.Usage, relative.StructuredContent!.Value.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task LinksCannotLeadOut()
    {
        using Tool outside = new();
        outside.Copy(Png, "secret.png");
        try
        {
            File.CreateSymbolicLink(_tool.PathOf("link.png"), outside.PathOf("secret.png"));
            Directory.CreateSymbolicLink(_tool.PathOf("linked"), outside.Folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine does not let tests make symbolic links.");
        }

        McpClient client = await Connect();
        foreach (string input in (string[])["link.png", "linked/secret.png", "linked/*.png"])
        {
            CallToolResult result = await client.CallToolAsync("info", new Dictionary<string, object?> { ["inputs"] = new[] { input } }, cancellationToken: Token);
            Assert.True(result.IsError, input);
        }

        CallToolResult write = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = new[] { _tool.Copy(Png) }, ["output"] = "linked/new.png" }, cancellationToken: Token);
        Assert.True(write.IsError);
        Assert.False(File.Exists(outside.PathOf("new.png")));
    }

    [Fact]
    public async Task AllowAddsFolders()
    {
        using Tool other = new();
        string png = other.Copy(Png);
        McpClient client = await Connect("--allow", other.Folder);
        CallToolResult result = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = new[] { other.PathOf(png) }, ["output"] = other.PathOf("out.qoi") }, cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        Assert.True(File.Exists(other.PathOf("out.qoi")));

        // --allow replaces the working folder rather than adding to it.
        CallToolResult here = await client.CallToolAsync("info", new Dictionary<string, object?> { ["inputs"] = new[] { _tool.PathOf(_tool.Copy(Png)) } }, cancellationToken: Token);
        Assert.True(here.IsError);
    }

    [Fact]
    public async Task InputsAreNotReplacedUnlessAsked()
    {
        string png = _tool.Copy(Png);
        byte[] before = File.ReadAllBytes(_tool.PathOf(png));
        McpClient client = await Connect();
        CallToolResult refused = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = new[] { png }, ["output"] = png }, cancellationToken: Token);
        Assert.True(refused.IsError);
        Assert.Equal(before, File.ReadAllBytes(_tool.PathOf(png)));
        CallToolResult replaced = await client.CallToolAsync("flip", new Dictionary<string, object?> { ["inputs"] = new[] { png }, ["direction"] = "horizontal", ["in_place"] = true }, cancellationToken: Token);
        Assert.False(replaced.IsError, Text(replaced));
        Assert.NotEqual(before, File.ReadAllBytes(_tool.PathOf(png)));
    }

    [Fact]
    public async Task RunTakesAWorkflowObject()
    {
        string jpeg = _tool.Copy(Jpeg);
        _tool.Copy(Jpeg, "resize");
        McpClient client = await Connect();
        using JsonDocument workflow = JsonDocument.Parse("""{ "egami": 1, "steps": [ { "op": "resize", "size": "64x" }, { "op": "save", "path": "run/{name}.png" } ] }""");
        CallToolResult dry = await client.CallToolAsync(
            "run",
            new Dictionary<string, object?> { ["inputs"] = new[] { jpeg }, ["workflow"] = workflow.RootElement, ["dry_run"] = true },
            cancellationToken: Token);
        Assert.False(dry.IsError, Text(dry));
        Assert.Contains("resize", dry.StructuredContent!.Value.GetProperty("output").GetString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_tool.PathOf("run")));

        // An input named like a step is still an input.
        CallToolResult result = await client.CallToolAsync(
            "run",
            new Dictionary<string, object?> { ["inputs"] = new[] { jpeg, "resize" }, ["workflow"] = workflow.RootElement },
            cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        Assert.Equal(2, Written(result).Count);
        Assert.Equal(0, _tool.Run("run", jpeg, "resize", "64x", "+", "save", "cli.png").Code);
        Assert.Equal(File.ReadAllBytes(_tool.PathOf("cli.png")), File.ReadAllBytes(_tool.PathOf("run/duck-420.png")));

        CallToolResult steps = await client.CallToolAsync("run", new Dictionary<string, object?> { ["list_steps"] = true }, cancellationToken: Token);
        Assert.Contains("adaptive-threshold", Text(steps), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MistakesComeBackAsFailedCalls()
    {
        McpClient client = await Connect();
        CallToolResult unknown = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = Files("a.png"), ["colour"] = "red" }, cancellationToken: Token);
        Assert.True(unknown.IsError);
        Assert.Contains("no argument 'colour'", Text(unknown), StringComparison.Ordinal);
        CallToolResult missing = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["output"] = "x.png" }, cancellationToken: Token);
        Assert.True(missing.IsError);
        CallToolResult type = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = Files("a.png"), ["quality"] = "high" }, cancellationToken: Token);
        Assert.True(type.IsError);
        CallToolResult stdin = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = Files("-"), ["output"] = "x.png" }, cancellationToken: Token);
        Assert.True(stdin.IsError);
        Assert.Contains("Standard input", Text(stdin), StringComparison.Ordinal);
        await Assert.ThrowsAsync<McpProtocolException>(async () => await client.CallToolAsync("nothing", cancellationToken: Token));
    }

    [Fact]
    public async Task ADifferenceIsAResultNotAnError()
    {
        string a = _tool.Copy(Png, "a.png");
        Assert.Equal(0, _tool.Run("flip", a, "horizontal", "-o", "b.png").Code);
        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync("compare", new Dictionary<string, object?> { ["image"] = a, ["other"] = "b.png" }, cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        Assert.Equal(ExitCodes.Failed, result.StructuredContent!.Value.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Object, result.StructuredContent!.Value.GetProperty("output").ValueKind);
    }

    [Fact]
    public async Task BatchesReportProgressAndKeepToTheInputCap()
    {
        for (int i = 0; i < 4; i++)
        {
            _tool.Copy(Png, $"in/{i}.png");
        }

        McpClient client = await Connect("--max-inputs", "3");
        CallToolResult capped = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = Files("in/*.png"), ["output"] = "out/{name}.qoi" }, cancellationToken: Token);
        Assert.True(capped.IsError);
        Assert.Contains("more than 3 files", Text(capped), StringComparison.Ordinal);

        McpClient roomy = await Connect();
        List<ProgressNotificationValue> heard = [];

        // The client's own progress callback stops when the result arrives, and the last notification can race it; a handler
        // of the session's own hears every one the server sent.
        await using IAsyncDisposable listening = roomy.RegisterNotificationHandler(NotificationMethods.ProgressNotification, (note, _) =>
        {
            ProgressNotificationParams value = JsonSerializer.Deserialize(note.Params, McpJsonUtilities.DefaultOptions.GetTypeInfo<ProgressNotificationParams>())!;
            lock (heard)
            {
                heard.Add(value.Progress);
            }

            return ValueTask.CompletedTask;
        });
        CallToolResult result = await roomy.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = Files("in/*.png"), ["output"] = "out/{name}.qoi", ["jobs"] = 1 }, new Progress<ProgressNotificationValue>(), cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        Assert.Equal(4, Written(result).Count);
        for (int wait = 0; wait < 100 && heard.Count < 4; wait++)
        {
            await Task.Delay(20, Token);
        }

        lock (heard)
        {
            Assert.Equal([1, 2, 3, 4], heard.Select(h => (int)h.Progress).Order());
            Assert.All(heard, h => Assert.Equal(4, h.Total));
        }
    }

    [Fact]
    public async Task EveryToolRuns()
    {
        string jpeg = _tool.Copy(Jpeg);
        string png = _tool.Copy(Png);
        _tool.Copy("gif/loop-3.gif", "anim.gif");
        McpClient client = await Connect();
        Dictionary<string, Dictionary<string, object?>> calls = new()
        {
            ["info"] = new() { ["inputs"] = new[] { jpeg }, ["all"] = true },
            ["convert"] = new() { ["inputs"] = new[] { jpeg }, ["output"] = "t/convert.png" },
            ["resize"] = new() { ["inputs"] = new[] { jpeg }, ["size"] = "50%", ["output"] = "t/resize.png" },
            ["thumbnail"] = new() { ["inputs"] = new[] { jpeg }, ["size"] = "64", ["output"] = "t/thumbnail.webp" },
            ["crop"] = new() { ["inputs"] = new[] { jpeg }, ["rect"] = "10,10,50,40", ["output"] = "t/crop.png" },
            ["rotate"] = new() { ["inputs"] = new[] { jpeg }, ["angle"] = 90, ["output"] = "t/rotate.jpg" },
            ["flip"] = new() { ["inputs"] = new[] { jpeg }, ["direction"] = "vertical", ["output"] = "t/flip.png" },
            ["auto_orient"] = new() { ["inputs"] = new[] { jpeg }, ["output"] = "t/auto-orient.png" },
            ["run"] = new() { ["inputs"] = new[] { jpeg }, ["workflow"] = JsonDocument.Parse("""{ "egami": 1, "steps": [ { "op": "grayscale" } ] }""").RootElement, ["output"] = "t/run.png" },
            ["compare"] = new() { ["image"] = png, ["other"] = png },
            ["stats"] = new() { ["inputs"] = new[] { png } },
            ["frames_split"] = new() { ["input"] = "anim.gif", ["output"] = "t/frames/{index}.png" },
            ["frames_join"] = new() { ["inputs"] = Files("t/frames/*.png"), ["output"] = "t/joined.gif" },
            ["metadata_show"] = new() { ["inputs"] = new[] { jpeg } },
            ["metadata_strip"] = new() { ["inputs"] = new[] { jpeg }, ["output"] = "t/strip.jpg" },
            ["metadata_copy"] = new() { ["inputs"] = new[] { png }, ["from"] = jpeg, ["output"] = "t/copy.png" },
            ["color"] = new() { ["inputs"] = new[] { jpeg }, ["to"] = "display-p3", ["output"] = "t/color.png" },
            ["text"] = new() { ["inputs"] = new[] { jpeg }, ["text"] = "egami", ["output"] = "t/text.png" },
            ["overlay"] = new() { ["inputs"] = new[] { jpeg }, ["image"] = png, ["output"] = "t/overlay.png" },
            ["montage"] = new() { ["inputs"] = new[] { jpeg, png }, ["output"] = "t/montage.png" },
            ["formats"] = new() { ["format"] = "png" },
        };
        Assert.Equal(ToolCatalog.Tools.Select(t => t.Name).Order(), calls.Keys.Order());
        foreach (ToolInfo tool in ToolCatalog.Tools)
        {
            CallToolResult result = await client.CallToolAsync(tool.Name, calls[tool.Name], cancellationToken: Token);
            Assert.False(result.IsError, $"{tool.Name}: {Text(result)}");
            Assert.Equal(0, result.StructuredContent!.Value.GetProperty("exitCode").GetInt32());
        }
    }

    [Fact]
    public async Task TheBuiltToolServesOverItsOwnStandardStreams()
    {
        string png = _tool.Copy(Png);
        StdioClientTransportOptions start = new()
        {
            Name = "egami",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "Egami.Cli.dll"), "mcp"],
            WorkingDirectory = _tool.Folder,
        };
        await using McpClient client = await McpClient.CreateAsync(new StdioClientTransport(start), cancellationToken: Token);
        Assert.Equal(ToolCatalog.Tools.Count, (await client.ListToolsAsync(cancellationToken: Token)).Count);
        CallToolResult result = await client.CallToolAsync("convert", new Dictionary<string, object?> { ["inputs"] = new[] { png }, ["output"] = "e2e.qoi" }, cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        Assert.True(File.Exists(_tool.PathOf("e2e.qoi")));
    }

    [Fact]
    public void TheRegistryEntryNamesThisVersion()
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Tool.Images))!, "src", "Egami.Cli", ".mcp", "server.json");
        using JsonDocument server = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(McpHost.Version, server.RootElement.GetProperty("version").GetString());
        JsonElement package = Assert.Single(server.RootElement.GetProperty("packages").EnumerateArray());
        Assert.Equal(McpHost.Version, package.GetProperty("version").GetString());
        Assert.Equal("Egami.Cli", package.GetProperty("identifier").GetString());
    }

    [Fact]
    public void ThePathPolicyFollowsLinksAndKeepsToWholeNames()
    {
        PathPolicy policy = new([_tool.Folder], "/");
        Assert.True(policy.Allows(_tool.PathOf("a/b/c.png")));
        Assert.True(policy.Allows(_tool.Folder));
        Assert.False(policy.Allows(_tool.Folder + "-sibling"));
        Assert.False(policy.Allows(Path.GetDirectoryName(_tool.Folder)!));
        Assert.Throws<UsageException>(() => policy.Check(Path.GetTempPath()));
    }

    private static string[] Files(params string[] names) => names;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static List<string> Written(CallToolResult result) => [.. result.StructuredContent!.Value.GetProperty("written").EnumerateArray().Select(e => e.GetString()!)];

    /// <summary>Starts egami mcp in this process on a pair of pipes and connects the SDK's client to it.</summary>
    private async Task<McpClient> Connect(params string[] options)
    {
        AnonymousPipeServerStream toServer = new(PipeDirection.Out);
        AnonymousPipeClientStream serverIn = new(PipeDirection.In, toServer.ClientSafePipeHandle);
        AnonymousPipeServerStream fromServer = new(PipeDirection.In);
        AnonymousPipeClientStream serverOut = new(PipeDirection.Out, fromServer.ClientSafePipeHandle);
        StringWriter error = new();
        CliConsole console = new(() => serverIn, () => serverOut, TextWriter.Null, TextWriter.Synchronized(error));
        Task<int> server = Task.Run(() => Cli.Run(["mcp", .. options], console, _tool.Folder, _ => null));
        McpClient client = await McpClient.CreateAsync(new StreamClientTransport(toServer, fromServer), cancellationToken: Token);
        _servers.Add(new Server(client, server, [toServer, fromServer, serverIn, serverOut]));
        return client;
    }

    private sealed class Server(McpClient client, Task<int> run, Stream[] streams) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            streams[0].Dispose();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
            foreach (Stream stream in streams)
            {
                stream.Dispose();
            }
        }
    }
}
