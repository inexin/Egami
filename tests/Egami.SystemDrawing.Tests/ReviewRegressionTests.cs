using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.SystemDrawing.Tests;

/// <summary>Regressions found in review of the stage A and C pull requests.</summary>
public class ReviewRegressionTests
{
    [Theory]
    [InlineData(PixelFormat.Format1bppIndexed, 0, 4)]
    [InlineData(PixelFormat.Format1bppIndexed, 3, 3)]
    [InlineData(PixelFormat.Format1bppIndexed, 1, 9)]
    [InlineData(PixelFormat.Format4bppIndexed, 0, 3)]
    [InlineData(PixelFormat.Format4bppIndexed, 1, 9)]
    public void UnlockingPartOfASubByteRowChangesOnlyTheLockedPixels(PixelFormat format, int x, int width)
    {
        // GDI+ measured (egami-compare BitmapProbes.LockWrite): only the pixel written changes.
        using Bitmap white = new(16, 1);
        for (int i = 0; i < 16; i++)
        {
            white.SetPixel(i, 0, Color.White);
        }

        using Bitmap b = white.Clone(new Rectangle(0, 0, 16, 1), format);
        BitmapData data = b.LockBits(new Rectangle(x, 0, width, 1), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        Marshal.WriteInt32(data.Scan0, unchecked((int)0xFF000000));
        b.UnlockBits(data);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(i == x ? Color.Black.ToArgb() : Color.White.ToArgb(), b.GetPixel(i, 0).ToArgb());
        }
    }

    [Fact]
    public void JpegQualityIsListedAsALongRange()
    {
        using Bitmap b = new(1, 1);
        ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using EncoderParameters list = b.GetEncoderParameterList(jpeg.Clsid)!;
        EncoderParameter quality = list.Param.Single(p => p.Encoder.Guid == Encoder.Quality.Guid);
        Assert.Equal(EncoderParameterValueType.ValueTypeLongRange, quality.ValueType);
        Assert.Equal(1, quality.NumberOfValues);
    }

    [Fact]
    public void BigEndianExifDoublesComeOutLittleEndianWhole()
    {
        // A big-endian EXIF block with one DOUBLE (type 12) tag, 0x9999, holding 1.5.
        byte[] tiff = new byte[8 + 2 + 12 + 4 + 8];
        "MM"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x9999);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 12);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(18), 26);
        BinaryPrimitives.WriteDoubleBigEndian(tiff.AsSpan(26), 1.5);

        using Raster<Rgb8> raster = Raster.Create<Rgb8>(4, 4);
        raster.Metadata.Exif = ExifData.Parse(tiff);
        using MemoryStream jpeg = new();
        raster.Save(jpeg, Egami.Formats.Jpeg.JpegFormat.Info);
        jpeg.Position = 0;
        using Image image = Image.FromStream(jpeg);
        PropertyItem item = image.GetPropertyItem(0x9999)!;
        Assert.Equal(12, item.Type);
        Assert.Equal(1.5, BinaryPrimitives.ReadDoubleLittleEndian(item.Value));
    }

    [Fact]
    public void ClonedImageAttributesKeepTheirWrapMode()
    {
        using Bitmap source = new(2, 2);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                source.SetPixel(x, y, Color.FromArgb(255, 255, 0, 0));
            }
        }

        using ImageAttributes original = new();
        original.SetWrapMode(Egami.SystemDrawing.Drawing2D.WrapMode.Tile);
        using ImageAttributes clone = (ImageAttributes)original.Clone();
        using Bitmap target = new(8, 8);
        using (Graphics g = Graphics.FromImage(target))
        {
            // Drawn from a source rectangle four times the image: tiled, every pixel is red; clamped, most are transparent.
            g.DrawImage(source, new Rectangle(0, 0, 8, 8), 0, 0, 8, 8, GraphicsUnit.Pixel, clone);
        }

        Assert.Equal(255, target.GetPixel(6, 6).A);
    }

    [Fact]
    public void FillRegionWithAnInfiniteRegionFillsTheClip()
    {
        // GDI+ (PathProbes.Regions): an infinite region fills the whole bitmap, or what the clip lets through.
        using Bitmap b = new(16, 12);
        using Graphics g = Graphics.FromImage(b);
        using SolidBrush red = new(Color.Red);
        using Region infinite = new();
        g.FillRegion(red, infinite);
        Assert.Equal(Color.Red.ToArgb(), b.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), b.GetPixel(15, 11).ToArgb());

        g.Clear(Color.Transparent);
        g.SetClip(new Rectangle(2, 2, 4, 4));
        g.TranslateTransform(100, 100);
        g.FillRegion(red, g.Clip);
        Assert.Equal(0, b.GetPixel(1, 1).A);
        Assert.Equal(Color.Red.ToArgb(), b.GetPixel(2, 2).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), b.GetPixel(5, 5).ToArgb());
        Assert.Equal(0, b.GetPixel(6, 6).A);
    }

    [Fact]
    public void RegionIsVisibleTakesSystemDrawingPoints()
    {
        // Inside an Egami namespace Point names Egami.Point; the public overload must take System.Drawing.Point.
        using Region region = new(new Rectangle(0, 0, 10, 10));
        Assert.True(region.IsVisible(new System.Drawing.Point(5, 5)));
        Assert.False(region.IsVisible(new System.Drawing.Point(20, 5), null));
        Assert.NotNull(typeof(Region).GetMethod(nameof(Region.IsVisible), [typeof(System.Drawing.Point)]));
    }
}
