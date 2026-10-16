namespace Egami.Formats.Jxl;

/// <summary>
/// The DCTs of ISO/IEC 18181-1 I.4 at every power-of-two size up to 256, in JPEG XL's scaling (the DC is the mean), and the
/// special 8×8 transforms: DCT2, DCT4, Hornuss, DCT4x8 and AFV. Ported from jxl-oxide's jxl-render (see THIRD-PARTY-NOTICES).
/// </summary>
internal static partial class JxlDct
{
    private static readonly float Sqrt2 = MathF.Sqrt(2);
    private static readonly float[][] SecHalfTables = BuildSecHalf();

    private static readonly float[] ScaleF =
    [
        1.0000000000000000f, 0.9996047255830407f, 0.9984194528776054f, 0.9964458326264695f, 0.9936866130906366f, 0.9901456355893141f,
        0.9858278282666936f, 0.9807391980963174f, 0.9748868211368796f, 0.9682788310563117f, 0.9609244059440204f, 0.9528337534340876f,
        0.9440180941651672f, 0.9344896436056892f, 0.9242615922757944f, 0.9133480844001980f, 0.9017641950288744f, 0.8895259056651056f,
        0.8766500784429904f, 0.8631544288990163f, 0.8490574973847023f, 0.8343786191696513f, 0.8191378932865928f, 0.8033561501721485f,
        0.7870549181591013f, 0.7702563888779096f, 0.7529833816270532f, 0.7352593067735488f, 0.7171081282466044f, 0.6985543251889097f,
        0.6796228528314652f, 0.6603391026591464f,
    ];

    private static readonly float[] AfvBasis =
    [
        0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f,
        0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f,
        0.876902929799142f, 0.2206518106944235f, -0.10140050393753763f, -0.1014005039375375f, 0.2206518106944236f, -0.10140050393753777f, -0.10140050393753772f, -0.10140050393753763f,
        -0.10140050393753758f, -0.10140050393753769f, -0.1014005039375375f, -0.10140050393753768f, -0.10140050393753768f, -0.10140050393753759f, -0.10140050393753763f, -0.10140050393753741f,
        0.0f, 0.0f, 0.40670075830260755f, 0.44444816619734445f, 0.0f, 0.0f, 0.19574399372042936f, 0.2929100136981264f,
        -0.40670075830260716f, -0.19574399372042872f, 0.0f, 0.11379074460448091f, -0.44444816619734384f, -0.29291001369812636f, -0.1137907446044814f, 0.0f,
        0.0f, 0.0f, -0.21255748058288748f, 0.3085497062849767f, 0.0f, 0.4706702258572536f, -0.1621205195722993f, 0.0f,
        -0.21255748058287047f, -0.16212051957228327f, -0.47067022585725277f, -0.1464291867126764f, 0.3085497062849487f, 0.0f, -0.14642918671266536f, 0.4251149611657548f,
        0.0f, -0.7071067811865474f, 0.0f, 0.0f, 0.7071067811865476f, 0.0f, 0.0f, 0.0f,
        0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f,
        -0.4105377591765233f, 0.6235485373547691f, -0.06435071657946274f, -0.06435071657946266f, 0.6235485373547694f, -0.06435071657946284f, -0.0643507165794628f, -0.06435071657946274f,
        -0.06435071657946272f, -0.06435071657946279f, -0.06435071657946266f, -0.06435071657946277f, -0.06435071657946277f, -0.06435071657946273f, -0.06435071657946274f, -0.0643507165794626f,
        0.0f, 0.0f, -0.4517556589999482f, 0.15854503551840063f, 0.0f, -0.04038515160822202f, 0.0074182263792423875f, 0.39351034269210167f,
        -0.45175565899994635f, 0.007418226379244351f, 0.1107416575309343f, 0.08298163094882051f, 0.15854503551839705f, 0.3935103426921022f, 0.0829816309488214f, -0.45175565899994796f,
        0.0f, 0.0f, -0.304684750724869f, 0.5112616136591823f, 0.0f, 0.0f, -0.290480129728998f, -0.06578701549142804f,
        0.304684750724884f, 0.2904801297290076f, 0.0f, -0.23889773523344604f, -0.5112616136592012f, 0.06578701549142545f, 0.23889773523345467f, 0.0f,
        0.0f, 0.0f, 0.3017929516615495f, 0.25792362796341184f, 0.0f, 0.16272340142866204f, 0.09520022653475037f, 0.0f,
        0.3017929516615503f, 0.09520022653475055f, -0.16272340142866173f, -0.35312385449816297f, 0.25792362796341295f, 0.0f, -0.3531238544981624f, -0.6035859033230976f,
        0.0f, 0.0f, 0.40824829046386274f, 0.0f, 0.0f, 0.0f, 0.0f, -0.4082482904638628f,
        -0.4082482904638635f, 0.0f, 0.0f, -0.40824829046386296f, 0.0f, 0.4082482904638634f, 0.408248290463863f, 0.0f,
        0.0f, 0.0f, 0.1747866975480809f, 0.0812611176717539f, 0.0f, 0.0f, -0.3675398009862027f, -0.307882213957909f,
        -0.17478669754808135f, 0.3675398009862011f, 0.0f, 0.4826689115059883f, -0.08126111767175039f, 0.30788221395790305f, -0.48266891150598584f, 0.0f,
        0.0f, 0.0f, -0.21105601049335784f, 0.18567180916109802f, 0.0f, 0.0f, 0.49215859013738733f, -0.38525013709251915f,
        0.21105601049335806f, -0.49215859013738905f, 0.0f, 0.17419412659916217f, -0.18567180916109904f, 0.3852501370925211f, -0.1741941265991621f, 0.0f,
        0.0f, 0.0f, -0.14266084808807264f, -0.3416446842253372f, 0.0f, 0.7367497537172237f, 0.24627107722075148f, -0.08574019035519306f,
        -0.14266084808807344f, 0.24627107722075137f, 0.14883399227113567f, -0.04768680350229251f, -0.3416446842253373f, -0.08574019035519267f, -0.047686803502292804f, -0.14266084808807242f,
        0.0f, 0.0f, -0.13813540350758585f, 0.3302282550303788f, 0.0f, 0.08755115000587084f, -0.07946706605909573f, -0.4613374887461511f,
        -0.13813540350758294f, -0.07946706605910261f, 0.49724647109535086f, 0.12538059448563663f, 0.3302282550303805f, -0.4613374887461554f, 0.12538059448564315f, -0.13813540350758452f,
        0.0f, 0.0f, -0.17437602599651067f, 0.0702790691196284f, 0.0f, -0.2921026642334881f, 0.3623817333531167f, 0.0f,
        -0.1743760259965108f, 0.36238173335311646f, 0.29210266423348785f, -0.4326608024727445f, 0.07027906911962818f, 0.0f, -0.4326608024727457f, 0.34875205199302267f,
        0.0f, 0.0f, 0.11354987314994337f, -0.07417504595810355f, 0.0f, 0.19402893032594343f, -0.435190496523228f, 0.21918684838857466f,
        0.11354987314994257f, -0.4351904965232251f, 0.5550443808910661f, -0.25468277124066463f, -0.07417504595810233f, 0.2191868483885728f, -0.25468277124066413f, 0.1135498731499429f,
    ];

    /// <summary>Gets the LLF scale for coefficient <paramref name="c"/> of a varblock side of 2^(5 - <paramref name="logb"/>) blocks.</summary>
    internal static float Scale(int c, int logb) => ScaleF[c << logb];

    /// <summary>A 2D DCT of a <paramref name="width"/>×<paramref name="height"/> area at <paramref name="offset"/>, rows then columns.</summary>
    internal static void Dct2D(Span<float> data, int offset, int stride, int width, int height, bool forward)
    {
        if (width * height <= 1 || Dct2DVector(data, offset, stride, width, height, forward))
        {
            return;
        }

        Span<float> scratch = stackalloc float[256];
        Span<float> column = stackalloc float[256];
        if (width > 1)
        {
            for (int y = 0; y < height; y++)
            {
                Dct(data.Slice(offset + (y * stride), width), scratch[..width], forward);
            }
        }

        if (height > 1)
        {
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    column[y] = data[offset + (y * stride) + x];
                }

                Dct(column[..height], scratch[..height], forward);
                for (int y = 0; y < height; y++)
                {
                    data[offset + (y * stride) + x] = column[y];
                }
            }
        }
    }

    /// <summary>Runs the transform of a varblock in place on its pixel area (I.4).</summary>
    internal static void Inverse(JxlTransformType t, Span<float> data, int offset, int stride)
    {
        switch (t)
        {
            case JxlTransformType.Dct2:
                Idct2(data, offset, stride, 2);
                Idct2(data, offset, stride, 4);
                Idct2(data, offset, stride, 8);
                break;
            case JxlTransformType.Dct4:
                Dct4(data, offset, stride);
                break;
            case JxlTransformType.Hornuss:
                Hornuss(data, offset, stride);
                break;
            case JxlTransformType.Dct4x8:
                Dct4x8(data, offset, stride, false);
                break;
            case JxlTransformType.Dct8x4:
                Dct4x8(data, offset, stride, true);
                break;
            case JxlTransformType.Afv0:
            case JxlTransformType.Afv1:
            case JxlTransformType.Afv2:
            case JxlTransformType.Afv3:
                Afv(data, offset, stride, t - JxlTransformType.Afv0);
                break;
            case JxlTransformType.Dct8 when Idct8x8Vectorized:
                Idct8x8(data, offset, stride);
                break;
            default:
                (int w, int h) = JxlTransforms8.Size(t);
                Dct2D(data, offset, stride, w * 8, h * 8, forward: false);
                break;
        }
    }

    /// <summary>A 1D DCT in JPEG XL's scaling (forward: DC is the mean; inverse: its exact inverse).</summary>
    private static void Dct(Span<float> io, Span<float> scratch, bool forward)
    {
        int n = io.Length;
        switch (n)
        {
            case <= 1:
                return;
            case 2:
                {
                    float t0 = io[0] + io[1];
                    float t1 = io[0] - io[1];
                    io[0] = forward ? t0 / 2 : t0;
                    io[1] = forward ? t1 / 2 : t1;
                    return;
                }

            case 4:
                Dct4Small(io, forward);
                return;
            case 8:
                Dct8(io, forward);
                return;
        }

        int half = n / 2;
        ReadOnlySpan<float> sec = SecHalf(n);
        Span<float> in0 = scratch[..half];
        Span<float> in1 = scratch[half..];
        Span<float> out0 = io[..half];
        Span<float> out1 = io[half..];
        if (forward)
        {
            for (int i = 0; i < half; i++)
            {
                in0[i] = (io[i] + io[n - i - 1]) / 2;
                in1[i] = (io[i] - io[n - i - 1]) / 2;
            }

            for (int i = 0; i < half; i++)
            {
                in1[i] *= sec[i];
            }

            Dct(in0, out0, forward: true);
            Dct(in1, out1, forward: true);
            in1[0] *= Sqrt2;
            for (int i = 0; i < half - 1; i++)
            {
                in1[i] += in1[i + 1];
            }

            for (int i = 0; i < half; i++)
            {
                io[2 * i] = in0[i];
                io[(2 * i) + 1] = in1[i];
            }
        }
        else
        {
            for (int i = 0; i < half; i++)
            {
                in0[i] = io[2 * i];
                in1[i] = io[(2 * i) + 1];
            }

            for (int i = 1; i < half; i++)
            {
                in1[half - i] += in1[half - i - 1];
            }

            in1[0] *= Sqrt2;
            Dct(in0, out0, forward: false);
            Dct(in1, out1, forward: false);
            for (int i = 0; i < half; i++)
            {
                in1[i] *= sec[i];
            }

            for (int i = 0; i < half; i++)
            {
                out0[i] = scratch[i] + scratch[i + half];
                out1[half - i - 1] = scratch[i] - scratch[i + half];
            }
        }
    }

    private static void Dct4Small(Span<float> io, bool forward)
    {
        const float Sec0 = 0.5411961f;
        const float Sec1 = 1.306563f;
        float i0 = io[0];
        float i1 = io[1];
        float i2 = io[2];
        float i3 = io[3];
        if (forward)
        {
            float sum03 = i0 + i3;
            float sum12 = i1 + i2;
            float tmp0 = (i0 - i3) * Sec0;
            float tmp1 = (i1 - i2) * Sec1;
            float out0 = (tmp0 + tmp1) / 4;
            float out1 = (tmp0 - tmp1) / 4;
            io[0] = (sum03 + sum12) / 4;
            io[1] = (out0 * Sqrt2) + out1;
            io[2] = (sum03 - sum12) / 4;
            io[3] = out1;
        }
        else
        {
            float tmp0 = i1 * Sqrt2;
            float tmp1 = i1 + i3;
            float out0 = (tmp0 + tmp1) * Sec0;
            float out1 = (tmp0 - tmp1) * Sec1;
            float sum02 = i0 + i2;
            float sub02 = i0 - i2;
            io[0] = sum02 + out0;
            io[1] = sub02 + out1;
            io[2] = sub02 - out1;
            io[3] = sum02 - out0;
        }
    }

    private static void Dct8(Span<float> io, bool forward)
    {
        ReadOnlySpan<float> sec = SecHalf(8);
        Span<float> a = stackalloc float[4];
        Span<float> b = stackalloc float[4];
        if (forward)
        {
            for (int i = 0; i < 4; i++)
            {
                a[i] = (io[i] + io[7 - i]) / 2;
                b[i] = (io[i] - io[7 - i]) * sec[i] / 2;
            }

            Dct4Small(a, forward: true);
            Dct4Small(b, forward: true);
            for (int i = 0; i < 4; i++)
            {
                io[i * 2] = a[i];
            }

            b[0] *= Sqrt2;
            for (int i = 0; i < 3; i++)
            {
                io[(i * 2) + 1] = b[i] + b[i + 1];
            }

            io[7] = b[3];
        }
        else
        {
            a[0] = io[0];
            a[1] = io[2];
            a[2] = io[4];
            a[3] = io[6];
            b[0] = io[1] * Sqrt2;
            b[1] = io[3] + io[1];
            b[2] = io[5] + io[3];
            b[3] = io[7] + io[5];
            Dct4Small(a, forward: false);
            Dct4Small(b, forward: false);
            for (int i = 0; i < 4; i++)
            {
                float r = b[i] * sec[i];
                io[i] = a[i] + r;
                io[7 - i] = a[i] - r;
            }
        }
    }

    private static ReadOnlySpan<float> SecHalf(int n) => SecHalfTables[System.Numerics.BitOperations.Log2((uint)n)];

    private static float[][] BuildSecHalf()
    {
        float[][] tables = new float[9][];
        for (int log = 2; log <= 8; log++)
        {
            int n = 1 << log;
            tables[log] = new float[n / 2];
            for (int k = 0; k < n / 2; k++)
            {
                tables[log][k] = (float)(1 / Math.Cos((2 * k + 1) / (2.0 * n) * Math.PI) / 2);
            }
        }

        return tables;
    }

    private static void Idct2(Span<float> d, int o, int s, int size)
    {
        int half = size / 2;
        Span<float> scratch = stackalloc float[64];
        for (int y = 0; y < half; y++)
        {
            for (int x = 0; x < half; x++)
            {
                float c00 = d[o + (y * s) + x];
                float c01 = d[o + (y * s) + x + half];
                float c10 = d[o + ((y + half) * s) + x];
                float c11 = d[o + ((y + half) * s) + x + half];
                scratch[(2 * y * size) + (2 * x)] = c00 + c01 + c10 + c11;
                scratch[(2 * y * size) + (2 * x) + 1] = c00 + c01 - c10 - c11;
                scratch[(((2 * y) + 1) * size) + (2 * x)] = c00 - c01 + c10 - c11;
                scratch[(((2 * y) + 1) * size) + (2 * x) + 1] = c00 - c01 - c10 + c11;
            }
        }

        for (int y = 0; y < size; y++)
        {
            scratch.Slice(y * size, size).CopyTo(d.Slice(o + (y * s), size));
        }
    }

    private static void Dct4(Span<float> d, int o, int s)
    {
        Idct2(d, o, s, 2);
        Span<float> scratch = stackalloc float[64];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                Span<float> block = scratch.Slice(((y * 2) + x) * 16, 16);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        // Filled transposed, as libjxl does.
                        block[(ix * 4) + iy] = d[o + ((y + (iy * 2)) * s) + x + (ix * 2)];
                    }
                }

                Dct2D(block, 0, 4, 4, 4, forward: false);
            }
        }

        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                ReadOnlySpan<float> block = scratch.Slice(((y * 2) + x) * 16, 16);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        d[o + (((y * 4) + iy) * s) + (x * 4) + ix] = block[(iy * 4) + ix];
                    }
                }
            }
        }
    }

    private static void Hornuss(Span<float> d, int o, int s)
    {
        Idct2(d, o, s, 2);
        Span<float> scratch = stackalloc float[64];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                Span<float> block = scratch.Slice(((y * 2) + x) * 16, 16);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        block[(iy * 4) + ix] = d[o + ((y + (iy * 2)) * s) + x + (ix * 2)];
                    }
                }

                float residual = 0;
                for (int k = 1; k < 16; k++)
                {
                    residual += block[k];
                }

                float avg = block[0] - (residual / 16);
                block[0] = block[5];
                block[5] = 0;
                for (int k = 0; k < 16; k++)
                {
                    block[k] += avg;
                }
            }
        }

        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                ReadOnlySpan<float> block = scratch.Slice(((y * 2) + x) * 16, 16);
                for (int iy = 0; iy < 4; iy++)
                {
                    for (int ix = 0; ix < 4; ix++)
                    {
                        d[o + (((y * 4) + iy) * s) + (x * 4) + ix] = block[(iy * 4) + ix];
                    }
                }
            }
        }
    }

    private static void Dct4x8(Span<float> d, int o, int s, bool transposed)
    {
        float c0 = d[o];
        float c1 = d[o + s];
        d[o] = c0 + c1;
        d[o + s] = c0 - c1;
        Span<float> scratch = stackalloc float[64];
        for (int idx = 0; idx < 2; idx++)
        {
            Span<float> half = scratch.Slice(idx * 32, 32);
            for (int iy = 0; iy < 4; iy++)
            {
                for (int ix = 0; ix < 8; ix++)
                {
                    half[(iy * 8) + ix] = d[o + (((iy * 2) + idx) * s) + ix];
                }
            }

            Dct2D(half, 0, 8, 8, 4, forward: false);
        }

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                if (transposed)
                {
                    d[o + (x * s) + y] = scratch[(y * 8) + x];
                }
                else
                {
                    d[o + (y * s) + x] = scratch[(y * 8) + x];
                }
            }
        }
    }

    private static void Afv(Span<float> d, int o, int s, int n)
    {
        int flipX = n % 2;
        int flipY = n / 2;
        Span<float> coeffs = stackalloc float[16];
        coeffs[0] = (d[o] + d[o + 1] + d[o + s]) * 4;
        for (int k = 1; k < 16; k++)
        {
            coeffs[k] = d[o + (2 * (k / 4) * s) + (2 * (k % 4))];
        }

        Span<float> samples = stackalloc float[16];
        for (int k = 0; k < 16; k++)
        {
            for (int i = 0; i < 16; i++)
            {
                samples[i] = MathF.FusedMultiplyAdd(coeffs[k], AfvBasis[(k * 16) + i], samples[i]);
            }
        }

        Span<float> s4x4 = stackalloc float[16];
        Span<float> s4x8 = stackalloc float[32];
        s4x4[0] = d[o] - d[o + 1] + d[o + s];
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                if ((ix | iy) != 0)
                {
                    s4x4[(ix * 4) + iy] = d[o + (2 * iy * s) + (2 * ix) + 1];
                }
            }
        }

        Dct2D(s4x4, 0, 4, 4, 4, forward: false);
        s4x8[0] = d[o] - d[o + s];
        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 8; ix++)
            {
                if ((ix | iy) != 0)
                {
                    s4x8[(iy * 8) + ix] = d[o + (((2 * iy) + 1) * s) + ix];
                }
            }
        }

        Dct2D(s4x8, 0, 8, 8, 4, forward: false);
        for (int iy = 0; iy < 4; iy++)
        {
            int ay = flipY == 0 ? iy : 3 - iy;
            for (int ix = 0; ix < 4; ix++)
            {
                int ax = flipX == 0 ? ix : 3 - ix;
                d[o + (((flipY * 4) + iy) * s) + (flipX * 4) + ix] = samples[(ay * 4) + ax];
            }
        }

        for (int iy = 0; iy < 4; iy++)
        {
            for (int ix = 0; ix < 4; ix++)
            {
                d[o + (((flipY * 4) + iy) * s) + ((1 - flipX) * 4) + ix] = s4x4[(iy * 4) + ix];
            }
        }

        for (int iy = 0; iy < 4; iy++)
        {
            s4x8.Slice(iy * 8, 8).CopyTo(d.Slice(o + ((((1 - flipY) * 4) + iy) * s), 8));
        }
    }
}
