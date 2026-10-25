using System.Globalization;
using System.Text;

using Egami.Formats;
using Egami.Operations;
using Egami.Pixels;
using Egami.Video.Cli.Host;
using Egami.Video.Editing;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// ffmpeg mode's editing filters (V9 design Q14, Build Plan task 22): <c>-vf</c>, <c>-af</c> and <c>-filter_complex</c> graphs of the
/// documented filters become one edit, rendered by <c>egami-video edit</c>; <c>deshake</c> and the vidstab pair become
/// <c>egami-video stabilize</c>. Expressions are taken only as numbers, the overlay's <c>W</c>, <c>H</c>, <c>w</c> and <c>h</c>
/// placement forms, <c>enable='between(t,a,b)'</c> and zoompan's constant or linear zoom; anything else stops with exit code 2.
/// Built from FFmpeg's published filter documentation only.
/// </summary>
internal static class FfmpegEdit
{
    /// <summary>The filters this maps, for the help text and the refusals.</summary>
    public const string FilterList =
        "fade, setpts (a constant factor), reverse, loop, tpad (clone), fps, scale, xfade, concat, overlay, hstack, vstack, xstack (grid), zoompan, chromakey, colorkey, despill, hflip and vflip (with enable), deshake, vidstabdetect and vidstabtransform; audio afade, acrossfade, amix, atempo, volume, areverse and aloop";

    /// <summary>The word in the native command that stands for the edit file.</summary>
    public const string EditFile = "{edit}";

    private static readonly string[] EditFilters = ["fade", "setpts", "reverse", "loop", "tpad", "freezeframes", "xfade", "concat", "overlay", "hstack", "vstack", "xstack", "zoompan", "chromakey", "colorkey", "despill", "afade", "acrossfade", "amix", "atempo", "volume", "areverse", "aloop"];

    private static readonly string[] StabilizeFilters = ["deshake", "vidstabdetect", "vidstabtransform"];

    /// <summary>Gets whether a line needs this mapping: it uses an editing filter, a timeline enable, or a looped image.</summary>
    public static bool Wants(FfmpegLine line)
    {
        if (line.Inputs.Any(i => i.Loop is not null))
        {
            return true;
        }

        foreach (string? graph in new[] { line.VideoFilter, line.AudioFilter, line.ComplexFilter })
        {
            if (graph is null)
            {
                continue;
            }

            foreach (GraphFilter filter in ParseGraph(graph, "w"))
            {
                if (EditFilters.Contains(filter.Name) || StabilizeFilters.Contains(filter.Name) || filter.Args.Contains("enable=", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The native command for a line, and the edit file's JSON when it renders an edit.</summary>
    public sealed record Mapped(string[] Words, string? Json);

    /// <summary>Maps a line; <see cref="EditFile"/> in the words stands for the edit file when <see cref="Mapped.Json"/> is set.</summary>
    public static Mapped Translate(FfmpegLine line, VideoSession session)
    {
        if (Stabilize(line) is { } stabilize)
        {
            return new Mapped(stabilize, null);
        }

        Builder builder = new(line, session);
        return builder.Build();
    }

    /// <summary><c>-vf deshake</c>, <c>vidstabdetect</c> to <c>-f null</c>, and <c>vidstabtransform</c>: egami-video stabilize.</summary>
    private static string[]? Stabilize(FfmpegLine line)
    {
        if (line.VideoFilter is not { } chain || line.ComplexFilter is not null)
        {
            return null;
        }

        List<string> filters = FfmpegTranslator.SplitChain(chain);
        string[] names = [.. filters.Select(f => f.Split('=')[0].Trim())];
        if (!names.Any(StabilizeFilters.Contains))
        {
            return null;
        }

        if (filters.Count != 1 || line.Inputs.Count != 1)
        {
            throw FfmpegMode.Unsupported($"-vf {chain}", why: "deshake and the vidstab filters run on one input, without other filters");
        }

        string input = line.Inputs[0].Path;
        string value = filters[0].Contains('=', StringComparison.Ordinal) ? filters[0][(filters[0].IndexOf('=', StringComparison.Ordinal) + 1)..] : string.Empty;
        List<string> words = ["stabilize", input];
        switch (names[0])
        {
            case "deshake":
                Named("deshake", value, []);
                words.AddRange(["--edges", "blur"]);
                break;
            case "vidstabdetect":
                Dictionary<string, string> detect = Named("vidstabdetect", value, ["result", "shakiness", "accuracy", "stepsize", "mincontrast", "show"]);
                if (line.Format != "null")
                {
                    throw FfmpegMode.Unsupported("-vf vidstabdetect", why: "it measures only, to -f null");
                }

                return [.. words, "--save-motion", detect.GetValueOrDefault("result", "transforms.trf")];
            case "vidstabtransform":
                Dictionary<string, string> transform = Named("vidstabtransform", value, ["input", "smoothing", "optzoom", "crop", "zoom", "interpol", "tripod"]);
                words.AddRange(["--motion", transform.GetValueOrDefault("input", "transforms.trf")]);
                if (transform.TryGetValue("smoothing", out string? smoothing))
                {
                    words.AddRange(["--smoothing", Number(smoothing, "vidstabtransform smoothing").ToString(CultureInfo.InvariantCulture)]);
                }

                if (transform.TryGetValue("crop", out string? crop) && crop == "black" || transform.TryGetValue("optzoom", out string? zoom) && zoom == "0")
                {
                    words.AddRange(["--edges", "black"]);
                }

                if (transform.TryGetValue("tripod", out string? tripod) && tripod != "0" || transform.TryGetValue("zoom", out string? extra) && Number(extra, "zoom") != 0)
                {
                    throw FfmpegMode.Unsupported("vidstabtransform tripod and zoom", why: "egami-video stabilize smooths the path and zooms only to hide the edges");
                }

                break;
        }

        return [.. words, .. Output(line)];
    }

    /// <summary>The output words every native command gets: -o, the codec, quality, size, rate and no audio.</summary>
    private static List<string> Output(FfmpegLine line)
    {
        List<string> words = ["-o", line.Output!];
        if (line.Codecs.TryGetValue("v", out string? codec) || line.Codecs.TryGetValue(string.Empty, out codec))
        {
            words.AddRange(["--codec", FfmpegTranslator.VideoCodec(codec)]);
        }

        if (line.Codecs.ContainsKey("a"))
        {
            throw FfmpegMode.Unsupported("-c:a", why: "with editing filters egami-video picks the audio codec for the container");
        }

        if (line.Quality is { } quality)
        {
            words.AddRange(["--quality", FfmpegTranslator.QualityFromQscale(quality)]);
        }

        if (line.NoAudio)
        {
            words.Add("--no-audio");
        }

        foreach ((string? value, string name) in new[] { (line.Start, "-ss"), (line.To, "-to"), (line.Duration, "-t"), (line.Frames, "-frames:v"), (line.SampleRate, "-ar"), (line.Channels, "-ac") })
        {
            if (value is not null)
            {
                throw FfmpegMode.Unsupported($"{name} after the inputs", why: "with editing filters, put -ss, -to and -t before the -i they cut");
            }
        }

        return words;
    }

    /// <summary>One filter of a graph with its input and output labels.</summary>
    private sealed record GraphFilter(List<string> Inputs, string Name, string Args, List<string> Outputs);

    /// <summary>Splits a filter graph into filters, joining the filters of a chain with labels of their own.</summary>
    private static List<GraphFilter> ParseGraph(string graph, string prefix)
    {
        List<GraphFilter> filters = [];
        int chainIndex = 0, linkIndex = 0;
        foreach (string chain in SplitChains(graph))
        {
            List<string> parts = FfmpegTranslator.SplitChain(chain);
            string? previous = null;
            for (int i = 0; i < parts.Count; i++)
            {
                string text = parts[i].Trim();
                List<string> inputs = [];
                while (text.StartsWith('['))
                {
                    int close = text.IndexOf(']', StringComparison.Ordinal);
                    inputs.Add(text[1..close]);
                    text = text[(close + 1)..].TrimStart();
                }

                List<string> outputs = [];
                while (text.EndsWith(']'))
                {
                    int open = text.LastIndexOf('[');
                    outputs.Insert(0, text[(open + 1)..^1]);
                    text = text[..open].TrimEnd();
                }

                if (previous is not null)
                {
                    inputs.Insert(0, previous);
                }
                if (outputs.Count == 0)
                {
                    outputs.Add(i == parts.Count - 1 ? $"#end{prefix}{chainIndex}" : $"#link{prefix}{linkIndex++}");
                }

                int equals = text.IndexOf('=', StringComparison.Ordinal);
                filters.Add(new GraphFilter(inputs, (equals < 0 ? text : text[..equals]).Trim(), equals < 0 ? string.Empty : text[(equals + 1)..].Trim(), outputs));
                previous = i == parts.Count - 1 ? null : outputs[0];
            }

            chainIndex++;
        }

        return filters;
    }

    private static List<string> SplitChains(string graph)
    {
        List<string> chains = [];
        StringBuilder current = new();
        char quote = '\0';
        for (int i = 0; i < graph.Length; i++)
        {
            char c = graph[i];
            if (c == '\\' && i + 1 < graph.Length)
            {
                current.Append(c).Append(graph[++i]);
            }
            else if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
                current.Append(c);
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                current.Append(c);
            }
            else if (c == ';')
            {
                chains.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.ToString().Trim().Length > 0)
        {
            chains.Add(current.ToString());
        }

        return chains;
    }

    /// <summary>A filter's options by name, with positional ones named in order.</summary>
    private static Dictionary<string, string> Named(string filter, string value, string[] names, string[]? positional = null, Dictionary<string, string>? aliases = null)
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        int next = 0;
        foreach (string part in value.Length == 0 ? [] : value.Split(':'))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            string name;
            if (equals <= 0)
            {
                if (positional is null || next >= positional.Length)
                {
                    throw FfmpegMode.Unsupported($"{filter}={value}", why: "give each option as name=value");
                }

                name = positional[next++];
            }
            else
            {
                name = part[..equals].Trim();
            }

            if (aliases is not null && aliases.TryGetValue(name, out string? full))
            {
                name = full;
            }

            if (!names.Contains(name))
            {
                throw FfmpegMode.Unsupported($"{filter} option {name}", why: names.Length == 0 ? $"{filter} takes no options here" : $"{filter} takes {string.Join(", ", names)}");
            }

            options[name] = (equals <= 0 ? part : part[(equals + 1)..]).Trim();
        }

        return options;
    }

    /// <summary>An ffmpeg colour: a name, #RRGGBB or 0xRRGGBB.</summary>
    private static Rgba8 Color(string text) =>
        EditCommands.ParseColor(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? "#" + text[2..] : text, "color");

    private static double Number(string text, string what) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : throw FfmpegMode.Unsupported($"{what} '{text}'", why: "only numbers are");

    /// <summary>A time as ffmpeg's options take it: seconds, or [-][HH:]MM:SS[.m...].</summary>
    private static double Seconds(string text, string what)
    {
        string t = text.Trim().TrimEnd('s');
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return seconds;
        }

        string[] parts = t.Split(':');
        double total = 0;
        foreach (string part in parts)
        {
            total = (total * 60) + Number(part, what);
        }

        return total;
    }

    /// <summary>A small expression: numbers, the given names, + - * / and parentheses, min and max.</summary>
    private static double Evaluate(string text, Func<string, double?> names, string what)
    {
        int at = 0;
        string s = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        double result = Sum();
        return at == s.Length ? result : throw FfmpegMode.Unsupported($"{what} '{text}'", why: "of the expressions only numbers, + - * /, min, max and the documented names are");

        double Sum()
        {
            double value = Product();
            while (at < s.Length && s[at] is '+' or '-')
            {
                char op = s[at++];
                double right = Product();
                value = op == '+' ? value + right : value - right;
            }

            return value;
        }

        double Product()
        {
            double value = Unary();
            while (at < s.Length && s[at] is '*' or '/')
            {
                char op = s[at++];
                double right = Unary();
                value = op == '*' ? value * right : value / right;
            }

            return value;
        }

        double Unary()
        {
            if (at < s.Length && s[at] == '-')
            {
                at++;
                return -Unary();
            }

            if (at < s.Length && s[at] == '(')
            {
                at++;
                double inner = Sum();
                Expect(')');
                return inner;
            }

            int start = at;
            if (at < s.Length && (char.IsDigit(s[at]) || s[at] == '.'))
            {
                while (at < s.Length && (char.IsDigit(s[at]) || s[at] is '.' or 'e' or 'E'))
                {
                    at++;
                }

                return Number(s[start..at], what);
            }

            while (at < s.Length && (char.IsLetterOrDigit(s[at]) || s[at] == '_'))
            {
                at++;
            }

            string name = s[start..at];
            if (name is "min" or "max")
            {
                Expect('(');
                double a = Sum();
                Expect(',');
                double b = Sum();
                Expect(')');
                return name == "min" ? Math.Min(a, b) : Math.Max(a, b);
            }

            return name.Length > 0 && names(name) is { } known ? known : throw FfmpegMode.Unsupported($"{what} '{text}'", why: $"'{name}' is not a name egami-video takes there");
        }

        void Expect(char c)
        {
            if (at >= s.Length || s[at] != c)
            {
                throw FfmpegMode.Unsupported($"{what} '{text}'");
            }

            at++;
        }
    }

    /// <summary>A timeline <c>enable</c>: between(t,a,b), gte(t,a) or lte(t,b).</summary>
    private static (MediaTime Start, MediaTime? End) Enable(string text, string filter)
    {
        string s = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        string[] args = s.Length > 4 && s.EndsWith(')') && s.IndexOf('(') is int open and > 0 ? s[(open + 1)..^1].Split(',') : [];
        return (s[..Math.Max(0, s.IndexOf('('))], args) switch
        {
            ("between", ["t", string a, string b]) => (MediaTime.FromSeconds(Number(a, "enable")), MediaTime.FromSeconds(Number(b, "enable"))),
            ("gte", ["t", string a]) => (MediaTime.FromSeconds(Number(a, "enable")), null),
            ("lte", ["t", string b]) => (MediaTime.Zero, MediaTime.FromSeconds(Number(b, "enable"))),
            _ => throw FfmpegMode.Unsupported($"{filter} enable='{text}'", why: "enable takes between(t,a,b), gte(t,a) and lte(t,b)"),
        };
    }

    /// <summary>What a source holds, probed when the mapping needs it.</summary>
    private sealed class Source
    {
        public required int Input { get; init; }

        public required string Path { get; init; }

        public required bool Image { get; init; }

        public double? Length { get; set; }

        public (int Width, int Height)? Size { get; set; }

        public double Rate { get; set; } = 25;

        public Rational FrameRate { get; set; } = new(25, 1);

        public int SampleRate { get; set; } = 48000;

        public bool HasAudio { get; set; }

        public bool Probed { get; set; }
    }

    /// <summary>A stream of the graph: clips on a track with layers and effects over them.</summary>
    private sealed class Value
    {
        public List<(Clip Clip, Transition? Transition, Source Source, double Length)> Items { get; } = [];

        public List<Layer> Layers { get; } = [];

        /// <summary>Gets the source each layer comes from, in the order of <see cref="Layers"/>.</summary>
        public List<Source> LayerSources { get; } = [];

        public List<EditEffect> Effects { get; } = [];

        public (int Width, int Height)? Scale { get; set; }

        public Rational? Rate { get; set; }

        public ChromaKey? Key { get; set; }

        public bool Unmuted { get; set; }

        public double VideoSpeed { get; set; } = 1;

        public double AudioSpeed { get; set; } = 1;

        public int VideoLoops { get; set; } = 1;

        public int AudioLoops { get; set; } = 1;

        public bool VideoReversed { get; set; }

        public bool AudioReversed { get; set; }

        public bool HasAudio => Items.Any(i => i.Source.HasAudio);

        public double Length => Items.Count == 0 ? 0 : Items.Sum(i => i.Length) - Items.Sum(i => i.Transition?.Duration.Seconds ?? 0);
    }

    private sealed class Builder(FfmpegLine line, VideoSession session)
    {
        private readonly List<Source> _sources = [];
        private readonly Dictionary<string, Value> _labels = new(StringComparer.Ordinal);
        private readonly Dictionary<int, Value> _inputs = [];
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private readonly HashSet<string> _audioLabels = new(StringComparer.Ordinal);
        private string? _videoOut, _audioOut;

        public Mapped Build()
        {
            for (int i = 0; i < line.Inputs.Count; i++)
            {
                FfmpegInput input = line.Inputs[i];
                if (input.Format is not (null or "image2"))
                {
                    throw FfmpegMode.Unsupported($"-f {input.Format}", why: "with editing filters the inputs are files");
                }

                string extension = System.IO.Path.GetExtension(input.Path).ToLowerInvariant();
                _sources.Add(new Source { Input = i, Path = input.Path, Image = FfmpegTranslator.IsImage(extension) });
            }

            if (System.IO.Path.GetExtension(line.Output!).ToLowerInvariant() is ".wav" or ".flac" or ".w64" or ".oga" or ".opus" or ".mp3" or ".m4a" or ".aiff" or ".caf")
            {
                string first = line.AudioFilter is { } chain ? FfmpegTranslator.SplitChain(chain)[0].Split('=')[0].Trim() : "filters";
                throw FfmpegMode.Unsupported($"-af {first}", why: "editing filters render a picture; to a sound file only the detection filters are, to -f null");
            }

            bool videoOff = line.NoVideo;
            if (videoOff)
            {
                throw FfmpegMode.Unsupported("-vn", why: "editing filters render a picture; for sound alone use egami-video audio");
            }

            if (line.VideoFilter is { } vf)
            {
                Run(ParseGraph(vf, "v"), "-vf");
            }

            if (line.AudioFilter is { } af)
            {
                Run(ParseGraph(af, "a"), "-af");
            }

            if (line.ComplexFilter is { } complex)
            {
                if (line.VideoFilter is not null || line.AudioFilter is not null)
                {
                    throw FfmpegMode.Unsupported("-filter_complex with -vf or -af", why: "give one graph");
                }

                Run(ParseGraph(complex, "c"), "-filter_complex");
            }

            Value video = Final();

            // Whether the output has sound depends on the sources, which a graph of filters that need no probing never read.
            foreach ((_, _, Source source, _) in video.Items)
            {
                Probe(source);
            }

            // With no -map, ffmpeg takes the sound of the first input that has any: an overlay's, when the main clip has none.
            if (line.Maps.Count == 0 && !video.HasAudio && !video.Layers.Any(l => !l.Clip.IsMuted))
            {
                for (int i = 0; i < video.Layers.Count; i++)
                {
                    Probe(video.LayerSources[i]);
                    if (video.LayerSources[i].HasAudio)
                    {
                        video.Layers[i] = video.Layers[i] with { Clip = video.Layers[i].Clip with { IsMuted = false } };
                        break;
                    }
                }
            }

            bool audio = !line.NoAudio && AudioMapped() && (video.HasAudio || video.Layers.Any(l => !l.Clip.IsMuted));
            if (audio)
            {
                Check(video.VideoSpeed, video.AudioSpeed, "setpts and atempo", "egami-video changes the speed of picture and sound together; give setpts and atempo the same factor, or -an");
                Check(video.VideoLoops, video.AudioLoops, "loop and aloop", "egami-video loops picture and sound together; give both, or -an");
                Check(video.VideoReversed ? 1 : 0, video.AudioReversed ? 1 : 0, "reverse and areverse", "egami-video reverses picture and sound together; give both, or -an");
            }

            VideoEdit edit = new();
            if (video.Scale is not null || video.Rate is not null || line.Size is not null || line.Rate is not null)
            {
                (int w, int h) = video.Scale ?? (line.Size is { } s ? ParseSize(s) : SizeOf(video));
                RateOf(video);
                Rational rate = video.Rate ?? (line.Rate is { } r ? EncodeCommand.ParseRate(r) : video.Items[0].Source.FrameRate);
                edit.Canvas = new EditCanvas(w, h, rate) { Fit = ClipFit.Stretch };
            }

            foreach ((Clip clip, Transition? transition, _, _) in video.Items)
            {
                edit.Track.Add(clip, transition);
            }

            foreach (Layer layer in video.Layers)
            {
                edit.Layers.Add(layer);
            }

            foreach (EditEffect effect in video.Effects)
            {
                edit.Effects.Add(effect);
            }

            List<string> words = ["edit", EditFile, .. Output(line)];
            if (!audio && !line.NoAudio)
            {
                words.Add("--no-audio");
            }

            if (line.Progress is { } progress)
            {
                words.AddRange(["--progress", progress is "pipe:1" or "pipe:" or "-" ? "-" : progress]);
            }

            return new Mapped([.. words], edit.ToJson());
        }

        private static void Check(double video, double audio, string what, string why)
        {
            if (Math.Abs(video - audio) > 1e-9)
            {
                throw FfmpegMode.Unsupported(what, why: why);
            }
        }

        private bool AudioMapped()
        {
            if (line.Maps.Count == 0)
            {
                return true;
            }

            // A graph's sound output by its label, an input's sound (0:a, 1:a:0) or a whole input (-map 0).
            return line.Maps.Any(m => _audioLabels.Contains(m.Trim('[', ']')) || System.Text.RegularExpressions.Regex.IsMatch(m, @"^\d+(:a(:\d+)?)?\??$"));
        }

        /// <summary>The stream the output takes: the graph's one unconnected video output, or input 0.</summary>
        private Value Final()
        {
            if (_videoOut is not null)
            {
                return _labels[_videoOut];
            }

            return In(0);
        }

        private Value In(int index)
        {
            if (index >= _sources.Count)
            {
                throw new UsageException($"The ffmpeg graph names input {index}, but the line has {_sources.Count}.");
            }

            if (_inputs.TryGetValue(index, out Value? value))
            {
                return value;
            }

            Source source = _sources[index];
            FfmpegInput input = line.Inputs[index];
            Clip clip;
            double length;
            if (source.Image)
            {
                if (input.FrameRate is { } fr)
                {
                    source.FrameRate = EncodeCommand.ParseRate(fr);
                    source.Rate = source.FrameRate.ToDouble();
                }

                length = input.Duration is { } t ? Seconds(t, "-t") : input.Loop is not null ? double.NaN : 1 / source.Rate;

                clip = Clip.Still(source.Path, MediaTime.FromSeconds(double.IsNaN(length) ? 1 : length));
                if (input.Loop is { } loop && loop != "1")
                {
                    throw FfmpegMode.Unsupported($"-loop {loop}", why: "egami-video takes -loop 1 on an image");
                }
            }
            else
            {
                if (input.Loop is not null)
                {
                    throw FfmpegMode.Unsupported("-loop", why: "only on an image input");
                }

                clip = Clip.Open(source.Path);
                double? start = input.Start is { } ss ? Seconds(ss, "-ss") : null;
                double? end = input.To is { } to ? Seconds(to, "-to") : input.Duration is { } d ? (start ?? 0) + Seconds(d, "-t") : null;
                if (start is not null || end is not null)
                {
                    clip = clip.Trim(MediaTime.FromSeconds(start ?? 0), end is { } e ? MediaTime.FromSeconds(e) : null);
                }

                length = end is { } stop ? stop - (start ?? 0) : double.NaN;
                if (double.IsNaN(length))
                {
                    length = -1 - (start ?? 0);
                }
            }

            if (input.Offset is not null)
            {
                throw FfmpegMode.Unsupported("-itsoffset", why: "with editing filters, place clips with the graph");
            }

            value = new Value();
            value.Items.Add((clip, null, source, length));
            _inputs[index] = value;
            return value;
        }

        /// <summary>The length of an item, probing its source when needed (a negative stored length is minus 1 minus the start).</summary>
        private double LengthOf(Value value, int item)
        {
            (Clip clip, Transition? transition, Source source, double length) = value.Items[item];
            if (length >= 0)
            {
                return length;
            }

            Probe(source);
            if (source.Length is not { } full)
            {
                throw new UsageException($"ffmpeg: {source.Path} reports no duration, which the graph needs.");
            }

            double known = Math.Max(0, full - (-1 - length));
            value.Items[item] = (clip, transition, source, known);
            return known;
        }

        private double LengthOf(Value value)
        {
            for (int i = 0; i < value.Items.Count; i++)
            {
                LengthOf(value, i);
            }

            return value.Length;
        }

        private void Probe(Source source)
        {
            if (source.Probed)
            {
                return;
            }

            source.Probed = true;
            string path = session.ResolvePath(source.Path);
            try
            {
                if (source.Image)
                {
                    RasterInfo info = Raster.Probe(path);
                    source.Size = (info.Width, info.Height);
                    return;
                }

                MediaInfo media = MediaFile.Probe(path);
                source.Length = media.Duration?.Seconds;
                if (media.VideoTrack is { } track)
                {
                    source.Size = (track.Width, track.Height);
                    source.FrameRate = track.FrameRate ?? new Rational(25, 1);
                    source.Rate = source.FrameRate.ToDouble();
                }

                if (media.Tracks.OfType<AudioTrackInfo>().FirstOrDefault() is { } sound)
                {
                    source.HasAudio = true;
                    source.SampleRate = sound.SampleRate;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or NotSupportedException or InvalidDataException)
            {
                throw new UsageException($"ffmpeg: cannot read {source.Path}, which the graph needs to place or time its clips: {error.Message}");
            }
        }

        private (int Width, int Height) SizeOf(Value value)
        {
            if (value.Scale is { } scaled)
            {
                return scaled;
            }

            Source source = value.Items[0].Source;
            Probe(source);
            return source.Size ?? throw new UsageException($"ffmpeg: {source.Path} has no picture.");
        }

        private double RateOf(Value value)
        {
            Source source = value.Items[0].Source;
            if (!source.Image)
            {
                Probe(source);
            }

            return source.Rate;
        }

        private static (int, int) ParseSize(string text)
        {
            string[] parts = text.Split('x');
            return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int w) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int h)
                ? (w, h)
                : throw FfmpegMode.Unsupported($"size {text}", why: "give WxH");
        }

        private Value Get(string label)
        {
            if (_labels.TryGetValue(label, out Value? value))
            {
                if (!_used.Add(label))
                {
                    throw FfmpegMode.Unsupported($"[{label}] used twice", why: "egami-video takes each graph output once; split is not taken");
                }

                return value;
            }

            string[] parts = label.Split(':');
            return int.TryParse(parts[0], CultureInfo.InvariantCulture, out int index) && parts.Length <= 3
                ? In(index)
                : throw new UsageException($"The ffmpeg graph uses [{label}] before any filter makes it.");
        }

        private static bool IsAudioFilter(string name) => name is "afade" or "acrossfade" or "amix" or "atempo" or "volume" or "areverse" or "aloop" or "loudnorm";

        /// <summary>How many inputs a filter takes, and whether each comes with its sound (concat with a=1).</summary>
        private static (int Count, bool Pairs) Needed(GraphFilter filter)
        {
            string Option(string name, string fallback) =>
                filter.Args.Split(':').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim() == name) is { } found ? found[1].Trim() : fallback;
            int Count(string name, string fallback) => int.TryParse(Option(name, fallback), CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 2;
            return filter.Name switch
            {
                "xfade" or "overlay" or "acrossfade" or "freezeframes" => (2, false),
                "hstack" or "vstack" or "xstack" or "amix" => (Count("inputs", "2"), false),
                "concat" => (Count("n", "2"), Option("a", "0") == "1"),
                _ => (1, false),
            };
        }

        private void Run(List<GraphFilter> filters, string option)
        {
            int unlabeled = 0;
            foreach (GraphFilter filter in filters)
            {
                // A filter without input labels takes the next input files' streams, as ffmpeg does.
                if (filter.Inputs.Count == 0)
                {
                    bool sound = option == "-af" || IsAudioFilter(filter.Name);
                    (int count, bool pairs) = Needed(filter);
                    for (int i = 0; i < count; i++, unlabeled++)
                    {
                        filter.Inputs.Add($"{unlabeled}:{(sound ? "a" : "v")}");
                        if (pairs)
                        {
                            filter.Inputs.Add($"{unlabeled}:a");
                        }
                    }
                }

                List<Value> inputs = [.. filter.Inputs.Select(Get)];
                Value result = Apply(filter, inputs, option);

                // The labels that carry sound: an audio filter's, and concat's sound output after its picture.
                if (option == "-af" || IsAudioFilter(filter.Name))
                {
                    _audioLabels.UnionWith(filter.Outputs);
                }
                else if (filter.Name == "concat" && filter.Outputs.Count > 1)
                {
                    _audioLabels.UnionWith(filter.Outputs.Skip(1));
                    _audioOut = filter.Outputs[1];
                }
                foreach (string output in filter.Outputs)
                {
                    _labels[output] = result;
                }

                if (filter.Outputs[0].StartsWith("#end", StringComparison.Ordinal) || !filters.Any(f => f.Inputs.Contains(filter.Outputs[0])))
                {
                    if (option == "-af" || IsAudioFilter(filter.Name))
                    {
                        _audioOut = filter.Outputs[0];
                    }
                    else
                    {
                        _videoOut = filter.Outputs[0];
                    }
                }
            }
        }

        private static Value Single(GraphFilter filter, List<Value> inputs)
        {
            if (inputs.Count != 1)
            {
                throw FfmpegMode.Unsupported($"{filter.Name} with {inputs.Count} inputs", why: $"{filter.Name} takes one");
            }

            return inputs[0];
        }

        private static (Clip Clip, Transition? Transition, Source Source, double Length) OnlyItem(GraphFilter filter, Value value)
        {
            return value.Items.Count == 1 && value.Layers.Count == 0
                ? value.Items[0]
                : throw FfmpegMode.Unsupported($"{filter.Name} after xfade, concat or overlay", why: $"egami-video applies {filter.Name} to one clip");
        }

        private void Change(GraphFilter filter, Value value, Func<Clip, double, (Clip, double)> change)
        {
            (Clip clip, Transition? transition, Source source, _) = OnlyItem(filter, value);
            double length = LengthOf(value, 0);
            (Clip changed, double newLength) = change(clip, length);
            value.Items[0] = (changed, transition, source, newLength);
        }

        private Value Apply(GraphFilter filter, List<Value> inputs, string option)
        {
            switch (filter.Name)
            {
                case "fade" or "afade":
                    return Fade(filter, Single(filter, inputs));
                case "setpts":
                    {
                        Value value = Single(filter, inputs);
                        double factor = PtsFactor(filter.Args);
                        if (factor != 1)
                        {
                            value.VideoSpeed *= 1 / factor;
                            Change(filter, value, (c, l) => (c.Speed(c.SpeedFactor * (1 / factor)), l * factor));
                        }

                        return value;
                    }

                case "atempo":
                    {
                        Value value = Single(filter, inputs);
                        double tempo = Number(Named("atempo", filter.Args, ["tempo"], ["tempo"]).GetValueOrDefault("tempo", "1"), "atempo");
                        value.AudioSpeed *= tempo;
                        if (Math.Abs(value.AudioSpeed - value.VideoSpeed) > 1e-9 && value.VideoSpeed == 1)
                        {
                            // atempo first: the picture follows when setpts comes after it.
                            return value;
                        }

                        return value;
                    }

                case "reverse" or "areverse":
                    {
                        Value value = Single(filter, inputs);
                        Named(filter.Name, filter.Args, []);
                        bool already = value.VideoReversed || value.AudioReversed;
                        if (filter.Name == "reverse")
                        {
                            value.VideoReversed = true;
                        }
                        else
                        {
                            value.AudioReversed = true;
                        }

                        if (!already)
                        {
                            Change(filter, value, (c, l) => (c.Reverse(), l));
                        }

                        return value;
                    }

                case "loop" or "aloop":
                    return Loop(filter, Single(filter, inputs));
                case "tpad":
                    {
                        Value value = Single(filter, inputs);
                        Dictionary<string, string> o = Named("tpad", filter.Args, ["stop_mode", "stop_duration", "stop", "start_mode", "start_duration", "start", "color"]);
                        if (o.GetValueOrDefault("stop_mode") != "clone" || o.ContainsKey("start") || o.ContainsKey("start_duration"))
                        {
                            throw FfmpegMode.Unsupported($"tpad={filter.Args}", why: "egami-video holds the last frame: stop_mode=clone with stop_duration or stop");
                        }

                        double hold = o.TryGetValue("stop_duration", out string? d) ? Seconds(d, "tpad stop_duration") : Number(o.GetValueOrDefault("stop", "0"), "tpad stop") / RateOf(value);
                        Change(filter, value, (c, l) => (c.Hold(MediaTime.FromSeconds(hold)), l + hold));
                        return value;
                    }

                case "freezeframes":
                    throw FfmpegMode.Unsupported("freezeframes", why: "it replaces frames without adding time, and egami-video's freeze adds time; use tpad=stop_mode=clone, or egami-video freeze");
                case "loudnorm":
                    throw FfmpegMode.Unsupported("loudnorm", why: "egami-video has no loudness normalization in an edit yet");
                case "fps":
                    {
                        Value value = Single(filter, inputs);
                        value.Rate = EncodeCommand.ParseRate(Named("fps", filter.Args, ["fps"], ["fps"]).GetValueOrDefault("fps", "25"));
                        return value;
                    }

                case "scale":
                    {
                        Value value = Single(filter, inputs);
                        Dictionary<string, string> o = Named("scale", filter.Args, ["w", "h", "flags"], ["w", "h", "flags"], new() { ["width"] = "w", ["height"] = "h" });
                        (int w, int h) = SizeOf(value);
                        int width = Dimension(o.GetValueOrDefault("w", "-1"), w, h, true), height = Dimension(o.GetValueOrDefault("h", "-1"), w, h, false);
                        if (width < 0 && height < 0)
                        {
                            throw FfmpegMode.Unsupported($"scale={filter.Args}");
                        }

                        int even = o.GetValueOrDefault("w") == "-2" || o.GetValueOrDefault("h") == "-2" ? 2 : 1;
                        width = width < 0 ? (int)Math.Round(height * (double)w / h / even) * even : width;
                        height = height < 0 ? (int)Math.Round(width * (double)h / w / even) * even : height;
                        value.Scale = (width, height);
                        return value;
                    }

                case "hflip" or "vflip":
                    {
                        Value value = Single(filter, inputs);
                        Dictionary<string, string> o = Named(filter.Name, filter.Args, ["enable"]);
                        EditEffect effect = EditCommands.Effect(filter.Name == "hflip" ? "flip horizontal" : "flip vertical");
                        if (o.TryGetValue("enable", out string? enable))
                        {
                            (MediaTime start, MediaTime? end) = Enable(enable, filter.Name);
                            effect = effect.Between(start, end);
                        }

                        value.Effects.Add(effect);
                        return value;
                    }

                case "xfade":
                    return Xfade(filter, inputs);
                case "acrossfade":
                    {
                        if (inputs.Count != 2 || !ReferenceEquals(inputs[0], inputs[1]))
                        {
                            throw FfmpegMode.Unsupported("acrossfade", why: "egami-video crossfades the sound where an xfade joins the same two inputs; give xfade first");
                        }

                        return inputs[0];
                    }

                case "concat":
                    return Concat(filter, inputs);
                case "overlay":
                    return Overlay(filter, inputs);
                case "amix":
                    {
                        Named("amix", filter.Args, ["inputs", "duration", "dropout_transition", "normalize"]);
                        Value first = inputs[0];
                        foreach (Value other in inputs.Skip(1))
                        {
                            if (!ReferenceEquals(other, first))
                            {
                                throw FfmpegMode.Unsupported("amix", why: "egami-video mixes the sound of an overlay's inputs; give the overlay first");
                            }
                        }

                        first.Unmuted = true;
                        for (int i = 0; i < first.Layers.Count; i++)
                        {
                            first.Layers[i] = first.Layers[i] with { Clip = first.Layers[i].Clip with { IsMuted = false } };
                        }

                        return first;
                    }

                case "hstack" or "vstack" or "xstack":
                    return Stack(filter, inputs);
                case "zoompan":
                    return ZoomPan(filter, Single(filter, inputs));
                case "chromakey" or "colorkey":
                    {
                        Value value = Single(filter, inputs);
                        Dictionary<string, string> o = Named(filter.Name, filter.Args, ["color", "similarity", "blend", "yuv"], ["color", "similarity", "blend"]);
                        Rgba8 color = Color(o.GetValueOrDefault("color", "black"));
                        value.Key = new ChromaKey(color)
                        {
                            Similarity = (float)Number(o.GetValueOrDefault("similarity", "0.01"), "similarity"),
                            Blend = (float)Number(o.GetValueOrDefault("blend", "0"), "blend"),
                            Despill = 0,
                        };
                        return value;
                    }

                case "despill":
                    {
                        Value value = Single(filter, inputs);
                        Dictionary<string, string> o = Named("despill", filter.Args, ["type", "mix", "expand"], ["type", "mix", "expand"]);
                        if (value.Key is null)
                        {
                            throw FfmpegMode.Unsupported("despill", why: "egami-video despills a chroma key; give chromakey or colorkey first");
                        }

                        value.Key = value.Key with
                        {
                            Despill = (float)Number(o.GetValueOrDefault("mix", "0.5"), "despill mix"),
                            Expand = (float)Number(o.GetValueOrDefault("expand", "0"), "despill expand"),
                        };
                        return value;
                    }

                case "volume":
                    {
                        Value value = Single(filter, inputs);
                        string level = Named("volume", filter.Args, ["volume"], ["volume"]).GetValueOrDefault("volume", "1");
                        double db = level.EndsWith("dB", StringComparison.OrdinalIgnoreCase)
                            ? Number(level[..^2], "volume")
                            : 20 * Math.Log10(Math.Max(1e-9, Number(level, "volume")));
                        for (int i = 0; i < value.Items.Count; i++)
                        {
                            (Clip c, Transition? t, Source s, double l) = value.Items[i];
                            value.Items[i] = (c.Volume(c.VolumeDecibels + db), t, s, l);
                        }

                        return value;
                    }

                default:
                    throw FfmpegMode.Unsupported($"{option} {filter.Name}", why: $"the filters taken are {FilterList}, and those of a plain -vf ({string.Join(", ", ["scale", "crop", "pad", "transpose", "hflip", "vflip", "fps"])}) when no editing filter is in the line");
            }
        }

        private static int Dimension(string text, int w, int h, bool width)
        {
            if (text is "-1" or "-2")
            {
                return -1;
            }

            return (int)Math.Round(Evaluate(text, n => n switch
            {
                "iw" or "in_w" => w,
                "ih" or "in_h" => h,
                _ => null,
            }, "scale " + (width ? "w" : "h")));
        }

        private static double PtsFactor(string text)
        {
            string s = text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("expr=", string.Empty, StringComparison.Ordinal);
            if (s.EndsWith("-STARTPTS", StringComparison.Ordinal))
            {
                s = s[..^"-STARTPTS".Length];
                if (s.StartsWith('(') && s.EndsWith(')'))
                {
                    s = s[1..^1];
                }
            }

            double? factor = s switch
            {
                "PTS" => 1,
                _ when s.StartsWith("PTS/", StringComparison.Ordinal) && double.TryParse(s[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0 => 1 / d,
                _ when s.StartsWith("PTS*", StringComparison.Ordinal) && double.TryParse(s[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 => m,
                _ when s.EndsWith("*PTS", StringComparison.Ordinal) && double.TryParse(s[..^4], NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0 => m,
                _ => null,
            };
            return factor is { } f && 1 / f is >= 0.1 and <= 10
                ? f
                : throw FfmpegMode.Unsupported($"setpts={text}", why: "egami-video takes a constant factor, such as PTS/2 or 2*PTS, from 0.1x to 10x");
        }

        private Value Fade(GraphFilter filter, Value value)
        {
            bool audio = filter.Name == "afade";
            Dictionary<string, string> o = audio
                ? Named("afade", filter.Args, ["type", "start_sample", "nb_samples", "start_time", "duration", "curve"], ["type", "start_sample", "nb_samples"],
                    new() { ["t"] = "type", ["ss"] = "start_sample", ["ns"] = "nb_samples", ["st"] = "start_time", ["d"] = "duration", ["c"] = "curve" })
                : Named("fade", filter.Args, ["type", "start_frame", "nb_frames", "start_time", "duration", "color"], ["type", "start_frame", "nb_frames"],
                    new() { ["t"] = "type", ["s"] = "start_frame", ["n"] = "nb_frames", ["st"] = "start_time", ["d"] = "duration", ["c"] = "color" });
            if (audio && o.TryGetValue("curve", out string? curve) && curve != "tri")
            {
                throw FfmpegMode.Unsupported($"afade curve={curve}", why: "egami-video fades the sound with the picture, on the default curve");
            }

            bool fadeIn = o.GetValueOrDefault("type", "in") switch
            {
                "in" => true,
                "out" => false,
                string other => throw FfmpegMode.Unsupported($"{filter.Name} type={other}"),
            };
            double rate = audio ? value.Items[0].Source.SampleRate : RateOf(value);
            if (audio)
            {
                Probe(value.Items[0].Source);
                rate = value.Items[0].Source.SampleRate;
            }

            double start = o.TryGetValue("start_time", out string? st) ? Seconds(st, "start_time") : Number(o.GetValueOrDefault(audio ? "start_sample" : "start_frame", "0"), "start") / rate;
            double duration = o.TryGetValue("duration", out string? d) ? Seconds(d, "duration") : Number(o.GetValueOrDefault(audio ? "nb_samples" : "nb_frames", audio ? "44100" : "25"), "length") / rate;
            Rgba8? color = !audio && o.TryGetValue("color", out string? c) ? Color(c) : null;
            MediaTime length = MediaTime.FromSeconds(duration);
            int index = fadeIn ? 0 : value.Items.Count - 1;
            (Clip clip, Transition? transition, Source source, double itemLength) = value.Items[index];
            if (value.Layers.Count > 0)
            {
                throw FfmpegMode.Unsupported($"{filter.Name} after overlay", why: "egami-video fades the clips under an overlay; fade them before it");
            }

            if (fadeIn)
            {
                if (start > 0.0005)
                {
                    throw FfmpegMode.Unsupported($"{filter.Name} in with a start of {start:0.###} s", why: "egami-video fades in from the clip's start");
                }

                if (clip.FadeInDuration != MediaTime.Zero && Math.Abs(clip.FadeInDuration.Seconds - duration) > 0.0005)
                {
                    throw FfmpegMode.Unsupported("fade and afade of different lengths", why: "egami-video fades the picture and the sound together");
                }

                value.Items[index] = (clip.FadeIn(length, color), transition, source, itemLength);
                return value;
            }

            double total = LengthOf(value);
            if (Math.Abs(start + duration - total) > Math.Max(1 / RateOf(value), 0.05))
            {
                throw FfmpegMode.Unsupported($"{filter.Name} out ending at {start + duration:0.###} s", why: $"egami-video fades out to the clip's end, at {total:0.###} s");
            }

            if (clip.FadeOutDuration != MediaTime.Zero && Math.Abs(clip.FadeOutDuration.Seconds - duration) > 0.0005)
            {
                throw FfmpegMode.Unsupported("fade and afade of different lengths", why: "egami-video fades the picture and the sound together");
            }

            value.Items[index] = (clip.FadeOut(length, color), transition, source, itemLength);
            return value;
        }

        private Value Loop(GraphFilter filter, Value value)
        {
            bool audio = filter.Name == "aloop";
            Dictionary<string, string> o = Named(filter.Name, filter.Args, ["loop", "size", "start", "time"], ["loop", "size", "start"]);
            int count = (int)Number(o.GetValueOrDefault("loop", "0"), "loop");
            if (count < 0)
            {
                throw FfmpegMode.Unsupported($"{filter.Name} loop={count}", why: "egami-video plays a clip a number of times, not forever");
            }

            double length = LengthOf(value, 0) / value.Items[0].Clip.LoopCount;
            Probe(value.Items[0].Source);
            double units = audio ? value.Items[0].Source.SampleRate : RateOf(value);
            double size = Number(o.GetValueOrDefault("size", "0"), "loop size");
            if (Number(o.GetValueOrDefault("start", "0"), "loop start") != 0 || size < Math.Floor(length * units))
            {
                throw FfmpegMode.Unsupported($"{filter.Name}={filter.Args}", why: $"egami-video loops whole clips: start=0 and a size of at least {Math.Ceiling(length * units)} (the clip's {(audio ? "samples" : "frames")})");
            }

            bool first = value.VideoLoops == 1 && value.AudioLoops == 1;
            if (audio)
            {
                value.AudioLoops = count + 1;
            }
            else
            {
                value.VideoLoops = count + 1;
            }

            if (first)
            {
                Change(filter, value, (c, l) => (c.Loop(count + 1), l * (count + 1)));
            }

            return value;
        }

        private Value Xfade(GraphFilter filter, List<Value> inputs)
        {
            if (inputs.Count != 2)
            {
                throw FfmpegMode.Unsupported("xfade", why: "it joins two inputs");
            }

            Dictionary<string, string> o = Named("xfade", filter.Args, ["transition", "duration", "offset"]);
            double duration = Seconds(o.GetValueOrDefault("duration", "1"), "xfade duration");
            double offset = Seconds(o.GetValueOrDefault("offset", "0"), "xfade offset");
            Value a = inputs[0], b = inputs[1];
            if (a.Layers.Count > 0 || b.Layers.Count > 0 || b.Items.Count != 1)
            {
                throw FfmpegMode.Unsupported("xfade of an overlay or a joined clip", why: "egami-video joins clips one after another on its track");
            }

            double length = LengthOf(a);
            if (offset + duration > length + 0.0005)
            {
                throw FfmpegMode.Unsupported($"xfade offset={offset} with duration={duration}", why: $"the first input lasts {length:0.###} s");
            }

            if (offset + duration < length - 0.0005)
            {
                // The first input is cut where the transition ends, as xfade does.
                (Clip last, Transition? lastTransition, Source source, double lastLength) = a.Items[^1];
                double cut = lastLength - (length - (offset + duration));
                if (last.SpeedFactor != 1 || last.IsReversed || last.LoopCount != 1)
                {
                    throw FfmpegMode.Unsupported($"xfade offset={offset}", why: "egami-video cuts only an unchanged clip where a transition ends; set offset to the first input's length minus duration");
                }

                MediaTime from = last.In ?? MediaTime.Zero;
                a.Items[^1] = (last.Trim(from, MediaTime.FromSeconds(from.Seconds + cut)), lastTransition, source, cut);
            }

            string name = o.GetValueOrDefault("transition", "fade");
            Transition transition;
            try
            {
                transition = EditCommands.ParseTransition($"{name}:{duration.ToString(CultureInfo.InvariantCulture)}");
            }
            catch (UsageException)
            {
                throw FfmpegMode.Unsupported($"xfade transition={name}", why: "the transitions taken are fade, fadeblack, fadewhite, dissolve, wipeleft, wiperight, wipeup, wipedown, slideleft, slideright, slideup, slidedown, circleopen, circleclose, radial, zoomin, pixelize and hblur");
            }

            (Clip clip, _, Source bSource, _) = b.Items[0];
            double bLength = LengthOf(b, 0);
            a.Items.Add((clip, transition, bSource, bLength));
            Merge(a, b);
            return a;
        }

        private void Merge(Value into, Value from)
        {
            into.Effects.AddRange(from.Effects);
            foreach (int key in _inputs.Where(p => ReferenceEquals(p.Value, from)).Select(p => p.Key).ToList())
            {
                _inputs[key] = into;
            }

            foreach (string key in _labels.Where(p => ReferenceEquals(p.Value, from)).Select(p => p.Key).ToList())
            {
                _labels[key] = into;
            }
        }

        private Value Concat(GraphFilter filter, List<Value> inputs)
        {
            Dictionary<string, string> o = Named("concat", filter.Args, ["n", "v", "a", "unsafe"]);
            int n = (int)Number(o.GetValueOrDefault("n", "2"), "concat n"), v = (int)Number(o.GetValueOrDefault("v", "1"), "concat v"), a = (int)Number(o.GetValueOrDefault("a", "0"), "concat a");
            if (v != 1 || a > 1 || inputs.Count != n * (v + a))
            {
                throw FfmpegMode.Unsupported($"concat={filter.Args}", why: "egami-video joins clips with one picture and at most one sound each");
            }

            Value result = new();
            for (int i = 0; i < n; i++)
            {
                Value segment = inputs[i * (v + a)];
                if (a == 1 && !ReferenceEquals(inputs[(i * 2) + 1], segment))
                {
                    throw FfmpegMode.Unsupported("concat of one input's picture with another's sound", why: "egami-video joins clips with their own sound");
                }

                if (segment.Layers.Count > 0)
                {
                    throw FfmpegMode.Unsupported("concat of an overlay", why: "egami-video joins clips one after another on its track");
                }

                for (int j = 0; j < segment.Items.Count; j++)
                {
                    LengthOf(segment, j);
                    (Clip clip, Transition? transition, Source source, double length) = segment.Items[j];
                    result.Items.Add((clip, j == 0 ? null : transition, source, length));
                }

                result.Effects.AddRange(segment.Effects);
            }

            if (a == 0 && !line.NoAudio && result.HasAudio && line.Maps.Count > 0 && !line.Maps.Any(m => m.Contains(":a", StringComparison.Ordinal)))
            {
                return result;
            }

            return result;
        }

        private Value Overlay(GraphFilter filter, List<Value> inputs)
        {
            if (inputs.Count != 2)
            {
                throw FfmpegMode.Unsupported("overlay", why: "it takes a main and an overlay input");
            }

            Dictionary<string, string> o = Named("overlay", filter.Args, ["x", "y", "enable", "eof_action", "shortest", "format"], ["x", "y"]);
            Value main = inputs[0], top = inputs[1];
            if (top.Layers.Count > 0 || top.Items.Count != 1)
            {
                throw FfmpegMode.Unsupported("overlay of a joined clip or an overlay", why: "egami-video lays one clip over the track");
            }

            if (o.TryGetValue("shortest", out string? shortest) && shortest != "0" || o.TryGetValue("eof_action", out string? eof) && eof != "repeat")
            {
                throw FfmpegMode.Unsupported("overlay shortest and eof_action", why: "egami-video holds the overlay's last frame to the end of its time, as eof_action=repeat");
            }

            (Clip clip, _, Source source, _) = top.Items[0];
            if (source.Image)
            {
                clip = Clip.Still(source.Path, MediaTime.FromSeconds(LengthOf(main)));
            }

            if (!top.Unmuted)
            {
                clip = clip.Muted();
            }

            Layer layer = Layer.Of(clip);
            if (top.Key is { } key)
            {
                layer = layer.Key(key);
            }

            string x = o.GetValueOrDefault("x", "0").Replace(" ", string.Empty, StringComparison.Ordinal), y = o.GetValueOrDefault("y", "0").Replace(" ", string.Empty, StringComparison.Ordinal);
            if (top.Scale is null && AnchorOf(x, y) is { } placed)
            {
                layer = layer.Place(placed.Anchor, placed.Margin);
            }
            else
            {
                (int w, int h) = SizeOf(top);
                (int mainW, int mainH) = SizeOf(main);
                Func<string, double?> names = n => n switch
                {
                    "W" or "main_w" => mainW,
                    "H" or "main_h" => mainH,
                    "w" or "overlay_w" => w,
                    "h" or "overlay_h" => h,
                    _ => null,
                };
                int left = (int)Math.Round(Evaluate(x, names, "overlay x")), topEdge = (int)Math.Round(Evaluate(y, names, "overlay y"));
                layer = layer.Place(new Rect(left, topEdge, w, h));
            }

            if (o.TryGetValue("enable", out string? enable))
            {
                (MediaTime start, MediaTime? end) = Enable(enable, "overlay");
                layer = layer.Between(start, end);
            }

            main.Layers.Add(layer);
            main.LayerSources.Add(source);
            main.Effects.AddRange(top.Effects);
            Merge(main, top);
            return main;
        }

        /// <summary>The overlay positions that are an anchor: 0:0, W-w-m:m, (W-w)/2:(H-h)/2 and the like.</summary>
        private static (Anchor Anchor, int Margin)? AnchorOf(string x, string y)
        {
            (int? Edge, bool Centre)? Side(string text, string big, string small, string bigLong, string smallLong)
            {
                string t = text.Replace(bigLong, big, StringComparison.Ordinal).Replace(smallLong, small, StringComparison.Ordinal);
                if (int.TryParse(t, CultureInfo.InvariantCulture, out int near) && near >= 0)
                {
                    return (near, false);
                }

                if (t == $"({big}-{small})/2")
                {
                    return (null, true);
                }

                return t.StartsWith($"{big}-{small}-", StringComparison.Ordinal) && int.TryParse(t[(big.Length + small.Length + 2)..], CultureInfo.InvariantCulture, out int far)
                    ? (-far - 1, false)
                    : t == $"{big}-{small}" ? (-1, false) : null;
            }

            if (Side(x, "W", "w", "main_w", "overlay_w") is not { } h || Side(y, "H", "h", "main_h", "overlay_h") is not { } v)
            {
                return null;
            }

            int Margin(int? edge) => edge is int e ? (e < 0 ? -e - 1 : e) : 0;
            int horizontal = h.Centre ? 0 : h.Edge < 0 ? 1 : -1, vertical = v.Centre ? 0 : v.Edge < 0 ? 1 : -1;
            int? margin = null;
            foreach ((int? edge, bool centre) in new[] { h, v })
            {
                if (!centre)
                {
                    if (margin is not null && margin != Margin(edge))
                    {
                        return null;
                    }

                    margin = Margin(edge);
                }
            }

            Anchor anchor = (horizontal, vertical) switch
            {
                (0, 0) => Anchor.Center,
                (-1, -1) => Anchor.TopLeft,
                (0, -1) => Anchor.Top,
                (1, -1) => Anchor.TopRight,
                (-1, 0) => Anchor.Left,
                (1, 0) => Anchor.Right,
                (-1, 1) => Anchor.BottomLeft,
                (0, 1) => Anchor.Bottom,
                _ => Anchor.BottomRight,
            };
            return (anchor, margin ?? 0);
        }

        private Value Stack(GraphFilter filter, List<Value> inputs)
        {
            Dictionary<string, string> o = Named(filter.Name, filter.Args, ["inputs", "shortest", "layout", "grid", "fill"]);
            if (o.TryGetValue("shortest", out string? shortest) && shortest != "0")
            {
                throw FfmpegMode.Unsupported($"{filter.Name} shortest", why: "egami-video stacks to the longest input");
            }

            List<Clip> clips = [];
            foreach (Value value in inputs)
            {
                (Clip clip, _, _, _) = OnlyItem(filter, value);
                clips.Add(clip);
            }

            Clip stacked;
            if (filter.Name == "hstack")
            {
                stacked = Egami.Video.Editing.Stack.Horizontal(clips);
            }
            else if (filter.Name == "vstack")
            {
                stacked = Egami.Video.Editing.Stack.Vertical(clips);
            }
            else
            {
                string layout = o.GetValueOrDefault("layout", string.Empty);
                (int columns, int rows) = o.TryGetValue("grid", out string? grid) && grid.Split('x') is [string c, string r]
                    ? ((int)Number(c, "xstack grid"), (int)Number(r, "xstack grid"))
                    : layout switch
                    {
                        "0_0|w0_0" => (2, 1),
                        "0_0|0_h0" => (1, 2),
                        "0_0|w0_0|0_h0|w0_h0" => (2, 2),
                        "0_0|w0_0|w0+w1_0" => (3, 1),
                        _ => throw FfmpegMode.Unsupported($"xstack layout={layout}", why: "egami-video stacks in a grid: give grid=CxR, or the layout of one"),
                    };
                stacked = Egami.Video.Editing.Stack.Grid(columns, rows, clips);
            }

            Value result = new();
            double length = 0;
            for (int i = 0; i < inputs.Count; i++)
            {
                length = Math.Max(length, LengthOf(inputs[i], 0));
            }

            result.Items.Add((stacked, null, inputs[0].Items[0].Source, length));
            return result;
        }

        private static Value ZoomPan(GraphFilter filter, Value value)
        {
            Dictionary<string, string> o = Named("zoompan", filter.Args, ["zoom", "x", "y", "d", "s", "fps"], null, new() { ["z"] = "zoom" });
            (Clip clip, Transition? transition, Source source, _) = OnlyItem(filter, value);
            if (!source.Image)
            {
                throw FfmpegMode.Unsupported("zoompan on a video", why: "egami-video pans and zooms an image; for a clip use egami-video slideshow or the edit's PanZoom");
            }

            double rate = o.TryGetValue("fps", out string? fps) ? Number(fps, "zoompan fps") : 25;
            double frames = Number(o.GetValueOrDefault("d", "90"), "zoompan d");
            string z = o.GetValueOrDefault("zoom", "1").Replace(" ", string.Empty, StringComparison.Ordinal);
            double end;
            if (double.TryParse(z, NumberStyles.Float, CultureInfo.InvariantCulture, out double constant))
            {
                end = constant;
            }
            else
            {
                // zoom+k, or min(zoom+k,M) and max(...): grows by k a frame from 1.
                string inner = z.StartsWith("min(", StringComparison.Ordinal) && z.EndsWith(')') ? z[4..^1] : z;
                string[] parts = inner.Split(',');
                double cap = parts.Length == 2 ? Number(parts[1], "zoompan zoom") : double.MaxValue;
                string grow = parts[0].Replace("pzoom", "zoom", StringComparison.Ordinal);
                if (!grow.StartsWith("zoom+", StringComparison.Ordinal))
                {
                    throw FfmpegMode.Unsupported($"zoompan z='{z}'", why: "egami-video takes a constant zoom or a linear one: zoom+k or min(zoom+k,max)");
                }

                end = Math.Min(1 + (Number(grow[5..], "zoompan zoom") * (frames - 1)), cap);
            }

            bool centre(string axis, string expression, string size) => expression.Replace(" ", string.Empty, StringComparison.Ordinal) switch
            {
                "0" => false,
                var e when e == $"{size}/2-({size}/zoom/2)" || e == $"{size}/2-{size}/zoom/2" => true,
                var e => throw FfmpegMode.Unsupported($"zoompan {axis}='{e}'", why: $"egami-video takes 0 or the centre, {size}/2-({size}/zoom/2)"),
            };
            bool cx = centre("x", o.GetValueOrDefault("x", "0"), "iw"), cy = centre("y", o.GetValueOrDefault("y", "0"), "ih");
            System.Drawing.RectangleF Window(double zoom)
            {
                float size = (float)(1 / zoom);
                return new System.Drawing.RectangleF(cx ? (1 - size) / 2 : 0, cy ? (1 - size) / 2 : 0, size, size);
            }

            double length = frames / rate;
            Clip still = Clip.Still(source.Path, MediaTime.FromSeconds(length)).PanZoom(Window(z.StartsWith("zoom", StringComparison.Ordinal) || z.StartsWith("min", StringComparison.Ordinal) ? 1 : end), Window(end));
            value.Items[0] = (still, transition, source, length);
            value.Scale = o.TryGetValue("s", out string? s) ? ParseSize(s) : (1280, 720);
            value.Rate = EncodeCommand.ParseRate(rate.ToString(CultureInfo.InvariantCulture));
            return value;
        }
    }
}
