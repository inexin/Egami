using System.CommandLine;
using System.Globalization;

using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;

namespace Egami.Cli.Commands;

/// <summary>
/// <c>egami run</c>: any number of steps over each input, separated by a bare <c>+</c>, each step's options right after its
/// name, with <c>save</c> steps that write (CLI design Q4, Q5); or the same steps from a JSON workflow file (Q6).
/// </summary>
internal static class RunCommand
{
    /// <summary>The options of <c>run</c> itself; every other option belongs to the step it follows.</summary>
    private static readonly Dictionary<string, bool> RunOptions = new(StringComparer.Ordinal)
    {
        // name → takes a value
        ["-o"] = true,
        ["--output"] = true,
        ["--workflow"] = true,
        ["-w"] = true,
        ["--save-workflow"] = true,
        ["--dry-run"] = false,
        ["-r"] = false,
        ["--recursive"] = false,
        ["-j"] = true,
        ["--jobs"] = true,
        ["--in-place"] = false,
        ["--fail-fast"] = false,
        ["--to"] = true,
        ["-v"] = false,
        ["--verbose"] = false,
        ["-vv"] = false,
        ["--debug"] = false,
        ["-q"] = false,
        ["--quiet"] = false,
        ["--limit"] = true,
    };

    /// <summary>The options of <c>save</c>: name → takes a value.</summary>
    private static readonly Dictionary<string, bool> SaveOptions = new(StringComparer.Ordinal)
    {
        ["--format"] = true,
        ["-f"] = true,
        ["--quality"] = true,
        ["--lossless"] = false,
        ["--effort"] = true,
        ["--bit-depth"] = true,
        ["--strip"] = false,
        ["--set"] = true,
    };

    /// <summary>The command, for help; the tool parses <c>run</c>'s arguments itself, since steps carry their own options.</summary>
    public static Command Create(CliSession session)
    {
        Command command = new(
            "run",
            "Run steps over images, separated by +: 'egami run in.jpg auto-orient + resize 1200x + sharpen 0.6 + save out.webp --quality 82'. "
            + "Every Egami operation is a step ('egami run --list'); save writes, and may come more than once.")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        command.Arguments.Add(new Argument<string[]>("inputs-and-steps") { Description = "Inputs (files, globs, -), then steps separated by +.", Arity = ArgumentArity.ZeroOrMore });
        command.Options.Add(new Option<string?>("--output", "-o") { Description = "Where a save without a path writes, and where the plan writes when it has no save: a file, folder, template or -.", HelpName = "path" });
        command.Options.Add(new Option<string?>("--workflow", "-w") { Description = "Take the steps from a JSON workflow file.", HelpName = "file" });
        command.Options.Add(new Option<string?>("--save-workflow") { Description = "Write the steps to a JSON workflow file (then run them, unless --dry-run).", HelpName = "file" });
        command.Options.Add(new Option<bool>("--dry-run") { Description = "Print the steps and the files each input would write, and stop." });
        command.Options.Add(new Option<bool>("--list") { Description = "List every step with its arguments." });
        command.Options.Add(new Option<string?>("--to") { Description = "Convert colors to this profile while reading.", HelpName = "profile" });
        command.Options.Add(new Option<bool>("--recursive", "-r") { Description = "Take every image in folders given as inputs." });
        command.Options.Add(new Option<int>("--jobs", "-j") { Description = "How many files to work on at once; 0 for one per processor.", HelpName = "count" });
        command.Options.Add(new Option<bool>("--in-place") { Description = "Allow outputs to replace their inputs." });
        command.Options.Add(new Option<bool>("--fail-fast") { Description = "Stop at the first file that fails." });
        command.SetAction(_ => ExitCodes.Usage);
        _ = session;
        return command;
    }

    /// <summary>Runs <c>run</c> from its raw arguments (everything after the word run).</summary>
    public static int Execute(CliSession session, string[] args)
    {
        if (args is ["--list"] or ["--list", ..])
        {
            ListSteps(session, args.Length > 1 ? args[1] : null);
            return ExitCodes.Success;
        }

        Parsed parsed = Parse(args);
        Verbosity verbosity = parsed.Flags.Contains("-q") ? Verbosity.Quiet : parsed.Flags.Contains("-vv") ? Verbosity.Debug : parsed.Flags.Contains("-v") ? Verbosity.Verbose : Verbosity.Normal;
        session.Configure(verbosity, parsed.Value("--limit"), 0);
        session.InPlace = parsed.Flags.Contains("--in-place");

        List<PipelineItem> items;
        if (parsed.Value("--workflow") is { } workflowPath)
        {
            if (parsed.Steps.Count > 0)
            {
                throw new UsageException("Give the steps either on the command line or with --workflow, not both.");
            }

            items = Workflow.Load(session.ResolvePath(workflowPath));
        }
        else
        {
            items = [.. parsed.Steps.Select(Bind)];
        }

        if (items.Count == 0)
        {
            throw new UsageException("No steps: 'egami run in.jpg resize 800x + save out.webp', or --workflow file.json. 'egami run --list' lists the steps.");
        }

        if (parsed.SaveDefaults.Count > 0)
        {
            if (items[^1] is not SaveItem)
            {
                items.Add(new SaveItem(SaveSpec.Default));
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is SaveItem save)
                {
                    ParsedStep defaults = new("save");
                    defaults.Named.AddRange(parsed.SaveDefaults);
                    SaveSpec extra = SaveSpecOf(defaults);
                    items[i] = new SaveItem(save.Save with
                    {
                        Format = save.Save.Format ?? extra.Format,
                        Common = Merge(extra.Common, save.Save.Common),
                        Settings = Merge(extra.Settings, save.Save.Settings),
                    });
                }
            }
        }

        ReadSpec read = new(parsed.Value("--to") is { } profile ? ValueParsers.ParseProfile(profile) : null);
        Plan plan = new(items, read);
        if (parsed.Value("--save-workflow") is { } savePath)
        {
            Workflow.Save(session.ResolvePath(savePath), items);
            session.Info($"wrote {savePath}");
        }

        int jobs = parsed.Value("--jobs") is { } jobsText ? ValueParsers.ParseInt(jobsText) : 0;
        if (jobs < 0)
        {
            throw new UsageException("--jobs must be 0 or more.");
        }

        if (parsed.Inputs.Count == 0)
        {
            if (parsed.Value("--save-workflow") is not null)
            {
                return ExitCodes.Success;
            }

            throw new UsageException("No inputs: 'egami run in.jpg resize 800x + save out.webp'.");
        }

        List<InputFile> inputs = Globber.Expand(session, parsed.Inputs, parsed.Flags.Contains("-r"));
        session.Configure(verbosity, parsed.Value("--limit"), inputs.Count > 1 && jobs != 1 ? 1 : 0);
        OutputTemplate? output = parsed.Value("-o") is { } outputText ? OutputTemplate.Parse(outputText) : null;
        if (output is { IsStdout: true } && inputs.Count > 1)
        {
            throw new UsageException("-o - writes one image to standard output; with several inputs give a template such as out/{name}.png.");
        }

        if (parsed.Flags.Contains("--dry-run"))
        {
            DryRun(session, inputs, plan, output);
            return ExitCodes.Success;
        }

        BatchOptions batch = new(jobs, parsed.Flags.Contains("--fail-fast"), session.InPlace);
        return Batch.Run(session, inputs, batch, input => Runner.Run(session, input, plan, output));
    }

    /// <summary>Binds one parsed step.</summary>
    internal static PipelineItem Bind(ParsedStep step)
    {
        if (step.Name == "save")
        {
            return new SaveItem(SaveSpecOf(step));
        }

        StepInfo info = StepCatalog.Find(step.Name) ?? throw new UsageException($"'{step.Name}' is not a step. 'egami run --list' lists them.");
        return new StepItem(StepCatalog.Bind(info, step.Positional, step.Named));
    }

    /// <summary>A save step's spec from its arguments.</summary>
    internal static SaveSpec SaveSpecOf(ParsedStep step)
    {
        if (step.Positional.Count > 1)
        {
            throw new UsageException($"save takes one path, not {string.Join(' ', step.Positional)}.");
        }

        Dictionary<string, string> common = new(StringComparer.Ordinal);
        List<string> sets = [];
        string? format = null;
        foreach ((string key, string value) in step.Named)
        {
            switch (key)
            {
                case "format": format = value; break;
                case "set": sets.Add(value); break;
                case "strip": common["strip-metadata"] = value; break;
                case "quality" or "lossless" or "effort" or "bit-depth":
                    common[key] = value;
                    break;
                default:
                    throw new UsageException($"save has no option --{key}; it takes a path, --format, --quality, --lossless, --effort, --bit-depth, --strip and --set.");
            }
        }

        return new SaveSpec(step.Positional.Count == 1 ? step.Positional[0] : null, format, ImageOptions.SetValues([.. sets]), common);
    }

    /// <summary>Splits <c>run</c>'s raw arguments into inputs, steps and run options.</summary>
    internal static Parsed Parse(string[] args)
    {
        Parsed parsed = new();
        ParsedStep? current = null;
        bool expectStep = false;
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (token == "+")
            {
                if (current is null && parsed.Steps.Count == 0)
                {
                    throw new UsageException("A + must come between two steps: 'resize 800x + sharpen'.");
                }

                expectStep = true;
                current = null;
                continue;
            }

            if (token.StartsWith('-') && token != "-" && !IsNegativeNumber(token))
            {
                (string name, string? inline) = SplitOption(token);
                string bare = name.TrimStart('-');
                if (current is not null && Accepts(current, bare) is { } takesValue)
                {
                    string value = takesValue ? inline ?? Next(args, ref i, name) : inline ?? BoolValue(args, ref i);
                    current.Named.Add(new(bare == "f" ? "format" : bare, value));
                    continue;
                }

                if (SaveOptions.TryGetValue(name, out bool saveTakesValue))
                {
                    // Writer options outside a save step apply to every save that does not set them itself.
                    string value = saveTakesValue ? inline ?? Next(args, ref i, name) : inline ?? BoolValue(args, ref i);
                    parsed.SaveDefaults.Add(new(bare == "f" ? "format" : bare, value));
                    continue;
                }

                string canonical = Canonical(name);
                if (RunOptions.TryGetValue(name, out bool runTakesValue))
                {
                    if (runTakesValue)
                    {
                        parsed.Values[canonical] = inline ?? Next(args, ref i, name);
                    }
                    else
                    {
                        parsed.Flags.Add(canonical);
                    }

                    continue;
                }

                throw new UsageException(current is null
                    ? $"Unknown option {name} for run. 'egami run --help' lists run's options; a step's options go after its name."
                    : $"{current.Name} has no option {name}. It takes {Usage(current.Name)}.");
            }

            if (expectStep || (current is null && parsed.Steps.Count == 0 && IsStepName(token)))
            {
                current = new ParsedStep(token.ToLowerInvariant());
                parsed.Steps.Add(current);
                expectStep = false;
                continue;
            }

            if (current is null)
            {
                parsed.Inputs.Add(token);
            }
            else
            {
                current.Positional.Add(token);
            }
        }

        if (expectStep)
        {
            throw new UsageException("The steps end with a +; give a step after it or remove it.");
        }

        return parsed;
    }

    private static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> defaults, IReadOnlyDictionary<string, string> own)
    {
        Dictionary<string, string> merged = new(defaults, StringComparer.Ordinal);
        foreach ((string key, string value) in own)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static void DryRun(CliSession session, List<InputFile> inputs, Plan plan, OutputTemplate? output)
    {
        TextWriter text = session.Console.Text;
        foreach (PipelineItem item in plan.WithFinalSave())
        {
            text.WriteLine(item switch
            {
                StepItem step => step.Step.ToString(),
                SaveItem save => "save" + (save.Save.Path is { } path ? $" {path}" : string.Empty) + (save.Save.Format is { } format ? $" --format {format}" : string.Empty)
                    + string.Concat(save.Save.Common.Select(c => $" --{c.Key} {c.Value}")) + string.Concat(save.Save.Settings.Select(s => $" --set {s.Key}={s.Value}")),
                _ => string.Empty,
            });
        }

        foreach (InputFile file in inputs)
        {
            InputSource input = new(file);
            Formats.RasterInfo info = input.Probe(session);
            IEnumerable<string> targets = plan.WithFinalSave().OfType<SaveItem>().Select(s => Runner.Resolve(session, input, s.Save, output, info.Format).Path);
            text.WriteLine($"{file.Display} -> {string.Join(", ", targets.Select(t => t == "-" ? "standard output" : Path.GetRelativePath(session.WorkingDirectory, t).Replace('\\', '/')))}");
        }
    }

    private static void ListSteps(CliSession session, string? name)
    {
        TextWriter text = session.Console.Text;
        if (name is not null)
        {
            StepInfo step = StepCatalog.Find(name) ?? throw new UsageException($"'{name}' is not a step. 'egami run --list' lists them.");
            text.WriteLine($"{step.Name}: {step.Summary}");
            foreach (OverloadInfo overload in step.Overloads)
            {
                text.WriteLine();
                text.WriteLine($"  {step.Name} {StepCatalog.Usage(new StepInfo(step.Name, step.Method, step.Summary, [overload], step.Invoke))}");
                foreach (ParamInfo parameter in overload.Parameters)
                {
                    string type = parameter.Choices is { } choices ? string.Join('|', choices) : parameter.Type;
                    string fallback = parameter.Default is { } d ? $", default {d}" : parameter.Required ? ", required" : string.Empty;
                    text.WriteLine($"    --{parameter.Name} <{type}>{fallback}");
                    if (parameter.Help.Length > 0)
                    {
                        text.WriteLine($"        {parameter.Help}");
                    }
                }
            }

            return;
        }

        text.WriteLine("Steps for 'egami run' (separate them with +; 'egami run --list <step>' shows one):");
        foreach (StepInfo step in StepCatalog.All)
        {
            text.WriteLine($"  {step.Name,-26} {step.Summary}");
        }

        text.WriteLine($"  {"save",-26} Writes the image: save <path> [--format f] [--quality q] [--lossless] [--effort e] [--bit-depth b] [--strip] [--set fmt.key=value].");
    }

    private static string Usage(string step)
    {
        return step == "save" ? "a path, --format, --quality, --lossless, --effort, --bit-depth, --strip and --set"
            : StepCatalog.Find(step) is { } info ? StepCatalog.Usage(info) : "nothing";
    }

    /// <summary>Whether the step takes an option, and whether it takes a value; <see langword="null"/> when it has no such option.</summary>
    private static bool? Accepts(ParsedStep step, string option)
    {
        if (step.Name == "save")
        {
            return SaveOptions.TryGetValue("--" + option, out bool save) || SaveOptions.TryGetValue("-" + option, out save) ? save : null;
        }

        if (StepCatalog.Find(step.Name) is not { } info)
        {
            return null;
        }

        foreach (OverloadInfo overload in info.Overloads)
        {
            if (overload.Parameters.FirstOrDefault(p => p.Name == option) is { } parameter)
            {
                return parameter.Type != "bool";
            }
        }

        return null;
    }

    private static bool IsStepName(string token) => token.Equals("save", StringComparison.OrdinalIgnoreCase) || StepCatalog.Find(token) is not null;

    private static bool IsNegativeNumber(string token) => token.Length > 1 && (char.IsDigit(token[1]) || token[1] == '.') && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static (string Name, string? Value) SplitOption(string token)
    {
        int equals = token.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 ? (token[..equals], token[(equals + 1)..]) : (token, null);
    }

    private static string Canonical(string name)
    {
        return name switch
        {
            "--output" => "-o",
            "-w" => "--workflow",
            "--recursive" => "-r",
            "-j" => "--jobs",
            "--verbose" => "-v",
            "--debug" => "-vv",
            "--quiet" => "-q",
            _ => name,
        };
    }

    private static string Next(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length || args[i + 1] == "+")
        {
            throw new UsageException($"{name} needs a value.");
        }

        return args[++i];
    }

    private static string BoolValue(string[] args, ref int i)
    {
        if (i + 1 < args.Length && args[i + 1].ToLowerInvariant() is "true" or "false" or "yes" or "no" or "on" or "off")
        {
            return args[++i];
        }

        return "true";
    }

    /// <summary><c>run</c>'s arguments, split.</summary>
    internal sealed class Parsed
    {
        public List<string> Inputs { get; } = [];

        public List<ParsedStep> Steps { get; } = [];

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

        public List<KeyValuePair<string, string>> SaveDefaults { get; } = [];

        public string? Value(string name) => Values.TryGetValue(name, out string? value) ? value : null;
    }

    /// <summary>One step as written: its name, positional arguments and named options.</summary>
    /// <param name="Name">The step's name.</param>
    internal sealed record ParsedStep(string Name)
    {
        public List<string> Positional { get; } = [];

        public List<KeyValuePair<string, string>> Named { get; } = [];
    }
}
