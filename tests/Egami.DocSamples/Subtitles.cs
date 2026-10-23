using System.Text;

using Egami.Pixels;
using Egami.Video;
using Egami.Video.Subtitles;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Subtitles
{
    private const string Srt = "1\r\n00:00:01,000 --> 00:00:03,500\r\n<i>Where were you?</i>\r\n\r\n2\r\n00:00:04,000 --> 00:00:06,000\r\nOut.\r\n";

    [Fact]
    public void LoadAndSave()
    {
        using SampleFiles files = new();
        File.WriteAllText("movie.srt", Srt);

        #region subtitles-load
        // SRT, WebVTT, ASS and SSA, told apart by their content. Saving an unchanged file gives back its exact bytes; the format
        // comes from the argument, then the extension, then the format it was read as.
        SubtitleDocument subtitles = SubtitleDocument.Load("movie.srt");
        foreach (SubtitleCue cue in subtitles.Cues)
        {
            Console.WriteLine($"{cue.Start.ToTimeSpan():g} {cue.PlainText}");
        }

        subtitles.Save("movie.vtt");
        subtitles.Save("movie.ass");
        #endregion

        Assert.Equal(File.ReadAllBytes("movie.srt"), Encoding.UTF8.GetBytes(SubtitleDocument.Load("movie.srt").ToText()));
        Assert.StartsWith("WEBVTT", File.ReadAllText("movie.vtt"), StringComparison.Ordinal);
        Assert.Contains("Dialogue: 0,0:00:01.00,0:00:03.50,Default,,0,0,0,,{\\i1}Where were you?", File.ReadAllText("movie.ass"), StringComparison.Ordinal);
    }

    [Fact]
    public void Cues()
    {
        SubtitleDocument subtitles = SubtitleDocument.Parse(Srt);

        #region subtitles-cues
        // Cues hold spans of styled text. Times are exact: shifting by a frame or scaling 23.976 to 25 fps keeps the fraction.
        subtitles.Cues.Add(new SubtitleCue(MediaTime.FromSeconds(7), MediaTime.FromSeconds(9),
        [
            new SubtitleSpan("Out "),
            new SubtitleSpan("where?") { Bold = true, Color = new Rgba8(255, 255, 0, 255) },
        ]));

        subtitles.Shift(MediaTime.FromSeconds(-0.5));
        subtitles.Scale(new Rational(24000, 25025));
        subtitles.Clip(MediaTime.Zero, MediaTime.FromSeconds(8));
        subtitles.FixOverlaps();
        string srt = subtitles.ToText(SubtitleFormat.Srt);
        #endregion

        Assert.Contains("Out <font color=\"#ffff00\"><b>where?</b></font>", srt, StringComparison.Ordinal);
        Assert.Equal(3, subtitles.Cues.Count);
    }

    [Fact]
    public void Ass()
    {
        const string script = """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 1920
            PlayResY: 1080

            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Sign,Arial,60,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,3,0,8,10,10,10,1

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:04.00,Sign,,0,0,0,,{\pos(960,100)\blur2}MARKET {\c&H00FFFF&}STREET
            """;

        #region subtitles-ass
        // ASS keeps its styles, layers, margins and every override tag: the ones Egami understands become fields, the rest stay
        // as written in OtherTags, so a script edited here and saved again still animates.
        SubtitleDocument sign = SubtitleDocument.Parse(script);
        SubtitleCue cue = sign.Cues[0];
        Console.WriteLine($"{cue.Style} at {cue.Position}, {sign.Styles[0].FontSize} pt");
        foreach (SubtitleSpan span in cue.Spans)
        {
            Console.WriteLine($"'{span.Text}' color {span.Color} tags {span.OtherTags}");
        }

        cue.End += MediaTime.FromSeconds(1);
        string saved = sign.ToText();
        #endregion

        Assert.Equal(new SubtitlePosition(960, 100), cue.Position);
        Assert.Equal("\\blur2", cue.Spans[0].OtherTags);
        Assert.Contains("0:00:01.00,0:00:05.00,Sign,,0,0,0,,{\\pos(960,100)\\blur2}MARKET {\\c&H00FFFF&}STREET", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void WebVtt()
    {
        const string vtt = """
            WEBVTT

            REGION
            id:chat
            width:40%
            lines:3
            scroll:up

            00:00.000 --> 00:02.000 region:chat align:left
            <v Ana>Hi <c.loud>there</c></v>

            00:02.500 --> 00:04.000 line:10% position:20%,line-left
            <ruby>漢<rt>kan</rt></ruby>
            """;

        #region subtitles-webvtt
        // WebVTT is read as the W3C spec says, the way browsers do: cue settings, regions, voices, classes, ruby and karaoke
        // timestamps. Writing to another format reports what it cannot keep as Subtitle.Dropped, once per kind.
        SubtitleDocument captions = SubtitleDocument.Parse(vtt);
        WebVttCueSettings settings = captions.Cues[0].WebVtt!;
        Console.WriteLine($"{settings.Region} {settings.Align}; {captions.Regions[0].Lines} lines");
        Console.WriteLine($"{captions.Cues[0].Spans[0].Voice}: {captions.Cues[0].PlainText}");

        captions.Cues[1].WebVtt = captions.Cues[1].WebVtt! with { Line = 85 };
        string ass = captions.ToText(SubtitleFormat.Ass);
        #endregion

        Assert.Equal("chat", settings.Region);
        Assert.Equal(WebVttAlign.Left, settings.Align);
        Assert.Equal(["loud"], captions.Cues[0].Spans[1].Classes);
        Assert.Equal("kan", captions.Cues[1].Spans[0].Ruby);
        Assert.Contains("Dialogue:", ass, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodingAndDiagnostics()
    {
        using SampleFiles files = new();
        File.WriteAllBytes("old.srt", [.. "1\r\n00:00:01,000 --> 00:00:02,000\r\nCaf"u8, 0xE9, .. "\r\n"u8]);
        List<string> log = [];

        #region subtitles-encoding
        // Files without a byte order mark that are not UTF-8 are read with the encoding you name, or else as Windows-1252, with a
        // Subtitle.Encoding warning. Output is always UTF-8. Strict turns every repaired line into an exception.
        MediaContext context = new() { Egami = EgamiContext.Default with { Diagnostics = d => log.Add($"{d.Id}: {d.Message}") } };
        SubtitleDocument old = SubtitleDocument.Load("old.srt", context: context);
        SubtitleDocument latin1 = SubtitleDocument.Load("old.srt", new SubtitleReaderOptions { Encoding = Encoding.Latin1 });
        Console.WriteLine($"{old.Cues[0].PlainText} / {latin1.Cues[0].PlainText}");

        try
        {
            SubtitleDocument.Parse("1\n00:00:01.000 --> 00:00:02.000\nHi\n", options: new SubtitleReaderOptions { Strict = true });
        }
        catch (CorruptDataException e)
        {
            Console.WriteLine(e.Message);
        }
        #endregion

        Assert.Equal("Café", old.Cues[0].PlainText);
        Assert.StartsWith("Subtitle.Encoding", Assert.Single(log), StringComparison.Ordinal);
    }
}
