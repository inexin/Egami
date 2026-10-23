using System.Text;

using Egami.Video.Subtitles;

namespace Egami.Video.Tests.Subtitles;

/// <summary>What the subs fuzz target (V1.2 Build Plan task 14) found, each cut down to a few lines.</summary>
public sealed class SubtitleFuzzFindingTests
{
    private static (MediaContext Context, List<EgamiDiagnostic> Reported) Capture()
    {
        List<EgamiDiagnostic> reported = [];
        return (MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } }, reported);
    }

    [Theory]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nKept\n\n2\n9999999999999999:00:00,000 --> 9999999999999999:00:01,000\nToo late\n")]
    [InlineData("WEBVTT\n\n9999999999999999:00:00.000 --> 9999999999999999:00:01.000\nToo late\n\n00:01.000 --> 00:02.000\nKept\n")]
    [InlineData("[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
        + "Dialogue: 0,9999999999999999:00:00.00,9999999999999999:00:01.00,Default,,0,0,0,,Too late\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,Kept\n")]
    public void TimesTooLateToHoldAreMalformed(string text)
    {
        (MediaContext context, List<EgamiDiagnostic> reported) = Capture();
        SubtitleDocument document = SubtitleDocument.Load(Encoding.UTF8.GetBytes(text), context: context);
        Assert.Equal("Kept", Assert.Single(document.Cues).PlainText);
        if (document.Format == SubtitleFormat.WebVtt)
        {
            // The WebVTT parsing algorithm skips a block whose timing line it cannot read; there is nothing to repair.
            return;
        }

        Assert.Contains(reported, d => d.Id == "Subtitle.Repaired");
        Assert.Throws<CorruptDataException>(() => SubtitleDocument.Load(Encoding.UTF8.GetBytes(text), new SubtitleReaderOptions { Strict = true }));
    }

    [Fact]
    public void KaraokeLongerThanATimeCanHoldStopsAtTheLimit()
    {
        const string Text = "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\k999999999999999999}Ka{\\k999999999999999999}ra{\\k50}oke\n";
        SubtitleDocument document = SubtitleDocument.Parse(Text, SubtitleFormat.Ass);
        Assert.Equal("Karaoke", document.Cues[0].PlainText);
        Assert.Contains("<", document.ToText(SubtitleFormat.WebVtt), StringComparison.Ordinal);
        Assert.NotEmpty(document.ToText(SubtitleFormat.Srt));
    }

    [Fact]
    public void OnlyCrAndLfBreakLinesWhenCuesAreWritten()
    {
        // A form feed is text in SRT; written as a line break it would turn the rest of the line into a timing line.
        const string Text = "1\n00:00:01,000 --> 00:00:03,500\nHello\f00:00:04,000 --> 00:00:05,000\nLine\u2028two\n";
        SubtitleDocument document = SubtitleDocument.Parse(Text, SubtitleFormat.Srt);
        document.Shift(MediaTime.FromSeconds(1));
        foreach (SubtitleFormat format in (SubtitleFormat[])[SubtitleFormat.Srt, SubtitleFormat.WebVtt, SubtitleFormat.Ass])
        {
            SubtitleDocument back = SubtitleDocument.Parse(document.ToText(format), format);
            Assert.Equal(document.Cues[0].PlainText, Assert.Single(back.Cues).PlainText);
        }
    }

    [Fact]
    public void AssCutOffInsideItsHeaderSavesBackUnchanged()
    {
        byte[] data = "[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize"u8.ToArray();
        (MediaContext context, List<EgamiDiagnostic> reported) = Capture();
        SubtitleDocument document = SubtitleDocument.Load(data, context: context);
        Assert.Empty(reported);
        using MemoryStream saved = new();
        document.Save(saved);
        Assert.Equal(data, saved.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x57, 0x45, 0x42, 0x56, 0x54, 0x54, 0x0A, 0x0A, 0x30, 0x30, 0x3A, 0x30, 0x30, 0x2E, 0x30, 0x30, 0x30, 0x20, 0x2D, 0x2D, 0x3E, 0x20, 0x30, 0x30, 0x3A, 0x30, 0x31, 0x2E, 0x30, 0x30, 0x30, 0x0A, 0x41, 0xFF, 0x0A })]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x31, 0x0A, 0x30, 0x30, 0x3A, 0x30, 0x30, 0x3A, 0x30, 0x30, 0x2C, 0x30, 0x30, 0x30, 0x20, 0x2D, 0x2D, 0x3E, 0x20, 0x30, 0x30, 0x3A, 0x30, 0x30, 0x3A, 0x30, 0x31, 0x2C, 0x30, 0x30, 0x30, 0x0A, 0x41, 0xFF, 0x0A })]
    public void BytesNotValidInTheFilesUnicodeEncodingAreReported(byte[] data)
    {
        // A WebVTT file is always UTF-8, and a byte order mark names the encoding: bad bytes become U+FFFD, which is reported.
        (MediaContext context, List<EgamiDiagnostic> reported) = Capture();
        SubtitleDocument document = SubtitleDocument.Load(data, context: context);
        Assert.Equal("A�", Assert.Single(document.Cues).PlainText);
        Assert.Contains(reported, d => d.Id == "Subtitle.Encoding");
    }

    [Fact]
    public void AssEventsWrittenAfterAnUnchangedHeaderFollowItsFormatLine()
    {
        // A second Format line among the events changes the fields of the events after it; retimed events are written in the
        // format of the header that is kept, so they read back.
        const string Text = "[Script Info]\nScriptType: v4.00+\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
            + "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,One\nFormat: Start, End, Text\nDialogue: 0:00:03.00,0:00:04.00,Two\n";
        SubtitleDocument document = SubtitleDocument.Parse(Text, SubtitleFormat.Ass);
        Assert.Equal(["One", "Two"], document.Cues.Select(c => c.PlainText));
        document.Shift(MediaTime.FromSeconds(1));
        SubtitleDocument back = SubtitleDocument.Parse(document.ToText(), SubtitleFormat.Ass);
        Assert.Equal(["One", "Two"], back.Cues.Select(c => c.PlainText));
        Assert.Equal([2.0, 4.0], back.Cues.Select(c => c.Start.Seconds));
    }
}
