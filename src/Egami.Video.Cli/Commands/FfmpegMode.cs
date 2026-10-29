using System.CommandLine;

using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// <c>egami-video ffmpeg</c> and <c>egami-video ffprobe</c> (Video CLI design Q14, Build Plan task 23): a documented subset of
/// FFmpeg's command-line options, mapped onto the native commands. Anything outside the subset stops with exit code 2 and names
/// the option; nothing is ignored silently. <c>--explain</c> prints the native command instead of running it. Built from
/// FFmpeg's published documentation only.
/// </summary>
internal static class FfmpegMode
{
    /// <summary>The option words ffmpeg mode takes, for help and the documentation.</summary>
    public const string FfmpegOptions =
        "-i, -f, -y, -n, -ss, -to, -t, -itsoffset, -framerate, -map, -c, -codec, -c:v, -c:a, -c:s, -vcodec, -acodec, -scodec, -an, -vn, -sn, -dn, -r, -s, -frames:v, -vframes, -q:v, -qscale:v, -ar, -ac, -sample_fmt, -metadata, -map_metadata, -map_chapters, -vf, -filter:v, -af, -filter:a, -lavfi, -filter_complex (psnr, ssim, showwavespic, showspectrumpic, and the editing filters), -loop 1 on an image, -segment_time, -segment_times, -update, -progress, -stats, -nostats, -stats_period, -hide_banner, -loglevel, -v, -nostdin, %d patterns and pipe:; filters scale, crop, pad, transpose, hflip, vflip, fps, thumbnail, select (scene), tile; to -f null the detection filters blackdetect, freezedetect, cropdetect, idet, signalstats, blurdetect, blockdetect, silencedetect, volumedetect, astats and ebur128; the audio filters " + AudioFilterPlan.NameList + " in -af; the editing filters " + FfmpegEdit.FilterList + ", with enable='between(t,a,b)', rendered as one edit; -f lavfi sources testsrc, testsrc2, smptebars, smptehdbars, color, sine, anullsrc and anoisesrc";

    /// <summary>The option words ffprobe mode takes.</summary>
    public const string FfprobeOptions =
        "-i, -show_streams, -show_format, -show_packets, -show_frames, -show_chapters, -select_streams, -show_entries, -of, -print_format, -output_format (json, csv, compact, default, flat, xml), -v, -loglevel, -hide_banner, -pretty";

    public static Command CreateFfmpeg(VideoSession session)
    {
        Command command = new("ffmpeg", $"Run an ffmpeg command line with egami-video: 'egami-video ffmpeg -i in.avi -c copy out.mkv'. --explain prints the native command instead. Takes {FfmpegOptions}; anything else stops with exit code 2.")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        command.Arguments.Add(new Argument<string[]>("arguments") { Description = "ffmpeg's arguments, as ffmpeg takes them.", Arity = ArgumentArity.ZeroOrMore });
        command.Options.Add(new Option<bool>("--explain") { Description = "Print the egami-video command the line stands for, and stop." });
        command.SetAction(_ => ExitCodes.Usage);
        _ = session;
        return command;
    }

    public static Command CreateFfprobe(VideoSession session)
    {
        Command command = new("ffprobe", $"Run an ffprobe command line with egami-video: 'egami-video ffprobe -of json -show_streams in.mkv'. --explain prints the native command instead. Takes {FfprobeOptions}; anything else stops with exit code 2.")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        command.Arguments.Add(new Argument<string[]>("arguments") { Description = "ffprobe's arguments, as ffprobe takes them.", Arity = ArgumentArity.ZeroOrMore });
        command.Options.Add(new Option<bool>("--explain") { Description = "Print the egami-video command the line stands for, and stop." });
        command.SetAction(_ => ExitCodes.Usage);
        _ = session;
        return command;
    }

    /// <summary>Runs an ffmpeg command line: parses it, maps it to native command lines and runs them in turn.</summary>
    public static int ExecuteFfmpeg(VideoSession session, string[] args)
    {
        bool explain = args.Contains("--explain");
        args = [.. args.Where(a => a != "--explain")];
        FfmpegLine parsed = FfmpegLine.Parse(args);
        if (FfmpegEdit.Wants(parsed))
        {
            return ExecuteEdit(session, parsed, explain);
        }

        IReadOnlyList<string[]> commands = FfmpegTranslator.Translate(parsed, session, explain);
        if (explain)
        {
            foreach (string[] command in commands)
            {
                session.Console.Text.WriteLine(Quote(["egami-video", .. command]));
            }

            return ExitCodes.Success;
        }

        foreach (string[] command in commands)
        {
            int code = RunNative(session, command, command);
            if (code != ExitCodes.Success)
            {
                return code;
            }
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// A line with editing filters: one edit file rendered by <c>egami-video edit</c>, or <c>egami-video stabilize</c>. --explain
    /// prints the command with <c>edit.json</c> for the file, then the file.
    /// </summary>
    private static int ExecuteEdit(VideoSession session, FfmpegLine line, bool explain)
    {
        if (!explain && line.Format != "null" && File.Exists(session.ResolvePath(line.Output!)) && !line.Overwrite)
        {
            throw new UsageException($"{line.Output} exists; ffmpeg -y overwrites it.");
        }

        FfmpegEdit.Mapped mapped = FfmpegEdit.Translate(line, session);
        string[] shown = [.. mapped.Words.Select(w => w == FfmpegEdit.EditFile ? "edit.json" : w)];
        if (explain)
        {
            session.Console.Text.WriteLine(Quote(["egami-video", .. shown]));
            if (mapped.Json is { } text)
            {
                session.Console.Text.WriteLine("edit.json:");
                session.Console.Text.WriteLine(text);
            }

            return ExitCodes.Success;
        }

        if (mapped.Json is not { } json)
        {
            return RunNative(session, mapped.Words, shown);
        }

        // The edit file goes in the working folder, so the clip paths in it read as the line gave them.
        string file = Path.Combine(session.WorkingDirectory, $".egami-ffmpeg-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, json);
        try
        {
            return RunNative(session, [.. mapped.Words.Select(w => w == FfmpegEdit.EditFile ? file : w)], shown);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static int RunNative(VideoSession session, string[] command, string[] shown)
    {
        session.Info($"ffmpeg: {Quote(["egami-video", .. shown])}");
        return VideoCli.Run(command, session.Console, session.WorkingDirectory, session.Environment, inner =>
        {
            inner.Cancellation = session.Cancellation;
            inner.Paths = session.Paths;
            inner.MaxInputs = session.MaxInputs;
            inner.ProgressNote = session.ProgressNote;
            inner.Progress = session.Progress;
        });
    }

    /// <summary>Runs an ffprobe command line.</summary>
    public static int ExecuteFfprobe(VideoSession session, string[] args)
    {
        bool explain = args.Contains("--explain");
        args = [.. args.Where(a => a != "--explain")];
        FfprobeLine line = FfprobeLine.Parse(args);
        if (explain)
        {
            session.Console.Text.WriteLine(Quote(["egami-video", .. line.Native()]));
            return ExitCodes.Success;
        }

        return FfprobeOutput.Run(session, line);
    }

    /// <summary>A command line as a shell would take it back: words with spaces, quotes or wildcards in quotes.</summary>
    internal static string Quote(IEnumerable<string> words)
    {
        return string.Join(' ', words.Select(w => w.Length == 0 || w.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '|' or '&' or '<' or '>' or ';' or '*' or '?')
            ? $"\"{w.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : w));
    }

    /// <summary>The error for an option outside the subset: exit code 2, naming the option.</summary>
    internal static UsageException Unsupported(string option, string tool = "ffmpeg", string? why = null)
    {
        return new UsageException($"{tool} option {option} is not supported{(why is null ? string.Empty : ": " + why)}. 'egami-video {tool} --help' lists the options egami-video takes; nothing is ignored.");
    }
}
