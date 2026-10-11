using Egami.Formats;
using Egami.Pixels;

namespace Egami.Tests.Yuv;

/// <summary>Phase 2.1.4 task 8: JPEG planes are identical to libjpeg-turbo's raw output over the 1.3 corpus, at every scale.</summary>
public class YuvJpegTests
{
    public static TheoryData<string, int> Decodes()
    {
        TheoryData<string, int> data = [];
        foreach (YuvJpegReferences.Decode d in YuvJpegReferences.Decodes())
        {
            data.Add(d.File, d.Scale);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Decodes))]
    public void PlanesMatchLibjpegTurbo(string file, int scale)
    {
        YuvJpegReferences.Decode reference = YuvJpegReferences.Decodes().Single(d => d.File == file && d.Scale == scale);
        byte[][] expected = YuvJpegReferences.ReadPlanes(reference.Reference, reference.Planes);
        ReaderOptions options = scale == 1 ? ReaderOptions.Default : ReaderOptions.Default with { MinimumSize = new Size(reference.Width, reference.Height) };
        using YuvRaster loaded = YuvRaster.Load(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", file), options: options);
        YuvRaster<byte> yuv = Assert.IsType<YuvRaster<byte>>(loaded);
        Assert.Equal((reference.Width, reference.Height), (yuv.Width, yuv.Height));
        Assert.Equal(YuvColor.Jpeg, yuv.Color);
        ChromaFormat format = reference.Subsampling switch
        {
            "gray" => ChromaFormat.Yuv400,
            "420" => ChromaFormat.Yuv420,
            "422" => ChromaFormat.Yuv422,
            _ => ChromaFormat.Yuv444,
        };
        Assert.Equal(format, yuv.ChromaFormat);
        Assert.Equal("JPEG", yuv.Metadata.FormatName);

        for (int p = 0; p < expected.Length; p++)
        {
            YuvPlane<byte> plane = p == 0 ? yuv.Y : p == 1 ? yuv.U! : yuv.V!;
            (int refWidth, int refHeight) = reference.Planes[p];

            // libjpeg-turbo's planes are padded to whole chroma samples; Egami's are the image size. Sampling that Egami does not
            // keep (4:1:1, 4:4:0) comes back replicated to 4:4:4.
            int sx = p == 0 ? 1 : Math.Max(1, reference.Planes[0].Width / refWidth), sy = p == 0 ? 1 : Math.Max(1, reference.Planes[0].Height / refHeight);
            if (format != ChromaFormat.Yuv444)
            {
                (sx, sy) = (1, 1);
            }

            for (int y = 0; y < plane.Height; y++)
            {
                ReadOnlySpan<byte> row = plane.Row(y);
                for (int x = 0; x < plane.Width; x++)
                {
                    Assert.True(expected[p][(y / sy * refWidth) + (x / sx)] == row[x], $"{file}: plane {p} differs at ({x}, {y}).");
                }
            }
        }
    }

    [Fact]
    public void ProbeSaysWhatLoadGives()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "images", "jpeg");
        YuvRasterInfo info = YuvRaster.Probe(Path.Combine(folder, "duck-422.jpg"));
        Assert.True(info.CanLoad);
        Assert.Equal(ChromaFormat.Yuv422, info.ChromaFormat);
        Assert.Equal(8, info.BitDepth);
        Assert.Equal(YuvColor.Jpeg, info.Color);
        Assert.Equal(320, info.Info.Width);
        Assert.Equal(ChromaFormat.Yuv444, YuvRaster.Probe(Path.Combine(folder, "duck-411.jpg")).ChromaFormat);
        Assert.Equal(ChromaFormat.Yuv400, YuvRaster.Probe(Path.Combine(folder, "duck-gray.jpg")).ChromaFormat);
        YuvRasterInfo cmyk = YuvRaster.Probe(Path.Combine(folder, "duck-cmyk.jpg"));
        Assert.False(cmyk.CanLoad);
        Assert.Null(cmyk.Color);
        YuvRasterInfo png = YuvRaster.Probe(Path.Combine(AppContext.BaseDirectory, "images", "ops", "duck.png"));
        Assert.False(png.CanLoad);
        Assert.Equal("PNG", png.Info.Format.Name);
    }

    [Fact]
    public void FilesWithoutPlanesThrowAClearError()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "images", "jpeg");
        foreach (string file in new[] { "duck-cmyk.jpg", "duck-ycck.jpg" })
        {
            NotSupportedException e = Assert.Throws<NotSupportedException>(() => YuvRaster.Load(Path.Combine(folder, file)));
            Assert.Contains("Raster.Load", e.Message, StringComparison.Ordinal);
        }

        Assert.Throws<NotSupportedException>(() => YuvRaster.Load(Path.Combine(AppContext.BaseDirectory, "images", "ops", "duck.png")));
        Assert.Throws<NotSupportedException>(() => YuvRaster.Load(Path.Combine(folder, "duck-420.jpg"), options: ReaderOptions.Default with { Region = new Rect(0, 0, 8, 8) }));
    }

    [Fact]
    public void MetadataSurvivesALoad()
    {
        using YuvRaster yuv = YuvRaster.Load(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "exif-tags.jpg"));
        Assert.NotNull(yuv.Metadata.Exif);
        using Raster rgb = yuv.ToRaster();
        Assert.NotNull(rgb.Metadata.Exif);
    }

    [Fact]
    public void ConvertedPlanesAreWithinALevelOfTheRgbDecode()
    {
        // The JPEG decoder keeps libjpeg-turbo's own color code; planes converted with YuvRaster's may differ by a level.
        string file = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-444.jpg");
        using Raster<Rgb8> direct = Raster.Load<Rgb8>(file);
        using YuvRaster yuv = YuvRaster.Load(file);
        using Raster<Rgb8> converted = yuv.ToRaster<Rgb8>();
        long same = 0, total = 0;
        for (int y = 0; y < direct.Height; y++)
        {
            for (int x = 0; x < direct.Width; x++)
            {
                Rgb8 a = direct.Row(y)[x], b = converted.Row(y)[x];
                foreach ((int p, int q) in new[] { (a.R, b.R), (a.G, b.G), (a.B, b.B) })
                {
                    Assert.InRange(Math.Abs(p - q), 0, 1);
                    same += p == q ? 1 : 0;
                    total++;
                }
            }
        }

        Assert.True(same > total * 0.95, $"{same} of {total} identical.");
    }

    [Fact]
    public void ParallelPipelineGivesTheSamePlanes()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "egret-420.jpg");
        using YuvRaster<byte> one = (YuvRaster<byte>)YuvRaster.Load(file, EgamiContext.Default with { MaxParallelism = 1 });
        using YuvRaster<byte> all = (YuvRaster<byte>)YuvRaster.Load(file, EgamiContext.Default with { MaxParallelism = 8 });
        for (int y = 0; y < one.Height; y++)
        {
            Assert.True(one.Y.Row(y).SequenceEqual(all.Y.Row(y)));
        }
    }
}
