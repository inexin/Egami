using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Egami.Video.Subtitles;

namespace Egami.Video.Tests.Subtitles;

/// <summary>
/// The web-platform-tests WebVTT parsing suite (BSD-3-Clause), fetched by tools/video/fetch-webvtt-tests.py into
/// .cache/video/wpt: the cue text tree tests compare our tree with the expected DOM, and the file parsing tests run their own
/// assertions in Node.js against the cues we read, shaped like the browser's VTTCue.
/// </summary>
public class WebVttConformanceTests
{
    private static string? Suite
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, ".cache", "video", "wpt", "webvtt", "parsing");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    public static TheoryData<string> CueTextFiles => [.. new[] { "entities", "tags", "text", "timestamps", "tree-building" }];

    [Theory]
    [MemberData(nameof(CueTextFiles))]
    public void CueTextTreesMatch(string file)
    {
        string? suite = Suite;
        Assert.SkipWhen(suite is null, "Run python tools/video/fetch-webvtt-tests.py to fetch the web-platform-tests WebVTT suite.");
        List<string> failures = [];
        int count = 0;
        foreach ((string input, string expected) in ReadDat(Path.Combine(suite, "cue-text-parsing", "dat", file + ".dat")))
        {
            count++;
            // The suite runs each input as the text of a cue in a file, so a blank line ends it and NULs are replaced.
            SubtitleDocument document = SubtitleDocument.Parse("WEBVTT\n\n00:00.000 --> 00:01.000\n" + input, SubtitleFormat.WebVtt);
            string actual = document.Cues.Count == 0 ? "" : Serialize(WebVttCueText.ParseTree(document.Cues[0].RawText!));
            if (actual != expected)
            {
                failures.Add($"input {JsonSerializer.Serialize(input)}\nexpected:\n{expected}\nactual:\n{actual}");
            }
        }

        Assert.True(count > 0);
        Assert.True(failures.Count == 0, $"{failures.Count} of {count} failed:\n\n" + string.Join("\n\n", failures));
    }

    public static TheoryData<string> FileParsingTests
    {
        get
        {
            TheoryData<string> data = [];
            if (Suite is { } suite)
            {
                foreach (string test in Directory.GetFiles(Path.Combine(suite, "file-parsing", "support"), "*.test").Order(StringComparer.Ordinal))
                {
                    data.Add(Path.GetFileNameWithoutExtension(test));
                }

                foreach (string vtt in Directory.GetFiles(Path.Combine(suite, "file-parsing", "support"), "*.vtt").Order(StringComparer.Ordinal))
                {
                    data.Add("error:" + Path.GetFileNameWithoutExtension(vtt));
                }
            }
            else
            {
                data.Add("missing");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FileParsingTests))]
    public void FileParsingMatchesBrowsers(string name)
    {
        string? suite = Suite;
        Assert.SkipWhen(suite is null, "Run python tools/video/fetch-webvtt-tests.py to fetch the web-platform-tests WebVTT suite.");
        string support = Path.Combine(suite, "file-parsing", "support");
        if (name.StartsWith("error:", StringComparison.Ordinal))
        {
            // A file that fails the signature check gives an error event in browsers, and an exception here.
            byte[] bad = File.ReadAllBytes(Path.Combine(support, name[6..] + ".vtt"));
            if (name.EndsWith("empty", StringComparison.Ordinal))
            {
                Assert.ThrowsAny<EgamiException>(() => SubtitleDocument.Load(bad, new SubtitleReaderOptions { Format = SubtitleFormat.WebVtt }));
                return;
            }

            Assert.ThrowsAny<EgamiException>(() => SubtitleDocument.Load(bad, new SubtitleReaderOptions { Format = SubtitleFormat.WebVtt }));
            return;
        }

        string node = FindNode();
        Assert.SkipWhen(node.Length == 0, "Node.js is not on the PATH.");
        string source = File.ReadAllText(Path.Combine(support, name + ".test")).ReplaceLineEndings("\n");
        string body = source.Split("\n\n", 2)[1].Split("\n===\n", 2)[0];
        byte[] vttBytes = File.ReadAllBytes(Path.Combine(suite, "file-parsing", "tests", "support", name + ".vtt"));
        SubtitleDocument document = SubtitleDocument.Load(vttBytes, new SubtitleReaderOptions { Format = SubtitleFormat.WebVtt });
        string script = Harness(Dump(document), body);
        string path = Path.Combine(Path.GetTempPath(), $"egami-wpt-{name}-{Environment.ProcessId}.js");
        File.WriteAllText(path, script);
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(node, path) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0 && output.Contains("PASS", StringComparison.Ordinal), output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string FindNode()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (string exe in (string[])["node", "node.exe"])
            {
                string candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return "";
    }

    private static string Dump(SubtitleDocument document)
    {
        StringBuilder json = new("{\"regions\":[");
        json.AppendJoin(',', document.Regions.Select(r => string.Create(CultureInfo.InvariantCulture,
            $"{{\"id\":{JsonSerializer.Serialize(r.Id)},\"width\":{N(r.Width)},\"lines\":{r.Lines},\"regionAnchorX\":{N(r.RegionAnchorX)},\"regionAnchorY\":{N(r.RegionAnchorY)},\"viewportAnchorX\":{N(r.ViewportAnchorX)},\"viewportAnchorY\":{N(r.ViewportAnchorY)},\"scroll\":\"{(r.ScrollUp ? "up" : "")}\"}}")));
        json.Append("],\"cues\":[");
        json.AppendJoin(',', document.Cues.Select(c =>
        {
            WebVttCueSettings s = c.WebVtt ?? new WebVttCueSettings();
            int region = s.Region is { } id ? document.Regions.FindLastIndex(r => r.Id == id) : -1;
            return string.Create(CultureInfo.InvariantCulture,
                $"{{\"id\":{JsonSerializer.Serialize(c.Id ?? "")},\"startTime\":{Seconds(c.Start)},\"endTime\":{Seconds(c.End)},\"text\":{JsonSerializer.Serialize(c.RawText)},\"region\":{(region < 0 ? "null" : region.ToString(CultureInfo.InvariantCulture))},\"vertical\":\"{s.Vertical switch { WebVttVertical.RightToLeft => "rl", WebVttVertical.LeftToRight => "lr", _ => "" }}\",\"snapToLines\":{(s.SnapToLines ? "true" : "false")},\"line\":{(s.Line is { } l ? N(l) : "\"auto\"")},\"lineAlign\":\"{s.LineAlign.ToString().ToLowerInvariant()}\",\"position\":{(s.Position is { } p ? N(p) : "\"auto\"")},\"positionAlign\":\"{s.PositionAlign switch { WebVttPositionAlign.LineLeft => "line-left", WebVttPositionAlign.LineRight => "line-right", WebVttPositionAlign.Center => "center", _ => "auto" }}\",\"size\":{N(s.Size)},\"align\":\"{s.Align.ToString().ToLowerInvariant()}\"}}");
        }));
        json.Append("]}");
        return json.ToString();

        // As the W3C algorithm adds them: hours, minutes and seconds, then the milliseconds over 1000.
        static string Seconds(MediaTime t)
        {
            long ms = t.Rescale(new Rational(1, 1000)).Ticks;
            return N((double)(ms / 1000) + ((ms % 1000) / 1000.0));
        }

        static string N(double v) => double.IsNegative(v) && v == 0 ? "-0" : v.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Harness(string json, string body)
    {
        return $$"""
            const data = {{json}};
            const cues = data.cues.map(c => Object.assign({}, c, { region: c.region === null ? null : data.regions[c.region] }));
            const document = { styleSheets: { length: 0 } };
            function fail(m, d) { throw new Error((m || '') + ' ' + d); }
            function assert_equals(a, b, m) { if (!Object.is(a, b)) fail(m, JSON.stringify(a) + ' !== ' + JSON.stringify(b)); }
            function assert_not_equals(a, b, m) { if (Object.is(a, b)) fail(m, JSON.stringify(a) + ' === ' + JSON.stringify(b)); }
            function assert_true(a, m) { if (a !== true) fail(m, 'not true'); }
            function assert_false(a, m) { if (a !== false) fail(m, 'not false'); }
            function assert_unreached(m) { fail(m, 'unreached'); }
            {
            {{body}}
            }
            console.log('PASS');
            """;
    }

    /// <summary>Reads an html5lib-style .dat file: each test's input and its expected tree, with Python escapes decoded.</summary>
    private static IEnumerable<(string Input, string Expected)> ReadDat(string path)
    {
        string[] lines = File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n');
        string state = "";
        StringBuilder input = new(), expected = new();
        foreach (string line in lines.Append("#data"))
        {
            if (line.StartsWith('#'))
            {
                if (line == "#data" && state != "")
                {
                    yield return (Unescape(input.ToString().TrimEnd('\n')), Unescape(expected.ToString().TrimEnd('\n')));
                    input.Clear();
                    expected.Clear();
                }

                state = line;
                continue;
            }

            if (state == "#data")
            {
                input.Append(line).Append('\n');
            }
            else if (state == "#document-fragment" && line.Length > 0)
            {
                expected.Append(line).Append('\n');
            }
        }
    }

    private static string Unescape(string text)
    {
        StringBuilder output = new();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                output.Append(text[i]);
                continue;
            }

            char c = text[++i];
            switch (c)
            {
                case 'n': output.Append('\n'); break;
                case 't': output.Append('\t'); break;
                case 'r': output.Append('\r'); break;
                case 'f': output.Append('\f'); break;
                case 'v': output.Append('\v'); break;
                case '0': output.Append('\0'); break;
                case '\\': output.Append('\\'); break;
                case 'x':
                    output.Append((char)int.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                case 'u':
                    output.Append((char)int.Parse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default:
                    output.Append('\\').Append(c);
                    break;
            }
        }

        return output.ToString();
    }

    private static string Serialize(VttNode root)
    {
        StringBuilder output = new();
        void Write(VttNode node, int depth)
        {
            string indent = "| " + new string(' ', depth * 2);
            switch (node.Kind)
            {
                case "#text":
                    output.Append(indent).Append('"').Append(node.Text).Append("\"\n");
                    return;
                case "#timestamp":
                    output.Append(indent).Append("<?timestamp ").Append(WebVttFormat.FormatTime(node.Time)).Append(">\n");
                    return;
            }

            string name = node.Kind is "c" or "v" or "lang" ? "span" : node.Kind;
            output.Append(indent).Append('<').Append(name).Append(">\n");
            string attributes = new(' ', (depth + 1) * 2);
            if (node.Classes.Count > 0)
            {
                output.Append("| ").Append(attributes).Append("class=\"").AppendJoin(' ', node.Classes).Append("\"\n");
            }

            if (node.Kind == "lang")
            {
                output.Append("| ").Append(attributes).Append("lang=\"").Append(node.Annotation).Append("\"\n");
            }

            if (node.Kind == "v")
            {
                output.Append("| ").Append(attributes).Append("title=\"").Append(node.Annotation).Append("\"\n");
            }

            foreach (VttNode child in node.Children)
            {
                Write(child, depth + 1);
            }
        }

        foreach (VttNode child in root.Children)
        {
            Write(child, 0);
        }

        return output.ToString().TrimEnd('\n');
    }
}
