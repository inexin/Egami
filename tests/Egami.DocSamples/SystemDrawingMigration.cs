using System.Drawing;
using System.Numerics;

using Egami.Drawing;
using Egami.Formats.Jpeg;
using Egami.Pixels;
using Egami.SystemDrawing;
using Egami.SystemDrawing.Imaging;

using EgamiFonts = Egami.Fonts;

namespace Egami.DocSamples;

/// <summary>The samples of the migration guide: from System.Drawing, through Egami.SystemDrawing, to Egami's own API.</summary>
[Collection("Samples")]
public class SystemDrawingMigration
{
    private static string Images => System.IO.Path.Combine(AppContext.BaseDirectory, "images");

    private static string FontFile => System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "DejaVuSans.ttf");

    [Fact]
    public void AsRaster()
    {
        using Bitmap bitmap = new(System.IO.Path.Combine(Images, "duck.png"));

        #region as-raster
        // A 32-bit or 24-bit bitmap's pixels as an Egami raster, no copy: Egami's operations change the bitmap itself.
        using (Bitmap argb = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb))
        {
            var view = (Raster<Bgra8>)argb.AsRaster();
            view.Apply(ops => ops.Grayscale().Sharpen(0.5f));
            Color gray = argb.GetPixel(10, 10);  // R, G and B are equal now

            // RotateFlip, a format change or SelectActiveFrame give the bitmap new pixels: fetch the view again after them.
            argb.RotateFlip(RotateFlipType.Rotate90FlipNone);
            view = (Raster<Bgra8>)argb.AsRaster();
            Assert.Equal(gray.R, gray.B);
            Assert.Equal(argb.Width, view.Width);
        }
        #endregion
    }

    [Fact]
    public void FromRaster()
    {
        #region from-raster
        // An Egami result handed back to code that still takes a Bitmap: FromRaster copies it into the matching pixel format.
        using Raster<Rgba8> badge = Raster.Create(64, 64, new Rgba8(30, 110, 200, 255));
        badge.Apply(ops => ops.Vignette());
        using Bitmap forOldCode = Bitmap.FromRaster(badge);  // 32-bit ARGB
        forOldCode.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"badge-{Guid.NewGuid():N}.png"), ImageFormat.Png);
        #endregion

        Assert.Equal(PixelFormat.Format32bppArgb, forOldCode.PixelFormat);
    }

    [Fact]
    public void Canvas()
    {
        #region canvas
        // Egami.Drawing calls in the middle of an existing Graphics: the canvas draws in the graphics' world coordinates and clip.
        using Bitmap bitmap = new(120, 80);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.TranslateTransform(10, 10);
            g.DrawRectangle(Pens.Black, 0, 0, 60, 40);                       // System.Drawing, as before
            Egami.Drawing.Canvas canvas = g.Canvas;                          // Egami, at the same place
            canvas.FillPath(Egami.Drawing.Path.Star(new Vector2(30, 20), 18, 8, 5), new SolidPaint(new Rgba8(240, 160, 0, 255)) { Antialias = true });
        }
        #endregion

        Assert.Equal(240, bitmap.GetPixel(40, 30).R);
    }

    [Fact]
    public void EgamiFont()
    {
        using Egami.SystemDrawing.Text.PrivateFontCollection fonts = new();
        fonts.AddFontFile(FontFile);
        using Egami.SystemDrawing.Font caption = new(new Egami.SystemDrawing.FontFamily("DejaVu Sans", fonts), 12);

        #region egami-font
        // The same font in Egami.Fonts: shaped text (kerning, ligatures, complex scripts), measured and drawn by Egami.Drawing.
        EgamiFonts.Font font = caption.ToEgamiFont();  // 16 pixels per em at 96 dpi, synthetic bold or italic as needed
        using Raster<Rgba8> label = Raster.Create(200, 30, new Rgba8(255, 255, 255, 255));
        using (RasterCanvas<Rgba8> canvas = label.CreateCanvas())
        {
            canvas.DrawText("Shaped by Egami", new Vector2(4, 20), font, new Rgba8(0, 0, 0, 255));
        }

        float width = font.MeasureText("Shaped by Egami").Advance;
        #endregion

        Assert.InRange(width, 50, 196);
    }

    [Fact]
    public void LoadAndSave()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string input = System.IO.Path.Combine(Images, "duck.png"), output = System.IO.Path.Combine(folder, "duck.jpg");

        #region load-save
        // Image.FromFile and Save become Raster.Load and Save; Probe reads the header alone, and each format has its own options.
        Egami.Formats.RasterInfo info = Raster.Probe(input);             // size, format, frames, without decoding pixels
        using Raster photo = Raster.Load(input);
        photo.Save(output, new JpegWriterOptions { Quality = 85 });     // instead of EncoderParameters
        #endregion

        Assert.Equal(info.Width, Raster.Probe(output).Width);
        Directory.Delete(folder, true);
    }

    [Fact]
    public void ThumbnailInThreeSteps()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string input = System.IO.Path.Combine(Images, "duck.png");

        // Step 1 and 2 keep the System.Drawing signature, so callers do not change.
        Step1(input, System.IO.Path.Combine(folder, "step1.jpg"));
        Step2(input, System.IO.Path.Combine(folder, "step2.jpg"));
        Step3(input, System.IO.Path.Combine(folder, "step3.jpg"));
        foreach (string step in new[] { "step1", "step2", "step3" })
        {
            Assert.Equal(200, Raster.Probe(System.IO.Path.Combine(folder, step + ".jpg")).Width);
        }

        Directory.Delete(folder, true);
    }

    #region step1
    // Step 1: the System.Drawing code as it was, compiled against Egami.SystemDrawing.
    private static void Step1(string input, string output)
    {
        using Image source = Image.FromFile(input);
        int height = source.Height * 200 / source.Width;
        using Bitmap thumb = new(200, height);
        using (Graphics g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = Egami.SystemDrawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, new Rectangle(0, 0, 200, height));
            using Egami.SystemDrawing.Font font = new("Arial", 10);
            g.DrawString("© Egami", font, Brushes.White, 4, height - 20);
        }

        thumb.Save(output, ImageFormat.Jpeg);
    }
    #endregion

    #region step2
    // Step 2: the hot spot (scaling) moves to Egami; the bitmap stays at the edges, so the watermark code is untouched.
    private static void Step2(string input, string output)
    {
        using Raster loaded = Raster.Load(input);
        using Raster<Rgba8> small = loaded.As<Rgba8>().Derive(ops => ops.Resize(200, 0));
        using Bitmap thumb = Bitmap.FromRaster(small);
        using (Graphics g = Graphics.FromImage(thumb))
        {
            using Egami.SystemDrawing.Font font = new("Arial", 10);
            g.DrawString("© Egami", font, Brushes.White, 4, thumb.Height - 20);
        }

        thumb.Save(output, ImageFormat.Jpeg);
    }
    #endregion

    #region step3
    // Step 3: all Egami: load, resize, draw the watermark on the raster's canvas, save with the JPEG options.
    private static void Step3(string input, string output)
    {
        using Raster loaded = Raster.Load(input);
        using Raster<Rgba8> thumb = loaded.As<Rgba8>().Derive(ops => ops.Resize(200, 0));
        EgamiFonts.Font font = new(EgamiFonts.Typeface.FromFile(FontFile), 13);
        using (RasterCanvas<Rgba8> canvas = thumb.CreateCanvas())
        {
            canvas.DrawText("© Egami", new Vector2(6, thumb.Height - 8), font, new Rgba8(255, 255, 255, 255));
        }

        thumb.Save(output, new JpegWriterOptions { Quality = 85 });
    }
    #endregion
}
