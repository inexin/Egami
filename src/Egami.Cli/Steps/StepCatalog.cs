using Egami.Cli.Host;
using Egami.Operations;

namespace Egami.Cli.Steps;

/// <summary>One parameter of a step: its kebab-case name, how it is spelled and parsed, and its help.</summary>
/// <param name="Name">The option name without dashes, such as <c>sigma</c>.</param>
/// <param name="Type">How the value is spelled, for help: <c>number</c>, <c>color</c>, <c>WxH</c>.</param>
/// <param name="Parse">Turns the text into the value the method takes.</param>
/// <param name="Required">Whether the step needs it.</param>
/// <param name="Default">The default, as text, or <see langword="null"/>.</param>
/// <param name="Help">One sentence of help.</param>
/// <param name="Choices">The names a choice takes, or <see langword="null"/>.</param>
internal sealed record ParamInfo(string Name, string Type, Func<string, object> Parse, bool Required, string? Default, string Help, string[]? Choices);

/// <summary>One way to call a step: the parameters of one overload of its method.</summary>
/// <param name="Parameters">The parameters the command line can spell, in the method's order.</param>
internal sealed record OverloadInfo(ParamInfo[] Parameters);

/// <summary>Adds a step to a chain: the overload and its parsed arguments (<see langword="null"/> for a default).</summary>
internal delegate void StepInvoker(RasterOps ops, int overload, object?[] args);

/// <summary>Adds a step that needs the image's current size to a chain.</summary>
internal delegate void SizedStepInvoker(RasterOps ops, object?[] args, Size size);

/// <summary>A step of <c>egami run</c>: a <c>RasterOps</c> method or one of the tool's own steps.</summary>
/// <param name="Name">The kebab-case name, such as <c>contrast-stretch</c>.</param>
/// <param name="Method">The <c>RasterOps</c> method it calls, or the tool's own name.</param>
/// <param name="Summary">One sentence of help.</param>
/// <param name="Overloads">The ways to call it, the richest first.</param>
/// <param name="Invoke">Adds it to a chain.</param>
internal sealed record StepInfo(string Name, string Method, string Summary, OverloadInfo[] Overloads, StepInvoker Invoke)
{
    /// <summary>Gets the invoker for a step that needs the current size, such as <c>resize 50%</c>; it replaces <see cref="Invoke"/>.</summary>
    public SizedStepInvoker? Sized { get; init; }

    /// <summary>Gets a value indicating whether the step can run in a streaming transcode, a band of rows at a time.</summary>
    public bool Streams { get; init; }

    /// <summary>Gets a value indicating whether the step keeps the image's size, so a later sized step can use the size from before it.</summary>
    public bool KeepsSize { get; init; }
}

/// <summary>A step bound to its arguments.</summary>
/// <param name="Info">The step.</param>
/// <param name="Overload">The overload that bound.</param>
/// <param name="Args">The parsed arguments, one per parameter of the overload, <see langword="null"/> where the default applies.</param>
/// <param name="Values">The arguments as written, by parameter name, for workflows and <c>--dry-run</c>.</param>
internal sealed record BoundStep(StepInfo Info, int Overload, object?[] Args, IReadOnlyList<KeyValuePair<string, string>> Values)
{
    /// <summary>Adds the step to a chain, given the image's size before it when the step needs it.</summary>
    public void AddTo(RasterOps ops, Size size)
    {
        if (Info.Sized is { } sized)
        {
            sized(ops, Args, size);
        }
        else
        {
            Info.Invoke(ops, Overload, Args);
        }
    }

    /// <summary>The step as a <c>run</c> argument list would write it, such as <c>resize 1200x --filter lanczos3</c>.</summary>
    public override string ToString()
    {
        return string.Join(' ', Values.Select(v => $"--{v.Key} {Quote(v.Value)}").Prepend(Info.Name));
    }

    private static string Quote(string value)
    {
        return value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : value;
    }
}

/// <summary>
/// Every step <c>egami run</c> knows: the steps generated from Egami's public <c>RasterOps</c> methods at build time, and the
/// tool's own steps (<c>resize</c> with the size syntax, <c>flip</c>), which take precedence over a generated step of the same name.
/// </summary>
internal static partial class StepCatalog
{
    /// <summary>Steps that can run in a streaming transcode (see <c>Raster.Transcode</c>).</summary>
    private static readonly HashSet<string> StreamingSteps =
    [
        "crop", "fit", "cover", "flip-horizontal", "brightness", "contrast", "saturation", "hue-rotate", "sepia", "grayscale", "invert",
        "opacity",
    ];

    /// <summary>Steps that change the image's size (or might), so a later sized step needs the size measured after them.</summary>
    private static readonly HashSet<string> SizeChangingSteps =
    [
        "crop", "fit", "cover", "pad", "resize", "rotate", "auto-orient", "trim", "entropy-crop", "deskew", "skew", "transform", "quad-distort",
        "tile", "drop-shadow", "glow", "flatten", "warp",
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["autoorient"] = "auto-orient",
    };

    private static readonly Lazy<Dictionary<string, StepInfo>> ByName = new(Create);

    /// <summary>Gets every step, sorted by name.</summary>
    public static IReadOnlyList<StepInfo> All => [.. ByName.Value.Values.OrderBy(s => s.Name, StringComparer.Ordinal)];

    /// <summary>Finds a step by name or alias.</summary>
    public static StepInfo? Find(string name)
    {
        string key = name.ToLowerInvariant();
        if (Aliases.TryGetValue(key, out string? target))
        {
            key = target;
        }

        return ByName.Value.TryGetValue(key, out StepInfo? step) ? step : null;
    }

    /// <summary>Binds a step to positional and named argument text, trying each overload in turn.</summary>
    public static BoundStep Bind(StepInfo step, IReadOnlyList<string> positional, IReadOnlyList<KeyValuePair<string, string>> named)
    {
        UsageException? parseError = null;
        for (int o = 0; o < step.Overloads.Length; o++)
        {
            ParamInfo[] parameters = step.Overloads[o].Parameters;
            string?[] texts = new string?[parameters.Length];
            bool fits = true;
            foreach ((string key, string value) in named)
            {
                int index = Array.FindIndex(parameters, p => p.Name == key);
                if (index < 0 || texts[index] is not null)
                {
                    fits = false;
                    break;
                }

                texts[index] = value;
            }

            int next = 0;
            foreach (string value in positional)
            {
                while (next < texts.Length && texts[next] is not null)
                {
                    next++;
                }

                if (next >= texts.Length)
                {
                    fits = false;
                    break;
                }

                texts[next++] = value;
            }

            if (!fits || parameters.Where((p, i) => p.Required && texts[i] is null).Any())
            {
                continue;
            }

            try
            {
                object?[] args = new object?[parameters.Length];
                List<KeyValuePair<string, string>> values = [];
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (texts[i] is { } text)
                    {
                        args[i] = parameters[i].Parse(text);
                        values.Add(new(parameters[i].Name, text));
                    }
                }

                return new BoundStep(step, o, args, values);
            }
            catch (UsageException error)
            {
                parseError ??= new UsageException($"{step.Name}: {error.Message}");
            }
        }

        throw parseError ?? new UsageException($"{step.Name} takes {Usage(step)}.");
    }

    /// <summary>The argument shapes of a step, for messages: <c>&lt;sigma&gt; [--color-space …]</c>.</summary>
    public static string Usage(StepInfo step)
    {
        return string.Join(" or ", step.Overloads.Select(o => o.Parameters.Length == 0
            ? "no arguments"
            : string.Join(' ', o.Parameters.Select(p => p.Required ? $"<{p.Name}>" : $"[--{p.Name} <{p.Type}>]"))));
    }

    /// <summary>Whether a step can run a band of rows at a time.</summary>
    public static bool Streams(StepInfo step) => step.Streams || StreamingSteps.Contains(step.Name);

    /// <summary>Whether a step keeps the image's size.</summary>
    public static bool KeepsSize(StepInfo step) => step.KeepsSize || !SizeChangingSteps.Contains(step.Name);

    private static partial List<StepInfo> CreateGenerated();

    private static Dictionary<string, StepInfo> Create()
    {
        Dictionary<string, StepInfo> steps = new(StringComparer.Ordinal);
        foreach (StepInfo step in CreateGenerated())
        {
            steps[step.Name] = step;
        }

        foreach (StepInfo step in BuiltinSteps.Create(steps))
        {
            steps[step.Name] = step;
        }

        return steps;
    }
}
