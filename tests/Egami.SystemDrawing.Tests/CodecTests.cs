namespace Egami.SystemDrawing.Tests;

/// <summary>
/// Loading and saving through Egami's codecs with GDI+'s pixel formats, flags, resolutions, frames and property items; the
/// expected values come from GDI+ on Windows (egami-compare, sysdrawing CodecProbes).
/// </summary>
public class CodecTests
{
    [Theory]
    [InlineData("pngsuite", "basn0g01.png", PixelFormat.Format1bppIndexed, 73744, 96f)]
    [InlineData("pngsuite", "basn0g08.png", PixelFormat.Format32bppArgb, 73794, 96f)]
    [InlineData("pngsuite", "basn2c08.png", PixelFormat.Format24bppRgb, 73744, 96f)]
    [InlineData("pngsuite", "basn3p04.png", PixelFormat.Format4bppIndexed, 73744, 96f)]
    [InlineData("pngsuite", "basn3p08.png", PixelFormat.Format8bppIndexed, 73744, 96f)]
    [InlineData("pngsuite", "basn6a08.png", PixelFormat.Format32bppArgb, 73746, 96f)]
    [InlineData("pngsuite", "tbbn3p08.png", PixelFormat.Format32bppArgb, 73746, 96f)]
    [InlineData("bmp", "pal8.bmp", PixelFormat.Format8bppIndexed, 77840, 96.01199f)]
    [InlineData("bmp", "rgb24.bmp", PixelFormat.Format24bppRgb, 77840, 96.01199f)]
    [InlineData("bmp", "rgba32.bmp", PixelFormat.Format32bppRgb, 77840, 96.01199f)]
    [InlineData("bmp", "v4-argb32.bmp", PixelFormat.Format32bppArgb, 73746, 96f)]
    [InlineData("jpeg", "duck-420.jpg", PixelFormat.Format24bppRgb, 73744, 96f)]
    [InlineData("jpeg", "duck-gray.jpg", PixelFormat.Format8bppIndexed, 73792, 96f)]
    [InlineData("tiff", "bilevel-g4.tif", PixelFormat.Format1bppIndexed, 77840, 72f)]
    [InlineData("tiff", "gray8.tif", PixelFormat.Format8bppIndexed, 77888, 72f)]
    [InlineData("tiff", "palette4.tif", PixelFormat.Format4bppIndexed, 77840, 72f)]
    [InlineData("tiff", "rgb16.tif", PixelFormat.Format48bppRgb, 77840, 72f)]
    [InlineData("tiff", "rgba8.tif", PixelFormat.Format32bppArgb, 77842, 72f)]
    [InlineData("gif", "colors-16.gif", PixelFormat.Format8bppIndexed, 77840, 96f)]
    [InlineData("gif", "loop-3.gif", PixelFormat.Format32bppArgb, 77840, 96f)]
    [InlineData("gif", "transparent-still.gif", PixelFormat.Format8bppIndexed, 77842, 96f)]
    [InlineData("ico", "magick-auto.ico", PixelFormat.Format32bppArgb, 73746, 96f)]
    public void FilesLoadInGdiPlusFormats(string folder, string file, PixelFormat format, int flags, float dpi)
    {
        using Image image = Image.FromFile(TestFiles.Image(folder, file));
        Assert.Equal(format, image.PixelFormat);
        Assert.Equal(flags, image.Flags);
        Assert.Equal(dpi, image.HorizontalResolution);
    }

    [Fact]
    public void AnimatedGifsHaveTimeFramesDelaysAndLoopCount()
    {
        using Image image = Image.FromFile(TestFiles.Image("gif", "loop-3.gif"));
        Assert.Equal([FrameDimension.Time.Guid], image.FrameDimensionsList);
        Assert.Equal(6, image.GetFrameCount(FrameDimension.Time));
        Assert.Throws<System.Runtime.InteropServices.ExternalException>(() => image.GetFrameCount(FrameDimension.Page));
        Assert.Equal("070000000700000007000000070000000700000007000000", Convert.ToHexString(image.GetPropertyItem(0x5100)!.Value!));
        Assert.Equal("0300", Convert.ToHexString(image.GetPropertyItem(0x5101)!.Value!));
        image.SelectActiveFrame(FrameDimension.Time, 5);
        Assert.Equal(64, image.Width);
    }

    [Fact]
    public void JpegPropertiesHoldTheExifTagsThenTheQuantizationTables()
    {
        using Image image = Image.FromFile(TestFiles.Image("jpeg", "exif-orientation-6.jpg"));
        Assert.Equal([0x0112, 0x5091, 0x5090], image.PropertyIdList);
        Assert.Equal("0600", Convert.ToHexString(image.GetPropertyItem(0x0112)!.Value!));
        Assert.StartsWith("0300020002000300050008000A000C00", Convert.ToHexString(image.GetPropertyItem(0x5090)!.Value!), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => image.GetPropertyItem(0x0110));
    }

    [Fact]
    public void TiffPropertiesAreTheFirstDirectorysTags()
    {
        using Image image = Image.FromFile(TestFiles.Image("tiff", "rgb8.tif"));
        Assert.Equal([0x0100, 0x0101, 0x0102, 0x0103, 0x0106, 0x0111, 0x0115, 0x0116, 0x0117, 0x011A, 0x011B, 0x011C, 0x0128], image.PropertyIdList);
        Assert.Equal("080008000800", Convert.ToHexString(image.GetPropertyItem(0x0102)!.Value!));
    }

    [Fact]
    public void SavingRoundTripsAndChoosesTheFormatsGdiPlusChooses()
    {
        using Bitmap argb = Gradient(PixelFormat.Format32bppArgb, alpha: true);
        Assert.Equal(PixelFormat.Format32bppArgb, Reload(argb, ImageFormat.Png).PixelFormat);
        Assert.Equal(PixelFormat.Format32bppRgb, Reload(argb, ImageFormat.Bmp).PixelFormat);
        Assert.Equal(PixelFormat.Format8bppIndexed, Reload(argb, ImageFormat.Gif).PixelFormat);
        Assert.Equal(PixelFormat.Format24bppRgb, Reload(argb, ImageFormat.Jpeg).PixelFormat);
        using Bitmap png = (Bitmap)Reload(argb, ImageFormat.Png);
        Assert.Equal(argb.GetPixel(3, 5), png.GetPixel(3, 5));
        Assert.Throws<ArgumentNullException>(() => argb.Save(new MemoryStream(), ImageFormat.Icon));
    }

    [Fact]
    public void EncoderParametersSetJpegQualityAndTiffDepth()
    {
        using Bitmap rgb = Gradient(PixelFormat.Format24bppRgb, alpha: false);
        ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().Single(c => c.MimeType == "image/jpeg");
        ImageCodecInfo tiff = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Tiff.Guid);
        long Size(ImageCodecInfo codec, Encoder setting, long value)
        {
            using EncoderParameters list = new(1);
            list.Param[0] = new EncoderParameter(setting, value);
            using MemoryStream s = new();
            rgb.Save(s, codec, list);
            return s.Length;
        }

        Assert.True(Size(jpeg, Encoder.Quality, 10) < Size(jpeg, Encoder.Quality, 100));
        using EncoderParameters depth = new(1);
        depth.Param[0] = new EncoderParameter(Encoder.ColorDepth, 1L);
        using MemoryStream bilevel = new();
        rgb.Save(bilevel, tiff, depth);
        bilevel.Position = 0;
        using Image back = Image.FromStream(bilevel);
        Assert.Equal(PixelFormat.Format1bppIndexed, back.PixelFormat);
        Assert.Throws<NotImplementedException>(() => rgb.GetEncoderParameterList(ImageCodecInfo.GetImageEncoders()[0].Clsid));
        Assert.Equal(5, rgb.GetEncoderParameterList(jpeg.Clsid)!.Param.Length);
    }

    [Fact]
    public void SaveAddWritesAMultipageTiff()
    {
        using Bitmap first = Gradient(PixelFormat.Format24bppRgb, alpha: false);
        using Bitmap second = new(3, 2);
        ImageCodecInfo tiff = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Tiff.Guid);
        using MemoryStream s = new();
        using (EncoderParameters start = new(1))
        {
            start.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            first.Save(s, tiff, start);
        }

        using (EncoderParameters page = new(1))
        {
            page.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
            first.SaveAdd(second, page);
        }

        using (EncoderParameters flush = new(1))
        {
            flush.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);
            first.SaveAdd(flush);
        }

        s.Position = 0;
        using Image back = Image.FromStream(s);
        Assert.Equal(2, back.GetFrameCount(FrameDimension.Page));
        back.SelectActiveFrame(FrameDimension.Page, 1);
        Assert.Equal(new System.Drawing.Size(3, 2), back.Size);
    }

    [Fact]
    public void UnreadableDataThrowsWhatGdiPlusThrows()
    {
        Assert.Throws<ArgumentException>(() => Image.FromStream(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8])));
        Assert.Throws<FileNotFoundException>(() => Image.FromFile("missing-" + Guid.NewGuid() + ".png"));
    }

    [Fact]
    public void TheCodecListMatchesGdiPlus()
    {
        Assert.Equal(["BMP", "JPEG", "GIF", "EMF", "WMF", "TIFF", "PNG", "ICO"], ImageCodecInfo.GetImageDecoders().Select(c => c.FormatDescription));
        Assert.Equal(["BMP", "JPEG", "GIF", "TIFF", "PNG"], ImageCodecInfo.GetImageEncoders().Select(c => c.FormatDescription));
        ImageCodecInfo png = ImageCodecInfo.GetImageEncoders()[4];
        Assert.Equal(new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e"), png.Clsid);
        Assert.Equal(ImageFormat.Png.Guid, png.FormatID);
        Assert.Equal("Png", ImageFormat.Png.ToString());
        Assert.Equal("MemoryBMP", ImageFormat.MemoryBmp.ToString());
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(16, 16)]
    [InlineData(24, 24)]
    [InlineData(48, 48)]
    [InlineData(64, 64)]
    [InlineData(256, 64)]
    public void IconsChooseTheSizeGdiPlusChooses(int asked, int chosen)
    {
        string file = TestFiles.Image("ico", "set.ico");
        using Icon icon = asked == 0 ? new Icon(file) : new Icon(file, asked, asked);
        Assert.Equal(chosen, icon.Width);
        using Bitmap bitmap = icon.ToBitmap();
        Assert.Equal(PixelFormat.Format32bppArgb, bitmap.PixelFormat);
    }

    [Fact]
    public void AnIconSavesTheFileItCameFrom()
    {
        byte[] file = File.ReadAllBytes(TestFiles.Image("ico", "set.ico"));
        using Icon icon = new(new MemoryStream(file));
        using MemoryStream saved = new();
        icon.Save(saved);
        Assert.Equal(file, saved.ToArray());
        Assert.Equal("(Icon)", icon.ToString());
    }

    private static Image Reload(Image image, ImageFormat format)
    {
        MemoryStream s = new();
        image.Save(s, format);
        s.Position = 0;
        return Image.FromStream(s);
    }

    private static Bitmap Gradient(PixelFormat format, bool alpha)
    {
        Bitmap b = new(16, 8, format);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                b.SetPixel(x, y, Color.FromArgb(alpha ? 255 - (y * 32) : 255, x * 16, y * 32, 255 - (x * 16)));
            }
        }

        return b;
    }
}
