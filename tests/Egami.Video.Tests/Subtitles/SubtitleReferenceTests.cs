using System.Globalization;
using System.Text;

using Egami.Video.Subtitles;

namespace Egami.Video.Tests.Subtitles;

/// <summary>
/// The FFmpeg conversion set (V1.2 Build Plan task 12, tools/video/make-subtitle-references.py): 25 sources converted to the other
/// two formats, 50 conversions. Egami.Video's conversion must give FFmpeg's cues (times, text, and bold, italic and underline runs),
/// apart from the differences listed in <see cref="Known"/>, each a place where FFmpeg loses or changes what the source says.
/// </summary>
public sealed class SubtitleReferenceTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "corpus", "subtitles", "references");

    /// <summary>Conversions whose cues differ from FFmpeg's, and why; the test fails when one of them stops differing.</summary>
    private static readonly Dictionary<string, string> Known = new()
    {
        ["vtt-entities.srt"] = "FFmpeg writes a no-break space as ASS's \\h code into SRT; Egami.Video writes the character",
        ["srt-ampersand.vtt"] = "FFmpeg copies '< 3 >' into WebVTT, where it opens a tag and the text is lost; Egami.Video writes &lt;",
        ["srt-hours.ass"] = "FFmpeg clips times past 9:59:59.99 to it, ASS's one hour digit; Egami.Video writes 10:00:00.00, which ASS readers take",
        ["vtt-hours.ass"] = "FFmpeg clips times past 9:59:59.99 to it, ASS's one hour digit; Egami.Video writes 10:00:00.00, which ASS readers take",
    };

    public static TheoryData<string> Conversions => [.. Directory.GetFiles(Path.Combine(Root, "ffmpeg")).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    [Fact]
    public void TheSetHasFiftyConversions() => Assert.Equal(50, Conversions.Count);

    [Theory]
    [MemberData(nameof(Conversions))]
    public void ConversionGivesFfmpegsCues(string conversion)
    {
        string source = Directory.GetFiles(Path.Combine(Root, "sources"), Path.GetFileNameWithoutExtension(conversion) + ".*").Single();
        SubtitleFormat target = Path.GetExtension(conversion) switch { ".srt" => SubtitleFormat.Srt, ".vtt" => SubtitleFormat.WebVtt, _ => SubtitleFormat.Ass };
        MediaContext quiet = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = _ => { } } };
        string ours = SubtitleDocument.Load(source, context: quiet).ToText(target, context: quiet);
        string[] expected = Describe(SubtitleDocument.Parse(File.ReadAllText(Path.Combine(Root, "ffmpeg", conversion)), target, context: quiet));
        string[] actual = Describe(SubtitleDocument.Parse(ours, target, context: quiet));
        if (Known.TryGetValue(conversion, out string? why))
        {
            Assert.False(expected.SequenceEqual(actual), $"{conversion} now matches FFmpeg; take it off the known list ({why}).");
            return;
        }

        Assert.Equal(expected, actual);
    }

    /// <summary>Each cue as its times and text, with [b], [i] and [u] before runs in bold, italics and underline.</summary>
    private static string[] Describe(SubtitleDocument document)
    {
        return [.. document.Cues.Where(c => !c.IsComment).Select(cue =>
        {
            StringBuilder text = new();
            foreach (SubtitleSpan span in cue.Spans.Where(s => s.Text.Length > 0))
            {
                string flags = (span.Bold ? "b" : "") + (span.Italic ? "i" : "") + (span.Underline ? "u" : "");
                text.Append(flags.Length > 0 ? $"[{flags}]" : "").Append(span.Text);
            }

            return string.Create(CultureInfo.InvariantCulture, $"{cue.Start.Seconds:F3}-{cue.End.Seconds:F3} {text.ToString().ReplaceLineEndings("|")}");
        })];
    }
}
