using System.Text.Json;

using Egami.Cli.Host;
using Egami.Fonts;

namespace Egami.Cli.Tests;

/// <summary><c>egami font convert|subset|info</c> (2.7.8): formats, options, outputs, reports and exit codes.</summary>
public sealed class FontCommandTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void ConvertWritesWoff2ByDefaultAndEachFormatByExtension()
    {
        Font("NotoSans-Regular.ttf");
        Expect(0, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "out/"));
        Assert.Equal(FontFileKind.Woff2, Probe("out/NotoSans-Regular.woff2").Kind);

        Expect(0, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "a.woff"));
        Assert.Equal(FontFileKind.Woff, Probe("a.woff").Kind);
        Expect(0, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "b.bin", "--format", "woff"));
        Assert.Equal(FontFileKind.Woff, Probe("b.bin").Kind);

        // Back to TTF from either: the same tables as the source (glyf glyph by glyph after WOFF2's transform).
        Expect(0, _tool.Run("font", "convert", "out/NotoSans-Regular.woff2", "-o", "back.ttf"));
        Expect(0, _tool.Run("font", "convert", "a.woff", "-o", "back-woff.ttf"));
        Typeface source = Typeface.FromFile(_tool.PathOf("NotoSans-Regular.ttf")), back = Typeface.FromFile(_tool.PathOf("back.ttf"));
        Typeface backWoff = Typeface.FromFile(_tool.PathOf("back-woff.ttf"));
        foreach (string tag in (string[])["GDEF", "GPOS", "GSUB", "OS/2", "cmap", "cvt ", "fpgm", "gasp", "glyf", "head", "hhea", "hmtx", "loca", "maxp", "name", "post", "prep"])
        {
            Assert.True(source.TryGetTable(new FontTag(tag), out ReadOnlyMemory<byte> a) && backWoff.TryGetTable(new FontTag(tag), out ReadOnlyMemory<byte> b) && a.Span.SequenceEqual(b.Span), tag);
        }

        Assert.Equal(FontFileKind.Sfnt, back.Container);
        Assert.Equal(source.GlyphCount, back.GlyphCount);
        foreach (string tag in (string[])["cmap", "GSUB", "GPOS", "hmtx", "name", "post", "OS/2"])
        {
            Assert.True(source.TryGetTable(new FontTag(tag), out ReadOnlyMemory<byte> a) && back.TryGetTable(new FontTag(tag), out ReadOnlyMemory<byte> b) && a.Span.SequenceEqual(b.Span), tag);
        }

        // To standard output, and with a template.
        ToolRun stdout = _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "-");
        Expect(0, stdout);
        Assert.Equal(FontFileKind.Woff2, FontLoader.Probe(stdout.Bytes).Kind);
        Expect(0, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "t/{name}-web.{format}", "-f", "woff"));
        Assert.Equal(FontFileKind.Woff, Probe("t/NotoSans-Regular-web.woff").Kind);
    }

    [Fact]
    public void ConvertOptions()
    {
        Font("NotoSans-Regular.ttf");
        Expect(0, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "t.woff2"));
        Expect(0, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "plain.woff2", "--no-transform"));
        Assert.True(new FileInfo(_tool.PathOf("plain.woff2")).Length > new FileInfo(_tool.PathOf("t.woff2")).Length);

        File.WriteAllText(_tool.PathOf("meta.xml"), """<?xml version="1.0" encoding="UTF-8"?><metadata version="1.0"><vendor name="Test"/></metadata>""");
        Expect(0, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "m.woff", "--metadata", "meta.xml"));
        Assert.Contains("vendor name=\"Test\"", Typeface.FromFile(_tool.PathOf("m.woff")).GetWoffMetadata(), StringComparison.Ordinal);

        Expect(ExitCodes.Usage, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "m.ttf", "--metadata", "meta.xml"));
        Expect(ExitCodes.Usage, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "m.woff2", "--metadata", "missing.xml"));
        Expect(ExitCodes.Usage, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "x.woff2", "--format", "png"));
        Expect(ExitCodes.Usage, _tool.Run("font", "convert", "NotoSans-Regular.ttf"));
    }

    [Fact]
    public void CollectionsConvertToWoff2OrTtcOnly()
    {
        Font("derived/NotoSansSerif.ttc");
        Expect(0, _tool.Run("font", "convert", "NotoSansSerif.ttc", "-o", "c.woff2"));
        FontProbe web = Probe("c.woff2");
        Assert.Equal((FontFileKind.Woff2, 2), (web.Kind, web.FaceCount));
        Expect(0, _tool.Run("font", "convert", "c.woff2", "-o", "c.ttc"));
        FontProbe ttc = Probe("c.ttc");
        Assert.Equal((FontFileKind.Collection, 2), (ttc.Kind, ttc.FaceCount));

        Expect(ExitCodes.Usage, _tool.Run("font", "convert", "NotoSansSerif.ttc", "-o", "c.woff"));
        Expect(ExitCodes.Usage, _tool.Run("font", "convert", "c.woff2", "-o", "c.ttf"));
        Font("NotoSans-Regular.ttf");
        Expect(ExitCodes.Usage, _tool.Run("font", "convert", "NotoSans-Regular.ttf", "-o", "one.ttc"));
    }

    [Fact]
    public void BatchesFoldersAndFailures()
    {
        Font("NotoSans-Regular.ttf", "in/a.ttf");
        Font("Roboto-Regular.ttf", "in/sub/b.ttf");
        Font("SourceSans3-Regular.otf", "in/c.otf");
        File.WriteAllText(_tool.PathOf("in/readme.txt"), "not a font");
        Expect(0, _tool.Run("font", "convert", "in", "--recursive", "-o", "web/{dir}/{name}.woff2", "--jobs", "2"));
        Assert.True(File.Exists(_tool.PathOf("web/a.woff2")));
        Assert.True(File.Exists(_tool.PathOf("web/sub/b.woff2")));
        Assert.True(File.Exists(_tool.PathOf("web/c.woff2")));

        Expect(ExitCodes.Usage, _tool.Run("font", "convert", "in"));
        Expect(ExitCodes.Usage, _tool.Run("font", "convert", "in/a.ttf", "in/c.otf", "-o", "one.woff2"));

        // A file that is no font fails alone; the rest are written.
        File.WriteAllText(_tool.PathOf("bad.ttf"), "not a font at all");
        ToolRun run = _tool.Run("font", "convert", "bad.ttf", "in/a.ttf", "-o", "mixed/");
        Expect(ExitCodes.Failed, run);
        Assert.Contains("not a TrueType", run.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(_tool.PathOf("mixed/a.woff2")));

        // In place: each input written over in its own format; never without --in-place.
        Expect(ExitCodes.Usage, _tool.Run("font", "convert", "in/a.ttf", "-o", "in/a.ttf"));
        Expect(0, _tool.Run("font", "convert", "web/a.woff2", "--in-place", "--no-transform"));
        Assert.Equal(FontFileKind.Woff2, Probe("web/a.woff2").Kind);

        // Limits apply to the font as it unpacks.
        Expect(ExitCodes.Limit, _tool.Run("font", "convert", "web/c.woff2", "-o", "c.otf", "--limit", "memory=100KB"));
    }

    [Fact]
    public void SubsetKeepsTheAskedCharactersAndReportsTheRest()
    {
        Font("NotoSans-Regular.ttf");
        ToolRun run = _tool.Run("font", "subset", "NotoSans-Regular.ttf", "--text", "Hello, office", "--text", "Å一", "-o", "out/");
        Expect(0, run);
        Assert.Matches(@"NotoSans-Regular\.ttf: \d+ of 3,884 glyphs kept; 1 of 11 characters missing: U\+4E00 一; 621,572 -> [\d,]+ bytes \(woff2\)", run.Text);
        Typeface full = Typeface.FromFile(_tool.PathOf("NotoSans-Regular.ttf"));
        Typeface subset = Typeface.FromFile(_tool.PathOf("out/NotoSans-Regular.woff2"));
        Assert.Equal(FontFileKind.Woff2, subset.Container);
        foreach (char c in "Hello, officeÅ")
        {
            Assert.True(subset.TryGetGlyph(c, out _), c.ToString());
        }

        // Layout is kept by default, cut down and renumbered, so the subset shapes as the full font does (the fi ligature included).
        Assert.True(subset.TryGetTable(new FontTag("GSUB"), out _));
        Assert.True(subset.GlyphCount < 40, $"{subset.GlyphCount} glyphs.");
        GlyphRun before = new Egami.Fonts.Font(full, 20).Shape("office"), after = new Egami.Fonts.Font(subset, 20).Shape("office");
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Positions.ToArray(), after.Positions.ToArray());
        Assert.True(after.Count < "office".Length, "A ligature forms.");

        // --no-layout: no layout tables, dense ids, a smaller file.
        Expect(0, _tool.Run("font", "subset", "NotoSans-Regular.ttf", "--text", "Hello, office", "--no-layout", "-o", "small.woff2"));
        Typeface small = Typeface.FromFile(_tool.PathOf("small.woff2"));
        Assert.False(small.TryGetTable(new FontTag("GSUB"), out _));
        Assert.True(small.GlyphCount < 20);
        Assert.True(new FileInfo(_tool.PathOf("small.woff2")).Length < new FileInfo(_tool.PathOf("out/NotoSans-Regular.woff2")).Length);

        // --retain-gids keeps the ids, with layout or without.
        foreach (string[] extra in (string[][])[[], ["--no-layout"]])
        {
            Expect(0, _tool.Run(["font", "subset", "NotoSans-Regular.ttf", "--text", "office", .. extra, "--retain-gids", "-o", "ids.ttf"]));
            Typeface ids = Typeface.FromFile(_tool.PathOf("ids.ttf"));
            Assert.Equal(full.GetGlyph('e'), ids.GetGlyph('e'));
            Assert.Equal(FontFileKind.Sfnt, ids.Container);
            Assert.Equal(extra.Length == 0, ids.TryGetTable(new FontTag("GSUB"), out _));
            if (extra.Length == 0)
            {
                Assert.Equal(new Egami.Fonts.Font(full, 20).Shape("office").Glyphs.ToArray(), new Egami.Fonts.Font(ids, 20).Shape("office").Glyphs.ToArray());
            }
        }
    }

    [Fact]
    public void SubsetTakesTextFilesAndUnicodeRanges()
    {
        Font("NotoSans-Regular.ttf");
        Directory.CreateDirectory(_tool.PathOf("pages"));
        File.WriteAllText(_tool.PathOf("pages/a.html"), "<p>Grüße</p>\n");
        File.WriteAllText(_tool.PathOf("pages/b.html"), "<p>Привет</p>\r\n");
        ToolRun run = _tool.Run("font", "subset", "NotoSans-Regular.ttf", "--text-file", "pages/*.html", "--unicodes", "U+0030-0039, U+00A9", "-o", "s.ttf");
        Expect(0, run);
        Assert.Contains("all ", run.Text, StringComparison.Ordinal);
        Assert.Contains("10 characters in the ranges", run.Text, StringComparison.Ordinal);
        Typeface subset = Typeface.FromFile(_tool.PathOf("s.ttf"));
        foreach (char c in "<p>/GrüßeПривет0123456789©")
        {
            Assert.True(subset.TryGetGlyph(c, out _), c.ToString());
        }

        Assert.False(subset.TryGetGlyph('A', out _));

        // Wildcards, and a text file from standard input.
        ToolRun wild = _tool.Run(["font", "subset", "NotoSans-Regular.ttf", "--unicodes", "U+4?", "--text-file", "-", "-o", "w.otf", "-f", "ttf"], "ż"u8.ToArray());
        Expect(0, wild);
        Typeface w = Typeface.FromFile(_tool.PathOf("w.otf"));
        Assert.True(w.TryGetGlyph('A', out _), wild.Text);
        Assert.True(w.TryGetGlyph('O', out _), wild.Text);
        Assert.True(w.TryGetGlyph('ż', out _), wild.Text);
        Assert.False(w.TryGetGlyph('P', out _));
    }

    [Theory]
    [InlineData("--unicodes", "0041")]
    [InlineData("--unicodes", "U+")]
    [InlineData("--unicodes", "U+0041-0030")]
    [InlineData("--unicodes", "U+110000")]
    [InlineData("--unicodes", "U+4?1")]
    [InlineData("--variation", "wght")]
    [InlineData("--variation", "wght=heavy")]
    [InlineData("--format", "ttc")]
    [InlineData("--face", "-1")]
    [InlineData("--text-file", "missing.txt")]
    public void BadSubsetValuesAreUsageErrors(string option, string value)
    {
        Font("NotoSans-Regular.ttf");
        Expect(ExitCodes.Usage, _tool.Run("font", "subset", "NotoSans-Regular.ttf", "--text", "a", option, value, "-o", "x.woff2"));
    }

    [Fact]
    public void SubsetNeedsSomethingToKeep()
    {
        Font("NotoSans-Regular.ttf");
        ToolRun run = _tool.Run("font", "subset", "NotoSans-Regular.ttf", "-o", "x.woff2");
        Expect(ExitCodes.Usage, run);
        Assert.Contains("--text, --text-file or --unicodes", run.Error, StringComparison.Ordinal);

        // A text of controls only keeps nothing either.
        Expect(ExitCodes.Usage, _tool.Run("font", "subset", "NotoSans-Regular.ttf", "--text", "\n\t", "-o", "x.woff2"));
    }

    [Fact]
    public void VariableFontsSubsetToAStaticInstance()
    {
        Font("derived/NotoSans-VF-subset.ttf");
        ToolRun plain = _tool.Run("font", "subset", "NotoSans-VF-subset.ttf", "--text", "Hello", "-o", "default.ttf");
        Expect(0, plain);
        Assert.Contains("variable font; the subset drops its axes", plain.Error, StringComparison.Ordinal);
        Assert.Contains("static at wght=400 wdth=100", plain.Text, StringComparison.Ordinal);

        ToolRun bold = _tool.Run("font", "subset", "NotoSans-VF-subset.ttf", "--text", "Hello", "--variation", "wght=700", "-o", "bold.ttf");
        Expect(0, bold);
        Assert.DoesNotContain("drops its axes", bold.Error, StringComparison.Ordinal);
        Assert.Contains("static at wght=700 wdth=100", bold.Text, StringComparison.Ordinal);
        Typeface a = Typeface.FromFile(_tool.PathOf("default.ttf")), b = Typeface.FromFile(_tool.PathOf("bold.ttf"));
        Assert.False(a.IsVariable);
        Assert.False(b.IsVariable);
        Assert.True(b.GetGlyphExtents(b.GetGlyph('H')).Width > a.GetGlyphExtents(a.GetGlyph('H')).Width);

        Expect(ExitCodes.Usage, _tool.Run("font", "subset", "NotoSans-VF-subset.ttf", "--text", "Hello", "--variation", "slnt=-10", "-o", "x.ttf"));
        Font("NotoSans-Regular.ttf");
        ToolRun ignored = _tool.Run("font", "subset", "NotoSans-Regular.ttf", "--text", "Hello", "--variation", "wght=700", "-o", "y.ttf");
        Expect(0, ignored);
        Assert.Contains("not a variable font", ignored.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SubsetOptionsForHintingNamesFacesAndFormats()
    {
        Font("NotoSans-Regular.ttf");
        Expect(0, _tool.Run("font", "subset", "NotoSans-Regular.ttf", "--text", "Hinting", "-o", "hinted.ttf"));
        Expect(0, _tool.Run("font", "subset", "NotoSans-Regular.ttf", "--text", "Hinting", "--no-hinting", "--name-prefix", "ABCDEF+", "-o", "bare.ttf"));
        Typeface hinted = Typeface.FromFile(_tool.PathOf("hinted.ttf")), bare = Typeface.FromFile(_tool.PathOf("bare.ttf"));
        Assert.True(hinted.TryGetTable(new FontTag("fpgm"), out _));
        Assert.False(bare.TryGetTable(new FontTag("fpgm"), out _));
        Assert.False(bare.TryGetTable(new FontTag("prep"), out _));
        Assert.StartsWith("ABCDEF+", bare.PostScriptName, StringComparison.Ordinal);
        Assert.True(new FileInfo(_tool.PathOf("bare.ttf")).Length < new FileInfo(_tool.PathOf("hinted.ttf")).Length);

        Expect(0, _tool.Run("font", "subset", "NotoSans-Regular.ttf", "--text", "Hinting", "-o", "w1.woff"));
        Assert.Equal(FontFileKind.Woff, Probe("w1.woff").Kind);

        Font("derived/NotoSansSerif.ttc");
        Expect(0, _tool.Run("font", "subset", "NotoSansSerif.ttc", "--text", "Serif", "--face", "1", "-o", "serif.ttf"));
        Assert.Contains("Serif", Typeface.FromFile(_tool.PathOf("serif.ttf")).FamilyName, StringComparison.Ordinal);
        Expect(ExitCodes.Usage, _tool.Run("font", "subset", "NotoSansSerif.ttc", "--text", "Serif", "--face", "2", "-o", "serif.ttf"));

        // Through standard output the report goes to standard error.
        ToolRun stdout = _tool.Run("font", "subset", "NotoSans-Regular.ttf", "--text", "Hi", "-o", "-");
        Expect(0, stdout);
        Assert.Equal(FontFileKind.Woff2, FontLoader.Probe(stdout.Bytes).Kind);
        Assert.Empty(stdout.Text);
        Assert.Contains("glyphs kept", stdout.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SubsetWarnsAboutTablesItLeavesOut()
    {
        Font("derived/NotoColorEmoji-COLRv1-subset.ttf");
        ToolRun run = _tool.Run("font", "subset", "NotoColorEmoji-COLRv1-subset.ttf", "--unicodes", "U+1F600-1F64F", "-o", "e.ttf");
        Expect(0, run);
        Assert.Contains("leaves out COLR, CPAL", run.Error, StringComparison.Ordinal);
        Assert.Contains("(color glyphs)", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void InfoDescribesFontsAsTextAndJson()
    {
        Font("NotoSans-Regular.ttf");
        Font("derived/NotoSans-VF-subset.ttf");
        Font("derived/NotoSansSerif.woff2");
        ToolRun text = _tool.Run("font", "info", "NotoSans-Regular.ttf", "NotoSans-VF-subset.ttf", "NotoSansSerif.woff2");
        Expect(0, text);
        Assert.Contains("NotoSans-Regular.ttf: TrueType font, 621,572 bytes", text.Text, StringComparison.Ordinal);
        Assert.Contains("name: Noto Sans Regular (NotoSans-Regular)", text.Text, StringComparison.Ordinal);
        Assert.Contains("outlines: TrueType; 3,884 glyphs; 1000 units per em", text.Text, StringComparison.Ordinal);
        Assert.Contains("embedding: installable", text.Text, StringComparison.Ordinal);
        Assert.Contains("tables (17): GDEF GPOS GSUB OS/2 cmap cvt fpgm", text.Text, StringComparison.Ordinal);
        Assert.Contains("Basic Latin (U+0000-007F): 97 of 128", text.Text, StringComparison.Ordinal);
        Assert.Contains("axes: wght 100-900 (default 400), wdth 62.5-100 (default 100)", text.Text, StringComparison.Ordinal);
        Assert.Contains("NotoSansSerif.woff2: WOFF2 collection of 2 fonts", text.Text, StringComparison.Ordinal);
        Assert.Contains("  font 1:", text.Text, StringComparison.Ordinal);

        ToolRun json = _tool.Run("font", "info", "NotoSans-Regular.ttf", "NotoSansSerif.woff2", "--json");
        Expect(0, json);
        using JsonDocument document = JsonDocument.Parse(json.Text);
        JsonElement files = document.RootElement.GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        JsonElement face = files[0].GetProperty("faces")[0];
        Assert.Equal("sfnt", files[0].GetProperty("container").GetString());
        Assert.Equal("Noto Sans", face.GetProperty("family").GetString());
        Assert.Equal(3884, face.GetProperty("glyphs").GetInt32());
        Assert.Equal(1000, face.GetProperty("unitsPerEm").GetInt32());
        Assert.Equal("installable", face.GetProperty("embedding").GetProperty("usage").GetString());
        Assert.Equal(17, face.GetProperty("tables").GetArrayLength());
        Assert.Contains("U+0020-007E, U+00A0-", face.GetProperty("unicodeRange").GetString(), StringComparison.Ordinal);
        Assert.Contains("SIL Open Font License", face.GetProperty("names").GetProperty("license").GetString(), StringComparison.Ordinal);
        Assert.Equal(2965, face.GetProperty("characters").GetInt32());
        Assert.Equal("woff2", files[1].GetProperty("container").GetString());
        Assert.Equal(2, files[1].GetProperty("faces").GetArrayLength());

        // A bare CFF font has no tables to list, but is described.
        Font("derived/SourceSans3.cff");
        Expect(0, _tool.Run("font", "info", "SourceSans3.cff"));
        File.WriteAllText(_tool.PathOf("bad.woff2"), "wOF2 but not really");
        Expect(ExitCodes.Failed, _tool.Run("font", "info", "bad.woff2"));
    }

    [Fact]
    public void ReadsWhatTheOtherCommandsWrite()
    {
        Font("SourceSans3-Regular.otf");
        Expect(0, _tool.Run("font", "subset", "SourceSans3-Regular.otf", "--text", "CFF outlines", "-o", "s.otf"));
        ToolRun info = _tool.Run("font", "info", "s.otf");
        Expect(0, info);
        Assert.Contains("OpenType font", info.Text, StringComparison.Ordinal);
        Assert.Contains("outlines: CFF;", info.Text, StringComparison.Ordinal);
    }

    private void Font(string file, string? name = null) => _tool.Copy(Path.Combine("..", "fonts", file), name ?? Path.GetFileName(file));

    private FontProbe Probe(string name) => FontLoader.Probe(File.ReadAllBytes(_tool.PathOf(name)));

    private static void Expect(int code, ToolRun run) => Assert.True(run.Code == code, $"exit {run.Code}, expected {code}: {run.Error}");
}
