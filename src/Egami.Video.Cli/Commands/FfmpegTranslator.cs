using System.Globalization;
using System.Text;

using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>Maps a parsed ffmpeg line onto egami-video command lines: the job each documented ffmpeg form does.</summary>
internal static class FfmpegTranslator
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".avif", ".jxl", ".qoi", ".tga", ".ppm", ".pgm", ".pam"];

    private static readonly string[] AudioExtensions = [".wav", ".flac", ".w64", ".oga", ".opus"];

    /// <summary>Audio files that also hold compressed audio: an audio job unless the line copies the audio (-c:a copy).</summary>
    private static readonly string[] CopyableAudioExtensions = [".aiff", ".aif", ".aifc", ".caf"];

    /// <summary>The native command lines for an ffmpeg line, in the order they run; <paramref name="explain"/> skips the check that the output is new.</summary>
    public static IReadOnlyList<string[]> Translate(FfmpegLine line, VideoSession session, bool explain = false)
    {
        string output = Path(line.Output!);
        string extension = System.IO.Path.GetExtension(output).ToLowerInvariant();
        FfmpegInput first = line.Inputs[0];
        string input = Path(first.Path);
        List<string> common = [];
        if (line.Progress is { } progress)
        {
            common.AddRange(["--progress", progress is "pipe:1" or "pipe:" or "-" ? "-" : progress]);
        }

        if (!explain)
        {
            CheckOverwrite(line, session, output);
        }
        string? format = OutputFormat(line.Format, output);
        List<string> outputWords = ["-o", output];
        if (format is not null && format is not ("image2" or "framemd5" or "framehash" or "null" or "segment" or "gif" or "apng"))
        {
            outputWords.AddRange(["--format", format]);
        }

        // Quality checks: -lavfi psnr or ssim between two inputs, to the null muxer.
        if (line.ComplexFilter is { } complex)
        {
            string[] names = [.. complex.Split(',', ';').Select(f => f.Split('=')[0].Trim())];
            if (line.Inputs.Count != 2 || names.Any(n => n is not ("psnr" or "ssim")))
            {
                throw FfmpegMode.Unsupported($"-lavfi {complex}", why: "only psnr and ssim between two inputs are");
            }

            // ffmpeg's psnr and ssim filters take the distorted clip first and the reference second.
            return [["compare", Path(line.Inputs[1].Path), Path(line.Inputs[0].Path), "--metric", string.Join(',', names), .. common]];
        }

        if (line.Format is "framemd5" or "framehash")
        {
            List<string> hash = ["hash", input, "--algorithm", line.Format == "framemd5" ? "md5" : "sha256", .. Streams(line), .. common];
            if (line.Codecs.TryGetValue(string.Empty, out string? codec) && codec == "copy")
            {
                hash.Add("--packets");
            }

            return [[.. hash, .. output == "-" ? [] : (string[])["-o", output]]];
        }

        if (first.Format == "concat")
        {
            RequireCopy(line, "concat");
            return [["concat", .. ConcatList(session, first.Path), .. outputWords, .. common]];
        }

        if (line.Format == "segment")
        {
            RequireCopy(line, "the segment muxer");
            List<string> segment = ["segment", input, "-o", Pattern(output, "part"), .. Streams(line), .. common];
            if (line.SegmentTime is { } every)
            {
                segment.AddRange(["--every", every]);
            }
            else if (line.SegmentTimes is { } times)
            {
                segment.AddRange(["--at", times]);
            }
            else
            {
                throw new UsageException("ffmpeg -f segment needs -segment_time or -segment_times.");
            }

            return [[.. segment]];
        }

        bool imagesIn = first.Format is "image2" || first.Path.Contains('%', StringComparison.Ordinal) || ImageExtensions.Contains(System.IO.Path.GetExtension(first.Path).ToLowerInvariant());
        if (imagesIn || first.Format == "yuv4mpegpipe")
        {
            List<string> encode = ["encode", .. line.Inputs.Select(i => Pattern(Path(i.Path), "frame")), .. outputWords, .. common];
            if ((first.FrameRate ?? line.Rate) is { } rate)
            {
                encode.AddRange(["--rate", rate]);
            }

            if (line.Codecs.TryGetValue("v", out string? codec) || line.Codecs.TryGetValue(string.Empty, out codec))
            {
                encode.AddRange(["--codec", VideoCodec(codec)]);
            }

            if (line.Quality is { } quality)
            {
                encode.AddRange(["--quality", QualityFromQscale(quality)]);
            }

            if (line.Size is { } size)
            {
                encode.AddRange(["--size", size]);
            }

            if (line.VideoFilter is not null)
            {
                throw FfmpegMode.Unsupported($"-vf {line.VideoFilter}", why: "egami-video encode takes no filters; resize with -s");
            }

            return [[.. encode]];
        }

        if (line.Inputs.Count > 1)
        {
            return [Embed(line, outputWords, common)];
        }

        List<string> steps = Filters(line.VideoFilter, out string? fps, out string? special);
        if (special is not null)
        {
            string[] parts = special.Split('|');
            return parts[0] switch
            {
                "thumbnail" => [["thumbnail", input, "-o", output, .. common]],
                "scenes" => [["scenes", input, "--method", "content", "--threshold", parts[1], "-o", Pattern(output, "scene"), .. common]],
                _ => [["sheet", input, "-o", output, "--grid", parts[1], .. common]],
            };
        }

        if (extension is ".gif" or ".apng" || (extension == ".webp" && !line.Codecs.ContainsKey("v")))
        {
            List<string> animate = ["animate", input, "-o", output, .. Range(line, first, durations: false), .. common];
            if ((fps ?? line.Rate) is { } rate)
            {
                animate.AddRange(["--rate", rate]);
            }

            if (steps is ["resize", var size])
            {
                animate.AddRange(["--size", size]);
            }
            else if (steps.Count > 0)
            {
                throw FfmpegMode.Unsupported($"-vf {line.VideoFilter}", why: "with an animation only scale and fps are");
            }

            return [[.. animate]];
        }

        if (ImageExtensions.Contains(extension) || line.Format == "image2" || output.Contains('%', StringComparison.Ordinal))
        {
            return [Frames(line, first, input, output, steps, fps, common)];
        }

        bool copiesAudio = (line.Codecs.TryGetValue("a", out string? audioCopy) ? audioCopy : line.Codecs.GetValueOrDefault(string.Empty)) == "copy";
        bool audioJob = AudioExtensions.Contains(extension) || (CopyableAudioExtensions.Contains(extension) && !copiesAudio) || line.NoVideo || line.SampleRate is not null || line.Channels is not null || line.SampleFormat is not null
            || line.AudioBitrate is not null
            || (line.Codecs.TryGetValue("a", out string? audioCodec) && audioCodec != "copy");
        if (audioJob)
        {
            List<string> audio = ["audio", input, .. outputWords, .. Streams(line), .. common];
            if (line.Codecs.TryGetValue("a", out string? codec) && codec != "copy")
            {
                audio.AddRange(["--codec", AudioCodec(codec)]);
            }

            if (line.SampleRate is { } sampleRate)
            {
                audio.AddRange(["--rate", sampleRate]);
            }

            if (line.AudioBitrate is { } bitrate)
            {
                audio.AddRange(["--bitrate", bitrate]);
            }

            if (line.Channels is { } channels)
            {
                audio.AddRange(["--channels", channels]);
            }

            if (line.SampleFormat is { } sampleFormat)
            {
                audio.AddRange(["--sample-format", SampleFormat(sampleFormat)]);
            }

            if (line.NoVideo)
            {
                audio.Add("--no-video");
            }

            return [[.. audio]];
        }

        if (steps.Count > 0 || (line.Codecs.TryGetValue("v", out string? videoCodec) && videoCodec != "copy") || line.Size is not null || line.Quality is not null)
        {
            List<string> run = ["run", input, .. steps];
            if (line.Size is { } size)
            {
                run.AddRange([.. steps.Count > 0 ? (string[])["+"] : [], "resize", size + "!"]);
            }

            run.AddRange(outputWords);
            if (line.Codecs.TryGetValue("v", out string? codec) && codec != "copy")
            {
                run.AddRange(["--codec", VideoCodec(codec)]);
            }

            if (line.Quality is { } quality)
            {
                run.AddRange(["--quality", QualityFromQscale(quality)]);
            }

            return [[.. run, .. common]];
        }

        if (line.Rate is not null)
        {
            RequireCopy(line, "-r without filters");
            return [["timestamps", input, .. outputWords, "--rate", line.Rate, .. Streams(line), .. common]];
        }

        if (first.Offset is { } offset)
        {
            RequireCopy(line, "-itsoffset");
            return [["timestamps", input, .. outputWords, "--shift", offset, .. Streams(line), .. common]];
        }

        bool cut = (line.Start ?? first.Start ?? line.To ?? first.To ?? line.Duration ?? first.Duration) is not null;
        if (line.Metadata.Count > 0 || line.MapMetadata is not null || line.MapChapters is not null)
        {
            if (cut || line.Maps.Count > 0)
            {
                throw FfmpegMode.Unsupported("-metadata with a cut or -map", why: "egami-video changes tags in a copy of its own; cut first, then run 'egami-video metadata'");
            }

            RequireCopy(line, "-metadata");
            List<string> metadata = ["metadata", input, .. outputWords, .. common];
            foreach (string pair in line.Metadata)
            {
                metadata.AddRange(pair.EndsWith('=') ? ["--remove", pair[..^1]] : ["--set", pair]);
            }

            if (line.MapMetadata == "-1")
            {
                metadata.Add("--clear");
            }
            else if (line.MapMetadata is { } source && source != "0")
            {
                throw FfmpegMode.Unsupported($"-map_metadata {source}", why: "only 0 (keep) and -1 (drop) are");
            }

            if (line.MapChapters is { } chapters && chapters != "0")
            {
                throw FfmpegMode.Unsupported($"-map_chapters {chapters}", why: "chapters are kept; only 0 is");
            }

            return [[.. metadata]];
        }

        CopyOnly(line, session);
        return cut
            ? [["trim", input, .. outputWords, .. Range(line, first), .. Streams(line), .. common]]
            : [["remux", input, .. outputWords, .. Streams(line), .. common]];
    }

    /// <summary>
    /// The steps of a <c>-vf</c> chain as egami run's words, the frame rate an <c>fps</c> filter asks for, and a filter that is a
    /// command of its own: thumbnail, select for scenes (<c>scenes|0.4</c>), tile for a contact sheet (<c>tile|4x4</c>).
    /// </summary>
    internal static List<string> Filters(string? chain, out string? fps, out string? special)
    {
        fps = null;
        special = null;
        List<string> steps = [];
        if (chain is null)
        {
            return steps;
        }

        foreach (string filter in SplitChain(chain))
        {
            int equals = filter.IndexOf('=', StringComparison.Ordinal);
            string name = (equals < 0 ? filter : filter[..equals]).Trim();
            string value = equals < 0 ? string.Empty : filter[(equals + 1)..].Trim();
            List<string> step = [];
            switch (name)
            {
                case "scale":
                    step.AddRange(["resize", ScaleSize(value)]);
                    break;
                case "crop":
                    string[] crop = Positional(value, ["w", "h", "x", "y"]);
                    if (crop.Length < 4 || crop[2].Length == 0 || crop[3].Length == 0)
                    {
                        throw FfmpegMode.Unsupported($"crop={value}", why: "give w:h:x:y as numbers");
                    }

                    step.AddRange(["crop", Whole(crop[2], "crop"), Whole(crop[3], "crop"), Whole(crop[0], "crop"), Whole(crop[1], "crop")]);
                    break;
                case "pad":
                    string[] pad = Positional(value, ["w", "h", "x", "y", "color"]);
                    if (pad.Length > 2 && pad[2] is not ("(ow-iw)/2" or "-1" or ""))
                    {
                        throw FfmpegMode.Unsupported($"pad={value}", why: "only a centred pad ((ow-iw)/2) is");
                    }

                    step.AddRange(["pad", Whole(pad[0], "pad"), Whole(pad[1], "pad")]);
                    if (pad.Length > 4 && pad[4].Length > 0)
                    {
                        step.AddRange(["--background", pad[4]]);
                    }

                    break;
                case "transpose":
                    step.AddRange(value.Replace("dir=", string.Empty, StringComparison.Ordinal) switch
                    {
                        "1" or "clock" => ["rotate", "90"],
                        "2" or "cclock" => ["rotate", "270"],
                        "0" or "cclock_flip" => ["rotate", "270", "+", "flip", "vertical"],
                        "3" or "clock_flip" => ["rotate", "90", "+", "flip", "vertical"],
                        _ => throw FfmpegMode.Unsupported($"transpose={value}"),
                    });
                    break;
                case "hflip":
                    step.AddRange(["flip", "horizontal"]);
                    break;
                case "vflip":
                    step.AddRange(["flip", "vertical"]);
                    break;
                case "fps":
                    fps = value.Replace("fps=", string.Empty, StringComparison.Ordinal);
                    break;
                case "thumbnail":
                    special = "thumbnail";
                    break;
                case "select" when value.Replace(" ", string.Empty, StringComparison.Ordinal) is var expression && expression.Contains("scene", StringComparison.Ordinal):
                    string threshold = new([.. expression.SkipWhile(c => c != ',').Skip(1).TakeWhile(c => char.IsDigit(c) || c == '.')]);
                    special = "scenes|" + (threshold.Length > 0 ? threshold : "0.4");
                    break;
                case "tile":
                    special = "tile|" + value.Split(':')[0].Replace("layout=", string.Empty, StringComparison.Ordinal);
                    break;
                default:
                    throw FfmpegMode.Unsupported($"-vf {name}", why: "the filters taken are scale, crop, pad, transpose, hflip, vflip, fps, thumbnail, select (scene) and tile");
            }

            if (step.Count > 0)
            {
                if (steps.Count > 0)
                {
                    steps.Add("+");
                }

                steps.AddRange(step);
            }
        }

        return special is not null && steps.Count > 0
            ? throw FfmpegMode.Unsupported($"-vf {chain}", why: "thumbnail, select and tile run on their own, without other filters")
            : steps;
    }

    /// <summary>
    /// A filter chain split at its commas, as the filtergraph syntax reads it: commas inside quotes or parentheses, or escaped
    /// with a backslash, stay in their filter; the quotes and escapes are taken out.
    /// </summary>
    internal static List<string> SplitChain(string chain)
    {
        List<string> filters = [];
        StringBuilder current = new();
        char quote = '\0';
        int depth = 0;
        for (int i = 0; i < chain.Length; i++)
        {
            char c = chain[i];
            if (c == '\\' && i + 1 < chain.Length)
            {
                current.Append(chain[++i]);
            }
            else if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == ',' && depth == 0)
            {
                filters.Add(current.ToString());
                current.Clear();
            }
            else
            {
                depth += c == '(' ? 1 : c == ')' ? -1 : 0;
                current.Append(c);
            }
        }

        filters.Add(current.ToString());
        return filters;
    }

    /// <summary><c>out-%04d.png</c> as an output template with the command's part placeholder: <c>out-{frame:0000}.png</c>.</summary>
    internal static string Pattern(string path, string part)
    {
        StringBuilder text = new();
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '%' && i + 1 < path.Length && path[i + 1] == '%')
            {
                text.Append('%');
                i++;
            }
            else if (path[i] == '%')
            {
                int end = i + 1;
                while (end < path.Length && char.IsDigit(path[end]))
                {
                    end++;
                }

                if (end >= path.Length || path[end] != 'd')
                {
                    throw FfmpegMode.Unsupported($"the pattern '{path}'", why: "only %d and %0Nd are");
                }

                string width = path[(i + 1)..end].TrimStart('0');
                int digits = width.Length > 0 ? int.Parse(width, CultureInfo.InvariantCulture) : 1;
                text.Append(digits > 1 ? $"{{{part}:{new string('0', digits)}}}" : $"{{{part}}}");
                i = end;
            }
            else
            {
                text.Append(path[i] is '{' or '}' ? new string(path[i], 2) : path[i]);
            }
        }

        return text.ToString();
    }

    private static string[] Frames(FfmpegLine line, FfmpegInput first, string input, string output, List<string> steps, string? fps, List<string> common)
    {
        if (steps.Count > 0)
        {
            throw FfmpegMode.Unsupported($"-vf {line.VideoFilter}", why: "with image outputs only fps is; resize the images with egami afterwards");
        }

        string template = Pattern(output, "frame");
        List<string> frames = ["frames", input, "-o", template, .. Streams(line), .. common];
        bool single = line.Frames == "1" || !template.Contains('{', StringComparison.Ordinal);
        if (single)
        {
            frames.AddRange(["--at", first.Start ?? line.Start ?? "0"]);
        }
        else
        {
            frames.AddRange(Range(line, first, durations: false));
            if (line.Frames is { } count)
            {
                // The first N frames are the frames before frame #N; after a start time they would be counted from elsewhere.
                frames.AddRange((first.Start ?? line.Start) is null
                    ? ["--to", "#" + count]
                    : throw FfmpegMode.Unsupported("-frames:v with -ss", why: "give the range as times, -ss and -to"));
            }
        }

        if ((fps ?? line.Rate) is { } rate && !single)
        {
            frames.AddRange(["--every", Every(rate)]);
        }

        if (line.Quality is { } quality)
        {
            frames.AddRange(["--quality", QualityFromQscale(quality)]);
        }

        return [.. frames];
    }

    private static string[] Embed(FfmpegLine line, List<string> outputWords, List<string> common)
    {
        if (line.Inputs.Skip(1).Any(i => (i.Start ?? i.To ?? i.Duration ?? i.Offset) is not null))
        {
            throw FfmpegMode.Unsupported("-ss, -t or -itsoffset on a second input", why: "embed copies whole tracks");
        }

        RequireCopy(line, "several inputs");
        Dictionary<int, List<string>> maps = [];
        foreach (string map in line.Maps)
        {
            string[] parts = map.Split(':', 2);
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int file) || file >= line.Inputs.Count)
            {
                throw FfmpegMode.Unsupported($"-map {map}", why: "a map names an input by number and an optional stream (1:a:0)");
            }

            (maps.TryGetValue(file, out List<string>? list) ? list : maps[file] = []).Add(parts.Length > 1 ? parts[1].TrimEnd('?') : string.Empty);
        }

        if (maps.Count > 0 && (!maps.TryGetValue(0, out List<string>? own) || own.Any(s => s.Length > 0)))
        {
            throw FfmpegMode.Unsupported("-map without 0", why: "embed keeps every track of the first input and adds tracks of the others");
        }

        List<string> embed = ["embed", Path(line.Inputs[0].Path)];
        for (int i = 1; i < line.Inputs.Count; i++)
        {
            foreach (string spec in maps.TryGetValue(i, out List<string>? picked) ? picked : maps.Count == 0 ? [string.Empty] : [])
            {
                embed.AddRange(["--add", Path(line.Inputs[i].Path) + (spec.Length > 0 ? ":" + spec : string.Empty)]);
            }
        }

        return [.. embed, .. outputWords, .. common];
    }

    /// <summary>
    /// <c>-ss</c>, <c>-to</c> and <c>-t</c> as <c>--from</c>, <c>--to</c> and <c>--duration</c>; for commands without
    /// <c>--duration</c> (<paramref name="durations"/> false), <c>-t</c> becomes <c>--to</c> at the start plus the length.
    /// </summary>
    private static IEnumerable<string> Range(FfmpegLine line, FfmpegInput first, bool durations = true)
    {
        string? start = first.Start ?? line.Start;
        if (start is not null)
        {
            yield return "--from";
            yield return start;
        }

        if ((first.To ?? line.To) is { } to)
        {
            yield return "--to";
            yield return to;
        }

        if ((first.Duration ?? line.Duration) is { } duration)
        {
            if (durations)
            {
                yield return "--duration";
                yield return duration;
            }
            else
            {
                double end = (start is null ? 0 : TimeValue.Parse(start, "-ss").Seconds) + TimeValue.Parse(duration, "-t").Seconds;
                yield return "--to";
                yield return end.ToString("0.###", CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>The track options for <c>-map</c>, <c>-an</c>, <c>-vn</c> and <c>-sn</c> with one input.</summary>
    private static IEnumerable<string> Streams(FfmpegLine line)
    {
        foreach (string map in line.Maps)
        {
            string[] parts = map.Split(':', 2);
            if (parts[0] != "0")
            {
                throw FfmpegMode.Unsupported($"-map {map}", why: "with one input, maps start with 0");
            }

            if (parts.Length > 1)
            {
                string spec = parts[1].TrimEnd('?');
                yield return "--track";
                yield return spec.StartsWith("m:language:", StringComparison.Ordinal)
                    ? throw FfmpegMode.Unsupported($"-map {map}", why: "give the kind with the language, as in 0:a:eng... egami-video writes it --track a:eng")
                    : spec;
            }
        }

        if (line.NoAudio)
        {
            yield return "--no-audio";
        }

        if (line.NoSubtitles)
        {
            yield return "--no-subtitles";
        }
    }

    /// <summary><c>scale=640:-1</c> in egami's size syntax.</summary>
    private static string ScaleSize(string value)
    {
        string[] parts = Positional(value, ["w", "h"]);
        string w = parts[0], h = parts.Length > 1 ? parts[1] : "-1";
        if (w.Contains("iw", StringComparison.Ordinal) || h.Contains("ih", StringComparison.Ordinal))
        {
            // iw/2 and iw*0.5 as a percentage.
            string expression = (w.Contains("iw", StringComparison.Ordinal) ? w : h).Replace("iw", string.Empty, StringComparison.Ordinal).Replace("ih", string.Empty, StringComparison.Ordinal);
            double factor = expression.Length > 1 && double.TryParse(expression[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number > 0
                ? expression[0] switch
                {
                    '/' => 1 / number,
                    '*' => number,
                    _ => throw FfmpegMode.Unsupported($"scale={value}", why: "of the expressions only iw/n and iw*f are"),
                }
                : throw FfmpegMode.Unsupported($"scale={value}", why: "of the expressions only iw/n and iw*f are");
            return (factor * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
        }

        bool wAuto = w is "-1" or "-2", hAuto = h is "-1" or "-2";
        return wAuto && hAuto ? throw FfmpegMode.Unsupported($"scale={value}")
            : wAuto ? "x" + Whole(h, "scale")
            : hAuto ? Whole(w, "scale")
            : $"{Whole(w, "scale")}x{Whole(h, "scale")}!";
    }

    private static string[] Positional(string value, string[] names)
    {
        string[] parts = value.Split(':');
        List<string> result = [];
        int next = 0;
        foreach (string part in parts)
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            int index = equals > 0 ? Array.IndexOf(names, part[..equals]) : next++;
            if (index < 0)
            {
                throw FfmpegMode.Unsupported($"{part[..equals]}= in a filter");
            }

            while (result.Count <= index)
            {
                result.Add(string.Empty);
            }

            result[index] = equals > 0 ? part[(equals + 1)..] : part;
        }

        return [.. result];
    }

    private static string Whole(string text, string filter) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? text : throw FfmpegMode.Unsupported($"{filter} value '{text}'", why: "only numbers are");

    private static void RequireCopy(FfmpegLine line, string what)
    {
        foreach ((string kind, string codec) in line.Codecs)
        {
            if (codec != "copy")
            {
                throw FfmpegMode.Unsupported($"-c{(kind.Length > 0 ? ":" + kind : string.Empty)} {codec}", why: $"{what} copies streams (-c copy)");
            }
        }
    }

    private static void CopyOnly(FfmpegLine line, VideoSession session)
    {
        RequireCopy(line, "a line without filters");
        if (!line.Codecs.TryGetValue(string.Empty, out string? all) || all != "copy")
        {
            bool every = line.Codecs.TryGetValue("v", out string? v) && v == "copy" && line.Codecs.TryGetValue("a", out string? a) && a == "copy";
            if (!every)
            {
                session.Warn("the streams are copied as -c copy would; ffmpeg would encode them again.");
            }
        }
    }

    private static void CheckOverwrite(FfmpegLine line, VideoSession session, string output)
    {
        if (output != "-" && !output.Contains('%', StringComparison.Ordinal) && !line.Overwrite && File.Exists(System.IO.Path.GetFullPath(output, session.WorkingDirectory)))
        {
            throw new IOException($"'{output}' already exists; give -y to replace it.");
        }
    }

    private static string? OutputFormat(string? format, string output)
    {
        if (format is null)
        {
            return output == "-" ? throw new UsageException("ffmpeg writes to pipe: only with -f (matroska, webm, ivf, yuv4mpegpipe, wav, flac, ogg).") : null;
        }

        return format switch
        {
            "matroska" => "mkv",
            "yuv4mpegpipe" => "y4m",
            "mov" or "mp4" => "mp4",
            "webm" or "ivf" or "avi" or "wav" or "flac" or "ogg" or "w64" => format,
            "image2" or "framemd5" or "framehash" or "null" or "segment" or "gif" or "apng" => format,
            _ => throw FfmpegMode.Unsupported($"-f {format}"),
        };
    }

    private static string Path(string path) => path is "pipe:" or "pipe:0" or "pipe:1" ? "-" : path.StartsWith("file:", StringComparison.Ordinal) ? path[5..] : path;

    private static string Every(string rate)
    {
        // fps=1 is a frame a second, fps=1/60 a frame a minute, fps=0.5 a frame every two seconds.
        double fps = rate.Split('/') is [var n, var d] && double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double num) && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out double den) && den > 0
            ? num / den
            : double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) ? f : 0;
        return fps > 0 ? (1 / fps).ToString("0.######", CultureInfo.InvariantCulture) : throw FfmpegMode.Unsupported($"fps={rate}");
    }

    /// <summary>ffmpeg's MJPEG <c>-q:v</c> (2 best, 31 worst) as a JPEG quality from 100 down to 30.</summary>
    private static string QualityFromQscale(string q)
    {
        return double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale) && scale >= 1 && scale <= 31
            ? ((int)Math.Round(100 - ((scale - 1) * 70 / 30))).ToString(CultureInfo.InvariantCulture)
            : throw FfmpegMode.Unsupported($"-q:v {q}", why: "give 1 to 31");
    }

    private static string VideoCodec(string codec) => codec switch
    {
        "mjpeg" => "mjpeg",
        "libvpx" or "vp8" => "vp8",
        "libvpx-vp9" or "vp9" => "vp9",
        "rawvideo" => "raw",
        _ => throw FfmpegMode.Unsupported($"-c:v {codec}", why: "egami-video encodes mjpeg, libvpx (VP8), libvpx-vp9 and rawvideo, and copies"),
    };

    private static string AudioCodec(string codec) => codec switch
    {
        "flac" => "flac",
        "alac" => "alac",
        "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" or "pcm_u8" or "pcm_s16be" or "pcm_s24be" or "pcm_s32be" => "pcm",
        "libopus" or "opus" => "opus",
        _ => throw FfmpegMode.Unsupported($"-c:a {codec}", why: "egami-video encodes flac, alac, PCM and libopus, and copies"),
    };

    private static string SampleFormat(string format) => format switch
    {
        "u8" or "u8p" => "u8",
        "s16" or "s16p" => "s16",
        "s32" or "s32p" => "s32",
        "flt" or "fltp" => "f32",
        "dbl" or "dblp" => "f64",
        _ => throw FfmpegMode.Unsupported($"-sample_fmt {format}"),
    };

    private static List<string> ConcatList(VideoSession session, string listFile)
    {
        string path = session.ResolvePath(listFile);
        string folder = System.IO.Path.GetDirectoryName(path)!;
        List<string> clips = [];
        foreach (string raw in File.ReadAllLines(path))
        {
            string text = raw.Trim();
            if (text.Length == 0 || text.StartsWith('#') || text.StartsWith("ffconcat", StringComparison.Ordinal))
            {
                continue;
            }

            if (!text.StartsWith("file ", StringComparison.Ordinal))
            {
                throw FfmpegMode.Unsupported($"the concat list line '{text}'", why: "only file lines are");
            }

            // Names in the list are relative to the list's folder; concat gets them relative to the working folder.
            string name = text[5..].Trim().Trim('\'', '"');
            string full = System.IO.Path.IsPathRooted(name) ? name : System.IO.Path.Combine(folder, name);
            clips.Add(System.IO.Path.GetRelativePath(session.WorkingDirectory, full).Replace('\\', '/'));
        }

        return clips;
    }
}
