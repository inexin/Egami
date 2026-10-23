using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Egami.Cli.Hosting.Mcp;

/// <summary>
/// The server behind a tool's <c>mcp</c> command: the official SDK's protocol over standard input and output, the tools of an
/// <see cref="McpCatalog"/>, and the folders calls may use, from the client's roots and <c>--allow</c>.
/// </summary>
internal sealed class McpServerHost : IDisposable
{
    private readonly CliHostSession _session;
    private readonly McpCatalog _catalog;
    private readonly IReadOnlyList<string> _allow;
    private readonly int _maxInputs;
    private readonly string? _limit;
    private readonly SemaphoreSlim _rootsLock = new(1, 1);
    private PathPolicy? _policy;
    private McpServer? _server;

    private McpServerHost(CliHostSession session, McpCatalog catalog, IReadOnlyList<string> allow, int maxInputs, string? limit)
    {
        _session = session;
        _catalog = catalog;
        _allow = allow;
        _maxInputs = maxInputs;
        _limit = limit;
    }

    public void Dispose() => _rootsLock.Dispose();

    /// <summary>Serves until the client closes standard input; returns the exit code.</summary>
    public static int Run(CliHostSession session, McpCatalog catalog, IReadOnlyList<string> allow, int maxInputs, string? limit)
    {
        if (maxInputs < 1)
        {
            throw new UsageException("--max-inputs must be 1 or more.");
        }

        using McpServerHost host = new(session, catalog, allow, maxInputs, limit);
        host.ServeAsync().GetAwaiter().GetResult();
        return ExitCodes.Success;
    }

    private async Task ServeAsync()
    {
        McpToolSet set = _catalog.Set;
        McpServerOptions options = new()
        {
            ServerInfo = new Implementation { Name = set.Name, Title = set.Title, Version = McpToolSet.Version, WebsiteUrl = set.WebsiteUrl },
            ServerInstructions = set.Instructions,
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. _catalog.Tools.Select(_catalog.Describe)] }),
                CallToolHandler = CallAsync,
#pragma warning disable MCP9005 // Roots: see PolicyAsync.
                NotificationHandlers =
                [
                    new(NotificationMethods.RootsListChangedNotification, (_, _) =>
                    {
                        _policy = null;
                        return ValueTask.CompletedTask;
                    }),
                ],
#pragma warning restore MCP9005
            },
        };

        Stream input = _session.Console.In();
        Stream output = _session.Console.Out();
        await using StreamServerTransport transport = new(input, output, set.Name);
        await using McpServer server = McpServer.Create(transport, options);
        _server = server;
        _session.Info($"{set.Name} mcp {McpToolSet.Version}: serving {_catalog.Tools.Count} tools on standard input and output");
        await server.RunAsync().ConfigureAwait(false);
    }

    private async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        CallToolRequestParams request = context.Params ?? throw new McpProtocolException("A tool call needs parameters.", McpErrorCode.InvalidParams);
        ToolInfo tool = _catalog.Find(request.Name)
            ?? throw new McpProtocolException($"{_catalog.Set.Name} has no tool '{request.Name}'.", McpErrorCode.InvalidParams);
        PathPolicy policy = await PolicyAsync(cancellationToken).ConfigureAwait(false);
        string working = policy.Allows(_session.WorkingDirectory) ? _session.WorkingDirectory : policy.Folders[0];
        Action<int, int>? progress = null;
        Action<double, double, string>? detail = null;
        List<Task> notes = [];
        if (request.ProgressToken is { } token && _server is { } server)
        {
            void Send(double done, double total, string message)
            {
                Task note = server.NotifyProgressAsync(token, new ProgressNotificationValue { Progress = (float)done, Total = (float)total, Message = message }, cancellationToken: CancellationToken.None);
                lock (notes)
                {
                    notes.Add(note);
                }
            }

            progress = (done, total) => Send(done, total, $"{done} of {total} files");
            detail = Send;
        }

        CallScope scope = new(policy, working, _session.Environment, _maxInputs, _limit, progress) { Note = detail };
        _session.Info($"{tool.Name}: {request.Arguments?.Count ?? 0} arguments");
        CallToolResult result = await McpCall.InvokeAsync(_catalog.Set, tool, request.Arguments, scope, cancellationToken).ConfigureAwait(false);

        // Progress goes out before the result, or the client has stopped listening for it.
        Task[] pending;
        lock (notes)
        {
            pending = [.. notes];
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _session.Info($"a progress notification was not sent: {error.Message}");
        }

        return result;
    }

    /// <summary>The folders calls may use: the client's roots and <c>--allow</c>, or the working folder when there are neither.</summary>
    private async Task<PathPolicy> PolicyAsync(CancellationToken cancellationToken)
    {
        if (_policy is { } known)
        {
            return known;
        }

        await _rootsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_policy is { } again)
            {
                return again;
            }

            List<string> folders = [.. _allow];

            // Roots are deprecated from protocol version 2026-07-28 (SEP-2577); clients on earlier versions still offer them, and
            // they are the only way such a client says which folders the user opened. Without them, --allow decides.
#pragma warning disable MCP9005
            if (_server is { ClientCapabilities.Roots: not null } server)
            {
                try
                {
                    ListRootsResult roots = await server.RequestRootsAsync(new ListRootsRequestParams(), cancellationToken).ConfigureAwait(false);
                    foreach (Root root in roots.Roots)
                    {
                        if (Uri.TryCreate(root.Uri, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                        {
                            folders.Add(uri.LocalPath);
                        }
                    }
                }
                catch (McpException error)
                {
                    _session.Warn($"the client did not list its roots: {error.Message}");
                }
            }
#pragma warning restore MCP9005

            if (folders.Count == 0)
            {
                // Some assistants start servers in the root of the disk; that is never a folder to hand over silently.
                if (string.Equals(Path.GetPathRoot(_session.WorkingDirectory), _session.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new McpException($"{_catalog.Set.Name} mcp has no folder to work in: the assistant opened none and the working folder is the root of the disk. Start it with --allow <folder>.");
                }

                folders.Add(_session.WorkingDirectory);
            }

            PathPolicy policy = new(folders, _session.WorkingDirectory, _catalog.Set.Name);
            _session.Info($"allowed folders: {string.Join(", ", policy.Folders)}");
            _policy = policy;
            return policy;
        }
        finally
        {
            _rootsLock.Release();
        }
    }
}
