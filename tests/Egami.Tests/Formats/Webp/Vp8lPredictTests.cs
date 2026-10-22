using Egami.Formats.Webp;

namespace Egami.Tests.Formats.Webp;

/// <summary>Undoing the predictor transform a run at a time gives what the format's per-pixel definition gives, for every mode.</summary>
public class Vp8lPredictTests
{
    public static TheoryData<int> Modes() => [.. Enumerable.Range(0, 16)];

    [Theory]
    [MemberData(nameof(Modes))]
    public void UndoRunMatchesThePerPixelPredictor(int mode)
    {
        Random random = new(mode);
        foreach (int width in (ReadOnlySpan<int>)[2, 3, 5, 8, 37, 64])
        {
            for (int trial = 0; trial < 20; trial++)
            {
                // Some trials use extreme channels only, so the clamps and the averages' rounding are both reached.
                uint[] above = Pixels(random, width, extreme: trial % 2 == 1);
                uint[] residuals = Pixels(random, width, extreme: trial % 4 == 3);
                uint[] expected = (uint[])residuals.Clone();
                for (int x = 1; x < width; x++)
                {
                    uint tr = x + 1 < width ? above[x + 1] : expected[0];
                    expected[x] = Vp8lDecoder.AddPixels(expected[x], Vp8lPredictors.Predict(mode, expected[x - 1], above[x], above[x - 1], tr));
                }

                // In runs of uneven length, as tiles split a row.
                uint[] actual = (uint[])residuals.Clone();
                for (int x = 1; x < width;)
                {
                    int end = Math.Min(width, x + 1 + random.Next(9));
                    Vp8lPredict.UndoRun(mode, actual, above, x, end);
                    x = end;
                }

                Assert.Equal(expected, actual);
            }
        }
    }

    private static uint[] Pixels(Random random, int width, bool extreme)
    {
        uint[] pixels = new uint[width];
        ReadOnlySpan<byte> edges = [0, 1, 127, 128, 254, 255];
        for (int x = 0; x < width; x++)
        {
            uint p = 0;
            for (int c = 0; c < 4; c++)
            {
                p = (p << 8) | (extreme ? edges[random.Next(edges.Length)] : (byte)random.Next(256));
            }

            pixels[x] = p;
        }

        return pixels;
    }
}
