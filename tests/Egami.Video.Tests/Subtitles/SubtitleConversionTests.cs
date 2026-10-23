using Egami.Pixels;
using Egami.Video.Subtitles;

namespace Egami.Video.Tests.Subtitles;

/// <summary>Every feature through every target format: kept when the target has it, otherwise left out and reported once.</summary>
public class SubtitleConversionTests
{
    private static readonly Rational Ms = new(1, 1000);

    private static SubtitleCue Cue(params SubtitleSpan[] spans) => new(new MediaTime(1000, Ms), new MediaTime(2000, Ms), spans);

    private static readonly Dictionary<string, (Func<SubtitleDocument> Make, Func<SubtitleDocument, bool> Kept)> Features = new()
    {
        ["bold"] = (() => One(Cue(new SubtitleSpan("x") { Bold = true })), d => d.Cues[0].Spans[0].Bold),
        ["italic"] = (() => One(Cue(new SubtitleSpan("x") { Italic = true })), d => d.Cues[0].Spans[0].Italic),
        ["underline"] = (() => One(Cue(new SubtitleSpan("x") { Underline = true })), d => d.Cues[0].Spans[0].Underline),
        ["strikeout"] = (() => One(Cue(new SubtitleSpan("x") { Strikeout = true })), d => d.Cues[0].Spans[0].Strikeout),
        ["color"] = (() => One(Cue(new SubtitleSpan("x") { Color = new Rgba8(0, 128, 255, 255) })), d => d.Cues[0].Spans[0].Color == new Rgba8(0, 128, 255, 255)),
        ["font"] = (() => One(Cue(new SubtitleSpan("x") { Font = "Georgia", FontSize = 30 })), d => d.Cues[0].Spans[0] is { Font: "Georgia", FontSize: 30 }),
        ["voice"] = (() => One(Cue(new SubtitleSpan("x") { Voice = "Ann" })), d => d.Cues[0].Spans[0].Voice == "Ann" || d.Cues[0].Actor == "Ann"),
        ["classes"] = (() => One(Cue(new SubtitleSpan("x") { Classes = ["loud"] })), d => d.Cues[0].Spans[0].Classes.SequenceEqual(["loud"])),
        ["ruby"] = (() => One(Cue(new SubtitleSpan("x") { Ruby = "r" })), d => d.Cues[0].Spans[0].Ruby == "r"),
        ["karaoke"] = (() => One(Cue(new SubtitleSpan("a") { Timestamp = new MediaTime(1000, Ms) }, new SubtitleSpan("b") { Timestamp = new MediaTime(1500, Ms) })),
            d => d.Cues[0].Spans.Count == 2 && d.Cues[0].Spans[1].Timestamp == new MediaTime(1500, Ms)),
        ["other tags"] = (() => One(Cue(new SubtitleSpan("x") { OtherTags = "\\blur3" })), d => d.Cues[0].Spans[0].OtherTags == "\\blur3"),
        ["alignment"] = (() => One(Align(Cue(new SubtitleSpan("x")), SubtitleAlignment.TopLeft)),
            d => (d.Cues[0].Alignment ?? Derived(d.Cues[0])) == SubtitleAlignment.TopLeft),
        ["position"] = (() => One(Place(Cue(new SubtitleSpan("x")))), d => d.Cues[0].Position == new SubtitlePosition(96, 72) || d.Cues[0].WebVtt is { Position: 25, Line: 25 }),
        ["layer"] = (() => One(Layer(Cue(new SubtitleSpan("x")))), d => d.Cues[0].Layer == 3),
        ["styles"] = (() => Styled(), d => d.Styles.Any(s => s.Name == "Sign") && d.Cues[0].Style == "Sign"),
        ["regions"] = (() => Region(), d => d.Regions.Count == 1),
    };

    // What each target keeps; the rest must be reported as Subtitle.Dropped.
    private static readonly Dictionary<SubtitleFormat, string[]> Keeps = new()
    {
        [SubtitleFormat.Srt] = ["bold", "italic", "underline", "strikeout", "color", "font", "alignment"],
        [SubtitleFormat.WebVtt] = ["bold", "italic", "underline", "voice", "classes", "ruby", "karaoke", "alignment", "position", "regions"],
        [SubtitleFormat.Ass] = ["bold", "italic", "underline", "strikeout", "color", "font", "voice", "karaoke", "other tags", "alignment", "position", "layer", "styles"],
    };

    public static TheoryData<string, SubtitleFormat, SubtitleFormat> Matrix
    {
        get
        {
            TheoryData<string, SubtitleFormat, SubtitleFormat> data = [];
            foreach (string feature in Features.Keys)
            {
                foreach (SubtitleFormat from in (SubtitleFormat[])[SubtitleFormat.Srt, SubtitleFormat.WebVtt, SubtitleFormat.Ass])
                {
                    foreach (SubtitleFormat to in (SubtitleFormat[])[SubtitleFormat.Srt, SubtitleFormat.WebVtt, SubtitleFormat.Ass])
                    {
                        data.Add(feature, from, to);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void FeaturesConvertOrAreReported(string feature, SubtitleFormat from, SubtitleFormat to)
    {
        (Func<SubtitleDocument> make, Func<SubtitleDocument, bool> kept) = Features[feature];
        bool fromKeeps = Keeps[from].Contains(feature);

        // Start from the feature as the source format holds it, when it can.
        SubtitleDocument source = SubtitleDocument.Parse(make().ToText(from), from);

        if (!fromKeeps)
        {
            Assert.False(kept(source), $"{from} should not hold {feature}");
            return;
        }

        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        string text = source.ToText(to, context: context);
        SubtitleDocument result = SubtitleDocument.Parse(text, to);
        bool dropped = reported.Any(d => d.Id == "Subtitle.Dropped");
        if (Keeps[to].Contains(feature))
        {
            Assert.True(kept(result), $"{from} to {to} lost {feature}:\n{text}");
            Assert.False(dropped, string.Join("\n", reported.Select(d => d.Message)));
        }
        else
        {
            Assert.False(kept(result), $"{from} to {to} kept {feature}?\n{text}");
            Assert.True(dropped, $"{from} to {to} dropped {feature} without a report:\n{text}");
            Assert.Single(reported, d => d.Id == "Subtitle.Dropped");
        }

        Assert.Equal(source.Cues[0].PlainText, result.Cues[0].PlainText);
    }

    private static SubtitleDocument One(SubtitleCue cue)
    {
        SubtitleDocument document = new();
        document.Cues.Add(cue);
        return document;
    }

    private static SubtitleCue Align(SubtitleCue cue, SubtitleAlignment alignment)
    {
        cue.Alignment = alignment;
        return cue;
    }

    private static SubtitleCue Place(SubtitleCue cue)
    {
        cue.Alignment = SubtitleAlignment.BottomCenter;
        cue.Position = new SubtitlePosition(96, 72);
        return cue;
    }

    private static SubtitleCue Layer(SubtitleCue cue)
    {
        cue.Layer = 3;
        return cue;
    }

    private static SubtitleDocument Styled()
    {
        SubtitleDocument document = One(Cue(new SubtitleSpan("x")));
        document.Cues[0].Style = "Sign";
        document.Styles.Add(new SubtitleStyle("Default"));
        document.Styles.Add(new SubtitleStyle("Sign") { FontName = "Georgia" });
        return document;
    }

    private static SubtitleDocument Region()
    {
        SubtitleDocument document = One(Cue(new SubtitleSpan("x")));
        document.Regions.Add(new SubtitleRegion("r") { Width = 50 });
        return document;
    }

    [Fact]
    public void AssStylesShowInSrtAndWebVttAsTags()
    {
        // API review Q1: a bold or italic style shows as tags, as FFmpeg writes it; \b0 in a bold style turns it off.
        const string Text = "[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\n"
            + "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n"
            + "Style: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1\n"
            + "Style: Sign,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,-1,-1,0,0,100,100,0,0,1,2,2,2,10,10,10,1\n\n"
            + "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:01.00,0:00:02.00,Sign,,0,0,0,,Loud {\\b0}quiet{\\r}loud\n"
            + "Comment: 0,0:00:02.00,0:00:03.00,Default,,0,0,0,,A note\n"
            + "Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,{\\rSign}Signed\n";
        SubtitleDocument document = SubtitleDocument.Parse(Text, SubtitleFormat.Ass);
        Assert.Equal("1\n00:00:01,000 --> 00:00:02,000\n<b><i>Loud </i></b><i>quiet</i><b><i>loud</i></b>\n\n2\n00:00:03,000 --> 00:00:04,000\n<b><i>Signed</i></b>\n\n",
            document.ToText(SubtitleFormat.Srt, new SubtitleWriterOptions { NewLine = "\n" }));
        Assert.Contains("<b><i>Signed</i></b>", document.ToText(SubtitleFormat.WebVtt), StringComparison.Ordinal);

        // Written again as ASS, edited cues keep to their style: no tags for what the style already does.
        document.Cues[0].Spans[0] = document.Cues[0].Spans[0] with { Text = "LOUD " };
        document.Cues[2].Spans.Add(document.Cues[2].Spans[0] with { Text = " too", Bold = false, OtherTags = null });
        string ass = document.ToText(SubtitleFormat.Ass);
        Assert.Contains(",Sign,,0,0,0,,LOUD {\\b0}quiet{\\r}loud\n", ass, StringComparison.Ordinal);
        Assert.Contains(",Default,,0,0,0,,{\\rSign}Signed{\\b0} too\n", ass, StringComparison.Ordinal);
        SubtitleDocument back = SubtitleDocument.Parse(ass, SubtitleFormat.Ass);
        Assert.Equal(document.Cues.Select(c => c.Spans.Select(s => (s.Text, s.Bold, s.Italic)).ToArray()), back.Cues.Select(c => c.Spans.Select(s => (s.Text, s.Bold, s.Italic)).ToArray()));
    }

    private static SubtitleAlignment? Derived(SubtitleCue cue) => cue.WebVtt is null ? null : cue.WebVtt switch
    {
        { Line: 0, Align: WebVttAlign.Left } => SubtitleAlignment.TopLeft,
        _ => null,
    };
}
