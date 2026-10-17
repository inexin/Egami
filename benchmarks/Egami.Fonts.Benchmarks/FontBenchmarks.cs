using BenchmarkDotNet.Attributes;

using Egami.Fonts;

namespace Egami.Fonts.Benchmarks;

/// <summary>Loading, glyph lookup, outlines and measuring on the committed test fonts. Other libraries are timed in egami-compare.</summary>
[MemoryDiagnoser]
public class FontBenchmarks
{
    private const string Text = "The quick brown fox jumps over the lazy dog. Sphinx of black quartz, judge my vow! 0123456789";

    private byte[] _noto = [];
    private byte[] _source = [];
    private Typeface _notoFace = null!;
    private Typeface _sourceFace = null!;
    private readonly CountingSink _sink = new();

    [GlobalSetup]
    public void Setup()
    {
        string root = Root();
        _noto = File.ReadAllBytes(Path.Combine(root, "tests", "fonts", "NotoSans-Regular.ttf"));
        _source = File.ReadAllBytes(Path.Combine(root, "tests", "fonts", "SourceSans3-Regular.otf"));
        _notoFace = Typeface.FromMemory(_noto);
        _sourceFace = Typeface.FromMemory(_source);
    }

    [Benchmark]
    public string LoadTrueTypeAndName() => Typeface.FromMemory(_noto).FamilyName;

    [Benchmark]
    public string LoadCffAndName() => Typeface.FromMemory(_source).FamilyName;

    [Benchmark]
    public int OutlinesTrueType()
    {
        _sink.Count = 0;
        for (ushort g = 0; g < 500; g++)
        {
            _notoFace.GetGlyphOutline(g, _sink);
        }

        return _sink.Count;
    }

    [Benchmark]
    public int OutlinesCff()
    {
        _sink.Count = 0;
        for (ushort g = 0; g < 500; g++)
        {
            _sourceFace.GetGlyphOutline(g, _sink);
        }

        return _sink.Count;
    }

    [Benchmark]
    public float MeasureText() => new Font(_notoFace, 16).MeasureText(Text).Advance;

    private static string Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private sealed class CountingSink : IGlyphOutlineSink
    {
        public int Count { get; set; }

        public void MoveTo(float x, float y) => Count++;

        public void LineTo(float x, float y) => Count++;

        public void QuadTo(float cx, float cy, float x, float y) => Count++;

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => Count++;

        public void Close() => Count++;
    }
}
