using System.CommandLine;

using Egami.Video.Filters;

namespace Egami.Video.Cli.Host;

/// <summary>
/// <c>--deinterlace [adaptive|bob]</c> and <c>--deinterlace-rate frame|field</c> (Video 1.2.0 design D6), with
/// <c>--deinterlace-frames</c> and <c>--field-order</c> for ffmpeg's deint and parity, for encode, run, frames and the editing
/// commands: decoded frames go through a <see cref="Deinterlacer"/> for the input's track. Like ffmpeg's <c>-vf yadif</c> and
/// <c>-vf yadif=mode=send_field</c>.
/// </summary>
internal static class Deinterlacing
{
    /// <summary>The options' names.</summary>
    public const string Mode = "--deinterlace";

    /// <summary>The rate option's name.</summary>
    public const string Rate = "--deinterlace-rate";

    /// <summary>The frame selection option's name.</summary>
    public const string Frames = "--deinterlace-frames";

    /// <summary>The field order option's name.</summary>
    public const string Order = "--field-order";

    /// <summary>Adds the options to a command, for help and parsing.</summary>
    public static void AddTo(Command command)
    {
        command.Options.Add(new Option<string?>(Mode)
        {
            Description = "Deinterlace the frames the decoder or container flags as interlaced: adaptive (the default) keeps the still parts sharp and guesses the moving ones, bob makes each frame from one field alone.",
            HelpName = "adaptive|bob",
            Arity = ArgumentArity.ZeroOrOne,
        });
        command.Options.Add(new Option<string?>(Rate)
        {
            Description = "With --deinterlace: frame (the default) gives one frame per frame, field one per field, doubling the frame rate (50i becomes 50p).",
            HelpName = "frame|field",
        });
        command.Options.Add(new Option<string?>(Frames)
        {
            Description = "With --deinterlace: flagged (the default) deinterlaces the frames flagged as interlaced, all every frame, for sources that flag none.",
            HelpName = "flagged|all",
        });
        command.Options.Add(new Option<string?>(Order)
        {
            Description = "With --deinterlace: the field order to use, tff (top field first) or bff, instead of what the frames and the track say.",
            HelpName = "tff|bff",
        });
    }

    /// <summary>
    /// Reads the options from <paramref name="value"/>, which gives an option's last value ("" for --deinterlace alone) or
    /// <see langword="null"/>; <see langword="null"/> when neither is given.
    /// </summary>
    public static DeinterlacerOptions? Parse(Func<string, string?> value)
    {
        string? mode = value(Mode);
        string? rate = value(Rate);
        string? frames = value(Frames);
        string? order = value(Order);
        if (mode is null && rate is null && frames is null && order is null)
        {
            return null;
        }

        return new DeinterlacerOptions
        {
            Mode = (mode ?? string.Empty).ToLowerInvariant() switch
            {
                "" or "adaptive" or "yadif" => DeinterlaceMode.Adaptive,
                "bob" => DeinterlaceMode.Bob,
                _ => throw new UsageException($"--deinterlace '{mode}' is not a mode: give adaptive or bob."),
            },
            Rate = (rate ?? "frame").ToLowerInvariant() switch
            {
                "frame" => DeinterlaceRate.Frame,
                "field" => DeinterlaceRate.Field,
                _ => throw new UsageException($"--deinterlace-rate '{rate}' is not a rate: give frame or field."),
            },
            Frames = (frames ?? "flagged").ToLowerInvariant() switch
            {
                "flagged" or "interlaced" => DeinterlaceFrames.Flagged,
                "all" => DeinterlaceFrames.All,
                _ => throw new UsageException($"--deinterlace-frames '{frames}' is not flagged or all."),
            },
            FieldOrder = order?.ToLowerInvariant() switch
            {
                null => null,
                "tff" or "top" => FieldOrder.TopFieldFirst,
                "bff" or "bottom" => FieldOrder.BottomFieldFirst,
                _ => throw new UsageException($"--field-order '{order}' is not tff or bff."),
            },
        };
    }

    /// <summary>Reads the options from a parse result.</summary>
    public static DeinterlacerOptions? Parse(ParseResult result) =>
        Parse(name => result.CommandResult.Command.Options.FirstOrDefault(o => o.Name == name) is { } option && result.GetResult(option) is { Implicit: false } found
            ? found.Tokens.Count > 0 ? found.Tokens[^1].Value : string.Empty
            : null);

    /// <summary>The frame rate of the output when the deinterlacer may double it.</summary>
    public static Rational? OutputRate(Rational? rate, DeinterlacerOptions? options) =>
        rate is { } r && options is { Rate: DeinterlaceRate.Field } ? new Rational(checked(r.Numerator * 2), r.Denominator) : rate;
}
