using BenchmarkDotNet.Attributes;

using Egami.Fonts;

namespace Egami.Fonts.Benchmarks;

/// <summary>Subsets of the committed CFF test fonts for a line of text: a CFF2 instance, and a CFF font with and without hints.</summary>
[MemoryDiagnoser]
public class SubsetBenchmarks
{
    private const string Text = "The quick brown fox jumps over the lazy dog. Sphinx of black quartz, judge my vow! 0123456789";

    private Typeface _serifAt800 = null!;
    private Typeface _source = null!;
    private int[] _characters = [];
    private ushort[] _serifGlyphs = [];
    private ushort[] _sourceGlyphs = [];

    [GlobalSetup]
    public void Setup()
    {
        string fonts = Path.Combine(Root(), "tests", "fonts");
        _serifAt800 = Typeface.FromFile(Path.Combine(fonts, "derived", "SourceSerif4-VF-subset.otf")).WithVariations(FontVariation.Weight(800));
        _source = Typeface.FromFile(Path.Combine(fonts, "SourceSans3-Regular.otf"));
        _characters = [.. Text.Distinct().Select(c => (int)c)];
        _serifGlyphs = [.. _characters.Select(c => _serifAt800.GetGlyph(c))];
        _sourceGlyphs = [.. _characters.Select(c => _source.GetGlyph(c))];
    }

    /// <summary>A CFF2 face's static instance, with its layout tables.</summary>
    [Benchmark]
    public int Cff2Instance() => FontSubsetter.Subset(_serifAt800, _serifGlyphs, new SubsetOptions { Characters = _characters, LayoutClosure = true }).Data.Length;

    [Benchmark]
    public int Cff() => FontSubsetter.Subset(_source, _sourceGlyphs).Data.Length;

    [Benchmark]
    public int CffWithoutHints() => FontSubsetter.Subset(_source, _sourceGlyphs, new SubsetOptions { DropHinting = true }).Data.Length;

    private static string Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
