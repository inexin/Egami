using System.CommandLine;
using System.CommandLine.Parsing;

using Egami.Cli.Pipeline;

namespace Egami.Cli.Host;

/// <summary>
/// The options every command shares (CLI design Q13): verbosity, quiet and safety limits. One set per command tree, since an
/// option belongs to the commands it is added to.
/// </summary>
internal sealed class GlobalOptions
{
    public Option<bool> Verbose { get; } = new("--verbose", "-v") { Description = "Write information to standard error.", Recursive = true };

    public Option<bool> Debug { get; } = new("--debug", "-vv") { Description = "Write debugging detail to standard error.", Recursive = true };

    public Option<bool> Quiet { get; } = new("--quiet", "-q") { Description = "Write only errors to standard error.", Recursive = true };

    public Option<string?> Limit { get; } = new("--limit")
    {
        Description = "Safety limits, such as pixels=100M,memory=2GB,frames=500 (names: pixels, width, height, frames, memory, metadata). EGAMI_LIMITS sets them too.",
        HelpName = "name=value,...",
        Recursive = true,
    };

    /// <summary>Adds the shared options to the root command and keeps them on the session.</summary>
    public static void AddTo(Command root, CliSession session)
    {
        GlobalOptions options = new();
        root.Options.Add(options.Verbose);
        root.Options.Add(options.Debug);
        root.Options.Add(options.Quiet);
        root.Options.Add(options.Limit);
        session.Globals = options;
    }

    /// <summary>Configures the session from the shared options.</summary>
    public static void Configure(CliSession session, ParseResult result, int maxParallelism = 0)
    {
        GlobalOptions options = session.Globals ?? throw new InvalidOperationException("The command tree has no shared options.");
        Verbosity verbosity = result.GetValue(options.Quiet) ? Verbosity.Quiet
            : result.GetValue(options.Debug) ? Verbosity.Debug
            : result.GetValue(options.Verbose) ? Verbosity.Verbose
            : Verbosity.Normal;
        session.Configure(verbosity, result.GetValue(options.Limit), maxParallelism);
    }
}

/// <summary>The options of commands that write images: output, format, writer settings, reading and batches.</summary>
internal sealed class ImageOptions
{
    public Argument<string[]> Inputs { get; } = new("inputs")
    {
        Description = "Image files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.",
        Arity = ArgumentArity.OneOrMore,
    };

    public Option<string?> Output { get; } = new("--output", "-o")
    {
        Description = "Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.",
        HelpName = "path",
    };

    public Option<string?> Format { get; } = new("--format", "-f")
    {
        Description = "The output format by name or extension, when -o's extension does not say (and always for -o -).",
        HelpName = "format",
    };

    public Option<int?> Quality { get; } = new("--quality") { Description = "Lossy quality from 0 to 100 (JPEG, WebP, AVIF, JPEG XL, TIFF's JPEG).", HelpName = "0-100" };

    public Option<bool?> Lossless { get; } = new("--lossless") { Description = "Lossless encoding (WebP, AVIF, JPEG XL)." };

    public Option<int?> Effort { get; } = new("--effort") { Description = "Encoder effort: higher is smaller and slower (WebP, AVIF, JPEG XL).", HelpName = "level" };

    public Option<int?> BitDepth { get; } = new("--bit-depth") { Description = "Bits per channel to write (PNG, AVIF).", HelpName = "bits" };

    public Option<bool> Strip { get; } = new("--strip") { Description = "Leave out metadata (EXIF, XMP, IPTC, text, resolution) but keep the color profile, so colors stay right." };

    public Option<string[]> Set { get; } = new("--set")
    {
        Description = "Any writer setting as fmt.key=value, such as png.compression-level=9; 'egami formats png' lists the keys.",
        HelpName = "fmt.key=value",
        AllowMultipleArgumentsPerToken = false,
    };

    public Option<bool> AutoOrient { get; } = new("--auto-orient") { Description = "Turn the image upright from its EXIF orientation first." };

    public Option<string?> To { get; } = new("--to") { Description = "Convert colors to this profile while reading: srgb, display-p3, adobe-rgb, rec2020, ... or an ICC file.", HelpName = "profile" };

    public Option<bool> Recursive { get; } = new("--recursive", "-r") { Description = "Take every image in folders given as inputs, at any depth." };

    public Option<int> Jobs { get; } = new("--jobs", "-j") { Description = "How many files to work on at once; 0 (the default) for one per processor.", HelpName = "count" };

    public Option<bool> InPlace { get; } = new("--in-place") { Description = "Allow outputs to replace their inputs." };

    public Option<bool> FailFast { get; } = new("--fail-fast") { Description = "Stop at the first file that fails." };

    /// <summary>Adds the options; <paramref name="read"/> adds the reading options and <paramref name="write"/> the writer options.</summary>
    public void AddTo(Command command, bool inputs = true, bool write = true, bool read = true)
    {
        if (inputs)
        {
            command.Arguments.Add(Inputs);
        }

        if (write)
        {
            command.Options.Add(Output);
            command.Options.Add(Format);
            command.Options.Add(Quality);
            command.Options.Add(Lossless);
            command.Options.Add(Effort);
            command.Options.Add(BitDepth);
            command.Options.Add(Strip);
            command.Options.Add(Set);
        }

        if (read)
        {
            command.Options.Add(AutoOrient);
            command.Options.Add(To);
        }

        command.Options.Add(Recursive);
        command.Options.Add(Jobs);
        if (write)
        {
            command.Options.Add(InPlace);
        }

        command.Options.Add(FailFast);
    }

    /// <summary>The save the options describe.</summary>
    public SaveSpec Save(ParseResult result, IReadOnlyDictionary<string, string>? extraCommon = null)
    {
        Dictionary<string, string> common = new(StringComparer.Ordinal);
        if (result.GetValue(Quality) is { } quality)
        {
            common["quality"] = quality.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (result.GetValue(Lossless) is { } lossless)
        {
            common["lossless"] = lossless ? "true" : "false";
        }

        if (result.GetValue(Effort) is { } effort)
        {
            common["effort"] = effort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (result.GetValue(BitDepth) is { } depth)
        {
            common["bit-depth"] = depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (result.GetValue(Strip))
        {
            common["strip-metadata"] = "true";
        }

        foreach ((string key, string value) in extraCommon ?? new Dictionary<string, string>())
        {
            common[key] = value;
        }

        return new SaveSpec(null, result.GetValue(Format), SetValues(result.GetValue(Set)), common);
    }

    /// <summary>Parses <c>--set</c> values into a dictionary.</summary>
    public static Dictionary<string, string> SetValues(string[]? values)
    {
        Dictionary<string, string> settings = new(StringComparer.Ordinal);
        foreach (string text in values ?? [])
        {
            int equals = text.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new UsageException($"--set '{text}' is not key=value, such as png.compression-level=9.");
            }

            settings[text[..equals].Trim().ToLowerInvariant()] = text[(equals + 1)..].Trim();
        }

        return settings;
    }

    /// <summary>The reading options.</summary>
    public ReadSpec Read(ParseResult result, bool firstFrameOnly = false)
    {
        return new ReadSpec(result.GetValue(To) is { } profile ? Steps.ValueParsers.ParseProfile(profile) : null, firstFrameOnly);
    }

    /// <summary>The batch options, and <c>--in-place</c> set on the session.</summary>
    public BatchOptions Batch(CliSession session, ParseResult result)
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

    /// <summary>Gets or sets the inputs to use instead of the parsed ones, when a command took a value off their end.</summary>
    public string[]? InputsOverride { get; set; }

    /// <summary>The inputs, expanded.</summary>
    public List<InputFile> ExpandInputs(CliSession session, ParseResult result)
    {
        return Globber.Expand(session, InputsOverride ?? result.GetValue(Inputs) ?? [], result.GetValue(Recursive));
    }

    /// <summary>The output template, checked against the number of inputs; <see langword="null"/> when none was given.</summary>
    public OutputTemplate? OutputTemplate(CliSession session, ParseResult result, IReadOnlyList<InputFile> inputs, bool required = true)
    {
        string? text = result.GetValue(Output);
        if (text is null)
        {
            if (required && !session.InPlace)
            {
                throw new UsageException("No output: give -o with a file, a folder, a template such as out/{name}.webp, or - for standard output.");
            }

            return null;
        }

        OutputTemplate template = Pipeline.OutputTemplate.Parse(text);
        if (inputs.Count > 1)
        {
            if (template.IsStdout)
            {
                throw new UsageException("-o - writes one image to standard output; with several inputs give a folder or a template such as out/{name}.png.");
            }

            bool folder = text.EndsWith('/') || text.EndsWith('\\') || (!template.HasPlaceholders && Directory.Exists(session.ResolvePath(text)));
            if (!folder && !template.IsPerInput)
            {
                throw new UsageException($"{inputs.Count} inputs would all be written to '{text}'; give a folder or a template with {{name}}, such as out/{{name}}.png.");
            }
        }

        return template;
    }
}
