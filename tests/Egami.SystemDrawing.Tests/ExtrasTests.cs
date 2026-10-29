using System.Runtime.InteropServices;

using Egami.Pixels;
using Egami.SystemDrawing.Imaging.Effects;

namespace Egami.SystemDrawing.Tests;

/// <summary>
/// Metafiles, effects, buffers, the animator and the escape hatches, checked against what GDI+ gives on Windows (egami-compare,
/// sysdrawing ExtrasProbes): the expected values here are copied from those measurements.
/// </summary>
public class ExtrasTests
{
    private static string Metafiles(string name) => TestFiles.Image(System.IO.Path.Combine("metafiles", "synthetic"), name);

    [Fact]
    public void EmfHeaderAndSizeAreGdiPlus()
    {
        using Metafile mf = new(Metafiles("gdi-shapes-basic.emf"));
        Assert.Equal((201, 151), (mf.Width, mf.Height));
        Assert.Equal(106.01739, mf.HorizontalResolution, 4);
        Assert.Equal(PixelFormat.Format32bppRgb, mf.PixelFormat);
        Assert.Equal(ImageFormat.Emf, mf.RawFormat);
        Assert.Equal(327683, mf.Flags);
        GraphicsUnit unit = GraphicsUnit.Inch;
        RectangleF bounds = mf.GetBounds(ref unit);
        Assert.Equal(201.0139, bounds.Width, 3);
        Assert.Equal(GraphicsUnit.Pixel, unit);
        Assert.Equal(4815.958, mf.PhysicalDimension.Width, 1);
        MetafileHeader h = mf.GetMetafileHeader();
        Assert.Equal((MetafileType.Emf, 65536, 456), (h.Type, h.Version, h.MetafileSize));
        Assert.Equal(new Rectangle(0, 0, 201, 151), h.Bounds);
        Assert.True(h.IsEmf() && h.IsEmfOrEmfPlus() && !h.IsEmfPlus() && !h.IsDisplay());
    }

    [Fact]
    public void EmfPlusAndPlaceableWmfHeadersAreGdiPlus()
    {
        MetafileHeader plus = Metafile.GetMetafileHeader(Metafiles("plus-shapes.dual.emf"));
        Assert.Equal((MetafileType.EmfPlusDual, unchecked((int)0xDBC01002), 96, 28), (plus.Type, plus.Version, plus.LogicalDpiX, plus.EmfPlusHeaderSize));
        Assert.True(plus.IsDisplay());
        Assert.Equal(new Rectangle(0, 0, 200, 150), plus.Bounds);

        using Metafile wmf = new(Metafiles("gdi-shapes-basic.wmf"));
        MetafileHeader h = wmf.GetMetafileHeader();
        Assert.Equal((MetafileType.WmfPlaceable, 768, 226), (h.Type, h.Version, h.MetafileSize));
        Assert.Equal((200, 150, 96f), (wmf.Width, wmf.Height, wmf.HorizontalResolution));
        Assert.Equal((1, 9, 113, 2, 9), (h.WmfHeader.Type, h.WmfHeader.HeaderSize, h.WmfHeader.Size, h.WmfHeader.NoObjects, h.WmfHeader.MaxRecord));
        Assert.Equal(ImageFormat.Wmf, wmf.RawFormat);
    }

    [Theory]
    [InlineData("gdi-shapes-basic.wmf", 19, "10103/0/2 1020B/0/4 1020C/0/4", "101F0/0/2")]
    [InlineData("gdi-shapes-basic.emf", 17, "1/0/168 27/0/16 25/0/4", "E/0/12")]
    [InlineData("plus-shapes.dual.emf", 11, "1/0/100 4001/1/16 401E/9/0", "E/0/12")]
    public void EnumerationListsTheRecordsGdiPlusLists(string file, int count, string first, string last)
    {
        using Metafile mf = new(Metafiles(file));
        List<string> records = [];
        using Bitmap target = new(8, 8);
        using (Graphics g = Graphics.FromImage(target))
        {
            g.EnumerateMetafile(mf, new PointF(0, 0), (type, flags, size, data, play) =>
            {
                records.Add($"{(int)type:X}/{flags:X}/{size}");
                Assert.Null(play);
                return true;
            });
        }

        Assert.Equal(count, records.Count);
        Assert.Equal(first, string.Join(" ", records.Take(3)));
        Assert.Equal(last, records[^1]);
    }

    [Fact]
    public void MetafilesLoadAsImagesAndRefuseWhatGdiPlusRefuses()
    {
        using Image image = Image.FromFile(Metafiles("gdi-shapes-basic.emf"));
        Assert.IsType<Metafile>(image);
        Assert.IsType<ExternalException>(Record.Exception(() => Graphics.FromImage(image)), exactMatch: false);
        Assert.Throws<NotImplementedException>(() => image.RotateFlip(RotateFlipType.Rotate90FlipNone));
        Assert.IsType<Metafile>(image.Clone());
        using Bitmap raster = new(image);
        Assert.Equal((201, 151, PixelFormat.Format32bppArgb), (raster.Width, raster.Height, raster.PixelFormat));
        Assert.IsType<ExternalException>(Record.Exception(() => new Metafile(TestFiles.Image("metafiles", "README.md"))), exactMatch: false);
    }

    [Fact]
    public void ToneEffectsMatchGdiPlusCurves()
    {
        // Measured on a gray ramp in GDI+ (ExtrasProbes.EffectCurves).
        static byte[] Ramp(Effect effect)
        {
            using Bitmap b = new(256, 1);
            for (int x = 0; x < 256; x++)
            {
                b.SetPixel(x, 0, Color.FromArgb(255, x, x, x));
            }

            b.ApplyEffect(effect, Rectangle.Empty);
            return [.. Enumerable.Range(0, 256).Select(x => b.GetPixel(x, 0).R)];
        }

        Near([40, 72, 168, 255], Pick(Ramp(new BrightnessContrastEffect(40, 0))));
        Near([0, 37, 128, 220], Pick(Ramp(new BrightnessContrastEffect(0, 30)), 0, 64, 128, 192));
        Near([0, 40, 160, 255], Pick(Ramp(new LevelsEffect(80, 0, 0)), 0, 32, 128, 224));
        Near([55, 174, 237], Pick(Ramp(new LevelsEffect(100, 40, 0)), 16, 128, 224));
        Near([0, 27, 155], Pick(Ramp(new ExposureCurveEffect(CurveChannel.All, -100)), 64, 128, 255));
        Near([22, 90, 255], Pick(Ramp(new ColorBalanceEffect(40, 0, 0)), 16, 64, 192));
        Near([0, 5, 89], Pick(Ramp(new BlackSaturationCurveEffect(CurveChannel.All, 60)), 32, 64, 128));
    }

    [Fact]
    public void ColorEffectsAreExact()
    {
        using Bitmap b = new(1, 1);
        b.SetPixel(0, 0, Color.FromArgb(255, 200, 100, 50));
        b.ApplyEffect(new InvertEffect(), Rectangle.Empty);
        Assert.Equal(Color.FromArgb(255, 55, 155, 205).ToArgb(), b.GetPixel(0, 0).ToArgb());
        b.ApplyEffect(new GrayScaleEffect(), Rectangle.Empty);
        Color g = b.GetPixel(0, 0);
        Assert.Equal(g.R, g.B);
        Assert.Throws<ArgumentException>(() => new BlurEffect(300, false));
        Assert.Equal(256, new ColorLookupTableEffect(new byte[10], new byte[256], new byte[256], new byte[256]).RedLookupTable.Length);
    }

    [Fact]
    public void BufferedGraphicsRendersPremultipliedColorsOpaque()
    {
        using Bitmap target = new(40, 30);
        using Graphics g = Graphics.FromImage(target);
        g.Clear(Color.White);
        using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(g, new Rectangle(5, 4, 20, 15)))
        {
            Assert.Equal(new RectangleF(5, 4, 20, 15), buffer.Graphics.VisibleClipBounds);
            using SolidBrush red = new(Color.FromArgb(128, 255, 0, 0));
            buffer.Graphics.FillRectangle(red, 0, 0, 40, 30);
            buffer.Render(g);
        }

        Assert.Equal(Color.White.ToArgb(), target.GetPixel(4, 4).ToArgb());
        Assert.Equal(Color.FromArgb(255, 128, 0, 0).ToArgb(), target.GetPixel(5, 4).ToArgb());
        Assert.Equal(Color.FromArgb(255, 128, 0, 0).ToArgb(), target.GetPixel(24, 18).ToArgb());
        Assert.Equal(Color.White.ToArgb(), target.GetPixel(25, 19).ToArgb());
        Assert.Equal(new System.Drawing.Size(225, 96), BufferedGraphicsManager.Current.MaximumBuffer);
    }

    [Fact]
    public void AnimatorStepsThroughGifFrames()
    {
        using Image gif = Image.FromFile(TestFiles.Image("gif", "loop-3.gif"));
        using Bitmap still = new(4, 4);
        Assert.True(ImageAnimator.CanAnimate(gif));
        Assert.False(ImageAnimator.CanAnimate(still));
        Assert.False(ImageAnimator.CanAnimate(null));
        using ManualResetEventSlim changed = new();
        void OnChanged(object? sender, EventArgs e) => changed.Set();
        ImageAnimator.Animate(gif, OnChanged);
        try
        {
            Assert.True(changed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            ImageAnimator.UpdateFrames(gif);
        }
        finally
        {
            ImageAnimator.StopAnimate(gif, OnChanged);
        }
    }

    [Fact]
    public void AsRasterIsAViewAndFromRasterACopy()
    {
        using Bitmap bitmap = new(4, 3);
        Raster view = bitmap.AsRaster();
        Raster<Bgra8> bgra = Assert.IsType<Raster<Bgra8>>(view);
        bgra.Row(1)[2] = new Bgra8(10, 20, 30, 255);
        Assert.Equal(Color.FromArgb(255, 30, 20, 10).ToArgb(), bitmap.GetPixel(2, 1).ToArgb());
        using Bitmap copy = Bitmap.FromRaster(view);
        Assert.Equal(PixelFormat.Format32bppArgb, copy.PixelFormat);
        Assert.Equal(Color.FromArgb(255, 30, 20, 10).ToArgb(), copy.GetPixel(2, 1).ToArgb());
        using Bitmap indexed = new(2, 2, PixelFormat.Format8bppIndexed);
        Assert.IsType<Raster<Rgba8>>(indexed.AsRaster());
    }

    [Fact]
    public void CanvasDrawsInWorldCoordinates()
    {
        using Bitmap bitmap = new(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.TranslateTransform(10, 10);
            g.Canvas.FillRect(new Egami.Drawing.RectF(0, 0, 4, 4), new Rgba8(255, 0, 0, 255));
        }

        Assert.Equal(255, bitmap.GetPixel(11, 11).R);
        Assert.Equal(0, bitmap.GetPixel(5, 5).A);
        using Bitmap rgb = new(4, 4, PixelFormat.Format24bppRgb);
        using Graphics h = Graphics.FromImage(rgb);
        Assert.Throws<InvalidOperationException>(() => h.Canvas);
    }

    /// <summary>Within a level of GDI+'s value, as Egami's tone tables round a little differently.</summary>
    private static void Near(int[] expected, byte[] actual) =>
        Assert.All(expected.Zip(actual), p => Assert.InRange(p.Second, p.First - 1, p.First + 1));

    private static byte[] Pick(byte[] values, params int[] at) => at.Length == 0 ? [values[0], values[32], values[128], values[224]] : [.. at.Select(i => values[i])];

    [Theory]
    [InlineData("offset-frame.emf")]
    [InlineData("offset-box.wmf")]
    public void AWholeMetafileIsDrawnFromItsFrameWhereverTheFrameStarts(string name)
    {
        // gdi-shapes-basic with the EMF frame (or the placeable WMF box) moved off (0, 0): GDI+ takes a whole image's source from
        // GetBounds, so drawing it whole equals drawing its frame rectangle as the source.
        using Metafile mf = new(Metafiles(name));
        GraphicsUnit unit = GraphicsUnit.Pixel;
        RectangleF frame = mf.GetBounds(ref unit);
        Assert.True(frame.X > 0 && frame.Y > 0);
        using Bitmap whole = Draw(g => g.DrawImage(mf, new Rectangle(0, 0, 120, 90)));
        using Bitmap framed = Draw(g => g.DrawImage(mf, new RectangleF(0, 0, 120, 90), frame, GraphicsUnit.Pixel));
        using Bitmap fromOrigin = Draw(g => g.DrawImage(mf, new RectangleF(0, 0, 120, 90), new RectangleF(0, 0, frame.Width, frame.Height), GraphicsUnit.Pixel));
        Assert.Equal(Pixels(framed), Pixels(whole));
        Assert.NotEqual(Pixels(fromOrigin), Pixels(whole));

        static Bitmap Draw(Action<Graphics> draw)
        {
            Bitmap b = new(120, 90);
            using Graphics g = Graphics.FromImage(b);
            g.Clear(Color.White);
            draw(g);
            return b;
        }

        static int[] Pixels(Bitmap b)
        {
            int[] p = new int[b.Width * b.Height];
            for (int y = 0; y < b.Height; y++)
            {
                for (int x = 0; x < b.Width; x++)
                {
                    p[(y * b.Width) + x] = b.GetPixel(x, y).ToArgb();
                }
            }

            return p;
        }
    }

    [Fact]
    public void AMetafileWithAHugeFrameLoadsAndDrawsItsPixelsOnlyWhenAsked()
    {
        // Fuzz finding (step 2.13 task 34): the frame was rasterized at load, overflowing for a frame of billions of pixels.
        byte[] data = File.ReadAllBytes(TestFiles.Image(System.IO.Path.Combine("fuzz", "sysdrawing-regressions"), "huge-frame.emf"));
        using Image image = Image.FromStream(new MemoryStream(data));
        Assert.True((long)image.Width * image.Height > 1L << 31);
        using (Bitmap target = new(16, 16))
        using (Graphics g = Graphics.FromImage(target))
        {
            g.DrawImage(image, new Rectangle(0, 0, 16, 16));
        }

        Assert.Throws<OutOfMemoryException>(() => image.Save(new MemoryStream(), ImageFormat.Png));
    }

    [Fact]
    public void AMetafileSavesAsPng()
    {
        using Metafile mf = new(Metafiles("gdi-shapes-basic.emf"));
        using MemoryStream png = new();
        mf.Save(png, ImageFormat.Png);
        png.Position = 0;
        using Image back = Image.FromStream(png);
        Assert.Equal((mf.Width, mf.Height), (back.Width, back.Height));
    }
}
