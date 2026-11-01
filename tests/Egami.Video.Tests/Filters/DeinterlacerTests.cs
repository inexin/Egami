using Egami.Pixels;
using Egami.Video.Filters;

namespace Egami.Video.Tests.Filters;

/// <summary>
/// Video 1.2.0 Build Plan task 1: the deinterlacer on the synthetic woven set. Kept lines come out unchanged, still pictures
/// come back exactly, moving ones close to the progressive pictures they were woven from, in every planar format.
/// </summary>
public sealed class DeinterlacerTests
{
    private const int Width = 160;
    private const int Height = 96;

    [Theory]
    [InlineData(DeinterlaceMode.Adaptive)]
    [InlineData(DeinterlaceMode.Bob)]
    public void KeptLinesPassUnchanged(DeinterlaceMode mode)
    {
        (List<YuvVideoFrame> frames, List<YuvRaster> pictures) = WovenClips.Clip("mixed", 6, Width, Height);
        List<YuvVideoFrame> output = Run(new Deinterlacer(new DeinterlacerOptions { Mode = mode }), frames);
        Assert.Equal(frames.Count, output.Count);
        for (int n = 0; n < output.Count; n++)
        {
            YuvRaster<byte> result = (YuvRaster<byte>)output[n].Raster;
            YuvRaster<byte> source = (YuvRaster<byte>)frames[n].Raster;
            for (int y = 0; y < Height; y += 2)
            {
                Assert.True(result.Y.Row(y)[..Width].SequenceEqual(source.Y.Row(y)[..Width]), $"frame {n} luma row {y}");
            }

            for (int y = 0; y < result.U!.Height; y += 2)
            {
                // 4:2:0 fields keep their own chroma rows.
                Assert.True(result.U.Row(y)[..result.U.Width].SequenceEqual(source.U!.Row(y)[..result.U.Width]), $"frame {n} chroma row {y}");
            }

            Assert.Equal(FieldOrder.Progressive, output[n].FieldOrder);
            Assert.Equal(frames[n].Timestamp, output[n].Timestamp);
        }

        Dispose(frames, output, pictures);
    }

    [Fact]
    public void StillPicturesComeBackExactly()
    {
        // Nothing moves, so Adaptive keeps the woven lines, even the one-line stripes Bob cannot keep.
        (List<YuvVideoFrame> frames, List<YuvRaster> pictures) = WovenClips.Clip("still", 5, Width, Height);
        List<YuvVideoFrame> adaptive = Run(new Deinterlacer(), frames);
        List<YuvVideoFrame> bob = Run(new Deinterlacer(new DeinterlacerOptions { Mode = DeinterlaceMode.Bob }), frames);
        for (int n = 0; n < frames.Count; n++)
        {
            Assert.Equal(100, WovenClips.Psnr(adaptive[n].Raster, pictures[2 * n]));
            Assert.True(WovenClips.Psnr(bob[n].Raster, pictures[2 * n]) < 30);
        }

        Dispose(frames, adaptive, bob, pictures);
    }

    [Theory]
    [InlineData("pan")]
    [InlineData("bar")]
    [InlineData("mixed")]
    public void MovingPicturesComeCloseToTheProgressiveOnes(string scene)
    {
        (List<YuvVideoFrame> frames, List<YuvRaster> pictures) = WovenClips.Clip(scene, 8, Width, Height);
        List<YuvVideoFrame> adaptive = Run(new Deinterlacer(new DeinterlacerOptions { Rate = DeinterlaceRate.Field }), frames);
        List<YuvVideoFrame> bob = Run(new Deinterlacer(new DeinterlacerOptions { Mode = DeinterlaceMode.Bob, Rate = DeinterlaceRate.Field }), frames);
        Assert.Equal(pictures.Count, adaptive.Count);
        double woven = 0, adaptivePsnr = 0, bobPsnr = 0;
        for (int t = 2; t < pictures.Count - 2; t++)
        {
            woven += WovenClips.Psnr(frames[t / 2].Raster, pictures[t]);
            adaptivePsnr += WovenClips.Psnr(adaptive[t].Raster, pictures[t]);
            bobPsnr += WovenClips.Psnr(bob[t].Raster, pictures[t]);
        }

        int count = pictures.Count - 4;
        woven /= count;
        adaptivePsnr /= count;
        bobPsnr /= count;
        Assert.True(adaptivePsnr > woven + 3, $"{scene}: adaptive {adaptivePsnr:F2} dB, woven {woven:F2} dB");
        Assert.True(scene == "mixed" || bobPsnr > woven, $"{scene}: bob {bobPsnr:F2} dB, woven {woven:F2} dB");
        Assert.True(adaptivePsnr > bobPsnr, $"{scene}: adaptive {adaptivePsnr:F2} dB, bob {bobPsnr:F2} dB");
        Assert.True(adaptivePsnr > 28, $"{scene}: adaptive {adaptivePsnr:F2} dB");
        Dispose(frames, adaptive, bob, pictures);
    }

    [Fact]
    public void FieldRateSplitsTheFrameTime()
    {
        (List<YuvVideoFrame> frames, List<YuvRaster> pictures) = WovenClips.Clip("bar", 4, Width, Height);
        List<YuvVideoFrame> output = Run(new Deinterlacer(new DeinterlacerOptions { Rate = DeinterlaceRate.Field }), frames);
        Assert.Equal(8, output.Count);
        for (int i = 0; i < output.Count; i++)
        {
            Assert.Equal(i * 0.02, output[i].Timestamp.Seconds, 9);
            Assert.Equal(0.02, output[i].Duration!.Value.Seconds, 9);
        }

        Dispose(frames, output, pictures);
    }

    [Fact]
    public void FieldRateTimesFramesWithoutDurationFromTheirNeighbors()
    {
        (List<YuvVideoFrame> frames, List<YuvRaster> pictures) = WovenClips.Clip("bar", 3, Width, Height);
        List<YuvVideoFrame> bare = [.. frames.Select(f => new YuvVideoFrame(WovenClips.Copy(f.Raster), f.Timestamp) { FieldOrder = f.FieldOrder })];
        List<YuvVideoFrame> output = Run(new Deinterlacer(new DeinterlacerOptions { Rate = DeinterlaceRate.Field }), bare);
        Assert.Equal([0, 0.02, 0.04, 0.06, 0.08, 0.1], output.Select(f => Math.Round(f.Timestamp.Seconds, 9)));
        Dispose(frames, bare, output, pictures);
    }

    [Fact]
    public void FlaggedPassesProgressiveFramesAndAllDeinterlacesThem()
    {
        (List<YuvVideoFrame> frames, List<YuvRaster> pictures) = WovenClips.Clip("bar", 4, Width, Height);
        List<YuvVideoFrame> unflagged = [.. frames.Select(f => new YuvVideoFrame(WovenClips.Copy(f.Raster), f.Timestamp, f.Duration))];
        List<YuvVideoFrame> passed = Run(new Deinterlacer(), unflagged);
        List<YuvVideoFrame> forced = Run(new Deinterlacer(new DeinterlacerOptions { Frames = DeinterlaceFrames.All }), unflagged);
        List<YuvVideoFrame> doubled = Run(new Deinterlacer(new DeinterlacerOptions { Rate = DeinterlaceRate.Field }), unflagged);
        for (int n = 0; n < frames.Count; n++)
        {
            Assert.Equal(100, WovenClips.Psnr(passed[n].Raster, frames[n].Raster));
            Assert.True(WovenClips.Psnr(forced[n].Raster, frames[n].Raster) < 100);
            Assert.Equal(100, WovenClips.Psnr(doubled[(2 * n) + 1].Raster, frames[n].Raster));
        }

        Assert.Equal(2 * frames.Count, doubled.Count);
        Dispose(frames, unflagged, passed, forced, doubled, pictures);
    }

    [Fact]
    public void FieldOrderComesFromTheFrameThenTheOptions()
    {
        (List<YuvVideoFrame> frames, List<YuvRaster> pictures) = WovenClips.Clip("bar", 6, Width, Height, FieldOrder.BottomFieldFirst);
        double right = MeanPsnr(Run(new Deinterlacer(new DeinterlacerOptions { Rate = DeinterlaceRate.Field }), frames), pictures);
        double wrong = MeanPsnr(Run(new Deinterlacer(new DeinterlacerOptions { Rate = DeinterlaceRate.Field, FieldOrder = FieldOrder.TopFieldFirst }), frames), pictures);
        Assert.True(right > wrong + 3, $"right order {right:F2} dB, wrong {wrong:F2} dB");

        // Unflagged frames take the track's order.
        List<YuvVideoFrame> unflagged = [.. frames.Select(f => new YuvVideoFrame(WovenClips.Copy(f.Raster), f.Timestamp, f.Duration))];
        VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), FieldOrder = FieldOrder.BottomFieldFirst, Width = Width, Height = Height };
        DeinterlacerOptions all = new() { Rate = DeinterlaceRate.Field, Frames = DeinterlaceFrames.All };
        double byTrack = MeanPsnr(Run(new Deinterlacer(track, all), unflagged), pictures);
        double byDefault = MeanPsnr(Run(new Deinterlacer(all), unflagged), pictures);
        Assert.Equal(right, byTrack, 6);
        Assert.Equal(wrong, byDefault, 6);
        Dispose(frames, unflagged, pictures);
    }

    [Theory]
    [InlineData(ChromaFormat.Yuv420, 8, false)]
    [InlineData(ChromaFormat.Yuv420, 10, false)]
    [InlineData(ChromaFormat.Yuv422, 10, false)]
    [InlineData(ChromaFormat.Yuv444, 12, false)]
    [InlineData(ChromaFormat.Yuv420, 16, false)]
    [InlineData(ChromaFormat.Yuv422, 8, true)]
    [InlineData(ChromaFormat.Yuv444, 10, true)]
    [InlineData(ChromaFormat.Yuv400, 8, false)]
    [InlineData(ChromaFormat.Yuv411, 8, false)]
    public void EveryPlanarFormatWorks(ChromaFormat chroma, int depth, bool alpha)
    {
        (List<YuvVideoFrame> frames, List<YuvRaster> pictures) = WovenClips.Clip("mixed", 6, 72, 40, chroma: chroma, depth: depth, alpha: alpha);
        List<YuvVideoFrame> output = Run(new Deinterlacer(new DeinterlacerOptions { Rate = DeinterlaceRate.Field }), frames);
        Assert.Equal(12, output.Count);
        for (int t = 0; t < output.Count; t++)
        {
            YuvRaster raster = output[t].Raster;
            Assert.Equal((chroma, depth, alpha), (raster.ChromaFormat, raster.BitDepth, raster.HasAlpha));
        }

        double woven = 0, result = 0;
        for (int t = 2; t < 10; t++)
        {
            woven += WovenClips.Psnr(frames[t / 2].Raster, pictures[t]);
            result += WovenClips.Psnr(output[t].Raster, pictures[t]);
        }

        Assert.True(result > woven + 8, $"{result / 8:F2} dB against {woven / 8:F2} woven");
        Dispose(frames, output, pictures);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(16)]
    public void VectorKernelsMatchTheScalarOnes(int depth)
    {
        Random random = new(depth);
        int max = (1 << depth) - 1;
        for (int width = 1; width < 90; width += 7)
        {
            for (int round = 0; round < 20; round++)
            {
                // Rows from noise and from smooth ramps, so both the edge search and the band clamp take every branch.
                ushort[][] rows = [.. Enumerable.Range(0, 12).Select(_ => Row(random, width, max, round % 3))];
                if (depth == 8)
                {
                    Compare(rows.Select(r => r.Select(v => (byte)v).ToArray()).ToArray(), width);
                }
                else
                {
                    Compare(rows, width);
                }
            }
        }
    }

    [Fact]
    public void FlushResetAndSizeChanges()
    {
        (List<YuvVideoFrame> frames, List<YuvRaster> pictures) = WovenClips.Clip("bar", 3, Width, Height);
        (List<YuvVideoFrame> small, List<YuvRaster> smallPictures) = WovenClips.Clip("bar", 2, 64, 32);
        using Deinterlacer filter = new();
        Assert.Equal(MediaTime.Zero, filter.Latency);
        filter.Send(frames[0]);
        Assert.False(filter.TryReceive(out _));
        filter.Send(frames[1]);
        Assert.Equal(0.04, filter.Latency.Seconds, 9);
        Assert.True(filter.TryReceive(out YuvVideoFrame? first));
        first.Dispose();

        // A new size finishes the frame held first.
        filter.Send(small[0]);
        Assert.True(filter.TryReceive(out YuvVideoFrame? held));
        Assert.Equal((Width, Height), (held.Width, held.Height));
        held.Dispose();
        filter.Send(small[1]);
        filter.Flush();
        Assert.True(filter.TryReceive(out YuvVideoFrame? a));
        Assert.True(filter.TryReceive(out YuvVideoFrame? b));
        Assert.False(filter.TryReceive(out _));
        Assert.Equal((64, 32), (b.Width, b.Height));
        a.Dispose();
        b.Dispose();

        filter.Send(frames[2]);
        filter.Send(frames[0]);
        filter.Reset();
        Assert.False(filter.TryReceive(out _));
        filter.Flush();
        Assert.False(filter.TryReceive(out _));
        Dispose(frames, small, pictures, smallPictures);
    }

    [Fact]
    public void BadOptionsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Deinterlacer(new DeinterlacerOptions { FieldOrder = FieldOrder.Progressive }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Deinterlacer(new DeinterlacerOptions { Mode = (DeinterlaceMode)7 }));
        Assert.Throws<ArgumentNullException>(() => new Deinterlacer((VideoTrackInfo)null!));
        Deinterlacer filter = new();
        filter.Dispose();
        Assert.Throws<ObjectDisposedException>(() => filter.Flush());
    }

    internal static List<YuvVideoFrame> Run(IVideoFilter filter, IEnumerable<YuvVideoFrame> frames)
    {
        List<YuvVideoFrame> output = [];
        using (filter)
        {
            foreach (YuvVideoFrame frame in frames)
            {
                filter.Send(frame);
                while (filter.TryReceive(out YuvVideoFrame? result))
                {
                    output.Add(result);
                }
            }

            filter.Flush();
            while (filter.TryReceive(out YuvVideoFrame? result))
            {
                output.Add(result);
            }
        }

        return output;
    }

    private static double MeanPsnr(List<YuvVideoFrame> output, List<YuvRaster> pictures)
    {
        double sum = 0;
        for (int t = 2; t < pictures.Count - 2; t++)
        {
            sum += WovenClips.Psnr(output[t].Raster, pictures[t]);
        }

        foreach (YuvVideoFrame frame in output)
        {
            frame.Dispose();
        }

        return sum / (pictures.Count - 4);
    }

    private static ushort[] Row(Random random, int width, int max, int kind)
    {
        ushort[] row = new ushort[width];
        int start = random.Next(max + 1);
        int slope = random.Next(-40, 41);
        for (int x = 0; x < width; x++)
        {
            row[x] = (ushort)(kind switch
            {
                0 => random.Next(max + 1),
                1 => Math.Clamp(start + (slope * x), 0, max),
                _ => random.Next(4) == 0 ? random.Next(max + 1) : Math.Clamp(start + (slope * x / 4), 0, max),
            });
        }

        return row;
    }

    private static void Compare<T>(T[][] rows, int width)
        where T : unmanaged, IEquatable<T>
    {
        DeinterlaceKernels.FieldRows<T> field = new()
        {
            Above = rows[0],
            Below = rows[1],
            Before = rows[2],
            After = rows[3],
            BeforeUp = rows[4],
            AfterUp = rows[5],
            BeforeDown = rows[6],
            AfterDown = rows[7],
            EarlierAbove = rows[8],
            EarlierBelow = rows[9],
            LaterAbove = rows[10],
            LaterBelow = rows[11],
        };
        T[] vector = new T[width];
        T[] scalar = new T[width];
        DeinterlaceKernels.Adaptive(field, vector);
        DeinterlaceKernels.Adaptive(field, scalar, vectors: false);
        Assert.Equal(scalar, vector);
        DeinterlaceKernels.Bob<T>(rows[0], rows[1], vector);
        DeinterlaceKernels.Bob<T>(rows[0], rows[1], scalar, vectors: false);
        Assert.Equal(scalar, vector);
    }

    private static void Dispose(params IEnumerable<IDisposable>[] lists)
    {
        foreach (IEnumerable<IDisposable> list in lists)
        {
            foreach (IDisposable item in list)
            {
                item.Dispose();
            }
        }
    }
}
