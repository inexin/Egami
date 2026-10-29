using System.IO.Pipes;
using System.Text.Json;

using Egami.Video.Cli.Mcp;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// egami-video mcp (Build Plan task 24): the server run in this process through the same entry point as the executable, with
/// the SDK's client on the other end of a pair of pipes. Every call does what the command line does, inside the allowed folders.
/// </summary>
public sealed class McpTests : IAsyncDisposable
{
    private static readonly string[] StandardInput = ["-"];

    private static readonly string[] CutClip = ["cut.webm"];

    private readonly Tool _tool = new();

    private readonly List<IAsyncDisposable> _servers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
            "info", "remux", "trim", "concat", "segment", "timestamps", "extract", "embed", "metadata",
            "subs_convert", "subs_shift", "subs_sync", "subs_extract", "subs_embed", "frames", "encode", "run",
            "thumbnail", "scenes", "analyze", "waveform", "spectrum", "generate", "sheet", "animate", "hash", "compare", "verify", "audio",
            "join", "fade", "overlay", "pip", "stack", "speed", "reverse", "loop", "freeze", "slideshow", "stabilize", "edit", "formats",
        ];
        Assert.Equal(expected, tools.Select(t => t.Name));
        foreach (McpClientTool tool in tools)
        {
            Assert.Equal("object", tool.ProtocolTool.InputSchema.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal(!VideoTools.Catalog.Find(tool.Name)!.Writes, tool.ProtocolTool.Annotations!.ReadOnlyHint);
            Assert.False(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("progress", out _));
        }

        Assert.Equal("egami-video", client.ServerInfo!.Name);
    }

    [Fact]
    public async Task InfoReturnsItsJson()
    {
        string clip = _tool.Copy("avi-mjpeg-420-pcm.avi");
        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync("info", new Dictionary<string, object?> { ["inputs"] = new[] { clip } }, cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        JsonElement output = result.StructuredContent!.Value.GetProperty("output");
        Assert.Equal("AVI", output.GetProperty("files")[0].GetProperty("format").GetString());

        ToolRun run = _tool.Run("info", clip, "--json");
        using JsonDocument cli = JsonDocument.Parse(run.Text);
        Assert.True(JsonElement.DeepEquals(cli.RootElement, output));
    }

    [Fact]
    public async Task RemuxWritesInsideTheFolderWithProgress()
    {
        string clip = _tool.Copy("avi-mjpeg-420-pcm.avi");
        McpClient client = await Connect();
        List<ProgressNotificationValue> notes = [];

        // The call's own progress callback stops when the result arrives, which can come before the notifications are handled;
        // a handler of the session's own hears every one the server sent, as egami's MCP tests do.
        await using IAsyncDisposable listening = client.RegisterNotificationHandler(NotificationMethods.ProgressNotification, (note, _) =>
        {
            ProgressNotificationParams value = JsonSerializer.Deserialize(note.Params, McpJsonUtilities.DefaultOptions.GetTypeInfo<ProgressNotificationParams>())!;
            lock (notes)
            {
                notes.Add(value.Progress);
            }

            return ValueTask.CompletedTask;
        });
        CallToolResult result = await client.CallToolAsync(
            "remux",
            new Dictionary<string, object?> { ["inputs"] = new[] { clip }, ["output"] = "out/{name}.mkv" },
            new Progress<ProgressNotificationValue>(),
            cancellationToken: Token);
        Assert.False(result.IsError, Text(result));
        Assert.True(File.Exists(_tool.PathOf("out/avi-mjpeg-420-pcm.mkv")));
        Assert.Equal(RemuxTests.Packets(_tool.PathOf(clip)), RemuxTests.Packets(_tool.PathOf("out/avi-mjpeg-420-pcm.mkv")));
        for (int i = 0; i < 250 && notes.Count == 0; i++)
        {
            await Task.Delay(20, Token);
        }

        lock (notes)
        {
            Assert.NotEmpty(notes);
        }
    }

    [Fact]
    public async Task TrimAndFramesThroughTheProtocol()
    {
        string clip = _tool.Copy("webm-vp8-opus.webm", "in.webm");
        McpClient client = await Connect();
        CallToolResult trim = await client.CallToolAsync(
            "trim",
            new Dictionary<string, object?> { ["inputs"] = new[] { clip }, ["from"] = "0.5", ["to"] = "1.5", ["output"] = "cut.webm" },
            cancellationToken: Token);
        Assert.False(trim.IsError, Text(trim));
        Assert.EndsWith("cut.webm", trim.StructuredContent!.Value.GetProperty("written")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(2, MediaFile.Probe(_tool.PathOf("cut.webm")).Tracks.Count);

        CallToolResult frames = await client.CallToolAsync(
            "frames",
            new Dictionary<string, object?> { ["inputs"] = CutClip, ["output"] = "shots/{frame:000}.png", ["count"] = 3 },
            cancellationToken: Token);
        Assert.False(frames.IsError, Text(frames));
        Assert.Equal(3, frames.StructuredContent!.Value.GetProperty("written").GetArrayLength());
        Assert.Equal(3, Directory.GetFiles(_tool.PathOf("shots"), "*.png").Length);
    }

    [Fact]
    public async Task OutsideTheFolderIsRefused()
    {
        string clip = _tool.Copy("avi-mjpeg-420-pcm.avi");
        McpClient client = await Connect();
        string outside = Path.Combine(Path.GetTempPath(), "egami-video-mcp-outside-" + Guid.NewGuid().ToString("N") + ".mkv");
        CallToolResult result = await client.CallToolAsync("remux", new Dictionary<string, object?> { ["inputs"] = new[] { clip }, ["output"] = outside }, cancellationToken: Token);
        Assert.True(result.IsError);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task StandardStreamsAreTheProtocol()
    {
        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync("info", new Dictionary<string, object?> { ["inputs"] = StandardInput }, cancellationToken: Token);
        Assert.True(result.IsError);
        Assert.Contains("Standard input and output carry the protocol", Text(result), StringComparison.Ordinal);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private async Task<McpClient> Connect(params string[] options)
    {
        AnonymousPipeServerStream toServer = new(PipeDirection.Out);
        AnonymousPipeClientStream serverIn = new(PipeDirection.In, toServer.ClientSafePipeHandle);
        AnonymousPipeServerStream fromServer = new(PipeDirection.In);
        AnonymousPipeClientStream serverOut = new(PipeDirection.Out, fromServer.ClientSafePipeHandle);
        StringWriter error = new();
        CliConsole console = new(() => serverIn, () => serverOut, TextWriter.Null, TextWriter.Synchronized(error));
        Task<int> server = Task.Run(() => VideoCli.Run(["mcp", .. options], console, _tool.Folder, _ => null));
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
