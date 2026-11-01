using Egami.Cli.Hosting;
using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Metafiles;
using Egami.Svg;

namespace Egami.Cli.Host;

/// <summary>
/// One run of the tool: its console, working folder, verbosity, safety limits and the Egami context every command reads and
/// writes through, with SVG, metafiles (CLI design Q2) and Egami.Formats.Extended's stable formats registered (its design, Q10)
/// and diagnostics going to standard error.
/// </summary>
internal sealed class CliSession : CliHostSession
{
    /// <summary>Gets the formats every command reads and writes.</summary>
    internal static EgamiContext Base { get; } = EgamiContext.Default.WithFormat(SvgFormat.Info).WithFormat(MetafileFormat.Info).WithExtendedFormats();

    public CliSession(CliConsole console, string workingDirectory, Func<string, string?> environment)
        : base("egami", console, workingDirectory, environment)
    {
        Context = Base;
    }

    /// <summary>Gets the safety limits in force.</summary>
    public SafetyLimits Limits { get; private set; } = SafetyLimits.Default;

    /// <summary>Gets the context to read and write with.</summary>
    public EgamiContext Context { get; private set; }

    /// <summary>Gets or sets the shared options of the command tree.</summary>
    public GlobalOptions? Globals { get; set; }

    /// <summary>Gets or sets the inputs and steps of a <c>run</c> given as values rather than words (<c>egami mcp</c>'s run tool).</summary>
    public RunRequest? Run { get; set; }

    /// <summary>Gets or sets how camera RAW inputs are developed (the <c>--raw-*</c> options), or <see langword="null"/> for the defaults.</summary>
    public Formats.Extended.Raw.RawReaderOptions? Raw { get; set; }

    /// <summary>Gets the reader options of an input: <paramref name="read"/> with the <c>--raw-*</c> options.</summary>
    public ReaderOptions? Reading(ReaderOptions? read) => RawOptions.Merge(Raw, read);

    /// <summary>Gets the formats the tool reads and writes.</summary>
    public IReadOnlyList<FormatInfo> Formats => Context.Formats;

    /// <summary>
    /// Sets the verbosity, the limits (from <c>EGAMI_LIMITS</c> and then <c>--limit</c>), how many cores one image may use and
    /// whether <c>--preview-formats</c> adds the formats in preview.
    /// </summary>
    public void Configure(Verbosity verbosity, string? limits, int maxParallelism, bool previewFormats = false)
    {
        Verbosity = verbosity;
        SafetyLimits safety = LimitSpec.Apply(SafetyLimits.Default, Environment("EGAMI_LIMITS"), "EGAMI_LIMITS");
        Limits = LimitSpec.Apply(safety, limits, "--limit");
        DiagnosticLevel level = verbosity switch
        {
            Verbosity.Debug => DiagnosticLevel.Debug,
            _ => DiagnosticLevel.Warning,
        };
        Context = (previewFormats ? PreviewFormats.Register(Base) : Base) with
        {
            Limits = Limits,
            DiagnosticLevel = level,
            Diagnostics = verbosity == Verbosity.Quiet ? null : Report,
            MaxParallelism = maxParallelism > 0 ? maxParallelism : Base.MaxParallelism,
        };
    }

    /// <summary>Loads an image named on the command line, such as an overlay or a displacement map.</summary>
    public Raster LoadImage(string path) => Raster.Load(ResolvePath(path), Context);

    /// <inheritdoc/>
    public override string InputNoun => "image";

    /// <inheritdoc/>
    public override string PatternExample => "*.jpg";

    /// <inheritdoc/>
    public override bool IsInput(string path)
    {
        string extension = Path.GetExtension(path).TrimStart('.');
        return extension.Length > 0 && Formats.Any(f => f.Reader is not null && f.Extensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)));
    }

    /// <inheritdoc/>
    public override int? Failure(Exception error) => FailureOf(error);

    /// <inheritdoc/>
    public override string Message(Exception error)
    {
        return error switch
        {
            LimitExceededException limit => $"exceeds a safety limit: {limit.Message} Raise it with --limit if you trust the file.",
            FileNotFoundException missing => missing.Message,
            _ => error.Message,
        };
    }

    /// <summary>The exit code an exception from one file maps to, or <see langword="null"/> for a bug, which is not caught.</summary>
    public static int? FailureOf(Exception error)
    {
        return error switch
        {
            LimitExceededException => ExitCodes.Limit,
            EgamiException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException => ExitCodes.Failed,
            _ => null,
        };
    }

    private void Report(EgamiDiagnostic diagnostic)
    {
        if (diagnostic.Level == DiagnosticLevel.Warning)
        {
            CountWarning();
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
