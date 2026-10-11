using Egami.Formats;
using Egami.Formats.Jbig2;

namespace Egami.Tests.Formats.Jbig2;

/// <summary>The shared MQ decoder and the packed 1-bit bitmaps.</summary>
public class MqDecoderTests
{
    [Fact]
    public void DecodesTheT88TestSequence()
    {
        // T.88 H.2: 30 coded bytes decode, in one context, to these 32 bytes of decisions, most significant bit first.
        byte[] coded =
        [
            0x84, 0xC7, 0x3B, 0xFC, 0xE1, 0xA1, 0x43, 0x04, 0x02, 0x20, 0x00, 0x00, 0x41, 0x0D, 0xBB, 0x86,
            0xF4, 0x31, 0x7F, 0xFF, 0x88, 0xFF, 0x37, 0x47, 0x1A, 0xDB, 0x6A, 0xDF, 0xFF, 0xAC,
        ];
        byte[] expected =
        [
            0x00, 0x02, 0x00, 0x51, 0x00, 0x00, 0x00, 0xC0, 0x03, 0x52, 0x87, 0x2A, 0xAA, 0xAA, 0xAA, 0xAA,
            0x82, 0xC0, 0x20, 0x00, 0xFC, 0xD7, 0x9E, 0xF6, 0xBF, 0x7F, 0xED, 0x90, 0x4F, 0x46, 0xA3, 0xBF,
        ];
        MqDecoder mq = new();
        mq.Start(coded, 0, coded.Length);
        byte context = 0;
        byte[] decoded = new byte[32];
        for (int i = 0; i < 256; i++)
        {
            decoded[i >> 3] |= (byte)(mq.Decode(ref context) << (7 - (i & 7)));
        }

        Assert.Equal(expected, decoded);
        Assert.Equal(0, mq.Overrun);
    }

    [Fact]
    public void FeedsOnesPastTheEndAndCountsTheOverrun()
    {
        MqDecoder mq = new();
        mq.Start([0x00], 0, 1);
        byte context = 0;
        for (int i = 0; i < 64; i++)
        {
            mq.Decode(ref context);
        }

        Assert.True(mq.Overrun > 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ComposesWordWiseAsPixelByPixel(int op)
    {
        Random random = new(op);
        for (int trial = 0; trial < 200; trial++)
        {
            Jbig2Bitmap target = RandomBitmap(random, random.Next(1, 200), random.Next(1, 20));
            Jbig2Bitmap source = RandomBitmap(random, random.Next(1, 200), random.Next(1, 20));
            int x = random.Next(-source.Width - 5, target.Width + 5);
            int y = random.Next(-source.Height - 2, target.Height + 2);
            int[,] expected = new int[target.Width, target.Height];
            for (int ty = 0; ty < target.Height; ty++)
            {
                for (int tx = 0; tx < target.Width; tx++)
                {
                    int old = target.Get(tx, ty);
                    int sx = tx - x;
                    int sy = ty - y;
                    if (sx < 0 || sy < 0 || sx >= source.Width || sy >= source.Height)
                    {
                        expected[tx, ty] = old;
                        continue;
                    }

                    int s = source.Get(sx, sy);
                    expected[tx, ty] = op switch
                    {
                        0 => old | s,
                        1 => old & s,
                        2 => old ^ s,
                        3 => (old ^ s) ^ 1,
                        _ => s,
                    };
                }
            }

            target.Compose(source, x, y, (Jbig2Combine)op);
            for (int ty = 0; ty < target.Height; ty++)
            {
                for (int tx = 0; tx < target.Width; tx++)
                {
                    Assert.Equal(expected[tx, ty], target.Get(tx, ty));
                }

                Assert.Equal(0UL, target.Row(ty)[^1] & ~Jbig2Bitmap.EdgeMask(target.Width));
            }
        }
    }

    private static Jbig2Bitmap RandomBitmap(Random random, int width, int height)
    {
        Jbig2Bitmap bitmap = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.Set(x, y, random.Next(2));
            }
        }

        return bitmap;
    }
}
