using Egami.Fonts.Layout;

namespace Egami.DocSamples;

[Collection("Samples")]
public class ParagraphLayout
{
    [Fact]
    public void LineBreakOpportunities()
    {
        #region line-breaks
        // Where lines may end: after spaces and hyphens, and always at the end; a line must end after a newline.
        List<int> breaks = [];
        foreach (LineBreak b in LineBreaks.Enumerate("A well-known fact.\nNext"))
        {
            breaks.Add(b.Index);                       // 2, 7, 13, 19 (mandatory), 23
        }

        // CSS switches: break-all lets letters break like ideographs.
        LineBreakOptions breakAll = new(WordBreak: WordBreak.BreakAll);
        int count = 0;
        foreach (LineBreak b in LineBreaks.Enumerate("word", breakAll))
        {
            count++;                                   // 4: after every letter
        }
        #endregion

        Assert.Equal([2, 7, 13, 19, 23], breaks);
        Assert.Equal(4, count);
    }
}
