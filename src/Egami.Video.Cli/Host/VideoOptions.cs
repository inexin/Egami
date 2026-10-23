using System.CommandLine;
using System.CommandLine.Parsing;

namespace Egami.Video.Cli.Host;

/// <summary>
/// The options every egami-video command shares: verbosity, safety limits and progress (V1.3 Progress design Q8). One set per
/// command tree, since an option belongs to the commands it is added to.
/// </summary>
internal sealed class VideoGlobalOptions
{
    public Option<bool> Verbose { get; } = new("--verbose", "-v") { Description = "Write information to standard error.", Recursive = true };

    public Option<bool> Debug { get; } = new("--debug", "-vv") { Description = "Write debugging detail to standard error.", Recursive = true };

    public Option<bool> Quiet { get; } = new("--quiet", "-q") { Description = "Write only errors to standard error: no warnings and no progress line.", Recursive = true };

    public Option<string?> Limit { get; } = new("--limit")
    {
        Description = "Safety limits for decoded frames, such as pixels=100M,memory=2GB (names: pixels, width, height, frames, memory, metadata). EGAMI_LIMITS sets them too.",
        HelpName = "name=value,...",
        Recursive = true,
    };

    public Option<string?> Progress { get; } = new("--progress")
    {
        Description = "Write progress to a file, or - for standard output: key=value lines as ffmpeg -progress writes them, or JSON with --progress-format json.",
        HelpName = "file|-",
        Recursive = true,
    };

    public Option<string?> ProgressFormat { get; } = new("--progress-format")
    {
        Description = "How --progress writes: keys (the default, ffmpeg's key=value blocks) or json (one object per line).",
        HelpName = "keys|json",
        Recursive = true,
    };

    /// <summary>Adds the shared options to the root command and keeps them on the session.</summary>
    public static void AddTo(Command root, VideoSession session)
    {
        VideoGlobalOptions options = new();
        root.Options.Add(options.Verbose);
        root.Options.Add(options.Debug);
        root.Options.Add(options.Quiet);
        root.Options.Add(options.Limit);
        root.Options.Add(options.Progress);
        root.Options.Add(options.ProgressFormat);
        session.Globals = options;
    }

    /// <summary>Configures the session from the shared options.</summary>
    public static void Configure(VideoSession session, ParseResult result)
    {
        VideoGlobalOptions options = session.Globals ?? throw new InvalidOperationException("The command tree has no shared options.");
        Verbosity verbosity = result.GetValue(options.Quiet) ? Verbosity.Quiet
            : result.GetValue(options.Debug) ? Verbosity.Debug
            : result.GetValue(options.Verbose) ? Verbosity.Verbose
            : Verbosity.Normal;
        session.Configure(verbosity, result.GetValue(options.Limit));
        session.ProgressSink ??= ProgressSink.Create(session, result.GetValue(options.Progress), result.GetValue(options.ProgressFormat));
    }
}

/// <summary>The options of commands that read media files and write one file per input: inputs, output, tracks and batches.</summary>
internal sealed class MediaOptions
{
    public Argument<string[]> Inputs { get; } = new("inputs")
    {
        Description = "Media files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.",
        Arity = ArgumentArity.OneOrMore,
    };

    public Option<string?> Output { get; } = new("--output", "-o")
    {
        Description = "Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output (with --format).",
        HelpName = "path",
    };

    public Option<string?> Format { get; } = new("--format", "-f")
    {
        Description = "The output container by name or extension (mkv, webm, mp4, m4a, avi, ivf, y4m, wav, flac, ogg, aiff, caf, ac3), when -o's extension does not say; always for -o -.",
        HelpName = "container",
    };

    public Option<string[]> Track { get; } = new("--track", "-t")
    {
        Description = "Which tracks: v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as info shows it. Repeat it or separate with commas; all tracks by default.",
        HelpName = "track",
        AllowMultipleArgumentsPerToken = false,
    };

    public Option<bool> NoAudio { get; } = new("--no-audio", "-an") { Description = "Leave out audio tracks." };

    public Option<bool> NoSubtitles { get; } = new("--no-subtitles", "-sn") { Description = "Leave out subtitle tracks." };

    public Option<bool> Recursive { get; } = new("--recursive", "-r") { Description = "Take every media file in folders given as inputs, at any depth." };

    public Option<int> Jobs { get; } = new("--jobs", "-j") { Description = "How many files to work on at once; 0 (the default) for one per processor.", HelpName = "count" };

    public Option<bool> InPlace { get; } = new("--in-place") { Description = "Allow outputs to replace their inputs." };

    public Option<bool> FailFast { get; } = new("--fail-fast") { Description = "Stop at the first file that fails." };

    /// <summary>Gets or sets the inputs to use instead of the parsed ones.</summary>
    public string[]? InputsOverride { get; set; }

    /// <summary>Adds the options; <paramref name="write"/> adds the output options and <paramref name="tracks"/> the track options.</summary>
    public void AddTo(Command command, bool write = true, bool tracks = true, bool inputs = true)
    {
        if (inputs)
        {
            command.Arguments.Add(Inputs);
        }

        if (write)
        {
            command.Options.Add(Output);
            command.Options.Add(Format);
        }

        if (tracks)
        {
            command.Options.Add(Track);
            command.Options.Add(NoAudio);
            command.Options.Add(NoSubtitles);
        }

        command.Options.Add(Recursive);
        command.Options.Add(Jobs);
        if (write)
        {
            command.Options.Add(InPlace);
        }

        command.Options.Add(FailFast);
    }

    /// <summary>The inputs, expanded.</summary>
    public List<InputFile> ExpandInputs(VideoSession session, ParseResult result)
    {
        return Globber.Expand(session, InputsOverride ?? result.GetValue(Inputs) ?? [], result.GetValue(Recursive));
    }

    /// <summary>The tracks to work on.</summary>
    public TrackSelection Tracks(ParseResult result)
    {
        return TrackSelection.Parse(result.GetValue(Track), result.GetValue(NoAudio), result.GetValue(NoSubtitles));
    }

    /// <summary>The batch options, and <c>--in-place</c> set on the session.</summary>
    public BatchOptions Batch(VideoSession session, ParseResult result)
    {
        bool inPlace = result.GetResult(InPlace) is not null && result.GetValue(InPlace);
        session.InPlace = inPlace;
        int jobs = result.GetValue(Jobs);
        if (jobs < 0)
        {
            throw new UsageException("--jobs must be 0 or more.");
        }

        return new BatchOptions(jobs, result.GetValue(FailFast), inPlace);
    }

    /// <summary>The output template, checked against the number of inputs.</summary>
    public OutputTemplate OutputTemplate(VideoSession session, ParseResult result, IReadOnlyList<InputFile> inputs, string example = "out/{name}.mkv")
    {
        string text = result.GetValue(Output)
            ?? throw new UsageException($"No output: give -o with a file, a folder, a template such as {example}, or - with --format for standard output.");
        return CheckTemplate(session, text, inputs, example);
    }

    /// <summary>Checks an output template against the number of inputs.</summary>
    public static OutputTemplate CheckTemplate(VideoSession session, string text, IReadOnlyList<InputFile> inputs, string example, string part = "frame")
    {
        OutputTemplate template = Egami.Cli.Hosting.OutputTemplate.Parse(text, part);
        if (inputs.Count > 1)
        {
            if (template.IsStdout)
            {
                throw new UsageException($"-o - writes one file to standard output; with several inputs give a folder or a template such as {example}.");
            }

            bool folder = text.EndsWith('/') || text.EndsWith('\\') || (!template.HasPlaceholders && Directory.Exists(session.ResolvePath(text)));
            if (!folder && !template.IsPerInput)
            {
                throw new UsageException($"{inputs.Count} inputs would all be written to '{text}'; give a folder or a template with {{name}}, such as {example}.");
            }
        }

        return template;
    }
}
