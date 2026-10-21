using Egami.Cli.Host;
using Egami.Formats;

namespace Egami.Cli.Steps;

/// <summary>One writer setting: a <c>--set fmt.key=value</c> key generated from a <c>WriterOptions</c> property.</summary>
/// <param name="Key">The kebab-case key, dotted for nested options: <c>compression-level</c>, <c>palette.max-colors</c>.</param>
/// <param name="Type">How the value is spelled, for help.</param>
/// <param name="Help">One sentence of help.</param>
/// <param name="Choices">The names a choice takes, or <see langword="null"/>.</param>
/// <param name="Read">Reads the setting from an options object, to show its default.</param>
internal sealed record SettingInfo(string Key, string Type, string Help, string[]? Choices, Func<WriterOptions, object?> Read);

/// <summary>The settings of one format's writer.</summary>
/// <param name="Prefix">The type's name without <c>WriterOptions</c>, lower case: <c>png</c>, <c>jxl</c>.</param>
/// <param name="Summary">One sentence of help.</param>
/// <param name="Defaults">The format's default options.</param>
/// <param name="Build">Builds options from setting values, starting from the defaults.</param>
/// <param name="Settings">Every setting.</param>
internal sealed record WriterInfo(string Prefix, string Summary, Func<WriterOptions> Defaults, Func<SettingValues, WriterOptions> Build, SettingInfo[] Settings)
{
    /// <summary>Whether this writer belongs to a format, by its name or one of its extensions.</summary>
    public bool Matches(FormatInfo format)
    {
        return Normalize(format.Name) == Prefix || format.Extensions.Any(e => Normalize(e) == Prefix);
    }

    internal static string Normalize(string text)
    {
        return new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}

/// <summary>Setting values by key; tracks which keys were used so unknown keys can be reported.</summary>
internal sealed class SettingValues(IReadOnlyDictionary<string, string> values)
{
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    /// <summary>Gets the keys no setting used.</summary>
    public IEnumerable<string> Unused => values.Keys.Where(k => !_used.Contains(k));

    /// <summary>Parses a key's value, or returns the current value when the key is not set.</summary>
    public T Get<T>(string key, Func<string, object> parse, T current)
    {
        if (!values.TryGetValue(key, out string? text))
        {
            return current;
        }

        _used.Add(key);
        object value = parse(text);
        return value is T typed ? typed : (T)value;
    }

    /// <summary>Whether any key starts with the prefix, so a nested options object needs building.</summary>
    public bool HasPrefix(string prefix) => values.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>The writer settings generated from each format's public <c>WriterOptions</c> type (CLI design Q8).</summary>
internal static partial class WriterCatalog
{
    private static readonly Lazy<List<WriterInfo>> Writers = new(CreateGenerated);

    /// <summary>Gets every format's writer settings.</summary>
    public static IReadOnlyList<WriterInfo> All => Writers.Value;

    /// <summary>The writer settings of a format, or <see langword="null"/> when it has no options type of its own.</summary>
    public static WriterInfo? Find(FormatInfo format) => Writers.Value.FirstOrDefault(w => w.Matches(format));

    /// <summary>Builds writer options for a format from setting values, which are checked against the format's keys.</summary>
    public static WriterOptions Build(FormatInfo format, IReadOnlyDictionary<string, string> values)
    {
        WriterInfo? writer = Find(format);
        if (writer is null)
        {
            if (values.Count == 0)
            {
                return WriterOptions.Default;
            }

            // Formats without their own options type take the shared ones.
            SettingValues shared = new(values);
            WriterOptions options = new()
            {
                StripMetadata = shared.Get("strip-metadata", ValueParsers.Bool, WriterOptions.Default.StripMetadata),
                OmitSrgbProfile = shared.Get("omit-srgb-profile", ValueParsers.Bool, WriterOptions.Default.OmitSrgbProfile),
            };
            ReportUnused(format, shared);
            return options;
        }

        SettingValues settings = new(values);
        WriterOptions built = writer.Build(settings);
        ReportUnused(format, settings);
        return built;
    }

    private static partial List<WriterInfo> CreateGenerated();

    private static void ReportUnused(FormatInfo format, SettingValues settings)
    {
        string? unknown = settings.Unused.FirstOrDefault();
        if (unknown is not null)
        {
            throw new UsageException($"{format.Name} has no setting '{unknown}'. Run 'egami formats {(format.Extensions.Count > 0 ? format.Extensions[0] : format.Name)}' to list them.");
        }
    }
}
