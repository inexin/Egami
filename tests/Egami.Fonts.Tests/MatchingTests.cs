using System.Diagnostics;

using Egami.Fonts.Matching;

namespace Egami.Fonts.Tests;

/// <summary>CSS Fonts Level 4 matching, name lookup in a collection, generic families and the installed fonts.</summary>
public class MatchingTests
{
    private static readonly FontStyle[] s_weights =
    [
        new(100), new(300), new(400), new(500), new(600), new(700), new(900),
    ];

    [Theory]
    [InlineData(400, 400)]
    [InlineData(450, 500)]
    [InlineData(500, 500)]
    [InlineData(700, 700)]
    [InlineData(800, 900)]
    [InlineData(950, 900)]
    [InlineData(350, 300)]
    [InlineData(50, 100)]
    [InlineData(200, 100)]
    public void WeightsFollowTheCssSteps(int wanted, int expected)
    {
        int index = StyleMatcher.Match(s_weights, new FontStyle(wanted));
        Assert.Equal(expected, s_weights[index].Weight);
    }

    [Theory]
    [InlineData(new[] { 300, 700 }, 400, 300)]
    [InlineData(new[] { 300, 700 }, 500, 300)]
    [InlineData(new[] { 600, 700 }, 450, 600)]
    [InlineData(new[] { 100, 600 }, 450, 100)]
    [InlineData(new[] { 400, 900 }, 600, 900)]
    [InlineData(new[] { 200, 300 }, 600, 300)]
    [InlineData(new[] { 500, 800 }, 300, 500)]
    public void SparseWeightsFollowTheCssSteps(int[] weights, int wanted, int expected)
    {
        Assert.Equal(expected, StyleMatcher.BestWeight(weights, wanted));
    }

    [Fact]
    public void WidthComesBeforeSlantAndSlantBeforeWeight()
    {
        FontStyle[] styles =
        [
            new(400, 5, FontSlant.Upright),
            new(700, 5, FontSlant.Italic),
            new(400, 3, FontSlant.Italic),
            new(400, 7, FontSlant.Oblique),
        ];

        // Normal width wins over a closer slant at another width.
        Assert.Equal(1, StyleMatcher.Match(styles, new FontStyle(400, 5, FontSlant.Italic)));

        // Condensed asked: narrower first.
        Assert.Equal(2, StyleMatcher.Match(styles, new FontStyle(400, 4, FontSlant.Upright)));

        // Expanded asked: wider first.
        Assert.Equal(3, StyleMatcher.Match(styles, new FontStyle(400, 6, FontSlant.Upright)));

        // Oblique falls back to italic before upright.
        Assert.Equal(1, StyleMatcher.Match(styles, new FontStyle(400, 5, FontSlant.Oblique)));
    }

    [Fact]
    public void SynthesisIsRecordedWhenTheMatchIsLighterOrUpright()
    {
        Assert.Equal(FontSynthesis.Bold | FontSynthesis.Italic, StyleMatcher.SynthesisFor(FontStyle.BoldItalic, FontStyle.Normal));
        Assert.Equal(FontSynthesis.None, StyleMatcher.SynthesisFor(new FontStyle(600), new FontStyle(600)));
        Assert.Equal(FontSynthesis.Bold, StyleMatcher.SynthesisFor(new FontStyle(600), new FontStyle(500)));
        Assert.Equal(FontSynthesis.None, StyleMatcher.SynthesisFor(FontStyle.Italic, new FontStyle(400, 5, FontSlant.Oblique)));
    }

    [Fact]
    public void ACollectionFindsFamiliesByEveryName()
    {
        var fonts = new FontCollection();
        Assert.Equal(11, fonts.AddDirectory(Path.Combine(TestFonts.Root, "tests", "fonts"), recursive: false));
        Assert.Contains(fonts.Families, f => f.Name == "Noto Sans");
        Assert.Equal(fonts.Families.Count, fonts.Count);

        Assert.True(fonts.TryGetFamily("noto sans", out FontFamily noto));
        Assert.Equal("Noto Sans", noto.Name);
        Assert.Contains(FontStyle.Bold, noto.Styles);

        FontMatch bold = fonts.Match("Noto Sans", FontStyle.Bold)!.Value;
        Assert.Equal("NotoSans-Bold", bold.Typeface.PostScriptName);
        Assert.Equal(FontSynthesis.None, bold.Synthesis);

        FontMatch boldItalic = fonts.Match("Noto Sans", FontStyle.BoldItalic)!.Value;
        Assert.Equal(FontSlant.Italic, boldItalic.Typeface.Style.Slant);
        Assert.Equal(FontSynthesis.Bold, boldItalic.Synthesis);
        Assert.Equal(FontSynthesis.Bold, boldItalic.ToFont(12).Synthesis);

        Assert.Equal("NotoSans-Bold", fonts.MatchTypeface("NotoSans-Bold", FontStyle.Normal)!.PostScriptName);
        Assert.Equal("NotoSans-Bold", fonts.MatchTypeface("Noto Sans Bold", FontStyle.Normal)!.PostScriptName);
        Assert.Null(fonts.Match("Comic Nonexistent", FontStyle.Normal));
        Assert.False(fonts.TryGetFamily("Comic Nonexistent", out _));

        Assert.Equal("DejaVu Sans Mono", fonts.Match(GenericFamily.Monospace, FontStyle.Normal)!.Value.Typeface.FamilyName);
        string sans = fonts.Match(GenericFamily.SansSerif, FontStyle.Normal)!.Value.Typeface.FamilyName;
        Assert.True(sans is "DejaVu Sans" or "Noto Sans", sans);
        Assert.Equal("Noto Serif", fonts.Match(GenericFamily.Serif, FontStyle.Normal)!.Value.Typeface.FamilyName);
    }

    [Fact]
    public void IndexedStylesEqualLoadedStyles()
    {
        var fonts = new FontCollection();
        fonts.AddDirectory(Path.Combine(TestFonts.Root, "tests", "fonts"));
        foreach (FontFamily family in fonts)
        {
            IReadOnlyList<Typeface> faces = family.Faces;
            Assert.Equal(family.Styles, faces.Select(f => f.Style));
            Assert.All(faces, f => Assert.Equal(family.Name, f.FamilyName));
        }
    }

    [Fact]
    public void AddedFacesAreFound()
    {
        var fonts = new FontCollection();
        fonts.Add(TestFonts.Load("Roboto-Regular.ttf"));
        Assert.Equal("Roboto", fonts.MatchTypeface("Roboto", FontStyle.Bold)!.FamilyName);
        Assert.Single(fonts);
    }

    [Fact]
    public void UnreadableFilesAreSkippedWithADiagnostic()
    {
        string dir = Directory.CreateTempSubdirectory("egami-fonts").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "broken.ttf"), [0, 1, 0, 0, 0, 9]);
            File.Copy(TestFonts.FontPath("Roboto-Regular.ttf"), Path.Combine(dir, "Roboto.TTF"));
            File.WriteAllText(Path.Combine(dir, "readme.txt"), "not a font");
            var seen = new List<EgamiDiagnostic>();
            var fonts = new FontCollection(new FontLoadOptions { Context = EgamiContext.Default with { Diagnostics = seen.Add } });
            Assert.Equal(1, fonts.AddDirectory(dir));
            Assert.Contains(seen, d => d.Id == "Font.FileSkipped");
            Assert.Throws<DirectoryNotFoundException>(() => fonts.AddDirectory(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FontconfigDirAndIncludeEntriesAreRead()
    {
        string dir = Directory.CreateTempSubdirectory("egami-fontconfig").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "conf.d"));
            File.WriteAllText(Path.Combine(dir, "fonts.conf"), """
                <?xml version="1.0"?>
                <!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd">
                <fontconfig>
                  <dir>/usr/share/fonts</dir>
                  <dir prefix="xdg">fonts</dir>
                  <dir>~/.fonts</dir>
                  <include ignore_missing="yes">conf.d</include>
                </fontconfig>
                """);
            File.WriteAllText(Path.Combine(dir, "conf.d", "10-extra.conf"), "<fontconfig><dir>/opt/fonts</dir></fontconfig>");
            var dirs = new List<string>();
            FontCollection.ReadFontconfig(Path.Combine(dir, "fonts.conf"), dirs, "/home/u", 0);
            Assert.Contains("/usr/share/fonts", dirs);
            Assert.Contains("/home/u/.fonts", dirs);
            Assert.Contains("/opt/fonts", dirs);
            Assert.Contains(dirs, d => d.EndsWith("fonts", StringComparison.Ordinal) && d.Contains("share", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void InstalledFontsAreIndexedQuickly()
    {
        var stopwatch = Stopwatch.StartNew();
        FontCollection system = FontCollection.System;
        system.Refresh();
        _ = system.Match(GenericFamily.SansSerif, FontStyle.Normal);
        stopwatch.Stop();
        int faces = system.Sum(f => f.Styles.Count);
        TestContext.Current.SendDiagnosticMessage($"Indexed {faces} installed faces and matched sans-serif in {stopwatch.ElapsedMilliseconds} ms.");
        if (faces > 0)
        {
            Assert.NotNull(system.Match(GenericFamily.SansSerif, FontStyle.Normal));
        }
    }
}
