namespace Egami.Video.Cli.Commands;

/// <summary>
/// ffmpeg mode's encoder options on the commands that encode video (<c>encode</c> and <c>run</c>): <c>-g</c> as
/// <c>--keyframe-interval</c>, and with <c>-c:v ffv1</c> FFV1's <c>-level 3</c>, <c>-coder</c>, <c>-context</c>, <c>-slices</c>
/// and <c>-slicecrc</c> as egami-video's FFV1 options. egami-video writes FFV1 version 3 only, so other levels stop.
/// </summary>
internal static class FfmpegEncoderOptions
{
    /// <summary>The commands with the line's encoder options added; a line whose job does not encode video stops naming the option.</summary>
    public static IReadOnlyList<string[]> Apply(FfmpegLine line, IReadOnlyList<string[]> commands)
    {
        if (line.EncoderOptions.Count == 0)
        {
            return commands;
        }

        string first = line.EncoderOptions.Keys.First();
        if (commands is not [[("encode" or "run"), ..] job])
        {
            throw FfmpegMode.Unsupported(first, why: "egami-video takes encoder options when it encodes video: images or Y4M into a clip, or -vf with -c:v");
        }

        bool ffv1 = line.Codecs.TryGetValue("v", out string? codec) && codec == "ffv1";
        List<string> words = [];
        foreach ((string name, string value) in line.EncoderOptions)
        {
            if (name != "-g" && !ffv1)
            {
                throw FfmpegMode.Unsupported(name, why: $"egami-video takes {name} for -c:v ffv1 only");
            }

            words.AddRange(name switch
            {
                "-g" => ["--keyframe-interval", value],
                "-level" => value == "3" ? [] : throw FfmpegMode.Unsupported($"-level {value}", why: "egami-video writes FFV1 version 3 (-level 3)"),
                "-coder" => ["--coder", value switch
                {
                    "0" or "rice" => "golomb",
                    "1" or "range_def" => "range-default",
                    "2" or "range_tab" => "range",
                    _ => throw FfmpegMode.Unsupported($"-coder {value}", why: "0 (Golomb-Rice), 1 (range coder, default table) and 2 (range coder, custom table) are"),
                }],
                "-context" => ["--context", value switch
                {
                    "0" => "small",
                    "1" => "large",
                    _ => throw FfmpegMode.Unsupported($"-context {value}", why: "0 and 1 are"),
                }],
                "-slices" => ["--slices", value],
                "-slicecrc" => value switch
                {
                    "1" => [],
                    "0" => ["--no-slice-crc"],
                    _ => throw FfmpegMode.Unsupported($"-slicecrc {value}", why: "0 and 1 are"),
                },
                _ => throw FfmpegMode.Unsupported(name),
            });
        }

        return [.. commands.Take(commands.Count - 1), [.. job, .. words]];
    }
}
