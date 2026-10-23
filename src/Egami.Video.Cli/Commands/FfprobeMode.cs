using System.Globalization;

using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Commands;

/// <summary>An ffprobe command line, parsed: the sections to show, the streams, the entries kept, and the writer.</summary>
internal sealed class FfprobeLine
{
    public string? Input { get; set; }

    public bool Streams { get; set; }

    public bool Format { get; set; }

    public bool Packets { get; set; }

    public bool Frames { get; set; }

    public bool Chapters { get; set; }

    public string? Select { get; set; }

    /// <summary>Gets or sets the sections and keys <c>-show_entries</c> keeps: section → keys (empty for every key).</summary>
    public Dictionary<string, HashSet<string>>? Entries { get; set; }

    public string Writer { get; set; } = "default";

    public Dictionary<string, string> WriterOptions { get; } = new(StringComparer.Ordinal);

    /// <summary>Parses the words after <c>ffprobe</c>; throws <see cref="UsageException"/> naming any option outside the subset.</summary>
    public static FfprobeLine Parse(string[] args)
    {
        FfprobeLine line = new();
        for (int i = 0; i < args.Length; i++)
        {
            string word = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"ffprobe option {word} needs a value.");
            switch (word)
            {
                case "-i": line.Input = Next(); break;
                case "-show_streams": line.Streams = true; break;
                case "-show_format": line.Format = true; break;
                case "-show_packets": line.Packets = true; break;
                case "-show_frames": line.Frames = true; break;
                case "-show_chapters": line.Chapters = true; break;
                case "-select_streams": line.Select = Next(); break;
                case "-show_entries": line.Entries = ParseEntries(Next()); break;
                case "-of" or "-print_format" or "-output_format":
                    string writer = Next();
                    int equals = writer.IndexOf('=', StringComparison.Ordinal);
                    line.Writer = equals < 0 ? writer : writer[..equals];
                    if (equals > 0)
                    {
                        foreach (string option in writer[(equals + 1)..].Split(':'))
                        {
                            string[] pair = option.Split('=', 2);
                            line.WriterOptions[Short(pair[0])] = pair.Length > 1 ? pair[1] : "1";
                        }
                    }

                    if (line.Writer is not ("json" or "csv" or "default" or "flat" or "xml" or "compact"))
                    {
                        throw FfmpegMode.Unsupported($"-of {line.Writer}", "ffprobe", "the writers taken are json, csv, compact, default, flat and xml");
                    }

                    break;
                case "-v" or "-loglevel": Next(); break;
                case "-hide_banner" or "-pretty": break;
                default:
                    if (word.StartsWith('-') && word != "-")
                    {
                        throw FfmpegMode.Unsupported(word, "ffprobe");
                    }

                    line.Input = line.Input is null ? word : throw new UsageException($"The ffprobe line has two inputs ('{line.Input}' and '{word}').");
                    break;
            }
        }

        if (line.Input is null)
        {
            throw new UsageException("The ffprobe line has no input file.");
        }

        if (line.Entries is { } entries)
        {
            line.Streams |= entries.ContainsKey("stream");
            line.Format |= entries.ContainsKey("format");
            line.Packets |= entries.ContainsKey("packet");
            line.Frames |= entries.ContainsKey("frame");
        }

        return line;
    }

    /// <summary>The native command the line stands for.</summary>
    public string[] Native()
    {
        List<string> words = ["info", Input is "pipe:" or "pipe:0" ? "-" : Input!, "--format", "ffprobe"];
        if (Packets)
        {
            words.Add("--packets");
        }

        if (Frames)
        {
            words.Add("--frames");
        }

        if (Select is { } select)
        {
            words.AddRange(["--track", select]);
        }

        return [.. words];
    }

    private static string Short(string option) => option switch
    {
        "print_section" => "p",
        "nokey" => "nk",
        "noprint_wrappers" => "nw",
        "item_sep" => "s",
        _ => option,
    };

    private static Dictionary<string, HashSet<string>> ParseEntries(string text)
    {
        Dictionary<string, HashSet<string>> entries = new(StringComparer.Ordinal);
        HashSet<string> Keys(string section) => entries.TryGetValue(section, out HashSet<string>? keys) ? keys : entries[section] = new(StringComparer.Ordinal);
        foreach (string part in text.Split(':'))
        {
            string[] pair = part.Split('=', 2);
            switch (pair[0])
            {
                case "stream" or "streams" or "format" or "packet" or "packets" or "frame" or "frames":
                    HashSet<string> keys = Keys(pair[0].TrimEnd('s'));
                    if (pair.Length > 1)
                    {
                        keys.UnionWith(pair[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
                    }

                    break;
                case "stream_tags":
                    Keys("stream").Add("tags");
                    break;
                case "stream_disposition":
                    Keys("stream").Add("disposition");
                    break;
                case "format_tags":
                    Keys("format").Add("tags");
                    break;
                default:
                    throw FfmpegMode.Unsupported($"-show_entries {pair[0]}", "ffprobe", "the sections taken are stream, format, packet and frame");
            }
        }

        return entries;
    }
}

/// <summary>Runs an ffprobe line through info's gathering and writes ffprobe's sections with the writer asked for.</summary>
internal static class FfprobeOutput
{
    public static int Run(VideoSession session, FfprobeLine line)
    {
        string input = line.Input is "pipe:" or "pipe:0" ? "-" : line.Input!;
        InputFile file = Globber.Expand(session, [input], recursive: false)[0];
        TrackSelection tracks = line.Select is { } select ? TrackSelection.Parse([select], false, false) : TrackSelection.All;
        InfoCommand.InfoRequest request = new(true, false, line.Packets, line.Frames, tracks);
        InfoCommand.InfoData data = InfoCommand.Gather(session, file, request);
        ReportObject report = FfprobeReport.Build([data], request);
        ReportObject kept = new();
        foreach ((string name, object? value) in report.Fields)
        {
            bool keep = name switch
            {
                "streams" => line.Streams,
                "format" => line.Format,
                "packets" => line.Packets,
                "frames" => line.Frames,
                _ => false,
            };
            if (keep)
            {
                kept.Add(name, Filter(name, value, line.Entries));
            }
        }

        if (line.Chapters)
        {
            List<ReportObject> chapters = kept.List("chapters");
            int id = 0;
            foreach (ChapterInfo chapter in data.Info.Chapters)
            {
                ReportObject entry = new ReportObject()
                    .Add("id", id++)
                    .Add("time_base", "1/1000000000")
                    .Add("start", chapter.Start.Rescale(Rational.Nanosecond).Ticks)
                    .Add("start_time", chapter.Start.Seconds.ToString("0.000000", CultureInfo.InvariantCulture));
                if (chapter.End is { } end)
                {
                    entry.Add("end", end.Rescale(Rational.Nanosecond).Ticks).Add("end_time", end.Seconds.ToString("0.000000", CultureInfo.InvariantCulture));
                }

                if (chapter.Title is { } title)
                {
                    entry.Add("tags", new Dictionary<string, string> { ["title"] = title });
                }

                chapters.Add(entry);
            }
        }

        TextWriter output = session.Console.Text;
        switch (line.Writer)
        {
            case "json":
                ReportWriter.Json(output, kept);
                break;
            case "xml":
                ReportWriter.Xml(output, "ffprobe", kept, new Dictionary<string, string> { ["streams"] = "stream", ["packets"] = "packet", ["frames"] = "frame", ["chapters"] = "chapter" });
                break;
            default:
                WriteSections(output, kept, line.Writer, line.WriterOptions);
                break;
        }

        return ExitCodes.Success;
    }

    private static object? Filter(string section, object? value, Dictionary<string, HashSet<string>>? entries)
    {
        if (entries is null || !entries.TryGetValue(section.TrimEnd('s'), out HashSet<string>? keys) || keys.Count == 0)
        {
            return value;
        }

        ReportObject Keep(ReportObject item)
        {
            ReportObject result = new();
            foreach ((string key, object? field) in item.Fields)
            {
                if (keys.Contains(key))
                {
                    result.Add(key, field);
                }
            }

            return result;
        }

        return value switch
        {
            ReportObject item => Keep(item),
            List<ReportObject> list => list.Select(Keep).ToList(),
            _ => value,
        };
    }

    /// <summary>ffprobe's default, compact, csv and flat writers, with the nokey, noprint_wrappers, print_section and item_sep options.</summary>
    private static void WriteSections(TextWriter output, ReportObject report, string writer, Dictionary<string, string> options)
    {
        bool noKey = options.TryGetValue("nk", out string? nk) ? nk == "1" : writer == "csv";
        bool noWrappers = options.TryGetValue("nw", out string? nw) && nw == "1";
        bool printSection = !(options.TryGetValue("p", out string? p) && p == "0");
        string separator = options.TryGetValue("s", out string? s) ? s : writer == "csv" ? "," : "|";
        foreach ((string name, object? value) in report.Fields)
        {
            string section = name switch { "streams" => "STREAM", "packets" => "PACKET", "frames" => "FRAME", "chapters" => "CHAPTER", _ => name.ToUpperInvariant() };
            IEnumerable<ReportObject> items = value switch
            {
                List<ReportObject> list => list,
                ReportObject item => [item],
                _ => [],
            };
            int index = 0;
            foreach (ReportObject item in items)
            {
                List<(string Key, string Value)> pairs = [.. Flatten(item, string.Empty)];
                switch (writer)
                {
                    case "default":
                        if (!noWrappers)
                        {
                            output.Write($"[{section}]\n");
                        }

                        foreach ((string key, string text) in pairs)
                        {
                            output.Write(noKey ? text + "\n" : $"{key}={text}\n");
                        }

                        if (!noWrappers)
                        {
                            output.Write($"[/{section}]\n");
                        }

                        break;
                    case "flat":
                        string prefix = value is List<ReportObject> ? $"{name}.{section.ToLowerInvariant()}.{index}." : $"{name}.";
                        foreach ((string key, string text) in pairs)
                        {
                            output.Write($"{prefix}{key.Replace(':', '_')}=\"{text}\"\n");
                        }

                        break;
                    default:
                        IEnumerable<string> fields = pairs.Select(pair => noKey ? pair.Value : $"{pair.Key}={pair.Value}");
                        output.Write((printSection ? section.ToLowerInvariant() + separator : string.Empty) + string.Join(separator, fields) + "\n");
                        break;
                }

                index++;
            }
        }
    }

    private static IEnumerable<(string Key, string Value)> Flatten(ReportObject item, string prefix)
    {
        foreach ((string key, object? value) in item.Fields)
        {
            switch (value)
            {
                case ReportObject child:
                    foreach ((string k, string v) in Flatten(child, prefix + key + ":"))
                    {
                        yield return (k, v);
                    }

                    break;
                case IReadOnlyDictionary<string, string> tags:
                    foreach ((string tag, string text) in tags)
                    {
                        yield return ($"{prefix}TAG:{tag}", text);
                    }

                    break;
                case List<ReportObject>:
                    break;
                default:
                    yield return (prefix + key, ReportWriter.Text(value));
                    break;
            }
        }
    }
}
