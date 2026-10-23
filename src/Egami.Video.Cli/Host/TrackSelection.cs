using System.Globalization;

namespace Egami.Video.Cli.Host;

/// <summary>
/// One <c>--track</c> value (Video CLI design Q4): a kind letter (<c>v</c>, <c>a</c>, <c>s</c> or <c>d</c> for data), optionally
/// followed by <c>:</c> and an index among the tracks of that kind counted from 0 (<c>a:1</c>, the second audio track) or a
/// language (<c>s:eng</c>); or a plain track number as <c>info</c> shows it (<c>3</c>).
/// </summary>
internal sealed record TrackSpec(TrackKind? Kind, int? Index, string? Language, int? Number, string Text)
{
    /// <summary>Parses one value; throws <see cref="UsageException"/> naming the value and the forms it may take.</summary>
    public static TrackSpec Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string value = text.Trim();
        if (value.Length > 0 && value.All(char.IsAsciiDigit))
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? new TrackSpec(null, null, null, number, text)
                : throw Wrong(text);
        }

        int colon = value.IndexOf(':', StringComparison.Ordinal);
        string letter = colon < 0 ? value : value[..colon];
        TrackKind kind = letter.ToLowerInvariant() switch
        {
            "v" or "video" => TrackKind.Video,
            "a" or "audio" => TrackKind.Audio,
            "s" or "subtitle" or "subtitles" => TrackKind.Subtitle,
            "d" or "data" => TrackKind.Other,
            _ => throw Wrong(text),
        };
        if (colon < 0)
        {
            return new TrackSpec(kind, null, null, null, text);
        }

        string rest = value[(colon + 1)..];
        if (rest.Length == 0)
        {
            throw Wrong(text);
        }

        if (rest.All(char.IsAsciiDigit))
        {
            return int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                ? new TrackSpec(kind, index, null, null, text)
                : throw Wrong(text);
        }

        return rest.All(c => char.IsAsciiLetter(c) || c == '-')
            ? new TrackSpec(kind, null, rest, null, text)
            : throw Wrong(text);
    }

    /// <summary>Whether a track of a file matches; <paramref name="indexInKind"/> is its position among tracks of its kind.</summary>
    public bool Matches(TrackInfo track, int indexInKind)
    {
        if (Number is { } number)
        {
            return track.Number == number;
        }

        if (track.Kind != Kind)
        {
            return false;
        }

        if (Index is { } index)
        {
            return indexInKind == index;
        }

        return Language is null || LanguageMatches(track.Language, Language);
    }

    /// <summary>Whether a track's language tag names the language asked for: eng and en both match "en-US".</summary>
    public static bool LanguageMatches(string? trackLanguage, string asked)
    {
        if (trackLanguage is null)
        {
            return false;
        }

        string primary = trackLanguage.Split('-')[0];
        string want = asked.Split('-')[0];
        return string.Equals(trackLanguage, asked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(primary, want, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Languages.ToTwoLetter(primary), Languages.ToTwoLetter(want), StringComparison.OrdinalIgnoreCase);
    }

    private static UsageException Wrong(string text) =>
        new($"--track '{text}' is not a track: give v, a, s or d with an optional :index or :language (a:1, s:eng), or a track number as 'egami-video info' shows it.");
}

/// <summary>
/// The tracks a command works on: every track unless <c>--track</c> names some, less audio with <c>--no-audio</c> and subtitles
/// with <c>--no-subtitles</c>.
/// </summary>
internal sealed class TrackSelection
{
    private readonly IReadOnlyList<TrackSpec> _specs;

    public TrackSelection(IReadOnlyList<TrackSpec> specs, bool noAudio, bool noSubtitles, bool noVideo = false)
    {
        _specs = specs;
        NoAudio = noAudio;
        NoSubtitles = noSubtitles;
        NoVideo = noVideo;
    }

    /// <summary>Gets a selection of every track.</summary>
    public static TrackSelection All { get; } = new([], false, false);

    /// <summary>Gets a value indicating whether audio tracks are left out.</summary>
    public bool NoAudio { get; }

    /// <summary>Gets a value indicating whether subtitle tracks are left out.</summary>
    public bool NoSubtitles { get; }

    /// <summary>Gets a value indicating whether video tracks are left out.</summary>
    public bool NoVideo { get; }

    /// <summary>Gets a value indicating whether <c>--track</c> named any tracks.</summary>
    public bool IsExplicit => _specs.Count > 0;

    /// <summary>Parses the option values.</summary>
    public static TrackSelection Parse(IEnumerable<string>? values, bool noAudio, bool noSubtitles, bool noVideo = false)
    {
        return new TrackSelection([.. (values ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)).Select(TrackSpec.Parse)], noAudio, noSubtitles, noVideo);
    }

    /// <summary>
    /// The tracks of a file the selection keeps, in the file's order; throws <see cref="UsageException"/> when a <c>--track</c>
    /// value matches no track, naming the tracks the file has.
    /// </summary>
    public IReadOnlyList<TrackInfo> Select(IReadOnlyList<TrackInfo> tracks, string display)
    {
        List<(TrackInfo Track, int Index)> indexed = [];
        Dictionary<TrackKind, int> counts = [];
        foreach (TrackInfo track in tracks)
        {
            int index = counts.GetValueOrDefault(track.Kind);
            counts[track.Kind] = index + 1;
            indexed.Add((track, index));
        }

        HashSet<TrackInfo> kept = [];
        foreach (TrackSpec spec in _specs)
        {
            List<TrackInfo> matches = [.. indexed.Where(t => spec.Matches(t.Track, t.Index)).Select(t => t.Track)];
            if (matches.Count == 0)
            {
                throw new UsageException($"{display}: --track {spec.Text} matches no track; it has {Describe(tracks)}.");
            }

            kept.UnionWith(matches);
        }

        return [.. tracks.Where(t => (_specs.Count == 0 || kept.Contains(t))
            && !(NoAudio && t.Kind == TrackKind.Audio)
            && !(NoSubtitles && t.Kind == TrackKind.Subtitle)
            && !(NoVideo && t.Kind == TrackKind.Video))];
    }

    /// <summary>The one track a command that takes one track works on: the first selected track of <paramref name="kind"/>.</summary>
    public TrackInfo SelectOne(IReadOnlyList<TrackInfo> tracks, TrackKind kind, string display)
    {
        IReadOnlyList<TrackInfo> selected = Select(tracks, display);
        return selected.FirstOrDefault(t => t.Kind == kind)
            ?? throw new UsageException($"{display}: no {Kinds.Name(kind)} track{(IsExplicit ? " among those --track picks" : string.Empty)}; it has {Describe(tracks)}.");
    }

    /// <summary>The tracks of a file, for messages: <c>1 (video VP9), 2 (audio Opus, eng)</c>.</summary>
    public static string Describe(IReadOnlyList<TrackInfo> tracks)
    {
        return tracks.Count == 0
            ? "no tracks"
            : string.Join(", ", tracks.Select(t => $"{t.Number} ({Kinds.Name(t.Kind)} {CodecNames.Name(t.CodecId)}{(t.Language is { } language ? ", " + language : string.Empty)})"));
    }
}

/// <summary>Track kinds as the tool names them.</summary>
internal static class Kinds
{
    public static string Name(TrackKind kind) => kind switch
    {
        TrackKind.Video => "video",
        TrackKind.Audio => "audio",
        TrackKind.Subtitle => "subtitle",
        _ => "data",
    };

    public static string Letter(TrackKind kind) => kind switch
    {
        TrackKind.Video => "v",
        TrackKind.Audio => "a",
        TrackKind.Subtitle => "s",
        _ => "d",
    };
}

/// <summary>The ISO 639-2 three-letter codes of the common languages, to match <c>s:eng</c> against "en" and back.</summary>
internal static class Languages
{
    private static readonly Dictionary<string, string> TwoLetter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "en",
        ["fre"] = "fr",
        ["fra"] = "fr",
        ["ger"] = "de",
        ["deu"] = "de",
        ["spa"] = "es",
        ["ita"] = "it",
        ["por"] = "pt",
        ["dut"] = "nl",
        ["nld"] = "nl",
        ["swe"] = "sv",
        ["nor"] = "no",
        ["nob"] = "nb",
        ["nno"] = "nn",
        ["dan"] = "da",
        ["fin"] = "fi",
        ["ice"] = "is",
        ["isl"] = "is",
        ["pol"] = "pl",
        ["cze"] = "cs",
        ["ces"] = "cs",
        ["slo"] = "sk",
        ["slk"] = "sk",
        ["hun"] = "hu",
        ["rum"] = "ro",
        ["ron"] = "ro",
        ["gre"] = "el",
        ["ell"] = "el",
        ["tur"] = "tr",
        ["rus"] = "ru",
        ["ukr"] = "uk",
        ["bul"] = "bg",
        ["srp"] = "sr",
        ["hrv"] = "hr",
        ["slv"] = "sl",
        ["est"] = "et",
        ["lav"] = "lv",
        ["lit"] = "lt",
        ["ara"] = "ar",
        ["heb"] = "he",
        ["per"] = "fa",
        ["fas"] = "fa",
        ["hin"] = "hi",
        ["ben"] = "bn",
        ["tha"] = "th",
        ["vie"] = "vi",
        ["ind"] = "id",
        ["may"] = "ms",
        ["msa"] = "ms",
        ["chi"] = "zh",
        ["zho"] = "zh",
        ["jpn"] = "ja",
        ["kor"] = "ko",
        ["cat"] = "ca",
        ["baq"] = "eu",
        ["eus"] = "eu",
        ["glg"] = "gl",
        ["wel"] = "cy",
        ["cym"] = "cy",
        ["gle"] = "ga",
    };

    /// <summary>The two-letter code for a three-letter one, or the code itself.</summary>
    public static string ToTwoLetter(string code) => TwoLetter.TryGetValue(code, out string? two) ? two : code;
}
