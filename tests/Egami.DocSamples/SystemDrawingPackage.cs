using System.Drawing;
using System.Runtime.InteropServices;

using Egami.SystemDrawing;
using Egami.SystemDrawing.Imaging;

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
}
