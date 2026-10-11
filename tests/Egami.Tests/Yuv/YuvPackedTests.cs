using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Tests.Yuv;

/// <summary>Phase 2.1.4 task 7: copies to and from NV12, NV21, P010, P016, YUYV and UYVY: exact both ways, at odd sizes and padded strides.</summary>
public class YuvPackedTests
{
    public static TheoryData<YuvPackedFormat, int, int> Cases()
    {
        TheoryData<YuvPackedFormat, int, int> data = [];
        foreach (YuvPackedFormat format in Enum.GetValues<YuvPackedFormat>())
        {
            foreach ((int w, int h) in new[] { (1, 1), (2, 2), (17, 9), (64, 3), (33, 34), (100, 1) })
            {
                data.Add(format, w, h);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsExactlyAndMatchesTheLayout(YuvPackedFormat format, int width, int height)
    {
        Random random = new(HashCode.Combine(format, width, height));
        bool twoPlanes = format is not (YuvPackedFormat.Yuyv or YuvPackedFormat.Uyvy);
        int sampleBytes = format is YuvPackedFormat.P010 or YuvPackedFormat.P016 ? 2 : 1;
        int depth = format switch { YuvPackedFormat.P010 => 10, YuvPackedFormat.P016 => 16, _ => 8 };
        int shift = sampleBytes == 2 ? 16 - depth : 0;
        int pairs = (width + 1) / 2;
        int rowBytes = twoPlanes ? width * sampleBytes : 4 * pairs;
        int stride = rowBytes + 14, chromaStride = (2 * pairs * sampleBytes) + 6;
        int chromaRows = (height + 1) / 2;

        // A frame of random bytes; low bits below an MSB-aligned sample and an odd width's padding luma do not survive a round trip.
        byte[] data = new byte[(stride * height) + (twoPlanes ? chromaStride * chromaRows : 0)];
        random.NextBytes(data);
        if (shift > 0)
        {
            Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(data.AsSpan());
            for (int i = 0; i < words.Length; i++)
            {
                words[i] &= (ushort)(0xFFFF << shift);
            }
        }

        ChromaFormat chroma = twoPlanes ? ChromaFormat.Yuv420 : ChromaFormat.Yuv422;
        using YuvRaster yuv = depth == 8 ? YuvRaster.Create(width, height, chroma, YuvColor.Bt709Limited) : YuvRaster.Create(width, height, chroma, depth, YuvColor.Bt709Limited);
        ReadOnlySpan<byte> luma = data.AsSpan(0, stride * height), chromaPlane = data.AsSpan(stride * height);
        yuv.CopyFromPacked(format, luma, stride, chromaPlane, chromaStride);

        // Every sample where the layout says it is.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int expected = twoPlanes ? Word(data, (y * stride) + (x * sampleBytes), sampleBytes) >> shift
                    : data[(y * stride) + (4 * (x / 2)) + (2 * (x % 2)) + (format == YuvPackedFormat.Yuyv ? 0 : 1)];
                Assert.Equal(expected, Sample(yuv, 0, x, y));
            }
        }

        for (int j = 0; j < (twoPlanes ? chromaRows : height); j++)
        {
            for (int i = 0; i < pairs; i++)
            {
                int u, v;
                if (twoPlanes)
                {
                    int at = (stride * height) + (j * chromaStride) + (2 * i * sampleBytes);
                    (u, v) = (Word(data, at, sampleBytes) >> shift, Word(data, at + sampleBytes, sampleBytes) >> shift);
                    if (format == YuvPackedFormat.Nv21)
                    {
                        (u, v) = (v, u);
                    }
                }
                else
                {
                    int at = (j * stride) + (4 * i) + (format == YuvPackedFormat.Yuyv ? 1 : 0);
                    (u, v) = (data[at], data[at + 2]);
                }

                Assert.Equal(u, Sample(yuv, 1, i, j));
                Assert.Equal(v, Sample(yuv, 2, i, j));
            }
        }

        // Back out, both with separate planes and with chroma after luma, then in again: the same samples.
        byte[] separate = new byte[data.Length];
        yuv.CopyToPacked(format, separate.AsSpan(0, stride * height), stride, separate.AsSpan(stride * height), chromaStride);
        byte[] joined = new byte[(stride * height) + (twoPlanes ? stride * chromaRows : 0)];
        yuv.CopyToPacked(format, joined, stride);
        using YuvRaster again = depth == 8 ? YuvRaster.Create(width, height, chroma, YuvColor.Bt709Limited) : YuvRaster.Create(width, height, chroma, depth, YuvColor.Bt709Limited);
        again.CopyFromPacked(format, joined, stride);
        for (int plane = 0; plane < 3; plane++)
        {
            int pw = plane == 0 ? width : pairs, ph = plane == 0 || !twoPlanes ? height : chromaRows;
            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    Assert.Equal(Sample(yuv, plane, x, y), Sample(again, plane, x, y));
                }
            }
        }

        for (int y = 0; y < height; y++)
        {
            // Row bytes match the original, apart from an odd width's padding luma, which repeats the last sample.
            for (int b = 0; b < rowBytes; b++)
            {
                int at = (y * stride) + b;
                bool padding = !twoPlanes && width % 2 == 1 && b == (4 * (pairs - 1)) + (format == YuvPackedFormat.Yuyv ? 2 : 3);
                Assert.Equal(padding ? Sample(yuv, 0, width - 1, y) : data[at], separate[at]);
            }
        }
    }

    [Fact]
    public void MismatchedRastersAndShortSpansThrow()
    {
        using YuvRaster<byte> yuv420 = YuvRaster.Create(16, 16, ChromaFormat.Yuv420, YuvColor.Jpeg);
        using YuvRaster<byte> yuv422 = YuvRaster.Create(16, 16, ChromaFormat.Yuv422, YuvColor.Jpeg);
        using YuvRaster<ushort> yuv12 = YuvRaster.Create(16, 16, ChromaFormat.Yuv420, 12, YuvColor.Jpeg);
        byte[] frame = new byte[16 * 24 * 2];
        Assert.Throws<ArgumentException>(() => yuv422.CopyFromPacked(YuvPackedFormat.Nv12, frame));
        Assert.Throws<ArgumentException>(() => yuv420.CopyFromPacked(YuvPackedFormat.Yuyv, frame));
        Assert.Throws<ArgumentException>(() => yuv420.CopyFromPacked(YuvPackedFormat.P010, frame));
        Assert.Throws<ArgumentException>(() => yuv12.CopyFromPacked(YuvPackedFormat.P010, frame));
        yuv12.CopyFromPacked(YuvPackedFormat.P016, frame);
        Assert.Throws<ArgumentOutOfRangeException>(() => yuv420.CopyFromPacked(YuvPackedFormat.Nv12, frame.AsSpan(0, (16 * 24) - 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => yuv420.CopyFromPacked(YuvPackedFormat.Nv12, frame, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => yuv420.CopyFromPacked(YuvPackedFormat.Nv12, frame.AsSpan(0, 256), 16, frame.AsSpan(0, 127)));
        yuv420.CopyFromPacked(YuvPackedFormat.Nv12, frame.AsSpan(0, 256), 16, frame.AsSpan(0, 128));
    }

    [Fact]
    public void SamplesAboveTheDepthAreClampedOnTheWayOut()
    {
        using YuvRaster<ushort> yuv = YuvRaster.Create(2, 2, ChromaFormat.Yuv420, 10, YuvColor.Bt709Limited);
        yuv.Y.Row(0).Fill(5000);
        byte[] frame = new byte[12];
        yuv.CopyToPacked(YuvPackedFormat.P010, frame);
        Assert.Equal(0xFFC0, BitConverter.ToUInt16(frame, 0));
    }

    private static int Word(byte[] data, int at, int bytes) => bytes == 1 ? data[at] : BitConverter.ToUInt16(data, at);

    private static int Sample(YuvRaster raster, int plane, int x, int y) => raster switch
    {
        YuvRaster<byte> b => (plane == 0 ? b.Y : plane == 1 ? b.U! : b.V!).Row(y)[x],
        YuvRaster<ushort> w => (plane == 0 ? w.Y : plane == 1 ? w.U! : w.V!).Row(y)[x],
        _ => throw new NotSupportedException(),
    };
}
