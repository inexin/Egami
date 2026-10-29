using System.Drawing;
using System.Runtime.InteropServices;

using Egami.SystemDrawing;
using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Imaging.Effects;

namespace Egami.DocSamples;

/// <summary>The samples of the System.Drawing drop-in guide (Egami.SystemDrawing).</summary>
[Collection("Samples")]
public class SystemDrawingPackage
{
    private static string Images => System.IO.Path.Combine(AppContext.BaseDirectory, "images");

    [Fact]
    public void Bitmaps()
    {
        string output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-{Guid.NewGuid():N}.png");
        try
        {
            #region bitmap
            // The same code as with System.Drawing.Common: only the using lines name Egami.SystemDrawing.
            using Bitmap bitmap = new(64, 32);
            for (int x = 0; x < 64; x++)
            {
                bitmap.SetPixel(x, 16, Color.FromArgb(255, x * 4, 0, 255 - (x * 4)));
            }

            Color middle = bitmap.GetPixel(32, 16);  // ARGB(255, 128, 0, 127)
            bitmap.SetResolution(300, 300);
            bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
            bitmap.Save(output, ImageFormat.Png);

            GraphicsUnit unit = GraphicsUnit.Inch;
            RectangleF bounds = bitmap.GetBounds(ref unit);  // 32 by 64, in pixels
            #endregion

            Assert.Equal(Color.FromArgb(255, 128, 0, 127), middle);
            Assert.Equal((32f, 64f, GraphicsUnit.Pixel), (bounds.Width, bounds.Height, unit));
            using Image back = Image.FromFile(output);
            Assert.Equal(300, back.HorizontalResolution, 1);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void PixelFormats()
    {
        #region pixel-formats
        // Every GDI+ pixel format, with GDI+'s conversions: 5-bit channels truncate when written and repeat their high bits when read.
        using Bitmap rgb555 = new(1, 1, PixelFormat.Format16bppRgb555);
        rgb555.SetPixel(0, 0, Color.FromArgb(10, 20, 30));
        Color read = rgb555.GetPixel(0, 0);  // ARGB(255, 8, 16, 24)

        // 48 and 64-bit formats hold linear light from 0 to 8192, as in GDI+.
        using Bitmap deep = new(1, 1, PixelFormat.Format48bppRgb);
        deep.SetPixel(0, 0, Color.FromArgb(255, 200, 100, 50));
        int bits = Image.GetPixelFormatSize(deep.PixelFormat);                                   // 48
        bool alpha = Image.IsAlphaPixelFormat(PixelFormat.Format64bppArgb);                      // true
        ImageFlags flags = (ImageFlags)new Bitmap(1, 1, PixelFormat.Format32bppArgb).Flags;     // HasAlpha
        #endregion

        Assert.Equal(Color.FromArgb(255, 8, 16, 24), read);
        Assert.Equal(Color.FromArgb(255, 200, 100, 50), deep.GetPixel(0, 0));
        Assert.True(bits == 48 && alpha && flags == ImageFlags.HasAlpha);
    }

    [Fact]
    public void LockBits()
    {
        using Bitmap bitmap = new(16, 8, PixelFormat.Format24bppRgb);

        #region lockbits
        // Locked in its own format, the bitmap's pixels are its own memory; in another format they are a converted copy that
        // goes back into the bitmap on UnlockBits.
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, 16, 8), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        byte[] row = new byte[data.Stride];
        for (int y = 0; y < data.Height; y++)
        {
            Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
            for (int x = 0; x < data.Width; x++)
            {
                row[(x * 4) + 2] = 255;  // red
            }

            Marshal.Copy(row, 0, data.Scan0 + (y * data.Stride), row.Length);
        }

        bitmap.UnlockBits(data);
        #endregion

        Assert.Equal(Color.FromArgb(255, 255, 0, 0), bitmap.GetPixel(7, 5));
    }

    [Fact]
    public void Codecs()
    {
        using Bitmap photo = new(System.IO.Path.Combine(Images, "duck.png"));

        #region codecs
        // The encoders GDI+ lists, by MIME type, with encoder parameters for JPEG quality and TIFF compression.
        ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using EncoderParameters settings = new(1);
        settings.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
        using MemoryStream file = new();
        photo.Save(file, jpeg, settings);

        file.Position = 0;
        using Image loaded = Image.FromStream(file);
        bool fromJpeg = loaded.RawFormat.Equals(ImageFormat.Jpeg);                           // true
        PixelFormat format = loaded.PixelFormat;                                             // Format24bppRgb
        bool readOnly = ((ImageFlags)loaded.Flags).HasFlag(ImageFlags.ReadOnly);             // true
        ImageCodecFlags can = jpeg.Flags;                                                    // Encoder, Decoder, SupportBitmap, Builtin
        EncoderParameterValueType type = settings.Param[0].ValueType;                        // ValueTypeLong
        EncoderValue lzw = EncoderValue.CompressionLZW;                                      // for Encoder.Compression and TIFF
        #endregion

        Assert.True(fromJpeg && readOnly && format == PixelFormat.Format24bppRgb);
        Assert.True(can.HasFlag(ImageCodecFlags.Encoder) && type == EncoderParameterValueType.ValueTypeLong && lzw == EncoderValue.CompressionLZW);
    }

    [Fact]
    public void Palettes()
    {
        #region palettes
        // Indexed bitmaps keep GDI+'s default palettes; Palette hands out a copy to change and set back.
        using Bitmap indexed = new(4, 4, PixelFormat.Format8bppIndexed);
        ColorPalette palette = indexed.Palette;
        palette.Entries[0] = Color.Navy;
        indexed.Palette = palette;
        bool halftone = (palette.Flags & (int)PaletteFlags.Halftone) != 0;  // true

        // ConvertFormat maps to a fixed palette, and Clone to an indexed format uses GDI+'s halftone palettes.
        using Bitmap photo = new(System.IO.Path.Combine(Images, "duck.png"));
        photo.ConvertFormat(PixelFormat.Format8bppIndexed, DitherType.None, PaletteType.FixedHalftone252);
        using Bitmap bilevel = photo.Clone(new Rectangle(0, 0, photo.Width, photo.Height), PixelFormat.Format1bppIndexed);
        #endregion

        Assert.True(halftone);
        Assert.Equal(Color.Navy.ToArgb(), indexed.GetPixel(0, 0).ToArgb());
        Assert.Equal(252, photo.Palette.Entries.Length);
        Assert.Equal(2, bilevel.Palette.Entries.Length);
    }

    [Fact]
    public void Frames()
    {
        #region frames
        // An animated GIF counts its frames in time; property items hold its delays (0x5100) and loop count (0x5101).
        using Image animation = Image.FromFile(System.IO.Path.Combine(Images, "loop.gif"));
        FrameDimension time = new(animation.FrameDimensionsList[0]);  // FrameDimension.Time
        int frames = animation.GetFrameCount(time);
        PropertyItem delays = animation.GetPropertyItem(0x5100)!;     // 4 bytes a frame, in hundredths of a second
        animation.SelectActiveFrame(time, frames - 1);

        // Thumbnails are 32-bit premultiplied.
        using Image thumbnail = animation.GetThumbnailImage(16, 12, null, IntPtr.Zero);
        #endregion

        Assert.Equal(FrameDimension.Time, time);
        Assert.Equal(frames * 4, delays.Len);
        Assert.Equal(PixelFormat.Format32bppPArgb, thumbnail.PixelFormat);
    }

    [Fact]
    public void Icons()
    {
        #region icon
        // An icon shows the image nearest the size asked for, as System.Drawing chooses it, and saves the file it came from.
        using Icon icon = new(System.IO.Path.Combine(Images, "app.ico"), 24, 24);
        using Bitmap image = icon.ToBitmap();  // 24 by 24, 32-bit ARGB
        using MemoryStream copy = new();
        icon.Save(copy);
        #endregion

        Assert.Equal(24, image.Width);
        Assert.True(copy.Length > 0);
    }

    [Fact]
    public void GraphicsBasics()
    {
        #region graphics
        // Draw on a bitmap as with System.Drawing: GDI+'s defaults are aliased shapes and whole-pixel centers.
        using Bitmap bitmap = new(80, 60);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PageUnit = GraphicsUnit.Millimeter;  // 1 mm is 96 / 25.4 pixels at the bitmap's 96 dpi
            using Pen pen = new(Color.Navy, 0.5f);
            g.DrawEllipse(pen, 2, 2, 15, 10);
        }

        Color corner = bitmap.GetPixel(0, 0);  // white
        #endregion

        Assert.Equal(Color.White.ToArgb(), corner.ToArgb());
    }

    [Fact]
    public void GraphicsTransforms()
    {
        #region transforms
        // Transforms prepend by default, so the last call applies first; Save and Restore bring settings back.
        using Bitmap bitmap = new(64, 64);
        using Graphics g = Graphics.FromImage(bitmap);
        g.TranslateTransform(32, 32);
        g.RotateTransform(45);
        GraphicsState saved = g.Save();
        g.ScaleTransform(2, 2);
        PointF[] points = [new(1, 0)];
        g.TransformPoints(CoordinateSpace.Device, CoordinateSpace.World, points);  // (33.41, 33.41)
        g.Restore(saved);

        using Matrix matrix = new();
        matrix.Rotate(90);
        matrix.Translate(10, 0, MatrixOrder.Append);
        float[] elements = matrix.Elements;  // 0, 1, -1, 0, 10, 0
        #endregion

        Assert.Equal(33.414, points[0].X, 2);
        Assert.Equal(10f, elements[4]);
    }

    [Fact]
    public void GraphicsClipping()
    {
        #region clipping
        // The clip is kept in device pixels, as GDI+ keeps it: changing the transform afterwards does not move it.
        using Bitmap bitmap = new(64, 48);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SetClip(new Rectangle(10, 10, 20, 20));
        g.ExcludeClip(new Rectangle(15, 15, 5, 5));
        g.TranslateTransform(100, 0);
        RectangleF bounds = g.ClipBounds;  // (-90, 10, 20, 20) in the new world coordinates
        g.ResetTransform();
        using SolidBrush brush = new(Color.Red);
        g.FillRectangle(brush, 0, 0, 64, 48);  // paints only inside the clip
        using Region region = new(new Rectangle(0, 0, 4, 4));
        region.Union(new RectangleF(2.5f, 2.5f, 4, 4));
        RectangleF[] scans = region.GetRegionScans(new Matrix());  // the pixels, as rectangles
        #endregion

        Assert.Equal(-90f, bounds.X);
        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(10, 10).ToArgb());
        Assert.Equal(0, bitmap.GetPixel(16, 16).A);
        Assert.NotEmpty(scans);
    }

    [Fact]
    public void GraphicsShapes()
    {
        #region shapes
        // Lines, rectangles, arcs, pies, polygons, Beziers and cardinal splines, stroked with a pen or filled with a brush.
        using Bitmap bitmap = new(64, 48);
        using Graphics g = Graphics.FromImage(bitmap);
        using Pen pen = new(Color.Black, 2);
        using SolidBrush brush = new(Color.FromArgb(128, 255, 0, 0));
        g.DrawLine(pen, 2, 2, 60, 2);
        g.FillRectangle(brush, 4, 6, 20, 12);
        g.DrawArc(pen, 30, 6, 30, 20, 0, 180);
        g.FillPie(brush, 4, 20, 24, 24, 30, 120);
        g.DrawCurve(pen, [new PointF(30, 40), new PointF(40, 30), new PointF(50, 44), new PointF(60, 34)], 0.5f);
        g.FillPolygon(brush, [new PointF(40, 4), new PointF(48, 16), new PointF(32, 16)], FillMode.Winding);
        #endregion

        Assert.Equal(255, bitmap.GetPixel(10, 2).A);
    }

    [Fact]
    public void GraphicsImages()
    {
        #region images
        // DrawImage at a point draws at the image's physical size; ImageAttributes adjust its colors and tile it.
        using Bitmap source = new(8, 8);
        source.SetResolution(48, 48);  // half the 96 dpi of the bitmap drawn on, so it is drawn twice as large
        using (Graphics s = Graphics.FromImage(source))
        {
            s.Clear(Color.Orange);
        }

        using Bitmap bitmap = new(64, 48);
        using Graphics g = Graphics.FromImage(bitmap);
        g.DrawImage(source, 0, 0);  // 16 by 16 pixels
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        using ImageAttributes attributes = new();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.5f });  // half the alpha
        attributes.SetWrapMode(WrapMode.Tile);
        g.DrawImage(source, new Rectangle(20, 0, 40, 40), 0, 0, 16, 16, GraphicsUnit.Pixel, attributes);
        #endregion

        Assert.Equal(255, bitmap.GetPixel(14, 14).A);
        Assert.Equal(0, bitmap.GetPixel(18, 18).A);
        Assert.InRange(bitmap.GetPixel(40, 20).A, 120, 135);
    }

    [Fact]
    public void Pens()
    {
        #region pens
        // Pens take GDI+'s caps, joins, dash styles (in multiples of the width), compound lines, inset alignment and arrow caps.
        using Bitmap bitmap = new(80, 60);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using Pen dashed = new(Color.Navy, 3) { DashStyle = DashStyle.DashDot, StartCap = LineCap.Round, EndCap = LineCap.ArrowAnchor };
        g.DrawLine(dashed, 5, 10, 75, 10);
        using Pen arrow = new(Color.DarkRed, 2) { CustomEndCap = new AdjustableArrowCap(4, 5) };
        g.DrawLine(arrow, 5, 30, 70, 30);
        using Pen inset = new(Color.Black, 4) { Alignment = PenAlignment.Inset, LineJoin = LineJoin.Round };
        g.DrawRectangle(inset, 5, 40, 30, 15);
        float[] pattern = dashed.DashPattern;  // 3, 1, 1, 1
        #endregion

        Assert.Equal([3f, 1, 1, 1], pattern);
    }

    [Fact]
    public void BrushesAndGradients()
    {
        #region brushes
        // Hatch, texture and gradient brushes, and the Brushes and Pens of the named colors.
        using Bitmap bitmap = new(80, 60);
        using Graphics g = Graphics.FromImage(bitmap);
        using HatchBrush hatch = new(HatchStyle.DiagonalCross, Color.Black, Color.White);
        g.FillRectangle(hatch, 0, 0, 40, 30);
        using LinearGradientBrush linear = new(new Rectangle(40, 0, 40, 30), Color.Red, Color.Blue, LinearGradientMode.Horizontal);
        linear.SetSigmaBellShape(0.5f);
        g.FillRectangle(linear, 40, 0, 40, 30);
        using PathGradientBrush radial = new([new PointF(0, 30), new PointF(40, 30), new PointF(20, 60)]) { CenterColor = Color.White, SurroundColors = [Color.Green] };
        g.FillRectangle(radial, 0, 30, 40, 30);
        using Bitmap tile = new(4, 4);
        tile.SetPixel(0, 0, Color.Orange);
        using TextureBrush texture = new(tile, WrapMode.TileFlipXY);
        g.FillRectangle(texture, 40, 30, 40, 30);
        g.DrawRectangle(Egami.SystemDrawing.Pens.Gray, 0, 0, 79, 59);
        g.FillEllipse(Egami.SystemDrawing.Brushes.Gold, 36, 26, 8, 8);
        Blend? blend = linear.Blend;  // 511 factors: the bell, as GDI+ samples it
        #endregion

        Assert.Equal(511, blend!.Factors.Length);
    }

    [Fact]
    public void Paths()
    {
        #region paths
        // Paths keep GDI+'s points and point types; lines, arcs and curves join the open figure, closed shapes are their own.
        using GraphicsPath path = new();
        path.AddLine(0, 0, 10, 10);
        path.AddArc(0, 0, 40, 20, 0, 90);
        path.CloseFigure();
        path.AddEllipse(50, 0, 20, 20);
        byte[] types = path.PathTypes;  // 0 1 1 3 3 3|128, then the ellipse's 13 points
        RectangleF bounds = path.GetBounds();
        bool inside = path.IsVisible(60, 10);
        using GraphicsPathIterator iterator = new(path);
        int figures = iterator.SubpathCount;  // 2
        path.Widen(new Pen(Color.Black, 2));

        using Bitmap bitmap = new(80, 30);
        using Graphics g = Graphics.FromImage(bitmap);
        g.FillPath(Egami.SystemDrawing.Brushes.Teal, path);
        g.SetClip(path);
        #endregion

        Assert.Equal(19, types.Length);
        Assert.Equal(0f, bounds.X);
        Assert.True(inside);
        Assert.Equal(2, figures);
    }

    [Fact]
    public void Metafiles()
    {
        #region metafiles
        // A metafile is an Image sized by its frame; DrawImage plays it as vectors, EnumerateMetafile walks its records.
        using Metafile metafile = new(System.IO.Path.Combine(Images, "shapes-plus.emf"));
        MetafileHeader header = metafile.GetMetafileHeader();  // EmfPlusDual, its bounds and resolution
        int records = 0;
        using Bitmap bitmap = new(400, 300);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            g.DrawImage(metafile, new Rectangle(0, 0, 400, 300));
            g.EnumerateMetafile(metafile, new PointF(0, 0), (type, flags, size, data, play) =>
            {
                records++;
                return true;
            });
        }
        #endregion

        Assert.True(header.IsEmfPlusDual());
        Assert.True(records > 2);
    }

    [Fact]
    public void Effects()
    {
        #region effects
        // GDI+ 1.1's effects, computed with Egami's operations; the color effects match GDI+ to a level or so.
        using Bitmap photo = new(System.IO.Path.Combine(Images, "duck.png"));
        using (BrightnessContrastEffect brighter = new(30, 20))
        {
            photo.ApplyEffect(brighter, Rectangle.Empty);
        }

        using (GrayScaleEffect gray = new())
        {
            photo.ApplyEffect(gray, new Rectangle(0, 0, photo.Width / 2, photo.Height));
        }

        using BlurEffect blur = new(4, false);
        using Bitmap target = new(photo.Width, photo.Height);
        using Graphics g = Graphics.FromImage(target);
        g.DrawImage(photo, blur);
        #endregion

        Color left = photo.GetPixel(2, photo.Height / 2);
        Assert.Equal(left.R, left.G);
    }

    [Fact]
    public void Extras()
    {
        #region extras
        // Off-screen buffers, cached bitmaps and animated GIFs, as WinForms code uses them.
        using Bitmap window = new(100, 80);
        using Graphics screen = Graphics.FromImage(window);
        using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(screen, new Rectangle(10, 10, 80, 60)))
        {
            buffer.Graphics.Clear(Color.SteelBlue);
            buffer.Render();
        }

        using Bitmap sprite = new(8, 8);
        using CachedBitmap cached = new(sprite, screen);
        screen.DrawCachedBitmap(cached, 2, 2);

        using Image animation = Image.FromFile(System.IO.Path.Combine(Images, "loop.gif"));
        bool animates = ImageAnimator.CanAnimate(animation);  // true: more than one timed frame
        ImageAnimator.UpdateFrames(animation);
        #endregion

        Assert.Equal(Color.SteelBlue.ToArgb(), window.GetPixel(20, 20).ToArgb());
        Assert.True(animates);
    }

    [Fact]
    public void EscapeHatches()
    {
        #region escape-hatches
        // Move to Egami's own API one call at a time: a bitmap's pixels as a raster (no copy for 32-bit ARGB), the graphics' canvas.
        using Bitmap bitmap = new(64, 48);
        Raster view = bitmap.AsRaster();  // Raster<Bgra8> over the bitmap's own pixels
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.TranslateTransform(10, 10);
            Egami.Drawing.Canvas canvas = g.Canvas;  // drawn in the graphics' world coordinates
            canvas.FillCircle(new System.Numerics.Vector2(5, 5), 4, new Egami.Pixels.Rgba8(255, 0, 0, 255));
        }

        using Bitmap back = Bitmap.FromRaster(view);  // a copy, 32-bit ARGB
        #endregion

        Assert.IsType<Raster<Egami.Pixels.Bgra8>>(view);
        Assert.Equal(255, back.GetPixel(15, 15).R);
    }
}
