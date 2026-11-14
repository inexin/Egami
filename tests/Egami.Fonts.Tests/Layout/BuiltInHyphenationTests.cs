using Egami.Fonts.Layout;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Hyphenation Build Plan task 7: the hyph-utf8 patterns Egami.Fonts ships (HyphenationDictionaries.BuiltIn).</summary>
public sealed class BuiltInHyphenationTests
{
    // The languages whose licences let Egami ship them (tools/make-hyphenation-patterns.py); the rest are loaded by users.
    private static readonly string[] Shipped =
    [
        "en-US", "en-GB", "de-1996", "de-1901", "de-CH-1901", "fr", "nb", "nn", "da", "es", "pt", "gl", "it", "nl", "eu", "la", "ga",
        "cy", "pl", "sk", "sl", "hr", "bg", "uk", "be", "et", "lt", "sq", "el-monoton", "af", "ka", "hi", "bn", "ta", "mn-Cyrl",
    ];

    private static string Hyphenate(Hyphenator hyphenator, string word)
    {
        int[] points = new int[word.Length];
        int count = hyphenator.GetPoints(word, points);
        for (int i = count - 1; i >= 0; i--)
        {
            word = word.Insert(points[i], "-");
        }

        return word;
    }

    [Fact]
    public void ShipsExactlyTheClearedLanguages()
    {
        Assert.Equal(Shipped.Order(StringComparer.OrdinalIgnoreCase), HyphenationDictionaries.BuiltIn.Languages.Order(StringComparer.OrdinalIgnoreCase));
        foreach (string notShipped in (string[])["sv", "ru", "ca", "is", "tr", "th", "hu", "lv", "cs", "sr-Cyrl", "hy", "fi", "ro"])
        {
            Assert.False(HyphenationDictionaries.BuiltIn.TryGet(notShipped, out _), notShipped);
        }
    }

    [Fact]
    public void EveryBuiltInLanguageLoadsAndPassesTheFullCheck()
    {
        // The built-in loader trusts its own resources; the public loader checks them all the way, as any user file.
        foreach (string tag in Shipped)
        {
            Assert.True(HyphenationDictionaries.BuiltIn.TryGet(tag, out Hyphenator? hyphenator), tag);
            using Stream resource = typeof(Hyphenator).Assembly.GetManifestResourceStream($"Egami.Fonts.Hyphenation.{tag}.ehyp")!;
            Hyphenator checkedCopy = Hyphenator.Load(resource, HyphenationFormat.Compiled);
            Assert.Equal(hyphenator.MinBefore, checkedCopy.MinBefore);
            Assert.Equal(hyphenator.MinAfter, checkedCopy.MinAfter);
            Assert.Contains(hyphenator.Metadata, m => m.Key == "licence" && m.Value.Length > 0);
            Assert.Contains(hyphenator.Metadata, m => m.Key == "source" && m.Value.StartsWith("hyph-utf8 ", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("en-US", "hyphenation", "hy-phen-ation")]
    [InlineData("en-US", "typesetting", "type-set-ting")]
    [InlineData("en-GB", "organisation", "or-gan-isa-tion")]
    [InlineData("de-1996", "Silbentrennung", "Sil-ben-tren-nung")]
    [InlineData("de-CH-1901", "Strassenbahn", "Stras-sen-bahn")]
    [InlineData("fr", "typographie", "ty-po-gra-phie")]
    [InlineData("nb", "skjønnlitteratur", "skjønn-lit-te-ra-tur")]
    [InlineData("nn", "kommunikasjon", "kom-mu-ni-ka-sjon")]
    [InlineData("da", "sprogvidenskab", "sprog-vi-den-skab")]
    [InlineData("es", "biblioteca", "bi-blio-te-ca")]
    [InlineData("it", "tipografia", "ti-po-gra-fia")]
    [InlineData("nl", "woordafbreking", "woord-af-bre-king")]
    [InlineData("pl", "dzielenie", "dzie-le-nie")]
    [InlineData("sl", "slovenščina", "slo-ven-šči-na")]
    [InlineData("bg", "българия", "бъл-га-рия")]
    [InlineData("el-monoton", "ελληνικά", "ελ-λη-νι-κά")]
    [InlineData("ka", "საქართველო", "სა-ქარ-თვე-ლო")]
    [InlineData("bn", "বাংলাদেশ", "বাং-লা-দেশ")]
    [InlineData("mn-Cyrl", "монголия", "мон-го-лия")]
    public void SpotWordsHyphenate(string tag, string word, string expected)
    {
        Assert.True(HyphenationDictionaries.BuiltIn.TryGet(tag, out Hyphenator? hyphenator));
        Assert.Equal(expected, Hyphenate(hyphenator, word));
    }

    [Theory]
    [InlineData("nb-NO", "nb")]
    [InlineData("NB_no", "nb")]
    [InlineData("no", "nb")]
    [InlineData("no-NO", "nb")]
    [InlineData("nn-NO", "nn")]
    [InlineData("de", "de-1996")]
    [InlineData("de-DE", "de-1996")]
    [InlineData("de-AT", "de-1996")]
    [InlineData("de-CH-1901", "de-CH-1901")]
    [InlineData("en", "en-US")]
    [InlineData("en-CA", "en-US")]
    [InlineData("en-GB", "en-GB")]
    [InlineData("fr-CA", "fr")]
    [InlineData("pt-BR", "pt")]
    [InlineData("el", "el-monoton")]
    [InlineData("mn", "mn-Cyrl")]
    public void TagsFallBack(string language, string expected)
    {
        Assert.True(HyphenationDictionaries.BuiltIn.TryGet(language, out Hyphenator? found), language);
        Assert.True(HyphenationDictionaries.BuiltIn.TryGet(expected, out Hyphenator? target));
        Assert.Same(target, found);
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("sv-SE")]
    [InlineData("zh-Hans")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void UnknownLanguagesAreNotFound(string? language)
    {
        Assert.False(HyphenationDictionaries.BuiltIn.TryGet(language, out Hyphenator? hyphenator));
        Assert.Null(hyphenator);
    }

    [Fact]
    public void BuiltInIsSharedAndReadOnlyButCopiesTakeMore()
    {
        HyphenationDictionaries builtIn = HyphenationDictionaries.BuiltIn;
        Assert.Same(builtIn, HyphenationDictionaries.BuiltIn);
        Assert.True(builtIn.IsReadOnly);
        Hyphenator swedish = Hyphenator.FromPatterns("1ba", minBefore: 1, minAfter: 1);
        Assert.Throws<InvalidOperationException>(() => builtIn.Add("sv", swedish));

        HyphenationDictionaries mine = new(builtIn);
        Assert.False(mine.IsReadOnly);
        mine.Add("sv", swedish);
        Assert.True(mine.TryGet("sv-SE", out Hyphenator? found));
        Assert.Same(swedish, found);
        Assert.False(builtIn.TryGet("sv", out _));

        // The copy shares the built-in hyphenators and keeps the fallbacks.
        Assert.True(mine.TryGet("no", out Hyphenator? norwegian));
        Assert.True(builtIn.TryGet("nb", out Hyphenator? bokmal));
        Assert.Same(bokmal, norwegian);
    }

    [Fact]
    public void ParagraphsHyphenateWithTheBuiltInDictionaries()
    {
        ParagraphStyle style = AutoHyphenTests.Style(hyphens: Hyphens.Auto) with
        {
            Hyphenation = new HyphenationOptions { Dictionaries = HyphenationDictionaries.BuiltIn },
        };
        TextLayout layout = Paragraph.Create("Typesetting hyphenation algorithms improve paragraph appearance considerably.", style).Layout(70);
        Assert.Contains(layout.Lines, l => l.AutoHyphen);
    }
}
