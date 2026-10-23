using System.Text;

using Egami.Pixels;
using Egami.Video.Subtitles;

namespace Egami.Video.Tests.Subtitles;

public class SubtitleFormatTests
{
    private static readonly Rational Ms = new(1, 1000);

    private static string PathOf(string name) => Corpus.PathOf(Path.Combine("subtitles", name));

    private static MediaTime At(long ms) => new(ms, Ms);

    private static (MediaContext Context, List<EgamiDiagnostic> Reported) Capture()
    {
        List<EgamiDiagnostic> reported = [];
        return (MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } }, reported);
    }

    [Theory]
    [InlineData("messy.srt", SubtitleFormat.Srt)]
    [InlineData("clean.srt", SubtitleFormat.Srt)]
    [InlineData("features.vtt", SubtitleFormat.WebVtt)]
    [InlineData("features.ass", SubtitleFormat.Ass)]
    public void UnchangedRoundTripsGiveTheSameBytes(string name, SubtitleFormat format)
    {
        byte[] original = File.ReadAllBytes(PathOf(name));
        SubtitleDocument document = SubtitleDocument.Load(PathOf(name));
        Assert.Equal(format, document.Format);
        using MemoryStream output = new();
        document.Save(output);
        Assert.Equal(Encoding.UTF8.GetString(original), Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public void ByteOrderMarkSurvivesARoundTrip()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. File.ReadAllBytes(PathOf("clean.srt"))];
        SubtitleDocument document = SubtitleDocument.Load(original);
        using MemoryStream output = new();
        document.Save(output);
        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public void AnEditedCueIsRewrittenAndTheOthersKeepTheirBytes()
    {
        string original = File.ReadAllText(PathOf("clean.srt"));
        SubtitleDocument document = SubtitleDocument.Parse(original);
        document.Cues[2].Spans[0] = new SubtitleSpan("Changed") { Italic = true };
        string written = document.ToText();
        Assert.Equal(original.Replace("Line 3", "<i>Changed</i>"), written);
    }

    [Fact]
    public void SrtRepairsAreReportedOncePerFix()
    {
        (MediaContext context, List<EgamiDiagnostic> reported) = Capture();
        SubtitleDocument document = SubtitleDocument.Load(PathOf("messy.srt"), context: context);
        string[] messages = [.. reported.Where(d => d.Id == "Subtitle.Repaired").Select(d => d.Message)];
        Assert.Contains(messages, m => m.Contains("'.'", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("blank line is missing", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("no number", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("not part of a cue", StringComparison.Ordinal));
        Assert.Equal(6, document.Cues.Count);
        Assert.Equal(At(4000), document.Cues[1].Start);
        Assert.Equal(SubtitleAlignment.TopCenter, document.Cues[1].Alignment);
        Assert.Equal("Top line\nsecond red", document.Cues[1].PlainText);
        Assert.Equal(new Rgba8(255, 0, 0, 255), document.Cues[1].Spans.Single(s => s.Text == "red").Color);
        Assert.True(document.Cues[1].Spans[0].Bold);
        Assert.Equal("No blank line before me", document.Cues[2].PlainText);
        Assert.Null(document.Cues[3].Id);
        Assert.Equal(At(11500), document.Cues[4].Start);
        Assert.Equal(At(12250), document.Cues[4].End);
        SubtitleSpan[] last = [.. document.Cues[5].Spans];
        Assert.True(last[0].Underline);
        Assert.True(last.Single(s => s.Text == "strike").Strikeout);
        Assert.Equal(("Arial", 20.0), (last[^1].Font, last[^1].FontSize!.Value));
    }

    [Theory]
    [InlineData("1\n00:00:01.000 --> 00:00:02,000\nx\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nx\n2\n00:00:03,000 --> 00:00:04,000\ny\n")]
    [InlineData("00:00:01,000 --> 00:00:02,000\nx\n")]
    [InlineData("junk\n\n1\n00:00:01,000 --> 00:00:02,000\nx\n")]
    public void StrictSrtThrowsInsteadOfRepairing(string text)
    {
        Assert.Throws<CorruptDataException>(() => SubtitleDocument.Parse(text, SubtitleFormat.Srt, new SubtitleReaderOptions { Strict = true }));
        Assert.NotEmpty(SubtitleDocument.Parse(text, SubtitleFormat.Srt).Cues);
    }

    [Fact]
    public void SrtWritesCanonicalForm()
    {
        SubtitleDocument document = new();
        document.Cues.Add(new SubtitleCue(At(1000), At(2500), [new SubtitleSpan("Hello "), new SubtitleSpan("bold") { Bold = true }, new SubtitleSpan(" both") { Bold = true, Italic = true }]));
        document.Cues.Add(new SubtitleCue(At(3_723_004), At(3_724_000), "Two\nlines") { Alignment = SubtitleAlignment.TopLeft });
        document.Cues.Add(new SubtitleCue(At(5000), At(6000), [new SubtitleSpan("red") { Color = new Rgba8(255, 0, 0, 255), Font = "Arial" }]));
        Assert.Equal(
            "1\r\n00:00:01,000 --> 00:00:02,500\r\nHello <b>bold<i> both</i></b>\r\n\r\n" +
            "2\r\n01:02:03,004 --> 01:02:04,000\r\n{\\an7}Two\r\nlines\r\n\r\n" +
            "3\r\n00:00:05,000 --> 00:00:06,000\r\n<font face=\"Arial\" color=\"#ff0000\">red</font>\r\n\r\n",
            document.ToText());
        SubtitleDocument back = SubtitleDocument.Parse(document.ToText());
        Assert.True(document.Cues.Zip(back.Cues).All(p => p.First.Spans.SequenceEqual(p.Second.Spans)));
        Assert.Equal(SubtitleAlignment.TopLeft, back.Cues[1].Alignment);
    }

    [Fact]
    public void WebVttFeaturesAreRead()
    {
        SubtitleDocument document = SubtitleDocument.Load(PathOf("features.vtt"));
        Assert.Equal("- Test file\nKind: captions\nLanguage: en", document.HeaderText);
        SubtitleRegion region = Assert.Single(document.Regions);
        Assert.Equal(new SubtitleRegion("fred") { Width = 40, ViewportAnchorX = 10, ViewportAnchorY = 90, ScrollUp = true }, region);
        Assert.Equal("::cue(.loud) { color: red; }", Assert.Single(document.StyleSheets));
        Assert.Equal(4, document.Cues.Count);
        SubtitleCue intro = document.Cues[0];
        Assert.Equal("intro", intro.Id);
        Assert.Equal(new WebVttCueSettings { Align = WebVttAlign.Start, Line = 0 }, intro.WebVtt);
        Assert.Equal("Roger Bingham", Assert.Single(intro.Spans).Voice);
        SubtitleCue loud = document.Cues[1];
        Assert.Equal(["loud", "big"], loud.Spans[0].Classes);
        Assert.Equal("Loud and bold & italic <tag>", loud.PlainText);
        Assert.Equal(new WebVttCueSettings { Region = "fred", Position = 10, PositionAlign = WebVttPositionAlign.LineLeft, Size = 35 }, loud.WebVtt);
        SubtitleCue ruby = document.Cues[2];
        Assert.Equal(("漢", "kan"), (ruby.Spans[0].Text, ruby.Spans[0].Ruby));
        Assert.Equal(("字", "ji"), (ruby.Spans[1].Text, ruby.Spans[1].Ruby));
        Assert.Equal("ja", ruby.Spans[^1].Language);
        Assert.Equal(WebVttVertical.RightToLeft, ruby.WebVtt!.Vertical);
        SubtitleSpan[] karaoke = [.. document.Cues[3].Spans];
        Assert.Equal([At(7000), At(7500), At(8000)], karaoke.Select(s => s.Timestamp!.Value));
    }

    [Fact]
    public void WebVttWritesCanonicalForm()
    {
        SubtitleDocument document = new(SubtitleFormat.WebVtt) { HeaderText = "Title" };
        document.Cues.Add(new SubtitleCue(At(1000), At(2000), [new SubtitleSpan("Hi") { Voice = "Ann" }, new SubtitleSpan(" & <you>") { Voice = "Ann", Italic = true }]) { Id = "a" });
        document.Cues.Add(new SubtitleCue(At(2000), At(3000), "top") { Alignment = SubtitleAlignment.TopRight });
        Assert.Equal(
            "WEBVTT Title\n\na\n00:00:01.000 --> 00:00:02.000\n<v Ann>Hi<i> &amp; &lt;you&gt;</i></v>\n\n" +
            "00:00:02.000 --> 00:00:03.000 line:0 align:right\ntop\n",
            document.ToText());
    }

    [Fact]
    public void AssFeaturesAreRead()
    {
        SubtitleDocument document = SubtitleDocument.Load(PathOf("features.ass"));
        Assert.Equal(SubtitleFormat.Ass, document.Format);
        Assert.Equal("Features", document.ScriptInfo.Single(p => p.Key == "Title").Value);
        Assert.Equal(2, document.Styles.Count);
        SubtitleStyle sign = document.Styles[1];
        Assert.Equal(("Georgia", 40.0, true, SubtitleAlignment.TopCenter), (sign.FontName, sign.FontSize, sign.Bold, sign.Alignment));
        Assert.Equal(new Rgba8(255, 255, 0, 255), sign.PrimaryColor);
        Assert.Equal(new Rgba8(0, 0, 0, 127), document.Styles[0].BackColor);
        SubtitleAttachment font = Assert.Single(document.Attachments);
        Assert.Equal("test_0.ttf", font.Name);
        Assert.Equal([.. Enumerable.Range(0, 256).Select(i => (byte)i).Concat(Enumerable.Range(0, 256).Select(i => (byte)i)).Concat(Enumerable.Range(0, 256).Select(i => (byte)i)), .. "end"u8.ToArray()], font.Data.ToArray());

        Assert.Equal(5, document.Cues.Count);
        SubtitleCue hello = document.Cues[0];
        Assert.Equal(("Alice", "Hello, world!\nSecond line"), (hello.Actor, hello.PlainText));
        Assert.True(hello.Spans[1].Italic);
        Assert.True(document.Cues[1].IsComment);
        SubtitleCue signCue = document.Cues[2];
        Assert.Equal((1, "Sign", SubtitleAlignment.TopCenter, new SubtitlePosition(960, 100)), (signCue.Layer, signCue.Style, signCue.Alignment!.Value, signCue.Position!.Value));
        Assert.Equal(new Rgba8(255, 0, 0, 255), signCue.Spans[0].Color);
        Assert.True(signCue.Spans[0].Bold);
        Assert.Equal("\\blur2\\fad(100,200)", signCue.Spans[^1].OtherTags);
        SubtitleCue karaoke = document.Cues[3];
        Assert.Equal([new MediaTime(600, new Rational(1, 100)), new MediaTime(650, new Rational(1, 100)), new MediaTime(680, new Rational(1, 100))],
            karaoke.Spans.Take(3).Select(s => s.Timestamp!.Value));
        Assert.Equal(("Times New Roman", 60.0), (karaoke.Spans[^1].Font, karaoke.Spans[^1].FontSize!.Value));
        Assert.EndsWith("big space", karaoke.PlainText, StringComparison.Ordinal);
        Assert.Equal((10, 10, 30), (karaoke.MarginLeft, karaoke.MarginRight, karaoke.MarginVertical));
        SubtitleCue drawing = document.Cues[4];
        Assert.Equal("Banner;10;0", drawing.Effect);
        Assert.True(drawing.Spans[0].IsDrawing);
        Assert.False(drawing.Spans[^1].IsDrawing);
    }

    [Fact]
    public void AssWritesTagsBackFromSpans()
    {
        SubtitleDocument document = SubtitleDocument.Load(PathOf("features.ass"));
        foreach (SubtitleCue cue in document.Cues)
        {
            cue.Spans.Add(new SubtitleSpan("!") { Underline = true, Strikeout = true });
        }

        document.Styles.Add(new SubtitleStyle("Extra"));
        string text = document.ToText();
        SubtitleDocument back = SubtitleDocument.Parse(text);
        Assert.Equal(document.Cues.Count, back.Cues.Count);
        for (int i = 0; i < document.Cues.Count; i++)
        {
            Assert.Equal(document.Cues[i].PlainText, back.Cues[i].PlainText);
            Assert.Equal(document.Cues[i].Spans.Select(s => (s.Bold, s.Italic, s.Color, s.Font, s.FontSize, s.OtherTags, s.Timestamp, s.IsDrawing)),
                back.Cues[i].Spans.Select(s => (s.Bold, s.Italic, s.Color, s.Font, s.FontSize, s.OtherTags, s.Timestamp, s.IsDrawing)));
            Assert.Equal((document.Cues[i].Alignment, document.Cues[i].Position, document.Cues[i].Layer, document.Cues[i].IsComment),
                (back.Cues[i].Alignment, back.Cues[i].Position, back.Cues[i].Layer, back.Cues[i].IsComment));
        }

        Assert.Equal(document.Styles, back.Styles);
        Assert.Equal(document.Attachments[0].Data.ToArray(), back.Attachments[0].Data.ToArray());
        Assert.Contains("[Aegisub Project Garbage]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SsaIsReadAndSavedAsAss()
    {
        SubtitleDocument document = SubtitleDocument.Load(PathOf("legacy.ssa"));
        Assert.Equal(SubtitleFormat.Ssa, document.Format);
        SubtitleStyle style = Assert.Single(document.Styles);
        Assert.Equal((SubtitleAlignment.TopCenter, true, new Rgba8(255, 255, 255, 255)), (style.Alignment, style.Bold, style.PrimaryColor));
        Assert.Equal(SubtitleAlignment.TopCenter, document.Cues[0].Alignment);
        Assert.Equal("Second\nline", document.Cues[1].PlainText);
        Assert.Throws<NotSupportedException>(() => document.ToText(SubtitleFormat.Ssa));
        string ass = document.ToText(SubtitleFormat.Ass);
        Assert.Contains("ScriptType: v4.00+", ass, StringComparison.Ordinal);
        Assert.Equal(SubtitleFormat.Ass, SubtitleDocument.Parse(ass).Format);
        Assert.Equal(document.Cues.Select(c => c.PlainText), SubtitleDocument.Parse(ass).Cues.Select(c => c.PlainText));
    }

    public static TheoryData<string, byte[]> Encodings
    {
        get
        {
            const string text = "1\r\n00:00:01,000 --> 00:00:02,000\r\nCafé — naïve €5\r\n";
            return new TheoryData<string, byte[]>
            {
                { "UTF-8 with a BOM", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)] },
                { "UTF-8", Encoding.UTF8.GetBytes(text) },
                { "UTF-16LE with a BOM", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)] },
                { "UTF-16BE with a BOM", [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)] },
                { "UTF-16LE without a BOM", Encoding.Unicode.GetBytes(text) },
                { "Windows-1252", [.. Encoding.Latin1.GetBytes(text.Replace('—', '-').Replace('€', '?')).Select(b => b == (byte)'-' && false ? (byte)0 : b)] },
            };
        }
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public void EncodingsAreReadAndGuessesReported(string name, byte[] data)
    {
        if (name == "Windows-1252")
        {
            // A real cp1252 file: the em dash and euro sign are 0x97 and 0x80.
            data = [.. Encoding.Latin1.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nCafé ").Concat((byte[])[0x97]).Concat(Encoding.Latin1.GetBytes(" naïve ")).Concat((byte[])[0x80]).Concat("5\r\n"u8.ToArray())];
        }

        (MediaContext context, List<EgamiDiagnostic> reported) = Capture();
        SubtitleDocument document = SubtitleDocument.Load(data, context: context);
        Assert.Equal("Café — naïve €5", document.Cues[0].PlainText);
        bool guessed = name is "Windows-1252" or "UTF-16LE without a BOM";
        Assert.Equal(guessed, reported.Any(d => d.Id == "Subtitle.Encoding"));
    }

    [Fact]
    public void TheEncodingOptionIsUsedForNonUtf8Files()
    {
        byte[] data = [.. "1\n00:00:01,000 --> 00:00:02,000\n"u8.ToArray(), 0xE9, (byte)'\n'];
        SubtitleDocument document = SubtitleDocument.Load(data, new SubtitleReaderOptions { Encoding = Encoding.Latin1 });
        Assert.Equal("é", document.Cues[0].PlainText);
    }

    [Fact]
    public void FormatIsToldFromContentThenExtension()
    {
        Assert.Equal(SubtitleFormat.WebVtt, SubtitleDocument.DetectFormat("WEBVTT\n\n"u8));
        Assert.Equal(SubtitleFormat.Ass, SubtitleDocument.DetectFormat("[Script Info]\nScriptType: v4.00+\n"u8));
        Assert.Equal(SubtitleFormat.Ssa, SubtitleDocument.DetectFormat("[Script Info]\nScriptType: v4.00\n"u8));
        Assert.Equal(SubtitleFormat.Srt, SubtitleDocument.DetectFormat("\n1\n00:00:01,000 --> 00:00:02,000\nx\n"u8));
        Assert.Null(SubtitleDocument.DetectFormat("hello"u8));
        string path = Path.Combine(Path.GetTempPath(), $"egami-empty-{Environment.ProcessId}.srt");
        File.WriteAllText(path, "");
        try
        {
            Assert.Equal(SubtitleFormat.Srt, SubtitleDocument.Load(path).Format);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Throws<UnsupportedFormatException>(() => SubtitleDocument.Load("hello"u8));
    }
}
