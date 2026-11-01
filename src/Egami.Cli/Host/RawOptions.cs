using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

using Egami.Cli.Hosting;
using Egami.Formats;
using Egami.Formats.Extended.Raw;

namespace Egami.Cli.Host;

/// <summary>
/// The <c>--raw-*</c> options every command shares: how a camera RAW input is developed (Egami.Formats.Extended's
/// <see cref="RawReaderOptions"/>). They change nothing for other inputs.
/// </summary>
internal sealed class RawOptions
{
    /// <summary>The options' names and whether each takes a value, for <c>run</c>, which parses its own arguments.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Names = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["--raw-output"] = true,
        ["--raw-preview"] = true,
        ["--raw-white-balance"] = true,
        ["--raw-temperature"] = true,
        ["--raw-tint"] = true,
        ["--raw-exposure"] = true,
        ["--raw-demosaic"] = true,
        ["--raw-highlights"] = true,
        ["--raw-noise-reduction"] = true,
        ["--raw-color-noise-reduction"] = true,
        ["--raw-lens-profiles"] = true,
        ["--raw-no-lens-corrections"] = false,
        ["--raw-auto-ca"] = false,
        ["--raw-tone-curve"] = false,
        ["--raw-no-profile"] = false,
    };

    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["--raw-output"] = "What a camera RAW input loads as: developed (default), half-size, preview or mosaic.",
        ["--raw-preview"] = "Which embedded preview --raw-output preview loads, 0 for the largest.",
        ["--raw-white-balance"] = "White balance of a RAW develop: as-shot (default), auto or custom (with --raw-temperature and --raw-tint).",
        ["--raw-temperature"] = "Color temperature in kelvin for --raw-white-balance custom.",
        ["--raw-tint"] = "Tint for --raw-white-balance custom, positive toward magenta.",
        ["--raw-exposure"] = "Exposure of a RAW develop in stops, such as +0.5 or -1.",
        ["--raw-demosaic"] = "Demosaic method: ahd (default), malvar or bilinear.",
        ["--raw-highlights"] = "Clipped highlights: clip (default), blend or rebuild.",
        ["--raw-noise-reduction"] = "Luminance noise reduction of a RAW develop, 0 to 100 (default 0).",
        ["--raw-color-noise-reduction"] = "Color noise reduction of a RAW develop, 0 to 100 (default 25).",
        ["--raw-lens-profiles"] = "Folders of Lensfun (.xml) and Adobe (.lcp) lens profiles, separated by the system's path separator.",
        ["--raw-no-lens-corrections"] = "Apply no lens corrections.",
        ["--raw-auto-ca"] = "Measure and correct lateral chromatic aberration when no lens profile covers the file.",
        ["--raw-tone-curve"] = "Give a develop without a profile tone curve the default contrast curve.",
        ["--raw-no-profile"] = "Leave out the camera profile's look (HueSatMap, LookTable and tone curve).",
    };

    private readonly Dictionary<string, Option> _options = [];

    /// <summary>Creates the options, each recursive so it works after any command.</summary>
    public RawOptions()
    {
        foreach ((string name, bool value) in Names)
        {
            Option option = value
                ? new Option<string?>(name) { Description = Descriptions[name], HelpName = "value", Recursive = true }
                : new Option<bool>(name) { Description = Descriptions[name], Recursive = true };
            _options[name] = option;
        }
    }

    /// <summary>Adds the options to a command.</summary>
    public void AddTo(Command command)
    {
        foreach (Option option in _options.Values)
        {
            command.Options.Add(option);
        }
    }

    /// <summary>Builds the RAW reader options a parse result gives, or <see langword="null"/> when it gives none.</summary>
    public RawReaderOptions? From(ParseResult result)
    {
        return Build(
            name => _options[name] is Option<string?> o ? result.GetValue(o) : null,
            name => _options[name] is Option<bool> o && result.GetValue(o));
    }

    /// <summary>
    /// Builds the RAW reader options from the options' values, or <see langword="null"/> when none is given.
    /// </summary>
    /// <exception cref="UsageException">A value is not one the option takes.</exception>
    public static RawReaderOptions? Build(Func<string, string?> value, Func<string, bool> flag)
    {
        if (!Names.Any(n => n.Value ? value(n.Key) is not null : flag(n.Key)))
        {
            return null;
        }

        RawReaderOptions raw = new();
        if (value("--raw-output") is string output)
        {
            raw = raw with { Output = Enum<RawOutput>("--raw-output", output) };
        }

        if (value("--raw-preview") is string preview)
        {
            raw = raw with { PreviewIndex = (int)Number("--raw-preview", preview, 0, 64) };
        }

        if (value("--raw-white-balance") is string wb)
        {
            raw = raw with { WhiteBalance = Enum<RawWhiteBalance>("--raw-white-balance", wb) };
        }

        if (value("--raw-temperature") is string temperature)
        {
            raw = raw with { Temperature = Number("--raw-temperature", temperature, 2000, 50000) };
        }

        if (value("--raw-tint") is string tint)
        {
            raw = raw with { Tint = Number("--raw-tint", tint, -150, 150) };
        }

        if (value("--raw-exposure") is string exposure)
        {
            raw = raw with { Exposure = Number("--raw-exposure", exposure, -10, 10) };
        }

        if (value("--raw-demosaic") is string demosaic)
        {
            raw = raw with { Demosaic = Enum<RawDemosaicMethod>("--raw-demosaic", demosaic) };
        }

        if (value("--raw-highlights") is string highlights)
        {
            raw = raw with { Highlights = Enum<RawHighlightMode>("--raw-highlights", highlights) };
        }

        if (value("--raw-noise-reduction") is string nr)
        {
            raw = raw with { LuminanceNoiseReduction = Number("--raw-noise-reduction", nr, 0, 100) };
        }

        if (value("--raw-color-noise-reduction") is string cnr)
        {
            raw = raw with { ColorNoiseReduction = Number("--raw-color-noise-reduction", cnr, 0, 100) };
        }

        if (value("--raw-lens-profiles") is string folders)
        {
            raw = raw with { LensProfiles = folders.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
        }

        return raw with
        {
            LensCorrections = !flag("--raw-no-lens-corrections"),
            AutoChromaticAberration = flag("--raw-auto-ca"),
            DefaultToneCurve = flag("--raw-tone-curve"),
            ApplyProfile = !flag("--raw-no-profile"),
        };
    }

    /// <summary>Gives a read the RAW options, keeping everything the read itself sets.</summary>
    public static ReaderOptions? Merge(RawReaderOptions? raw, ReaderOptions? read)
    {
        if (raw is null)
        {
            return read;
        }

        read ??= new ReaderOptions();
        return raw with
        {
            Recovery = read.Recovery,
            ReadMetadata = read.ReadMetadata,
            ConvertColorTo = read.ConvertColorTo,
            Hdr = read.Hdr,
            Frames = read.Frames,
            MinimumSize = read.MinimumSize,
            Region = read.Region,
        };
    }

    private static T Enum<T>(string option, string text)
        where T : struct, Enum
    {
        string name = text.Replace("-", "", StringComparison.Ordinal);
        return System.Enum.TryParse(name, ignoreCase: true, out T value) && System.Enum.IsDefined(value)
            ? value
            : throw new UsageException($"{option} takes {string.Join(", ", System.Enum.GetNames<T>().Select(Kebab))}, not '{text}'.");
    }

    private static double Number(string option, string text, double min, double max)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= min && v <= max
            ? v
            : throw new UsageException($"{option} takes a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}, not '{text}'.");
    }

    private static string Kebab(string name) => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
