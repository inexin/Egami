using System.Numerics;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using Egami.Drawing;
using Egami.Pixels;
using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Text;

using EgamiFonts = Egami.Fonts;
using SdRectangle = System.Drawing.Rectangle;

namespace Egami.SystemDrawing.Benchmarks;

/// <summary>
/// The overhead of the drop-in over Egami: each category pairs a System.Drawing-style call with the native Egami calls that do the
/// same work on a 512 by 512 image. The gate is at most 10 % over the baseline.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class DropInBenchmarks : IDisposable
{
    private const int Size = 512;
    private const string Line = "The quick brown fox jumps over the lazy dog";

    private Bitmap _argb = null!;
    private Bitmap _parb = null!;
    private Graphics _argbGraphics = null!;
    private Graphics _parbGraphics = null!;
    private Raster<Bgra8> _raster = null!;
    private Raster<PremulBgra8> _premul = null!;
    private RasterCanvas<Bgra8> _canvas = null!;
    private RasterCanvas<PremulBgra8> _premulCanvas = null!;
    private Bitmap _photo = null!;
    private Raster<Rgba8> _photoRaster = null!;
    private byte[] _png = [];
    private Font _font = null!;
    private EgamiFonts.Font _egamiFont;
    private PrivateFontCollection _fonts = null!;

    [GlobalSetup]
    public void Setup()
    {
        _argb = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        _parb = new Bitmap(Size, Size, PixelFormat.Format32bppPArgb);
        _argbGraphics = Graphics.FromImage(_argb);
        _parbGraphics = Graphics.FromImage(_parb);
        _argbGraphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        _parbGraphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        _raster = Raster.Create<Bgra8>(Size, Size);
        _premul = Raster.Create<PremulBgra8>(Size, Size);
        _canvas = new RasterCanvas<Bgra8>(_raster);
        _premulCanvas = new RasterCanvas<PremulBgra8>(_premul);
        string duck = System.IO.Path.Combine(AppContext.BaseDirectory, "duck.png");
        _png = File.ReadAllBytes(duck);
        _photo = new Bitmap(duck);
        using Raster loaded = Raster.Load(_png);
        _photoRaster = loaded.As<Rgba8>();
        _fonts = new PrivateFontCollection();
        _fonts.AddFontFile(System.IO.Path.Combine(AppContext.BaseDirectory, "DejaVuSans.ttf"));
        _font = new Font(new FontFamily("DejaVu Sans", _fonts), 12);
        _egamiFont = _font.ToEgamiFont() with { Kerning = false };
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _argbGraphics.Dispose();
        _parbGraphics.Dispose();
        _argb.Dispose();
        _parb.Dispose();
        _canvas.Dispose();
        _premulCanvas.Dispose();
        _raster.Dispose();
        _premul.Dispose();
        _photo.Dispose();
        _photoRaster.Dispose();
        _fonts.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FillRectangle PArgb")]
    public void FillRectEgamiPremul() => _premulCanvas.FillRect(new RectF(10, 10, 490, 490), new SolidPaint(new Rgba8(200, 30, 30, 255)));

    [Benchmark]
    [BenchmarkCategory("FillRectangle PArgb")]
    public void FillRectanglePArgb() => _parbGraphics.FillRectangle(Brushes.Firebrick, 10, 10, 490, 490);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FillRectangle Argb")]
    public void FillRectEgami() => _canvas.FillRect(new RectF(10, 10, 490, 490), new SolidPaint(new Rgba8(200, 30, 30, 255)));

    [Benchmark]
    [BenchmarkCategory("FillRectangle Argb")]
    public void FillRectangleArgb() => _argbGraphics.FillRectangle(Brushes.Firebrick, 10, 10, 490, 490);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("FillEllipse AntiAlias")]
    public void FillOvalEgami() => _premulCanvas.FillOval(new RectF(10, 10, 490, 490), new SolidPaint(new Rgba8(30, 30, 200, 255)) { Antialias = true });

    [Benchmark]
    [BenchmarkCategory("FillEllipse AntiAlias")]
    public void FillEllipseAntiAlias()
    {
        _parbGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        _parbGraphics.FillEllipse(Brushes.Navy, 10, 10, 490, 490);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("DrawImage scaled")]
    public void DrawImageEgami() => _premulCanvas.DrawImage(_photoRaster, new RectF(0, 0, Size, Size));

    [Benchmark]
    [BenchmarkCategory("DrawImage scaled")]
    public void DrawImageScaled() => _parbGraphics.DrawImage(_photo, new SdRectangle(0, 0, Size, Size));

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("DrawString")]
    public void DrawTextEgami()
    {
        for (int i = 0; i < 20; i++)
        {
            _premulCanvas.DrawText(Line, new Vector2(4, 20 + (i * 24)), _egamiFont, new SolidPaint(new Rgba8(0, 0, 0, 255)));
        }
    }

    [Benchmark]
    [BenchmarkCategory("DrawString")]
    public void DrawString()
    {
        for (int i = 0; i < 20; i++)
        {
            _parbGraphics.DrawString(Line, _font, Brushes.Black, 2, 4 + (i * 24));
        }
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("MeasureString")]
    public float MeasureEgami() => _egamiFont.MeasureText(Line).Advance;

    [Benchmark]
    [BenchmarkCategory("MeasureString")]
    public float MeasureString() => _parbGraphics.MeasureString(Line, _font).Width;

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Load PNG")]
    public void LoadEgami()
    {
        using Raster raster = Raster.Load(_png);
    }

    [Benchmark]
    [BenchmarkCategory("Load PNG")]
    public void ImageFromStream()
    {
        using MemoryStream stream = new(_png);
        using Image image = Image.FromStream(stream);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Save PNG")]
    public void SaveEgami()
    {
        using MemoryStream stream = new();
        _photoRaster.Save(stream, "png");
    }

    [Benchmark]
    [BenchmarkCategory("Save PNG")]
    public void ImageSave()
    {
        using MemoryStream stream = new();
        _photo.Save(stream, ImageFormat.Png);
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Pixels")]
    public long SumEgami()
    {
        long sum = 0;
        for (int y = 0; y < Size; y++)
        {
            foreach (Bgra8 p in _raster.Row(y))
            {
                sum += p.G;
            }
        }

        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("Pixels")]
    public unsafe long SumLockBits()
    {
        BitmapData data = _argb.LockBits(new SdRectangle(0, 0, Size, Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        long sum = 0;
        try
        {
            for (int y = 0; y < Size; y++)
            {
                foreach (Bgra8 p in new ReadOnlySpan<Bgra8>((byte*)data.Scan0 + (y * data.Stride), Size))
                {
                    sum += p.G;
                }
            }
        }
        finally
        {
            _argb.UnlockBits(data);
        }

        return sum;
    }
}
