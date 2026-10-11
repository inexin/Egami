using Egami.Pixels;

namespace Egami.Tests.Yuv;

/// <summary>
/// Phase 2.1.4 task 3: conversions against the exact double reference. At most one level apart at the output depth, and identical
/// for at least 99% of samples: exhaustively over every 8-bit Y, U, V triple and every 8-bit RGB color, and over random pictures
/// in every format, siting, filter and depth.
/// </summary>
public class YuvConversionTests
{
    public static TheoryData<YuvMatrix, YuvRange> Colors()
    {
        TheoryData<YuvMatrix, YuvRange> data = [];
        foreach (YuvMatrix matrix in Enum.GetValues<YuvMatrix>())
        {
            foreach (YuvRange range in Enum.GetValues<YuvRange>())
            {
                data.Add(matrix, range);
            }
        }

        return data;
    }

    public static TheoryData<ChromaFormat, ChromaSiting, int> Layouts()
    {
        TheoryData<ChromaFormat, ChromaSiting, int> data = [];
        foreach (ChromaFormat format in Enum.GetValues<ChromaFormat>())
        {
            foreach (ChromaSiting siting in Enum.GetValues<ChromaSiting>())
            {
                if (format is ChromaFormat.Yuv420 or ChromaFormat.Yuv422 || siting == ChromaSiting.Center)
                {
                    foreach (int depth in new[] { 8, 10, 12, 16 })
                    {
                        data.Add(format, siting, depth);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Colors))]
    public void EveryEightBitTripleToRgb(YuvMatrix matrix, YuvRange range)
    {
        // 4096 × 4096 pixels hold every (Y, U, V): Y is the low byte of x, U the high nibble of x and low of y, V the high byte of y.
        YuvColor color = new(matrix, range, ChromaSiting.Center);
        using YuvRaster<byte> yuv = YuvRaster.Create(4096, 4096, ChromaFormat.Yuv444, color);
        for (int y = 0; y < 4096; y++)
        {
            Span<byte> l = yuv.Y.Row(y), u = yuv.U!.Row(y), v = yuv.V!.Row(y);
            for (int x = 0; x < 4096; x++)
            {
                l[x] = (byte)x;
                u[x] = (byte)((x >> 8) | ((y & 15) << 4));
                v[x] = (byte)(y >> 4);
            }
        }

        using Raster<Rgba8> rgb = yuv.ToRaster<Rgba8>();
        Tally tally = new();
        for (int y = 0; y < 4096; y++)
        {
            ReadOnlySpan<Rgba8> row = rgb.Row(y);
            ReadOnlySpan<byte> l = yuv.Y.Row(y), u = yuv.U!.Row(y), v = yuv.V!.Row(y);
            for (int x = 0; x < 4096; x++)
            {
                (double r, double g, double b) = YuvReference.ToRgb(l[x], u[x], v[x], color, 8);
                tally.Add(row[x].R, YuvReference.Round(r * 255, 255), (l[x], u[x], v[x]));
                tally.Add(row[x].G, YuvReference.Round(g * 255, 255), (l[x], u[x], v[x]));
                tally.Add(row[x].B, YuvReference.Round(b * 255, 255), (l[x], u[x], v[x]));
                Assert.Equal(255, row[x].A);
            }
        }

        tally.Check();
    }

    [Theory]
    [MemberData(nameof(Colors))]
    public void EveryEightBitColorFromRgb(YuvMatrix matrix, YuvRange range)
    {
        YuvColor color = new(matrix, range, ChromaSiting.Center);
        using Raster<Rgb8> rgb = Raster.Create<Rgb8>(4096, 4096);
        for (int y = 0; y < 4096; y++)
        {
            Span<Rgb8> row = rgb.Row(y);
            for (int x = 0; x < 4096; x++)
            {
                row[x] = new Rgb8((byte)x, (byte)((x >> 8) | ((y & 15) << 4)), (byte)(y >> 4));
            }
        }

        using YuvRaster<byte> yuv = YuvRaster.FromRaster(rgb, ChromaFormat.Yuv444, color);
        Tally tally = new();
        for (int y = 0; y < 4096; y++)
        {
            ReadOnlySpan<Rgb8> row = rgb.Row(y);
            ReadOnlySpan<byte> l = yuv.Y.Row(y), u = yuv.U!.Row(y), v = yuv.V!.Row(y);
            for (int x = 0; x < 4096; x++)
            {
                Rgb8 p = row[x];
                (double ey, double cb, double cr) = YuvReference.FromRgb(p.R / 255.0, p.G / 255.0, p.B / 255.0, color, 8);
                tally.Add(l[x], YuvReference.Round(ey, 255), (p.R, p.G, p.B));
                tally.Add(u[x], YuvReference.Round(cb, 255), (p.R, p.G, p.B));
                tally.Add(v[x], YuvReference.Round(cr, 255), (p.R, p.G, p.B));
            }
        }

        tally.Check();
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void RandomPicturesToRgb(ChromaFormat format, ChromaSiting siting, int depth)
    {
        Tally tally = new($"{format} {siting} {depth}-bit to RGB");
        foreach ((int w, int h) in new[] { (37, 23), (64, 2), (1, 1), (2, 5), (17, 1) })
        {
            foreach (YuvMatrix matrix in Enum.GetValues<YuvMatrix>())
            {
                foreach (YuvRange range in Enum.GetValues<YuvRange>())
                {
                    foreach (ChromaUpsampling mode in Enum.GetValues<ChromaUpsampling>())
                    {
                        YuvColor color = new(matrix, range, siting);
                        Random random = new(HashCode.Combine(format, siting, depth, w, h, matrix, range, mode));
                        if (depth == 8)
                        {
                            using YuvRaster<byte> yuv = YuvRaster.Create(w, h, format, color);
                            Fill(yuv, random);
                            CheckToRgb<byte, Rgba8>(yuv, mode, 255, tally);
                            CheckToRgb<byte, Rgba16>(yuv, mode, 65535, tally);
                        }
                        else
                        {
                            using YuvRaster<ushort> yuv = YuvRaster.Create(w, h, format, depth, color);
                            Fill(yuv, random);
                            CheckToRgb<ushort, Rgba8>(yuv, mode, 255, tally);
                            CheckToRgb<ushort, Rgba16>(yuv, mode, 65535, tally);
                        }
                    }
                }
            }
        }

        tally.Check();
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void RandomPicturesFromRgb(ChromaFormat format, ChromaSiting siting, int depth)
    {
        Tally tally = new($"RGB to {format} {siting} {depth}-bit");
        foreach ((int w, int h) in new[] { (37, 23), (64, 2), (1, 1), (2, 5), (17, 1), (6, 3) })
        {
            foreach (YuvMatrix matrix in Enum.GetValues<YuvMatrix>())
            {
                foreach (YuvRange range in Enum.GetValues<YuvRange>())
                {
                    foreach (ChromaDownsampling mode in Enum.GetValues<ChromaDownsampling>())
                    {
                        YuvColor color = new(matrix, range, siting);
                        Random random = new(HashCode.Combine(format, siting, depth, w, h, matrix, range, mode));
                        using Raster<Rgba8> rgb8 = Raster.Create<Rgba8>(w, h);
                        using Raster<Rgba16> rgb16 = Raster.Create<Rgba16>(w, h);
                        for (int y = 0; y < h; y++)
                        {
                            Span<Rgba8> r8 = rgb8.Row(y);
                            Span<Rgba16> r16 = rgb16.Row(y);
                            for (int x = 0; x < w; x++)
                            {
                                r8[x] = new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                                r16[x] = new Rgba16((ushort)random.Next(65536), (ushort)random.Next(65536), (ushort)random.Next(65536), (ushort)random.Next(65536));
                            }
                        }

                        CheckFromRgb(rgb8, 255, format, color, depth, mode, tally);
                        CheckFromRgb(rgb16, 65535, format, color, depth, mode, tally);
                    }
                }
            }
        }

        tally.Check();
    }

    [Fact]
    public void GrayStaysGrayAndAlphaIsKept()
    {
        foreach (YuvMatrix matrix in Enum.GetValues<YuvMatrix>())
        {
            foreach (YuvRange range in Enum.GetValues<YuvRange>())
            {
                using Raster<Rgba8> source = Raster.Create<Rgba8>(256, 1);
                for (int x = 0; x < 256; x++)
                {
                    source.Row(0)[x] = new Rgba8((byte)x, (byte)x, (byte)x, (byte)(255 - x));
                }

                // Identity stores G, B and R as they are, so only 4:4:4 keeps gray exactly.
                ChromaFormat format = matrix == YuvMatrix.Identity ? ChromaFormat.Yuv444 : ChromaFormat.Yuv420;
                using YuvRaster<byte> yuv = YuvRaster.FromRaster(source, format, new YuvColor(matrix, range, ChromaSiting.Center), alpha: true);
                Assert.True(yuv.HasAlpha);
                using Raster<Rgba8> back = yuv.ToRaster<Rgba8>();
                for (int x = 0; x < 256; x++)
                {
                    Rgba8 p = back.Row(0)[x];
                    Assert.InRange(p.R, x - 1, x + 1);
                    Assert.Equal(p.R, p.G);
                    Assert.Equal(p.R, p.B);
                    Assert.Equal(255 - x, p.A);
                    if (matrix != YuvMatrix.Identity)
                    {
                        Assert.Equal(128, yuv.U!.Row(0)[x / 2]);
                    }
                }
            }
        }
    }

    [Fact]
    public void OtherPixelTypesGoThroughTheNearestDirectType()
    {
        Random random = new(5);
        using YuvRaster<ushort> yuv = YuvRaster.Create(33, 9, ChromaFormat.Yuv420, 10, YuvColor.Bt2020Limited);
        Fill(yuv, random);
        using Raster<Rgba16> direct = yuv.ToRaster<Rgba16>();
        using Raster<RgbaF32> floats = yuv.ToRaster<RgbaF32>();
        using Raster<Bgra8> bgra = yuv.ToRaster<Bgra8>();
        using Raster<Argb8> argb = yuv.ToRaster<Argb8>();
        for (int y = 0; y < 9; y++)
        {
            for (int x = 0; x < 33; x++)
            {
                Rgba16 d = direct.Row(y)[x];
                Assert.Equal(d.R / 65535f, floats.Row(y)[x].R, 1e-6f);
                Assert.Equal(argb.Row(y)[x].R, bgra.Row(y)[x].R);
                Assert.Equal(argb.Row(y)[x].B, bgra.Row(y)[x].B);
            }
        }

        // The natural type follows the depth.
        using Raster natural = yuv.ToRaster();
        Assert.IsType<Raster<Rgba16>>(natural);
    }

    [Fact]
    public void SamplesAboveTheDepthAreClamped()
    {
        using YuvRaster<ushort> yuv = YuvRaster.Create(4, 2, ChromaFormat.Yuv444, 10, new YuvColor(YuvMatrix.Bt709, YuvRange.Full, ChromaSiting.Center));
        for (int y = 0; y < 2; y++)
        {
            yuv.Y.Row(y).Fill(65535);
            yuv.U!.Row(y).Fill(512);
            yuv.V!.Row(y).Fill(512);
        }

        using Raster<Rgba16> rgb = yuv.ToRaster<Rgba16>();
        Assert.Equal(new Rgba16(65535, 65535, 65535, 65535), rgb.Row(0)[0]);
    }

    internal static void Fill<TSample>(YuvRaster<TSample> yuv, Random random)
        where TSample : unmanaged
    {
        int max = yuv.MaxSample;
        foreach (YuvPlane<TSample>? plane in new[] { yuv.Y, yuv.U, yuv.V, yuv.A })
        {
            if (plane is null)
            {
                continue;
            }

            for (int y = 0; y < plane.Height; y++)
            {
                Span<TSample> row = plane.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    YuvConvert.SetSample(row, x, random.Next(max + 1));
                }
            }
        }
    }

    internal static int[,] Plane<TSample>(YuvPlane<TSample> plane)
        where TSample : unmanaged
    {
        int[,] values = new int[plane.Height, plane.Width];
        for (int y = 0; y < plane.Height; y++)
        {
            ReadOnlySpan<TSample> row = plane.Row(y);
            for (int x = 0; x < plane.Width; x++)
            {
                values[y, x] = YuvConvert.Sample(row, x, int.MaxValue);
            }
        }

        return values;
    }

    private static void CheckToRgb<TSample, TPixel>(YuvRaster<TSample> yuv, ChromaUpsampling mode, int outMax, Tally tally)
        where TSample : unmanaged
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> rgb = yuv.ToRaster<TPixel>(new YuvConvertOptions { Upsampling = mode });
        int[,,] expected = YuvReference.PlanesToRgb(Plane(yuv.Y), yuv.U is null ? null : Plane(yuv.U), yuv.V is null ? null : Plane(yuv.V), yuv.ChromaFormat, yuv.Color, yuv.BitDepth, mode, outMax);
        tally.Case = $"{yuv.ChromaFormat} {yuv.Color} {yuv.BitDepth}-bit {mode} to {typeof(TPixel).Name}";
        for (int y = 0; y < yuv.Height; y++)
        {
            Span<TPixel> row = rgb.Row(y);
            for (int x = 0; x < yuv.Width; x++)
            {
                (int r, int g, int b) = Channels(row[x]);
                tally.Add(r, expected[y, x, 0], (x, y, 0));
                tally.Add(g, expected[y, x, 1], (x, y, 1));
                tally.Add(b, expected[y, x, 2], (x, y, 2));
            }
        }
    }

    private static void CheckFromRgb<TPixel>(Raster<TPixel> rgb, int inMax, ChromaFormat format, YuvColor color, int depth, ChromaDownsampling mode, Tally tally)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int w = rgb.Width, h = rgb.Height;
        int[,,] codes = new int[h, w, 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (codes[y, x, 0], codes[y, x, 1], codes[y, x, 2]) = Channels(rgb.Row(y)[x]);
            }
        }

        (int[,] ey, int[,]? eu, int[,]? ev) = YuvReference.RgbToPlanes(codes, inMax, format, color, depth, mode);
        YuvConvertOptions options = new() { Downsampling = mode };
        tally.Case = $"{typeof(TPixel).Name} to {format} {color} {depth}-bit {mode}";
        if (depth == 8)
        {
            using YuvRaster<byte> yuv = YuvRaster.FromRaster(rgb, format, color, alpha: true, options);
            Compare(yuv);
        }
        else
        {
            using YuvRaster<ushort> yuv = YuvRaster.FromRaster(rgb, format, depth, color, alpha: true, options);
            Compare(yuv);
        }

        void Compare<TSample>(YuvRaster<TSample> yuv)
            where TSample : unmanaged
        {
            Assert.True(yuv.HasAlpha);
            Assert.Equal(format, yuv.ChromaFormat);
            Same(Plane(yuv.Y), ey);
            if (eu is not null)
            {
                Same(Plane(yuv.U!), eu);
                Same(Plane(yuv.V!), ev!);
            }
            else
            {
                Assert.Null(yuv.U);
            }
        }

        void Same(int[,] actual, int[,] expected)
        {
            Assert.Equal(expected.GetLength(0), actual.GetLength(0));
            Assert.Equal(expected.GetLength(1), actual.GetLength(1));
            for (int y = 0; y < expected.GetLength(0); y++)
            {
                for (int x = 0; x < expected.GetLength(1); x++)
                {
                    tally.Add(actual[y, x], expected[y, x], (x, y, 0));
                }
            }
        }
    }

    private static (int R, int G, int B) Channels<TPixel>(TPixel pixel)
    {
        return pixel switch
        {
            Rgba8 p => (p.R, p.G, p.B),
            Rgba16 p => (p.R, p.G, p.B),
            Rgb8 p => (p.R, p.G, p.B),
            _ => throw new NotSupportedException(),
        };
    }

    /// <summary>Counts samples identical to, and within one level of, the reference.</summary>
    internal sealed class Tally(string name = "", double minimumIdentical = 0.99)
    {
        private readonly string _name = name;
        private long _total;
        private long _identical;

        internal string Case { get; set; } = "";

        internal void Add(int actual, int expected, object where)
        {
            _total++;
            if (actual == expected)
            {
                _identical++;
            }
            else if (Math.Abs(actual - expected) > 1)
            {
                Assert.Fail($"{Case}: {actual} where the reference has {expected}, at {where}.");
            }
        }

        internal void Check()
        {
            double share = (double)_identical / _total;
            Assert.True(share >= minimumIdentical, $"{_name}: only {share:P3} of {_total} samples identical to the reference.");
        }
    }
}
