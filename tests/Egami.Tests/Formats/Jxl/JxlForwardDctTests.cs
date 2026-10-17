using Egami.Formats.Jxl;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.7 task 3: every forward transform the encoder uses (DCT2x2 to DCT64x64 with every rectangle, IDENTITY, DCT4x4,
/// DCT4x8 and the four AFVs), followed by 2.9.3's inverse, gives the block back within float rounding; the LF samples it reports
/// give the decoder's lowest frequencies back.
/// </summary>
public class JxlForwardDctTests
{
    public static TheoryData<int> Transforms
    {
        get
        {
            TheoryData<int> data = [];
            foreach (JxlTransformType t in Enum.GetValues<JxlTransformType>())
            {
                (int w, int h) = JxlTransforms8.Size(t);
                if (w <= 8 && h <= 8)
                {
                    data.Add((int)t);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Transforms))]
    public void ForwardThenInverseGivesTheBlockBack(int type)
    {
        JxlTransformType t = (JxlTransformType)type;
        (int w8, int h8) = JxlTransforms8.Size(t);
        int width = w8 * 8;
        int height = h8 * 8;

        // The block sits inside a wider plane, as in a frame.
        int stride = width + 16;
        int offset = (3 * stride) + 8;
        float[] plane = new float[stride * (height + 6)];
        Random random = new((int)t + 17);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // A gradient with noise, at the scale of XYB's Y.
                plane[offset + (y * stride) + x] = (0.3f * x / width) + (0.2f * y / height) + ((float)random.NextDouble() * 0.2f) - 0.1f;
            }
        }

        float[] original = (float[])plane.Clone();
        JxlForwardDct.Forward(t, plane, offset, stride);
        JxlDct.Inverse(t, plane, offset, stride);
        float worst = 0;
        for (int i = 0; i < plane.Length; i++)
        {
            worst = MathF.Max(worst, MathF.Abs(plane[i] - original[i]));
        }

        Assert.True(worst < 2e-6f, $"{t}: largest difference {worst}");
    }

    [Theory]
    [MemberData(nameof(Transforms))]
    public void LfSamplesGiveTheLowestFrequenciesBack(int type)
    {
        JxlTransformType t = (JxlTransformType)type;
        (int w8, int h8) = JxlTransforms8.Size(t);
        int stride = w8 * 8;
        float[] plane = new float[stride * h8 * 8];
        Random random = new((int)t + 3);
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = (float)random.NextDouble();
        }

        float[] pixels = (float[])plane.Clone();
        JxlForwardDct.Forward(t, plane, 0, stride);
        float[] lf = new float[w8 * h8];
        JxlForwardDct.LfOf(t, plane, 0, stride, lf, 0, w8);

        // The LF samples are near the means of their 8×8 blocks (exactly for DCT8).
        for (int by = 0; by < h8; by++)
        {
            for (int bx = 0; bx < w8; bx++)
            {
                float mean = 0;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        mean += pixels[(((by * 8) + y) * stride) + (bx * 8) + x] / 64;
                    }
                }

                Assert.True(MathF.Abs(lf[(by * w8) + bx] - mean) < (t == JxlTransformType.Dct8 ? 1e-6f : 0.25f), $"{t} block {bx},{by}: LF {lf[(by * w8) + bx]}, mean {mean}");
            }
        }

        if (JxlTransforms8.IsSmall(t))
        {
            Assert.Equal(plane[0], lf[0]);
            return;
        }

        // The decoder's LLF step (I.6) from these samples.
        float[] llf = (float[])lf.Clone();
        JxlDct.Dct2D(llf, 0, w8, w8, h8, forward: true);
        int logW = System.Numerics.BitOperations.Log2((uint)w8);
        int logH = System.Numerics.BitOperations.Log2((uint)h8);
        for (int y = 0; y < h8; y++)
        {
            for (int x = 0; x < w8; x++)
            {
                float decoded = llf[(y * w8) + x] / (JxlDct.Scale(y, 5 - logH) * JxlDct.Scale(x, 5 - logW));
                Assert.True(MathF.Abs(decoded - plane[(y * stride) + x]) < 1e-5f, $"{t} LLF {x},{y}: {decoded} for {plane[(y * stride) + x]}");
            }
        }
    }
}
