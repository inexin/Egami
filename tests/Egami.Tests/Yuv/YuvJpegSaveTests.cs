using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Tests.Yuv;

/// <summary>Phase 2.1.4 task 9: JPEG from planes gives libjpeg-turbo's bytes at the same settings, and other planes convert first.</summary>
public class YuvJpegSaveTests
{
    public static TheoryData<string> Encodes()
    {
        TheoryData<string> data = [];
        foreach (YuvJpegReferences.Encode e in YuvJpegReferences.Encodes())
        {
            data.Add(e.File);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Encodes))]
    public void BytesMatchLibjpegTurbo(string file)
    {
        YuvJpegReferences.Encode encode = YuvJpegReferences.Encodes().Single(e => e.File == file);
        YuvJpegReferences.Decode source = YuvJpegReferences.Decodes().First(d => d.Reference == encode.Source && d.Scale == 1);
        byte[][] planes = YuvJpegReferences.ReadPlanes(source.Reference, source.Planes);
        ChromaFormat format = encode.Subsampling switch { "gray" => ChromaFormat.Yuv400, "420" => ChromaFormat.Yuv420, "422" => ChromaFormat.Yuv422, _ => ChromaFormat.Yuv444 };
        using YuvRaster<byte> yuv = YuvRaster.Create(encode.Width, encode.Height, format, YuvColor.Jpeg);
        for (int p = 0; p < planes.Length; p++)
        {
            YuvPlane<byte> plane = p == 0 ? yuv.Y : p == 1 ? yuv.U! : yuv.V!;
            for (int y = 0; y < plane.Height; y++)
            {
                planes[p].AsSpan(y * source.Planes[p].Width, plane.Width).CopyTo(plane.Row(y));
            }
        }

        using MemoryStream output = new();
        yuv.Save(output, JpegFormat.Info, new JpegWriterOptions { Quality = encode.Quality });
        byte[] expected = File.ReadAllBytes(Path.Combine(YuvJpegReferences.Folder, encode.File));
        Assert.Equal(expected.Length, output.Length);
        Assert.True(expected.AsSpan().SequenceEqual(output.ToArray()), $"{file} differs.");
    }

    [Fact]
    public void OtherColorsAreConvertedToJpegsFirst()
    {
        using Raster<Rgb8> rgb = Raster.Load<Rgb8>(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-444.jpg"));
        using YuvRaster<ushort> video = YuvRaster.FromRaster(rgb, ChromaFormat.Yuv420, 10, YuvColor.Bt709Limited);
        using MemoryStream output = new();
        video.Save(output, "jpg");
        output.Position = 0;
        using YuvRaster loaded = YuvRaster.Load(output);
        Assert.Equal(ChromaFormat.Yuv420, loaded.ChromaFormat);
        using Raster<Rgb8> back = loaded.ToRaster<Rgb8>();
        double error = 0;
        for (int y = 0; y < rgb.Height; y++)
        {
            for (int x = 0; x < rgb.Width; x++)
            {
                error += Math.Abs(rgb.Row(y)[x].G - back.Row(y)[x].G);
            }
        }

        Assert.True(error / (rgb.Width * rgb.Height) < 3, $"Mean error {error / (rgb.Width * rgb.Height)}.");
    }

    [Fact]
    public void OtherFormatsSaveThroughARaster()
    {
        using YuvRaster yuv = YuvRaster.Load(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-420.jpg"));
        using MemoryStream png = new();
        yuv.Save(png, "png");
        png.Position = 0;
        using Raster loaded = Raster.Load(png);
        using Raster expected = yuv.ToRaster();
        Assert.Equal(expected.Width, loaded.Width);
        Assert.Equal("PNG", loaded.Metadata.FormatName);
    }

    [Fact]
    public void GrayAndGrayRequestsWriteLumaOnly()
    {
        using YuvRaster yuv = YuvRaster.Load(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-420.jpg"));
        using MemoryStream output = new();
        yuv.Save(output, JpegFormat.Info, new JpegWriterOptions { ColorType = JpegColorType.Gray });
        output.Position = 0;
        using YuvRaster gray = YuvRaster.Load(output);
        Assert.Equal(ChromaFormat.Yuv400, gray.ChromaFormat);
    }
}
