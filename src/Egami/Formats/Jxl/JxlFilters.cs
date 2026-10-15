namespace Egami.Formats.Jxl;

/// <summary>
/// The restoration filters (Annex J) and the non-separable upsampling (K.2), on float planes with mirrored edges: Gaborish,
/// the edge-preserving filter's up to three steps, and 2×, 4× and 8× upsampling. Ported from jxl-oxide's jxl-render.
/// </summary>
internal static class JxlFilters
{
    internal static readonly float[] Up2 =
    [
        -0.01716200f, -0.03452303f, -0.04022174f, -0.02921014f, -0.00624645f, 0.14111091f, 0.28896755f, 0.00278718f,
        -0.01610267f, 0.56661550f, 0.03777607f, -0.01986694f, -0.03144731f, -0.01185068f, -0.00213539f,
    ];

    internal static readonly float[] Up4 =
    [
        -0.02419067f, -0.03491987f, -0.03693351f, -0.03094285f, -0.00529785f, -0.01663432f, -0.03556863f, -0.03888905f,
        -0.03516850f, -0.00989469f, 0.23651958f, 0.33392945f, -0.01073543f, -0.01313181f, -0.03556694f, 0.13048175f,
        0.40103025f, 0.03951150f, -0.02077584f, 0.46914198f, -0.00209270f, -0.01484589f, -0.04064806f, 0.18942530f,
        0.56279892f, 0.06674400f, -0.02335494f, -0.03551682f, -0.00754830f, -0.02267919f, -0.02363578f, 0.00315804f,
        -0.03399098f, -0.01359519f, -0.00091653f, -0.00335467f, -0.01163294f, -0.01610294f, -0.00974088f, -0.00191622f,
        -0.01095446f, -0.03198464f, -0.04455121f, -0.02799790f, -0.00645912f, 0.06390599f, 0.22963888f, 0.00630981f,
        -0.01897349f, 0.67537268f, 0.08483369f, -0.02534994f, -0.02205197f, -0.01667999f, -0.00384443f,
    ];

    internal static readonly float[] Up8 =
    [
        -0.02928613f, -0.03706353f, -0.03783812f, -0.03324558f, -0.00447632f, -0.02519406f, -0.03752601f, -0.03901508f,
        -0.03663285f, -0.00646649f, -0.02066407f, -0.03838633f, -0.04002101f, -0.03900035f, -0.00901973f, -0.01626393f,
        -0.03954148f, -0.04046620f, -0.03979621f, -0.01224485f, 0.29895328f, 0.35757708f, -0.02447552f, -0.01081748f,
        -0.04314594f, 0.23903219f, 0.41119301f, -0.00573046f, -0.01450239f, -0.04246845f, 0.17567618f, 0.45220643f,
        0.02287757f, -0.01936783f, -0.03583255f, 0.11572472f, 0.47416733f, 0.06284440f, -0.02685066f, 0.42720050f,
        -0.02248939f, -0.01155273f, -0.04562755f, 0.28689496f, 0.49093869f, -0.00007891f, -0.01545926f, -0.04562659f,
        0.21238920f, 0.53980934f, 0.03369474f, -0.02070211f, -0.03866988f, 0.14229550f, 0.56593398f, 0.08045181f,
        -0.02888298f, -0.03680918f, -0.00542229f, -0.02920477f, -0.02788574f, -0.02118180f, -0.03942402f, -0.00775547f,
        -0.02433614f, -0.03193943f, -0.02030828f, -0.04044014f, -0.01074016f, -0.01930822f, -0.03620399f, -0.01974125f,
        -0.03919545f, -0.01456093f, -0.00045072f, -0.00360110f, -0.01020207f, -0.01231907f, -0.00638988f, -0.00071592f,
        -0.00279122f, -0.00957115f, -0.01288327f, -0.00730937f, -0.00107783f, -0.00210156f, -0.00890705f, -0.01317668f,
        -0.00813895f, -0.00153491f, -0.02128481f, -0.04173044f, -0.04831487f, -0.03293190f, -0.00525260f, -0.01720322f,
        -0.04052736f, -0.05045706f, -0.03607317f, -0.00738030f, -0.01341764f, -0.03965629f, -0.05151616f, -0.03814886f,
        -0.01005819f, 0.18968273f, 0.33063684f, -0.01300105f, -0.01372950f, -0.04017465f, 0.13727832f, 0.36402234f,
        0.01027890f, -0.01832107f, -0.03365072f, 0.08734506f, 0.38194295f, 0.04338228f, -0.02525993f, 0.56408126f,
        0.00458352f, -0.01648227f, -0.04887868f, 0.24585519f, 0.62026135f, 0.04314807f, -0.02213737f, -0.04158014f,
        0.16637289f, 0.65027023f, 0.09621636f, -0.03101388f, -0.04082742f, -0.00904519f, -0.02790922f, -0.02117818f,
        0.00798662f, -0.03995711f, -0.01243427f, -0.02231705f, -0.02946266f, 0.00992055f, -0.03600283f, -0.01684920f,
        -0.00111684f, -0.00411204f, -0.01297130f, -0.01723725f, -0.01022545f, -0.00165306f, -0.00313110f, -0.01218016f,
        -0.01763266f, -0.01125620f, -0.00231663f, -0.01374149f, -0.03797620f, -0.05142937f, -0.03117307f, -0.00581914f,
        -0.01064003f, -0.03608089f, -0.05272168f, -0.03375670f, -0.00795586f, 0.09628104f, 0.27129991f, -0.00353779f,
        -0.01734151f, -0.03153981f, 0.05686230f, 0.28500998f, 0.02230594f, -0.02374955f, 0.68214326f, 0.05018048f,
        -0.02320852f, -0.04383616f, 0.18459474f, 0.71517975f, 0.10805613f, -0.03263677f, -0.03637639f, -0.01394373f,
        -0.02511203f, -0.01728636f, 0.05407331f, -0.02867568f, -0.01893131f, -0.00240854f, -0.00446511f, -0.01636187f,
        -0.02377053f, -0.01522848f, -0.00333334f, -0.00819975f, -0.02964169f, -0.04499287f, -0.02745350f, -0.00612408f,
        0.02727416f, 0.19446600f, 0.00159832f, -0.02232473f, 0.74982506f, 0.11452620f, -0.03348048f, -0.01605681f,
        -0.02070339f, -0.00458223f,
    ];

    /// <summary>Mirrors an index into 0 to <paramref name="length"/> - 1 (-1 is 0, -2 is 1, length is length - 1).</summary>
    internal static int Mirror(int i, int length)
    {
        while (true)
        {
            if (i < 0)
            {
                i = -(i + 1);
            }
            else if (i >= length)
            {
                i = (2 * length) - i - 1;
            }
            else
            {
                return i;
            }
        }
    }

    /// <summary>Gaborish (J.2): a 3×3 convolution of each plane with its own side and diagonal weights, normalized.</summary>
    internal static void Gaborish(float[][] planes, int width, int height, int stride, float[] weights)
    {
        float[] output = new float[planes[0].Length];
        for (int c = 0; c < 3; c++)
        {
            float w0 = weights[2 * c];
            float w1 = weights[(2 * c) + 1];
            float norm = 1 / (1 + (4 * w0) + (4 * w1));
            float[] p = planes[c];
            for (int y = 0; y < height; y++)
            {
                int up = Mirror(y - 1, height) * stride;
                int row = y * stride;
                int down = Mirror(y + 1, height) * stride;
                for (int x = 0; x < width; x++)
                {
                    int l = Mirror(x - 1, width);
                    int r = Mirror(x + 1, width);
                    float side = p[up + x] + p[down + x] + p[row + l] + p[row + r];
                    float diag = p[up + l] + p[up + r] + p[down + l] + p[down + r];
                    output[row + x] = (p[row + x] + (side * w0) + (diag * w1)) * norm;
                }
            }

            Array.Copy(output, p, Math.Min(output.Length, p.Length));
        }
    }

    /// <summary>
    /// The edge-preserving filter (J.3): steps 0 (only with 3 iterations), 1 and 2 (with 2 or 3), each a weighted mean over a
    /// small kernel whose weights fall with the summed absolute differences of the surrounding patches.
    /// </summary>
    internal static void Epf(float[][] planes, int width, int height, int stride, JxlLoopFilter lf, Func<int, int, float> sigmaOfBlock)
    {
        float[][] scratch = [new float[planes[0].Length], new float[planes[0].Length], new float[planes[0].Length]];
        if (lf.EpfIterations == 3)
        {
            EpfStep(0, planes, scratch, width, height, stride, lf, sigmaOfBlock);
            Swap(planes, scratch);
        }

        EpfStep(1, planes, scratch, width, height, stride, lf, sigmaOfBlock);
        Swap(planes, scratch);
        if (lf.EpfIterations >= 2)
        {
            EpfStep(2, planes, scratch, width, height, stride, lf, sigmaOfBlock);
            Swap(planes, scratch);
        }
    }

    /// <summary>Upsamples a plane by 2, 4 or 8 with the 5×5 kernels of the image header (or their defaults), clamped to the window's range.</summary>
    internal static float[] Upsample(float[] plane, int width, int height, int stride, int factor, JxlImageHeader image)
    {
        float[] weights = factor switch
        {
            2 => image.Upsampling2Weights ?? Up2,
            4 => image.Upsampling4Weights ?? Up4,
            _ => image.Upsampling8Weights ?? Up8,
        };
        int matN = factor / 2;
        float[] kernels = new float[matN * matN * 25];
        int wi = 0;
        for (int y = 0; y < 5 * matN; y++)
        {
            int my = y / 5;
            int ky = y % 5;
            for (int x = y; x < 5 * matN; x++)
            {
                int mx = x / 5;
                int kx = x % 5;
                float w = weights[wi++];
                kernels[(((my * matN) + mx) * 25) + (ky * 5) + kx] = w;
                kernels[(((mx * matN) + my) * 25) + (kx * 5) + ky] = w;
            }
        }

        int outWidth = width * factor;
        int outHeight = height * factor;
        float[] output = new float[(long)outWidth * outHeight];
        for (int y = 0; y < outHeight; y++)
        {
            int refY = y / factor;
            int my = Math.Min(y % factor, factor - (y % factor) - 1);
            bool flipV = y % factor >= matN;
            for (int x = 0; x < outWidth; x++)
            {
                int refX = x / factor;
                int mx = Math.Min(x % factor, factor - (x % factor) - 1);
                bool flipH = x % factor >= matN;
                int kernel = ((my * matN) + mx) * 25;
                float sum = 0;
                float min = float.PositiveInfinity;
                float max = float.NegativeInfinity;
                for (int iy = 0; iy < 5; iy++)
                {
                    int ky = flipV ? 4 - iy : iy;
                    int sy = Mirror(refY + iy - 2, height) * stride;
                    for (int ix = 0; ix < 5; ix++)
                    {
                        int kx = flipH ? 4 - ix : ix;
                        float sample = plane[sy + Mirror(refX + ix - 2, width)];
                        sum += kernels[kernel + (ky * 5) + kx] * sample;
                        min = MathF.Min(min, sample);
                        max = MathF.Max(max, sample);
                    }
                }

                output[((long)y * outWidth) + x] = float.IsFinite(min) ? Math.Clamp(sum, min, max) : float.NaN;
            }
        }

        return output;
    }

    private static void Swap(float[][] a, float[][] b)
    {
        for (int c = 0; c < 3; c++)
        {
            (a[c], b[c]) = (b[c], a[c]);
        }
    }

    private static void EpfStep(int step, float[][] input, float[][] output, int width, int height, int stride, JxlLoopFilter lf, Func<int, int, float> sigmaOfBlock)
    {
        ReadOnlySpan<int> kernel = step == 0 ? [0, -2, -1, -1, 0, -1, 1, -1, -2, 0, -1, 0, 1, 0, 2, 0, -1, 1, 0, 1, 1, 1, 0, 2] : [0, -1, 0, 1, -1, 0, 1, 0];
        ReadOnlySpan<int> dist = step switch
        {
            0 => [0, -1, 1, 0, 0, 0, -1, 0, 0, 1],
            1 => [0, -1, 0, 0, 0, 1, -1, 0, 1, 0],
            _ => [0, 0],
        };
        float stepMultiplier = step == 0 ? lf.Pass0SigmaScale : step == 2 ? lf.Pass2SigmaScale : 1;
        float border = lf.BorderSadMul;
        ReadOnlySpan<float> scale = lf.ChannelScale;
        Span<float> sums = stackalloc float[3];
        for (int y = 0; y < height; y++)
        {
            bool yBorder = ((y + 1) & 6) == 0;
            for (int x = 0; x < width; x++)
            {
                int at = (y * stride) + x;
                float sigma = sigmaOfBlock(x / 8, y / 8);
                if (sigma < 0.3f)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        output[c][at] = input[c][at];
                    }

                    continue;
                }

                float sm = yBorder || (x & 7) is 0 or 7 ? stepMultiplier * border : stepMultiplier;
                float negInvSigma = 6.6f * (MathF.Sqrt(0.5f) - 1) / sigma * sm;
                float sumWeights = 1;
                for (int c = 0; c < 3; c++)
                {
                    sums[c] = input[c][at];
                }

                for (int k = 0; k < kernel.Length; k += 2)
                {
                    int kx = x + kernel[k];
                    int ky = y + kernel[k + 1];
                    float d = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        float[] p = input[c];
                        float acc = 0;
                        for (int i = 0; i < dist.Length; i += 2)
                        {
                            float a = p[(Mirror(ky + dist[i + 1], height) * stride) + Mirror(kx + dist[i], width)];
                            float b = p[(Mirror(y + dist[i + 1], height) * stride) + Mirror(x + dist[i], width)];
                            acc += MathF.Abs(a - b);
                        }

                        d += scale[c] * acc;
                    }

                    float weight = MathF.Max(0, 1 + (d * negInvSigma));
                    sumWeights += weight;
                    int kat = (Mirror(ky, height) * stride) + Mirror(kx, width);
                    for (int c = 0; c < 3; c++)
                    {
                        sums[c] += weight * input[c][kat];
                    }
                }

                for (int c = 0; c < 3; c++)
                {
                    output[c][at] = sums[c] / sumWeights;
                }
            }
        }
    }
}
