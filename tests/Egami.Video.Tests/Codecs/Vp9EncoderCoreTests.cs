using Egami.Pixels;
using Egami.Video.Codecs.Vp9;

namespace Egami.Video.Tests.Codecs;

/// <summary>The VP9 encoder core: transforms, the bool encoder, and frames its own decoder reads back exactly.</summary>
public class Vp9EncoderCoreTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    public void ForwardThenInverseGivesTheResidualBack(int txSize, int txType)
    {
        int n = 4 << txSize;
        short[] residual = new short[n * n];
        uint seed = 7;
        for (int i = 0; i < residual.Length; i++)
        {
            seed = (seed * 1664525) + 1013904223;
            residual[i] = (short)((int)(seed >> 24) - 128);
        }

        int[] coeff = new int[n * n];
        Vp9Transform.Forward(residual, n, coeff, txSize, txType);
        byte[] dst = new byte[n * n];
        Array.Fill(dst, (byte)128);
        Vp9Transform.InverseAdd<byte>(coeff, dst, n, txSize, txType, n * n, 8);
        for (int i = 0; i < residual.Length; i++)
        {
            Assert.InRange(dst[i] - 128 - residual[i], -2, 2);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void VectorForwardTransformsMatchTheScalarOnes(int txSize)
    {
        Assert.SkipUnless(Vp9TransformSimd.IsSupported, "No AVX2 here.");
        int n = 4 << txSize;
        short[] residual = new short[n * n];
        int[] scalar = new int[n * n], vector = new int[n * n];
        uint seed = 11;
        for (int round = 0; round < 40; round++)
        {
            for (int i = 0; i < residual.Length; i++)
            {
                seed = (seed * 1664525) + 1013904223;
                residual[i] = round switch
                {
                    0 => 255,
                    1 => -255,
                    2 => (short)((i & 1) == 0 ? 255 : -255),
                    _ => (short)((int)(seed >> 23) - 255),
                };
            }

            for (int txType = 0; txType < 4; txType++)
            {
                Vp9Transform.Forward(residual, n, scalar, txSize, txType, txSize == 3);
                Vp9TransformSimd.Forward(residual, n, vector, txSize, txType);
                Assert.Equal(scalar, vector);
            }
        }
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 8)]
    [InlineData(32, 32)]
    [InlineData(64, 64)]
    [InlineData(24, 3)]
    public void PixelKernelsMatchPlainLoops(int w, int h)
    {
        uint seed = 5;
        byte[] a = new byte[200 * 80], b = new byte[200 * 80];
        foreach (byte[] array in new[] { a, b })
        {
            for (int i = 0; i < array.Length; i++)
            {
                seed = (seed * 1664525) + 1013904223;
                array[i] = (byte)(seed >> 24);
            }
        }

        long sad = 0, sse = 0;
        short[] expected = new short[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int d = a[3 + (y * 100) + x] - b[7 + (y * 90) + x];
                sad += Math.Abs(d);
                sse += d * d;
                expected[(y * w) + x] = (short)d;
            }
        }

        Assert.Equal(sad, Vp9EncoderSimd.Sad(a, 3, 100, b, 7, 90, w, h));
        Assert.Equal(sse, Vp9EncoderSimd.Sse(a, 3, 100, b, 7, 90, w, h));
        short[] residual = new short[w * h];
        Vp9EncoderSimd.Subtract(a, 3, 100, b, 7, 90, residual, w, h);
        Assert.Equal(expected, residual);
        Assert.Equal(sse, Vp9EncoderSimd.SumSquares(residual));
    }

    [Fact]
    public void BoolEncoderOutputReadsBack()
    {
        byte[] buffer = new byte[16];
        Vp9BoolEncoder w = default;
        w.Start(buffer, 0);
        uint seed = 99;
        List<(int Bit, int Prob)> written = [];
        for (int i = 0; i < 5000; i++)
        {
            seed = (seed * 1664525) + 1013904223;
            int prob = 1 + (int)((seed >> 8) % 255);
            int bit = ((seed >> 20) & 255) >= prob ? 1 : 0;
            written.Add((bit, prob));
            w.Write(bit, prob);
        }

        int length = w.Stop();
        byte[] data = w.Buffer;
        Vp9BoolDecoder r = default;
        Assert.False(r.Init(data, 0, length));
        foreach ((int bit, int prob) in written)
        {
            Assert.Equal(bit, r.Read(prob));
        }

        Assert.False(r.HasError);
    }

    [Theory]
    [InlineData(64, 64, 60, 0)]
    [InlineData(100, 70, 120, 4)]
    [InlineData(33, 17, 200, 2)]
    [InlineData(130, 72, 0, 4)]
    public void KeyframesDecodeToTheEncodersReconstruction(int width, int height, int q, int effort)
    {
        using Vp8EncoderTests.Clip clip = Vp8EncoderTests.Clip.Moving(width, height, 1);
        using Vp9Encoder<byte> encoder = new(width, height, 8, 1, 1, 1, 0, Egami.EgamiContext.Default.Memory);
        Load(encoder, clip.Frames[0]);
        ReadOnlyMemory<byte> frame = encoder.Encode(new Vp9FramePlan { Key = true, Quantizer = q, Effort = effort, LoopFilterLevel = 0 });
        using Vp9Decoder<byte> decoder = new(Egami.EgamiContext.Default.Memory);
        decoder.DecodeFrame(frame.Span, out Vp9FrameResult result);
        Assert.True(result.Shown);
        Vp9Frame<byte> shown = decoder.FrameToShow!;
        Vp9Frame<byte> recon = encoder.Reconstruction;
        for (int p = 0; p < 3; p++)
        {
            for (int y = 0; y < shown.CropHeight(p); y++)
            {
                Assert.True(shown.Plane(p).Slice(y * shown.Strides[p], shown.CropWidth(p)).SequenceEqual(recon.Plane(p).Slice(y * recon.Strides[p], recon.CropWidth(p))),
                    $"plane {p} row {y}");
            }
        }

        double psnr = Psnr(clip.Frames[0].Y, shown);
        Assert.True(psnr > (q == 0 ? 98 : 25), $"{psnr:F1} dB, {frame.Length} bytes");
    }

    internal static void Load(Vp9Encoder<byte> encoder, YuvRaster<byte> picture)
    {
        for (int y = 0; y < picture.Height; y++)
        {
            encoder.SetSourceRow(0, y, picture.Y.Row(y));
        }

        for (int y = 0; y < picture.U!.Height; y++)
        {
            encoder.SetSourceRow(1, y, picture.U.Row(y));
            encoder.SetSourceRow(2, y, picture.V!.Row(y));
        }

        encoder.FinishSource();
    }

    internal static double Psnr(YuvPlane<byte> source, Vp9Frame<byte> frame)
    {
        double sum = 0;
        for (int y = 0; y < source.Height; y++)
        {
            ReadOnlySpan<byte> a = source.Row(y);
            ReadOnlySpan<byte> b = frame.Plane(0).Slice(y * frame.Strides[0], source.Width);
            for (int x = 0; x < source.Width; x++)
            {
                double d = a[x] - b[x];
                sum += d * d;
            }
        }

        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * source.Width * source.Height / sum);
    }
}

/// <summary>Inter frames: the encoder's reconstruction is what its decoder makes of the frame.</summary>
public class Vp9EncoderInterTests
{
    [Theory]
    [InlineData(96, 64, 80, 4, 0)]
    [InlineData(150, 90, 120, 6, 0)]
    [InlineData(150, 90, 120, 6, -1)]
    [InlineData(64, 48, 40, 1, 0)]
    public void InterFramesDecodeToTheEncodersReconstruction(int width, int height, int q, int effort, int filter)
    {
        using Vp8EncoderTests.Clip clip = Vp8EncoderTests.Clip.Moving(width, height, 8);
        using Vp9Encoder<byte> encoder = new(width, height, 8, 1, 1, 1, 0, Egami.EgamiContext.Default.Memory);
        using Vp9Decoder<byte> decoder = new(Egami.EgamiContext.Default.Memory);
        long total = 0;
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            Vp9EncoderCoreTests.Load(encoder, clip.Frames[i]);
            Vp9FramePlan plan = i == 0
                ? new Vp9FramePlan { Key = true, Quantizer = q, Effort = effort, LoopFilterLevel = filter }
                : new Vp9FramePlan { Quantizer = q, Effort = effort, LoopFilterLevel = filter, RefreshFlags = 1, AltRefAhead = i % 2 == 0 };
            ReadOnlyMemory<byte> frame = encoder.Encode(plan);
            total += frame.Length;
            decoder.DecodeFrame(frame.Span, out _);
            Vp9Frame<byte> shown = decoder.FrameToShow!;
            if (filter == 0)
            {
                Vp9Frame<byte> recon = encoder.Reconstruction;
                for (int p = 0; p < 3; p++)
                {
                    for (int y = 0; y < shown.CropHeight(p); y++)
                    {
                        Assert.True(shown.Plane(p).Slice(y * shown.Strides[p], shown.CropWidth(p)).SequenceEqual(recon.Plane(p).Slice(y * recon.Strides[p], recon.CropWidth(p))),
                            $"frame {i} plane {p} row {y}");
                    }
                }
            }

            double psnr = Vp9EncoderCoreTests.Psnr(clip.Frames[i].Y, shown);
            Assert.True(psnr > 25, $"frame {i}: {psnr:F1} dB");
        }
    }
}
