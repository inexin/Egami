using System.Collections.Concurrent;

using Egami.Formats;
using Egami.Metafiles;
using Egami.Svg;

namespace Egami.Cli.Host;

/// <summary>How much the tool writes to standard error (CLI design Q13).</summary>
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
/// One run of the tool: its console, working folder, verbosity, safety limits and the Egami context every command reads and
/// writes through, with SVG and metafiles registered (CLI design Q2) and diagnostics going to standard error.
/// </summary>
internal sealed class CliSession
{
    private static readonly EgamiContext Base = EgamiContext.Default.WithFormat(SvgFormat.Info).WithFormat(MetafileFormat.Info);

    private int _warnings;

    public CliSession(CliConsole console, string workingDirectory, Func<string, string?> environment)
    {
        Console = console;
        WorkingDirectory = workingDirectory;
        Environment = environment;
        Context = Base;
    }

    /// <summary>Gets the console.</summary>
    public CliConsole Console { get; }

    /// <summary>Gets the folder relative paths start from.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets the environment variable reader.</summary>
    public Func<string, string?> Environment { get; }

    /// <summary>Gets how much goes to standard error.</summary>
    public Verbosity Verbosity { get; private set; } = Verbosity.Normal;

    /// <summary>Gets the safety limits in force.</summary>
    public SafetyLimits Limits { get; private set; } = SafetyLimits.Default;

    /// <summary>Gets the context to read and write with.</summary>
    public EgamiContext Context { get; private set; }

    /// <summary>Gets or sets the shared options of the command tree.</summary>
    public GlobalOptions? Globals { get; set; }

    /// <summary>Gets or sets a value indicating whether an output may replace its input (<c>--in-place</c>).</summary>
    public bool InPlace { get; set; }

    /// <summary>Gets the number of warnings written.</summary>
    public int Warnings => _warnings;

    /// <summary>Gets or sets the folders every path must stay inside, or <see langword="null"/> for any path (MCP design Q4).</summary>
    public PathPolicy? Paths { get; set; }

    /// <summary>Gets or sets the most inputs one command may take, or <see langword="null"/> for no limit (MCP design Q6).</summary>
    public int? MaxInputs { get; set; }

    /// <summary>Gets or sets the token that stops a batch between files.</summary>
    public CancellationToken Cancellation { get; set; }

    /// <summary>Gets or sets what hears of each finished file of a batch: files done so far, and the batch's size.</summary>
    public Action<int, int>? Progress { get; set; }

    /// <summary>Gets or sets the inputs and steps of a <c>run</c> given as values rather than words (<c>egami mcp</c>'s run tool).</summary>
    public RunRequest? Run { get; set; }

    /// <summary>Gets the files written, in the order they were finished.</summary>
    public ConcurrentQueue<string> Written { get; } = new();

    /// <summary>Gets the formats the tool reads and writes.</summary>
    public IReadOnlyList<FormatInfo> Formats => Context.Formats;

    /// <summary>Sets the verbosity, the limits (from <c>EGAMI_LIMITS</c> and then <c>--limit</c>) and how many cores one image may use.</summary>
    public void Configure(Verbosity verbosity, string? limits, int maxParallelism)
    {
        Verbosity = verbosity;
        SafetyLimits safety = LimitSpec.Apply(SafetyLimits.Default, Environment("EGAMI_LIMITS"), "EGAMI_LIMITS");
        Limits = LimitSpec.Apply(safety, limits, "--limit");
        DiagnosticLevel level = verbosity switch
        {
            Verbosity.Debug => DiagnosticLevel.Debug,
            _ => DiagnosticLevel.Warning,
        };
        Context = Base with
        {
            Limits = Limits,
            DiagnosticLevel = level,
            Diagnostics = verbosity == Verbosity.Quiet ? null : Report,
            MaxParallelism = maxParallelism > 0 ? maxParallelism : Base.MaxParallelism,
        };
    }

    /// <summary>A path made absolute against the working folder, and checked against <see cref="Paths"/>.</summary>
    public string ResolvePath(string path)
    {
        string full = Path.GetFullPath(path, WorkingDirectory);
        Paths?.Check(full);
        return full;
    }

    /// <summary>Loads an image named on the command line, such as an overlay or a displacement map.</summary>
    public Raster LoadImage(string path) => Raster.Load(ResolvePath(path), Context);

    /// <summary>Writes a warning, unless quiet.</summary>
    public void Warn(string message)
    {
        Interlocked.Increment(ref _warnings);
        if (Verbosity != Verbosity.Quiet)
        {
            Console.Error.WriteLine($"egami: warning: {message}");
        }
    }

    /// <summary>Writes information with <c>-v</c>.</summary>
    public void Info(string message)
    {
        if (Verbosity >= Verbosity.Verbose)
        {
            Console.Error.WriteLine($"egami: {message}");
        }
    }

    /// <summary>Writes an error; always shown.</summary>
    public void Error(string message) => Console.Error.WriteLine($"egami: error: {message}");

    private void Report(EgamiDiagnostic diagnostic)
    {
        if (diagnostic.Level == DiagnosticLevel.Warning)
        {
            Interlocked.Increment(ref _warnings);
        }

        string where = diagnostic.Frame is { } frame ? $" (frame {frame})" : string.Empty;
        string level = diagnostic.Level switch
        {
            DiagnosticLevel.Warning => "warning",
            DiagnosticLevel.Info => "info",
            _ => "debug",
        };
        Console.Error.WriteLine($"egami: {level}: {diagnostic.Source} {diagnostic.Id}: {diagnostic.Message}{where}");
    }
}

/// <summary>The inputs and steps of a <c>run</c> given as values, so no input is mistaken for a step or an option.</summary>
/// <param name="Inputs">The inputs: files, globs or folders.</param>
/// <param name="Items">The steps, as a workflow file gives them.</param>
internal sealed record RunRequest(IReadOnlyList<string> Inputs, List<Pipeline.PipelineItem> Items);
