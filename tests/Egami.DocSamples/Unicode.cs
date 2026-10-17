using Egami.Fonts;
using Egami.Fonts.Unicode;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Unicode
{
    [Fact]
    public void CharacterProperties()
    {
        #region properties
        // Egami's own Unicode 17.0 tables: the same answer on every runtime and machine.
        Script script = UnicodeData.GetScript('\u0915');                          // Devanagari
        ReadOnlySpan<FontTag> tags = script.OpenTypeTags;                         // dev2, then deva
        BidiClass bidi = UnicodeData.GetBidiClass('\u0627');                      // ArabicLetter
        JoiningType joining = UnicodeData.GetJoiningType('\u0628');               // DualJoining
        JoiningGroup group = UnicodeData.GetJoiningGroup('\u0628');               // Beh
        VerticalOrientation upright = UnicodeData.GetVerticalOrientation('\u6F22');   // Upright
        EastAsianWidth width = UnicodeData.GetEastAsianWidth('\u6F22');               // Wide
        bool emoji = UnicodeData.IsEmojiPresentation(0x1F600);                    // true
        UnicodeData.TryGetBidiMirroringGlyph('(', out int mirror);                // ')'
        BidiPairedBracketType bracket = UnicodeData.GetBidiPairedBracketType('['); // Open
        #endregion

        Assert.Equal(Script.Devanagari, script);
        Assert.Equal("dev2", tags[0].ToString());
        Assert.Equal(BidiClass.ArabicLetter, bidi);
        Assert.Equal(JoiningType.DualJoining, joining);
        Assert.Equal(JoiningGroup.Beh, group);
        Assert.Equal(VerticalOrientation.Upright, upright);
        Assert.Equal(EastAsianWidth.Wide, width);
        Assert.True(emoji);
        Assert.Equal(')', mirror);
        Assert.Equal(BidiPairedBracketType.Open, bracket);
    }

    [Fact]
    public void BidiLevelsAndVisualOrder()
    {
        #region bidi
        // A Hebrew word inside English text: three runs, the middle one drawn right to left.
        string text = "abc \u05D0\u05D1\u05D2 def";
        using BidiResult result = Bidi.Resolve(text, TextDirection.Auto);
        int paragraph = result.ParagraphLevel;           // 0: the first strong character is Latin
        foreach (BidiRun run in result.Reorder(0, result.Length))
        {
            Console.WriteLine($"{run.Start}+{run.Length} {(run.IsRightToLeft ? "right to left" : "left to right")}");
        }
        #endregion

        Assert.Equal(0, paragraph);
        Assert.Equal(3, result.Runs.Length);
    }

    [Fact]
    public void GraphemesAndWords()
    {
        #region segments
        // A family emoji is one grapheme cluster of eight UTF-16 units; "can't" is one word.
        string text = "\uD83D\uDC68\u200D\uD83D\uDC69\u200D\uD83D\uDC67 can't stop";
        var graphemes = new List<TextSegment>();
        foreach (TextSegment grapheme in TextSegmenter.Graphemes(text))
        {
            graphemes.Add(grapheme);
        }

        var words = new List<string>();
        foreach (TextSegment word in TextSegmenter.Words(text))
        {
            words.Add(text.Substring(word.Start, word.Length));
        }

        bool inside = TextSegmenter.IsGraphemeBoundary(text, 2); // false: inside the family
        #endregion

        Assert.Equal(new TextSegment(0, 8), graphemes[0]);
        Assert.Contains("can't", words);
        Assert.False(inside);
    }
}
