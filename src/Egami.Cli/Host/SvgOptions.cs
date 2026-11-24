using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

using Egami.Cli.Hosting;
using Egami.Formats;
using Egami.Svg;

namespace Egami.Cli.Host;

/// <summary>
/// The SVG options every command shares: <c>--animate [fps]</c> loads an animated SVG input as all its frames, and
/// <c>--at &lt;time&gt;</c> draws it at one moment (Egami.Svg's <see cref="SvgDecodeOptions"/>). They change nothing for
/// other inputs.
/// </summary>
internal sealed class SvgOptions
{
    /// <summary>The frame rate <c>--animate</c> loads at when it is given without one.</summary>
    public const double DefaultRate = 30;

    /// <summary>The options' names and whether each takes a value, for <c>run</c>, which parses its own arguments; --animate's is optional.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Names = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["--animate"] = false,
        ["--at"] = true,
    };

    private readonly Option<double?> _animate = new("--animate")
    {
        Description = "Load an animated SVG input as all its frames, this many a second (30 when no rate is given): one loop, or up to the end of its last animation, at most 10 seconds.",
        HelpName = "fps",
        Arity = ArgumentArity.ZeroOrOne,
        Recursive = true,
    };

    private readonly Option<string?> _at = new("--at")
    {
        Description = "Draw an SVG input as it looks this long after it loads, such as 1.5s, 1500ms or 1.5 (seconds); with --animate, the first frame's moment.",
        HelpName = "time",
        Recursive = true,
    };

    /// <summary>
    /// Gives a bare <c>--animate</c> (one not followed by a frame rate) its default rate in place, so the parser does not take
    /// the next word, such as an input file, as the rate.
    /// </summary>
    public static string[] Normalize(string[] args)
    {
        string[]? copy = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                break;
            }

            if (args[i] == "--animate" && !(i + 1 < args.Length && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                copy ??= [.. args];
                copy[i] = "--animate=" + DefaultRate.ToString(CultureInfo.InvariantCulture);
            }
        }

        return copy ?? args;
    }

    /// <summary>Adds the options to a command.</summary>
    public void AddTo(Command command)
    {
        command.Options.Add(_animate);
        command.Options.Add(_at);
    }

    /// <summary>Builds the SVG reader options a parse result gives, or <see langword="null"/> when it gives none.</summary>
    public SvgDecodeOptions? From(ParseResult result)
    {
        return Build(result.GetResult(_animate) is not null, result.GetValue(_animate)?.ToString("R", CultureInfo.InvariantCulture), result.GetValue(_at));
    }

    /// <summary>Builds the SVG reader options from the options' values, or <see langword="null"/> when none is given.</summary>
    /// <param name="animate">Whether <c>--animate</c> is given.</param>
    /// <param name="rate">Its frame rate, or <see langword="null"/> for <see cref="DefaultRate"/>.</param>
    /// <param name="at">The <c>--at</c> time, or <see langword="null"/>.</param>
    /// <exception cref="UsageException">A value is not one the option takes.</exception>
    public static SvgDecodeOptions? Build(bool animate, string? rate, string? at)
    {
        if (!animate && at is null)
        {
            return null;
        }

        double? fps = null;
        if (animate)
        {
            fps = rate is null ? DefaultRate
                : double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r > 0 && r <= 1000 ? r
                : throw new UsageException($"--animate takes a frame rate from 0 to 1000, such as 24 or 29.97, not '{rate}'.");
        }

        return new SvgDecodeOptions { AnimationFrameRate = fps, Time = at is null ? null : ParseTime(at, "--at") };
    }

    /// <summary>Parses a moment: <c>1.5s</c>, <c>1500ms</c> or <c>1.5</c> (seconds).</summary>
    /// <exception cref="UsageException">The text is not a time of at most a day.</exception>
    public static TimeSpan ParseTime(string text, string option)
    {
        string value = text.Trim().ToLowerInvariant();
        double scale = 1;
        if (value.EndsWith("ms", StringComparison.Ordinal))
        {
            value = value[..^2];
            scale = 0.001;
        }
        else if (value.EndsWith('s'))
        {
            value = value[..^1];
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !(number >= 0) || number * scale > 86_400)
        {
            throw new UsageException($"{option} takes a time such as 1.5s, 1500ms or 1.5 (seconds), at most a day, not '{text}'.");
        }

        return TimeSpan.FromSeconds(number * scale);
    }

    /// <summary>Gives a read the SVG options, keeping everything the read itself sets.</summary>
    public static ReaderOptions? Merge(SvgDecodeOptions? svg, ReaderOptions? read)
    {
        if (svg is null)
        {
            return read;
        }

        read ??= new ReaderOptions();
        return svg with
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
}
