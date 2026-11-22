using Egami.Video.Codecs.Hevc.Encoder;

namespace Egami.Video.Hevc.Tests;

/// <summary>The motion search's 8-bit kernels give exactly the sums of the general ones.</summary>
public sealed class SearchKernelTests
{
    [Theory]
    [InlineData(8, 8)]
    [InlineData(16, 8)]
    [InlineData(8, 16)]
    [InlineData(16, 16)]
    [InlineData(32, 16)]
    [InlineData(64, 64)]
    [InlineData(8, 4)]
    [InlineData(4, 8)]
    public void NarrowKernelsMatchTheWideOnes(int w, int h)
    {
        Random random = new(w * 100 + h);
        const int Stride = 80;
        ushort[] a = new ushort[Stride * 70], b = new ushort[Stride * 70];
        for (int i = 0; i < a.Length; i++)
        {
            (a[i], b[i]) = ((ushort)random.Next(256), (ushort)random.Next(256));
        }

        // Extremes too: the largest differences the 16-bit lanes must hold.
        for (int i = 0; i < Stride * 8; i++)
        {
            (a[i], b[i]) = ((i & 1) == 0 ? (ushort)255 : (ushort)0, (i & 1) == 0 ? (ushort)0 : (ushort)255);
        }

        byte[] a8 = HevcReference.Bytes(a), b8 = HevcReference.Bytes(b);
        foreach (int at in (int[])[0, 3, Stride + 5])
        {
            Assert.Equal(HevcMotionSearch.Satd(a, at, Stride, b, at + 1, Stride, w, h), HevcMotionSearch.Satd(a, at, Stride, b, at + 1, Stride, w, h, true));
            if ((w & 7) == 0)
            {
                Assert.Equal(HevcMotionSearch.Sad(a, at, Stride, b, at + 2, Stride, w, h), HevcMotionSearch.Sad8(a8, at, Stride, b8, at + 2, Stride, w, h));
            }
        }
    }
}
