using System.Globalization;

namespace Egami.Video.Cli.Commands;

/// <summary>One input of an ffmpeg command line with the options given before it.</summary>
internal sealed class FfmpegInput
{
    public required string Path { get; init; }

    public string? Format { get; set; }

    public string? Start { get; set; }

    public string? To { get; set; }

    public string? Duration { get; set; }

    public string? Offset { get; set; }

    public string? FrameRate { get; set; }

    public string? Loop { get; set; }
}

/// <summary>An ffmpeg command line, parsed into inputs, one output and the output's options, as FFmpeg's documentation describes them.</summary>
internal sealed class FfmpegLine
{
    public List<FfmpegInput> Inputs { get; } = [];

    public string? Output { get; set; }

    public bool Overwrite { get; set; }

    public bool NoOverwrite { get; set; }

    public List<string> Maps { get; } = [];

    /// <summary>Gets the codecs by stream type: "" for all, "v", "a" and "s".</summary>
    public Dictionary<string, string> Codecs { get; } = new(StringComparer.Ordinal);

    public bool NoVideo { get; set; }

    public bool NoAudio { get; set; }

    public bool NoSubtitles { get; set; }

    public string? Start { get; set; }

    public string? To { get; set; }

    public string? Duration { get; set; }

    public string? Rate { get; set; }

    public string? Size { get; set; }

    public string? Frames { get; set; }

    public string? Quality { get; set; }

    public string? SampleRate { get; set; }

    public string? AudioBitrate { get; set; }

    public string? Channels { get; set; }

    public string? SampleFormat { get; set; }

    public string? Format { get; set; }

    public List<string> Metadata { get; } = [];

    public string? MapMetadata { get; set; }

    public string? MapChapters { get; set; }

    public string? VideoFilter { get; set; }

    public string? AudioFilter { get; set; }

    public string? ComplexFilter { get; set; }

    public string? SegmentTime { get; set; }

    public string? SegmentTimes { get; set; }

    /// <summary>Gets the MP4 muxer flags of -movflags, such as frag_keyframe and empty_moov.</summary>
    public List<string> MovFlags { get; } = [];

    /// <summary>Gets or sets -frag_duration: the fragment length in microseconds.</summary>
    public string? FragmentDuration { get; set; }

    public string? Progress { get; set; }

    /// <summary>Parses the words after <c>ffmpeg</c>; throws <see cref="UsageException"/> naming any option outside the subset.</summary>
    public static FfmpegLine Parse(string[] args)
    {
        FfmpegLine line = new();
        FfmpegInput pending = new() { Path = string.Empty };
        bool pendingUsed = false;
        for (int i = 0; i < args.Length; i++)
        {
            string word = args[i];
            if (word == "-" || !word.StartsWith('-') || IsNumber(word))
            {
                if (line.Output is not null)
                {
                    throw new UsageException($"The ffmpeg line has more than one output ('{line.Output}' and '{word}'); egami-video ffmpeg writes one output per line.");
                }

                line.Output = word;
                continue;
            }

            string name = word;
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new UsageException($"ffmpeg option {name} needs a value.");
                }

                return args[++i];
            }

            // Options before an -i belong to that input; after the last -i, to the output.
            bool beforeInput = line.Output is null && args.Skip(i + 1).Contains("-i");
            switch (name)
            {
                case "-i":
                    line.Inputs.Add(new FfmpegInput
                    {
                        Path = Next(),
                        Format = pending.Format,
                        Start = pending.Start,
                        To = pending.To,
                        Duration = pending.Duration,
                        Offset = pending.Offset,
                        FrameRate = pending.FrameRate,
                        Loop = pending.Loop,
                    });
                    pending = new FfmpegInput { Path = string.Empty };
                    pendingUsed = false;
                    break;
                case "-y": line.Overwrite = true; break;
                case "-n": line.NoOverwrite = true; break;
                case "-hide_banner" or "-nostdin" or "-stats" or "-nostats" or "-dn": break;
                case "-loglevel" or "-v" or "-stats_period" or "-update": Next(); break;
                case "-safe" when beforeInput: Next(); break;
                case "-progress": line.Progress = Next(); break;
                case "-f" when beforeInput: pending.Format = Next(); pendingUsed = true; break;
                case "-f": line.Format = Next(); break;
                case "-ss" when beforeInput: pending.Start = Next(); pendingUsed = true; break;
                case "-ss": line.Start = Next(); break;
                case "-to" when beforeInput: pending.To = Next(); pendingUsed = true; break;
                case "-to": line.To = Next(); break;
                case "-t" when beforeInput: pending.Duration = Next(); pendingUsed = true; break;
                case "-t": line.Duration = Next(); break;
                case "-loop" when beforeInput: pending.Loop = Next(); pendingUsed = true; break;
                case "-itsoffset" when beforeInput: pending.Offset = Next(); pendingUsed = true; break;
                case "-framerate" or "-r" when beforeInput: pending.FrameRate = Next(); pendingUsed = true; break;
                case "-map": line.Maps.Add(Next()); break;
                case "-c" or "-codec": line.Codecs[string.Empty] = Next(); break;
                case "-c:v" or "-codec:v" or "-vcodec" or "-c:v:0": line.Codecs["v"] = Next(); break;
                case "-c:a" or "-codec:a" or "-acodec" or "-c:a:0": line.Codecs["a"] = Next(); break;
                case "-c:s" or "-codec:s" or "-scodec": line.Codecs["s"] = Next(); break;
                case "-an": line.NoAudio = true; break;
                case "-vn": line.NoVideo = true; break;
                case "-sn": line.NoSubtitles = true; break;
                case "-r": line.Rate = Next(); break;
                case "-s": line.Size = Next(); break;
                case "-frames:v" or "-vframes" or "-frames": line.Frames = Next(); break;
                case "-q:v" or "-qscale:v" or "-qscale" or "-q": line.Quality = Next(); break;
                case "-ar": line.SampleRate = Next(); break;
                case "-b:a" or "-ab": line.AudioBitrate = Next(); break;
                case "-ac": line.Channels = Next(); break;
                case "-sample_fmt": line.SampleFormat = Next(); break;
                case "-metadata": line.Metadata.Add(Next()); break;
                case "-map_metadata": line.MapMetadata = Next(); break;
                case "-map_chapters": line.MapChapters = Next(); break;
                case "-vf" or "-filter:v" or "-filter:v:0": line.VideoFilter = Next(); break;
                case "-af" or "-filter:a" or "-filter:a:0": line.AudioFilter = Next(); break;
                case "-lavfi" or "-filter_complex": line.ComplexFilter = Next(); break;
                case "-segment_time": line.SegmentTime = Next(); break;
                case "-segment_times": line.SegmentTimes = Next(); break;
                case "-movflags": line.MovFlags.AddRange(Next().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "-frag_duration": line.FragmentDuration = Next(); break;
                default:
                    throw FfmpegMode.Unsupported(name);
            }
        }

        if (pendingUsed)
        {
            throw new UsageException("The ffmpeg line has input options after the last -i; put them before the -i they belong to.");
        }

        if (line.Inputs.Count == 0)
        {
            throw new UsageException("The ffmpeg line has no -i input.");
        }

        return line.Output is null ? throw new UsageException("The ffmpeg line has no output file.") : line;
    }

    private static bool IsNumber(string word) => word.Length > 1 && (char.IsDigit(word[1]) || word[1] == '.') && double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
}
