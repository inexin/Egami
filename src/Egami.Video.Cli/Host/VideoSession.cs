using Egami.Cli.Host;

namespace Egami.Video.Cli.Host;

/// <summary>
/// One run of egami-video: the shared host's console, folders and verbosity, the Egami.Video context every command reads and
/// writes through, with container warnings going to standard error, and an egami session for the image work (steps, image
/// files, contact sheets).
/// </summary>
internal sealed class VideoSession : CliHostSession
{
    public VideoSession(CliConsole console, string workingDirectory, Func<string, string?> environment)
        : base("egami-video", console, workingDirectory, environment)
    {
        Images = new CliSession(console, workingDirectory, environment);
        Context = MediaContext.Default with { Warnings = Report };
    }

    /// <summary>Gets the egami session that image steps, image files and the Egami context come from.</summary>
    public CliSession Images { get; }

    /// <summary>Gets the context to read and write video with.</summary>
    public MediaContext Context { get; private set; }

    /// <summary>Gets or sets the shared options of the command tree.</summary>
    public VideoGlobalOptions? Globals { get; set; }

    /// <summary>Gets or sets where long jobs report progress, or <see langword="null"/> for nowhere.</summary>
    public ProgressSink? ProgressSink { get; set; }

    /// <inheritdoc/>
    public override string InputNoun => "file";

    /// <inheritdoc/>
    public override string PatternExample => "*.mkv";

    /// <summary>Sets the verbosity and the image limits (from <c>EGAMI_LIMITS</c> and <c>--limit</c>).</summary>
    public void Configure(Verbosity verbosity, string? limits)
    {
        Verbosity = verbosity;
        Images.Configure(verbosity, limits, 0);
        Images.Paths = Paths;
        Images.MaxInputs = MaxInputs;
        Images.Cancellation = Cancellation;
        Context = Context with { Egami = Images.Context };
    }

    /// <inheritdoc/>
    public override bool IsInput(string path)
    {
        string extension = Path.GetExtension(path).TrimStart('.');
        return extension.Length > 0 && Context.FindContainer(extension) is not null;
    }

    /// <inheritdoc/>
    public override int? Failure(Exception error) => FailureOf(error);

    /// <inheritdoc/>
    public override string Message(Exception error) => Images.Message(error);

    /// <summary>The exit code an exception from one file maps to, or <see langword="null"/> for a bug, which is not caught.</summary>
    public static int? FailureOf(Exception error)
    {
        return error switch
        {
            OperationCanceledException => ExitCodes.Cancelled,
            InvalidDataException or EndOfStreamException or FormatException => ExitCodes.Failed,
            _ => CliSession.FailureOf(error),
        };
    }

    private void Report(MediaWarning warning)
    {
        if (Verbosity != Verbosity.Quiet)
        {
            CountWarning();
            string track = warning.TrackNumber is { } number ? $" (track {number})" : string.Empty;
            Console.Error.WriteLine($"egami-video: warning: {warning.Code}: {warning.Message}{track}");
        }
    }
}
