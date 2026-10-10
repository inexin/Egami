using BenchmarkDotNet.Attributes;

using Egami.Colors;

namespace Egami.Benchmarks;

/// <summary>
/// Phase 2.1.2 compiled color transforms on one 4000-pixel row, single-threaded: the matrix form (Display P3 to sRGB) in 8 and
/// 32 bits, and grids (RGB to CMYK, CMYK to RGB) in 8 bits. Rgba8 and Cmyk8 layouts, so alpha is skipped as a raster would.
/// </summary>
[MemoryDiagnoser]
public class ColorTransformBenchmarks
{
    private const int Width = 4000;
    private readonly byte[] _rgba = new byte[Width * 4];
    private readonly byte[] _cmyk = new byte[Width * 4];
    private readonly byte[] _out = new byte[Width * 4];
    private readonly float[] _rgbF = new float[Width * 3];
    private readonly float[] _outF = new float[Width * 3];
    private CompiledTransform _matrix = null!;
    private CompiledTransform _toCmyk = null!;
    private CompiledTransform _fromCmyk = null!;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(1);
        random.NextBytes(_rgba);
        random.NextBytes(_cmyk);
        for (int i = 0; i < _rgbF.Length; i++)
        {
            _rgbF[i] = (float)random.NextDouble();
        }

        string icc = Path.Combine(AppContext.BaseDirectory, "images", "icc", "synthetic", "cmyk-lut16-v2.icc");
        ColorProfile cmyk = File.Exists(icc) ? ColorProfile.FromIcc(File.ReadAllBytes(icc)) : FindCmyk();
        _matrix = CompiledTransform.Compile(ColorProfile.DisplayP3, ColorProfile.Srgb, RenderingIntent.Perceptual, false);
        _toCmyk = CompiledTransform.Compile(ColorProfile.Srgb, cmyk, RenderingIntent.Perceptual, false);
        _fromCmyk = CompiledTransform.Compile(cmyk, ColorProfile.Srgb, RenderingIntent.Perceptual, false);
    }

    [Benchmark]
    public void MatrixRgba8() => _matrix.Apply(_rgba, new ByteLayout(4, false), _out, new ByteLayout(4, false), Width);

    [Benchmark]
    public void MatrixRgbF32() => _matrix.Apply(_rgbF, _outF, Width);

    [Benchmark]
    public void GridRgba8ToCmyk8() => _toCmyk.Apply(_rgba, new ByteLayout(4, false), _out, new ByteLayout(4, false), Width);

    [Benchmark]
    public void GridCmyk8ToRgba8() => _fromCmyk.Apply(_cmyk, new ByteLayout(4, false), _out, new ByteLayout(4, false), Width);

    private static ColorProfile FindCmyk()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string path = Path.Combine(d.FullName, "tests", "images", "icc", "synthetic", "cmyk-lut16-v2.icc");
            if (File.Exists(path))
            {
                return ColorProfile.FromIcc(File.ReadAllBytes(path));
            }
        }

        throw new FileNotFoundException("tests/images/icc/synthetic/cmyk-lut16-v2.icc");
    }
}
