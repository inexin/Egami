using System.Collections.Concurrent;

namespace Egami.Cli.Hosting;

/// <summary>How much the tool writes to standard error.</summary>
internal enum Verbosity
{
    /// <summary>Errors only (<c>-q</c>).</summary>
    Quiet,

    /// <summary>Errors and warnings, the default.</summary>
    Normal,

    /// <summary>Also information (<c>-v</c>).</summary>
    Verbose,

    /// <summary>Also debugging detail (<c>-vv</c>).</summary>
    Debug,
}

/// <summary>Standard input, output and error, swapped for buffers in tests.</summary>
/// <param name="In">Standard input, for <c>-</c> as an input.</param>
/// <param name="Out">Standard output as bytes, for <c>-o -</c>.</param>
/// <param name="Text">Standard output as text.</param>
/// <param name="Error">Standard error.</param>
internal sealed record CliConsole(Func<Stream> In, Func<Stream> Out, TextWriter Text, TextWriter Error)
{
    /// <summary>Gets the process's own standard streams.</summary>
    public static CliConsole System { get; } = new(
        Console.OpenStandardInput,
        Console.OpenStandardOutput,
        Console.Out,
        TextWriter.Synchronized(Console.Error));
}

/// <summary>
/// One run of a tool, as the shared host sees it: its name, console, working folder, verbosity, the folders it may use, and how
/// a failed file maps to an exit code. Each tool derives its own session with the library context its commands use.
/// </summary>
internal abstract class CliHostSession
{
    private int _warnings;

    /// <summary>Starts a session for the tool <paramref name="toolName"/>, such as <c>egami</c>.</summary>
    protected CliHostSession(string toolName, CliConsole console, string workingDirectory, Func<string, string?> environment)
    {
        ToolName = toolName;
        Console = console;
        WorkingDirectory = workingDirectory;
        Environment = environment;
    }

    /// <summary>Gets the command's name, which starts every message and names <c>&lt;tool&gt;-&lt;name&gt;</c> programs.</summary>
    public string ToolName { get; }

    /// <summary>Gets the console.</summary>
    public CliConsole Console { get; }

    /// <summary>Gets the folder relative paths start from.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets the environment variable reader.</summary>
    public Func<string, string?> Environment { get; }

    /// <summary>Gets or sets how much goes to standard error.</summary>
    public Verbosity Verbosity { get; protected set; } = Verbosity.Normal;

    /// <summary>Gets or sets a value indicating whether an output may replace its input (<c>--in-place</c>).</summary>
    public bool InPlace { get; set; }

    /// <summary>Gets the number of warnings written.</summary>
    public int Warnings => _warnings;

    /// <summary>Gets or sets the folders every path must stay inside, or <see langword="null"/> for any path (<c>mcp</c>).</summary>
    public PathPolicy? Paths { get; set; }

    /// <summary>Gets or sets the most inputs one command may take, or <see langword="null"/> for no limit (<c>mcp</c>).</summary>
    public int? MaxInputs { get; set; }

    /// <summary>Gets or sets the token that stops a batch between files.</summary>
    public CancellationToken Cancellation { get; set; }

    /// <summary>Gets or sets what hears of each finished file of a batch: files done so far, and the batch's size.</summary>
    public Action<int, int>? Progress { get; set; }

    /// <summary>
    /// Gets or sets what hears how far a long job within a file has come (the MCP server's progress notifications): work done,
    /// the whole (files of the batch, so 1.5 of 3 is halfway through the second file), and a line saying where it is.
    /// </summary>
    public Action<double, double, string>? ProgressNote { get; set; }

    /// <summary>Gets the files written, in the order they were finished.</summary>
    public ConcurrentQueue<string> Written { get; } = new();

    /// <summary>Gets the word for one input in messages, such as <c>image</c>.</summary>
    public virtual string InputNoun => "file";

    /// <summary>Gets a file pattern to suggest for a folder, such as <c>*.jpg</c>.</summary>
    public virtual string PatternExample => "*";

    /// <summary>Whether a file found in a folder given with <c>--recursive</c> is an input the tool reads.</summary>
    public virtual bool IsInput(string path) => true;

    /// <summary>The exit code an exception from one file maps to, or <see langword="null"/> for a bug, which is not caught.</summary>
    public abstract int? Failure(Exception error);

    /// <summary>A one-line message for a failed file.</summary>
    public virtual string Message(Exception error) => error.Message;

    /// <summary>A path made absolute against the working folder, and checked against <see cref="Paths"/>.</summary>
    public string ResolvePath(string path)
    {
        string full = Path.GetFullPath(path, WorkingDirectory);
        Paths?.Check(full);
        return full;
    }

    /// <summary>Writes a warning, unless quiet.</summary>
    public void Warn(string message)
    {
        CountWarning();
        if (Verbosity != Verbosity.Quiet)
        {
            Console.Error.WriteLine($"{ToolName}: warning: {message}");
        }
    }

    /// <summary>Writes information with <c>-v</c>.</summary>
    public void Info(string message)
    {
        if (Verbosity >= Verbosity.Verbose)
        {
            Console.Error.WriteLine($"{ToolName}: {message}");
        }
    }

    /// <summary>Writes an error; always shown.</summary>
    public void Error(string message) => Console.Error.WriteLine($"{ToolName}: error: {message}");

    /// <summary>Counts a warning that was written some other way, such as a library diagnostic.</summary>
    protected void CountWarning() => Interlocked.Increment(ref _warnings);
}
