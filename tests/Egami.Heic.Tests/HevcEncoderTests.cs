using Egami.Heic.Hevc;

namespace Egami.Heic.Tests;

/// <summary>
/// Encodes synthetic pictures with <see cref="HevcEncoder"/> and decodes them with Egami's HEVC decoder (held bit-exact to HM): the
/// decoded planes must equal the encoder's reconstruction sample for sample, and stay close to the source.
/// </summary>
public class HevcEncoderTests
{
    [Theory]
    [InlineData(64, 64, 1, 8, 30, 0)]
    [InlineData(64, 64, 1, 8, 30, 4)]
    [InlineData(200, 136, 1, 8, 27, 4)]
    [InlineData(130, 70, 1, 8, 37, 2)]
    [InlineData(96, 80, 1, 10, 30, 4)]
    [InlineData(120, 72, 1, 8, 22, 8)]
    public void DecoderMatchesReconstruction(int width, int height, int chroma, int bitDepth, int qp, int effort)
    {
        HevcEncoderSettings settings = new() { Width = width, Height = height, ChromaFormat = chroma, BitDepth = bitDepth, Qp = qp, Effort = effort };
        (ushort[][] planes, int[] strides) = Synthetic(settings, 1);
        HevcEncodedPicture encoded = HevcEncoder.Encode(settings, planes, strides, TestContext.Current.CancellationToken);
        HevcPicture decoded = Decode(encoded);
        AssertSame(encoded.Reconstruction, decoded);
        Assert.InRange(Psnr(settings, planes, strides, decoded), qp < 30 ? 32 : 26, 99);
    }

    internal static HevcPicture Decode(HevcEncodedPicture encoded) =>
        HevcDecoder.Decode([HevcNal.Parse(encoded.Vps), HevcNal.Parse(encoded.Sps), HevcNal.Parse(encoded.Pps), HevcNal.Parse(encoded.Slice)],
            SafetyLimits.Default, out _, 1, TestContext.Current.CancellationToken);

    internal static void AssertSame(HevcPicture expected, HevcPicture actual)
    {
        Assert.Equal(expected.Planes.Length, actual.Planes.Length);
        for (int c = 0; c < expected.Planes.Length; c++)
        {
            ushort[] a = expected.Planes[c], b = actual.Planes[c];
            int index = a.AsSpan().CommonPrefixLength(b);
            Assert.True(index == a.Length, $"Component {c} differs first at ({index % expected.Widths[c]}, {index / expected.Widths[c]}): {(index < a.Length ? a[index] : 0)} != {(index < b.Length ? b[index] : 0)}.");
        }
    }

    /// <summary>A picture with gradients, edges, texture and noise in every component.</summary>
    internal static (ushort[][] Planes, int[] Strides) Synthetic(HevcEncoderSettings s, int seed)
    {
        Random random = new(seed);
        int components = s.ChromaFormat == 0 ? 1 : 3;
        ushort[][] planes = new ushort[components][];
        int[] strides = new int[components];
        int max = (1 << s.BitDepth) - 1;
        for (int c = 0; c < components; c++)
        {
            int w = c == 0 ? s.Width : s.Width / s.SubWidthC, h = c == 0 ? s.Height : s.Height / s.SubHeightC;
            int scaleX = c == 0 ? 1 : s.SubWidthC, scaleY = c == 0 ? 1 : s.SubHeightC;
            planes[c] = new ushort[w * h];
            strides[c] = w;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int lx = x * scaleX, ly = y * scaleY;
                    double v = 0.3 + (0.4 * lx / s.Width) + (0.2 * Math.Sin(ly / 7.0 + c));
                    if ((lx / 24 + ly / 16) % 3 == 0)
                    {
                        v += 0.15;
                    }

                    if (lx > s.Width / 2 && ly > s.Height / 2)
                    {
                        v += 0.1 * Math.Sin(lx * 1.3) * Math.Cos(ly * 0.9);
                    }

                    v += (random.NextDouble() - 0.5) * 0.04;
                    planes[c][(y * w) + x] = (ushort)Math.Clamp((int)(v * max), 0, max);
                }
            }
        }

        return (planes, strides);
    }

    internal static double Psnr(HevcEncoderSettings s, ushort[][] planes, int[] strides, HevcPicture decoded)
    {
        double sse = 0;
        int width = s.Width, height = s.Height;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double d = planes[0][(y * strides[0]) + x] - decoded.Planes[0][(y * decoded.Widths[0]) + x];
                sse += d * d;
            }
        }

        double max = (1 << s.BitDepth) - 1;
        return 10 * Math.Log10(max * max * width * height / Math.Max(sse, 1e-9));
    }
}
