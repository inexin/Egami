using Egami.Pixels;

namespace Egami.Tests.Yuv;

/// <summary>
/// Phase 2.1.4 task 11: property tests over random sizes and strides. Wrap accepts exactly the planes that are long enough and
/// reads every sample where the stride says; the interleaved copies round-trip and never touch bytes outside their rows.
/// </summary>
public class YuvPropertyTests
{
    [Fact]
    public void WrapAcceptsExactlyTheLongEnoughPlanes()
    {
        Random random = new(11);
        for (int i = 0; i < 500; i++)
        {
            int width = random.Next(1, 70), height = random.Next(1, 40);
            ChromaFormat format = (ChromaFormat)random.Next(4);
            (int cw, int ch) = YuvPlaneSize.ChromaSize(format, width, height);
            int yStride = width + random.Next(0, 9), cStride = cw + random.Next(0, 9);
            int yNeeded = ((height - 1) * yStride) + width, cNeeded = cw == 0 ? 0 : ((ch - 1) * cStride) + cw;
            ushort[] y = Fill(random, yNeeded), u = Fill(random, cNeeded), v = Fill(random, cNeeded);

            using (YuvRaster<ushort> yuv = YuvRaster.Wrap<ushort>(y, yStride, u, cStride, v, cStride, width, height, format, 12, YuvColor.Bt709Limited))
            {
                int row = random.Next(height), col = random.Next(width);
                Assert.Equal(y[(row * yStride) + col], yuv.Y.Row(row)[col]);
                if (cw > 0)
                {
                    int crow = random.Next(ch), ccol = random.Next(cw);
                    Assert.Equal(u[(crow * cStride) + ccol], yuv.U!.Row(crow)[ccol]);
                    Assert.Equal(v[(crow * cStride) + ccol], yuv.V!.Row(crow)[ccol]);
                }
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Wrap<ushort>(y.AsMemory(1), yStride, u, cStride, v, cStride, width, height, format, 12, YuvColor.Bt709Limited));
            if (cw > 0)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Wrap<ushort>(y, yStride, u.AsMemory(1), cStride, v, cStride, width, height, format, 12, YuvColor.Bt709Limited));
            }

            // A stride below the width is refused (0 means packed, so only widths above 1 can have one).
            if (cw > 1)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Wrap<ushort>(y, yStride, u, cw - 1, v, cStride, width, height, format, 12, YuvColor.Bt709Limited));
            }

            if (width > 1)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => YuvRaster.Wrap<ushort>(y, width - 1, u, cStride, v, cStride, width, height, format, 12, YuvColor.Bt709Limited));
            }
        }
    }

    [Fact]
    public void PackedCopiesRoundTripAndStayInsideTheirRows()
    {
        Random random = new(12);
        for (int i = 0; i < 400; i++)
        {
            YuvPackedFormat format = (YuvPackedFormat)random.Next(6);
            int width = random.Next(1, 90), height = random.Next(1, 30);
            bool twoPlanes = format is not (YuvPackedFormat.Yuyv or YuvPackedFormat.Uyvy);
            int sampleBytes = format is YuvPackedFormat.P010 or YuvPackedFormat.P016 ? 2 : 1;
            int depth = format switch { YuvPackedFormat.P010 => 10, YuvPackedFormat.P016 => random.Next(3) switch { 0 => 10, 1 => 12, _ => 16 }, _ => 8 };
            int pairs = (width + 1) / 2;
            int rowBytes = twoPlanes ? width * sampleBytes : 4 * pairs;
            int stride = Math.Max(rowBytes, twoPlanes ? 2 * pairs * sampleBytes : 0) + (sampleBytes * random.Next(0, 10));
            int chromaRows = twoPlanes ? (height + 1) / 2 : 0;

            ChromaFormat chroma = twoPlanes ? ChromaFormat.Yuv420 : ChromaFormat.Yuv422;
            using YuvRaster yuv = depth == 8 ? YuvRaster.Create(width, height, chroma, YuvColor.Jpeg) : YuvRaster.Create(width, height, chroma, depth, YuvColor.Jpeg);
            if (yuv is YuvRaster<byte> b)
            {
                YuvConversionTests.Fill(b, random);
            }
            else
            {
                YuvConversionTests.Fill((YuvRaster<ushort>)yuv, random);
            }

            // The frame is pre-filled with a marker: padding bytes between rows must keep it.
            byte[] frame = new byte[(stride * (height + chromaRows)) + 3];
            Array.Fill(frame, (byte)0xA5);
            yuv.CopyToPacked(format, frame.AsSpan(0, frame.Length - 3), stride);
            for (int row = 0; row < height + chromaRows; row++)
            {
                int used = row < height ? rowBytes : 2 * pairs * sampleBytes;
                Assert.All(frame.AsSpan((row * stride) + used, stride - used).ToArray(), x => Assert.Equal(0xA5, x));
            }

            Assert.All(frame.AsSpan(frame.Length - 3).ToArray(), x => Assert.Equal(0xA5, x));
            using YuvRaster again = depth == 8 ? YuvRaster.Create(width, height, chroma, YuvColor.Jpeg) : YuvRaster.Create(width, height, chroma, depth, YuvColor.Jpeg);
            again.CopyFromPacked(format, frame, stride);
            byte[] second = new byte[frame.Length];
            Array.Fill(second, (byte)0xA5);
            again.CopyToPacked(format, second.AsSpan(0, second.Length - 3), stride);
            Assert.Equal(frame, second);
        }
    }

    private static ushort[] Fill(Random random, int count)
    {
        ushort[] values = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = (ushort)random.Next(4096);
        }

        return values;
    }
}
