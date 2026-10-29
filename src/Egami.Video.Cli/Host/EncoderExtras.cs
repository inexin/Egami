using System.CommandLine;
using System.Globalization;

using Egami.Video.Codecs;

namespace Egami.Video.Cli.Host;

/// <summary>
/// The encoder options of <c>encode</c> and <c>run</c> beyond quality and effort: <c>--keyframe-interval</c> for every codec,
/// and FFV1's expert settings (<see cref="Ffv1EncoderOptions"/>, V6 design Q7): <c>--slices</c>, <c>--no-slice-crc</c>,
/// <c>--coder</c> and <c>--context</c>. Like ffmpeg's <c>-g</c>, <c>-slices</c>, <c>-slicecrc</c>, <c>-coder</c> and <c>-context</c>.
/// </summary>
internal sealed record EncoderExtras
{
    /// <summary>Gets the most frames from one keyframe to the next, or <see langword="null"/> for the codec's default.</summary>
    public int? KeyframeInterval { get; init; }

    /// <summary>Gets FFV1's settings, or <see langword="null"/> when no FFV1 option was given.</summary>
    public Ffv1EncoderOptions? Ffv1 { get; init; }

    /// <summary>Adds the options to a command, for help and parsing.</summary>
    public static void AddTo(Command command)
    {
        command.Options.Add(new Option<string?>("--keyframe-interval") { Description = "The most frames from one keyframe to the next; FFV1 makes every frame a keyframe unless this is given.", HelpName = "frames" });
        command.Options.Add(new Option<string?>("--slices") { Description = "FFV1: slices a frame is cut into, coded in parallel, 1 to 1024; picked from the frame size by default.", HelpName = "count" });
        command.Options.Add(new Option<bool>("--no-slice-crc") { Description = "FFV1: leave out the slice CRCs that let verify check the file." });
        command.Options.Add(new Option<string?>("--coder") { Description = "FFV1: range (the default, smallest), range-default (the default state table) or golomb (faster, 8 bits at most).", HelpName = "range|range-default|golomb" });
        command.Options.Add(new Option<string?>("--context") { Description = "FFV1: the context model, small or large; picked from --effort by default.", HelpName = "small|large" });
    }

    /// <summary>Reads the options from <paramref name="value"/>, which gives an option's last value (or "" for a flag) or <see langword="null"/>.</summary>
    public static EncoderExtras Parse(Func<string, string?> value)
    {
        Ffv1EncoderOptions ffv1 = Ffv1EncoderOptions.Default;
        bool any = false;
        if (value("--slices") is { } slices)
        {
            ffv1 = ffv1 with { Slices = Number(slices, "--slices", 1, 1024) };
            any = true;
        }

        if (value("--no-slice-crc") is not null)
        {
            ffv1 = ffv1 with { SliceCrc = false };
            any = true;
        }

        if (value("--coder") is { } coder)
        {
            ffv1 = ffv1 with
            {
                Coder = coder.ToLowerInvariant() switch
                {
                    "range" or "range-custom" => Ffv1Coder.RangeCustomTable,
                    "range-default" => Ffv1Coder.RangeDefaultTable,
                    "golomb" or "golomb-rice" => Ffv1Coder.GolombRice,
                    _ => throw new UsageException($"--coder '{coder}' is not an FFV1 coder: give range, range-default or golomb."),
                },
            };
            any = true;
        }

        if (value("--context") is { } context)
        {
            ffv1 = ffv1 with
            {
                ContextModel = context.ToLowerInvariant() switch
                {
                    "small" or "0" => Ffv1ContextModel.Small,
                    "large" or "1" => Ffv1ContextModel.Large,
                    _ => throw new UsageException($"--context '{context}' is not an FFV1 context model: give small or large."),
                },
            };
            any = true;
        }

        return new EncoderExtras
        {
            KeyframeInterval = value("--keyframe-interval") is { } interval ? Number(interval, "--keyframe-interval", 1, 100_000) : null,
            Ffv1 = any ? ffv1 : null,
        };
    }

    /// <summary>Reads the options from a parse result.</summary>
    public static EncoderExtras Parse(ParseResult result)
    {
        return Parse(name => result.CommandResult.Command.Options.FirstOrDefault(o => o.Name == name) is { } option && result.GetResult(option) is { Implicit: false } found
            ? found.Tokens.Count > 0 ? found.Tokens[^1].Value : string.Empty
            : null);
    }

    /// <summary>Checks the options against the codec: FFV1's go with FFV1 only.</summary>
    public void Check(VideoCodec codec)
    {
        if (Ffv1 is not null && codec != Ffv1Codec.Instance)
        {
            throw new UsageException($"--slices, --no-slice-crc, --coder and --context are for --codec ffv1, not {codec.Name}.");
        }
    }

    /// <summary>The writer options with these settings.</summary>
    public VideoWriterOptions Apply(VideoWriterOptions options) => options with
    {
        KeyframeInterval = KeyframeInterval ?? options.KeyframeInterval,
        Ffv1 = Ffv1 ?? options.Ffv1,
    };

    private static int Number(string text, string option, int min, int max) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= min && value <= max
            ? value
            : throw new UsageException($"{option} must be a whole number from {min} to {max}.");
}
