namespace Egami.Formats.Jxl;

/// <summary>
/// The 17 dequantization matrices of each channel (I.2.4): the defaults or their coded parameters (Hornuss, DCT2, DCT4, DCT4x8,
/// AFV, banded DCT weights or raw values), turned into weights. Translated from jxl-oxide's jxl-vardct dequant.rs.
/// </summary>
internal sealed class JxlDequantMatrices
{
    private static readonly float[] SeqA = [-1.025f, -0.78f, -0.65012f, -0.19041574f, -0.20819396f, -0.421064f, -0.32733846f];
    private static readonly float[] SeqB = [-0.30419582f, -0.36330363f, -0.3566038f, -0.34430745f, -0.33699593f, -0.30180866f, -0.27321684f];
    private static readonly float[] SeqC = [-1.2f, -1.2f, -0.8f, -0.7f, -0.7f, -0.4f, -0.5f];
    private static readonly float[][] Dct4x8Params = [[2198.0505f, -0.96269625f, -0.7619425f, -0.65511405f], [764.36554f, -0.926302f, -0.967523f, -0.2784529f], [527.10754f, -1.4594386f, -1.4500821f, -1.5843723f]];
    private static readonly float[][] Dct4Params = [[2200f, 0, 0, 0], [392f, 0, 0, 0], [112f, -0.25f, -0.25f, -0.5f]];
    private static readonly float[] AfvFreqs = [0, 0, 0.8517779f, 5.3777843f, 0, 0, 4.734748f, 5.4492455f, 1.659827f, 4.0f, 7.275749f, 10.423227f, 2.6629324f, 7.6306577f, 8.962389f, 12.971662f];

    private static readonly Lazy<JxlDequantMatrices> DefaultSet = new(() =>
    {
        JxlBitReader none = new([], 0, 0);
        JxlDequantMatrices set = new([.. Enumerable.Range(0, 17).Select(i => Weights(none, i, Default(i)))]);
        DefaultMatrices = set;
        return set;
    });

    private static JxlDequantMatrices? DefaultMatrices;

    // Index i holds matrix set entry i: per channel, weights row by row in the wide layout, and the same transposed.
    private readonly float[][][] _matrices;
    private readonly float[][][] _transposed;

    private JxlDequantMatrices(float[][][] matrices)
    {
        _matrices = matrices;
        _transposed = new float[matrices.Length][][];
        for (int i = 0; i < matrices.Length; i++)
        {
            if (DefaultMatrices is JxlDequantMatrices defaults && ReferenceEquals(matrices[i], defaults._matrices[i]))
            {
                _transposed[i] = defaults._transposed[i];
                continue;
            }

            (int w, int h) = JxlTransforms8.MatrixSize(i);
            _transposed[i] = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                float[] m = matrices[i][c];
                float[] t = new float[m.Length];
                for (int k = 0; k < t.Length; k++)
                {
                    int mx = k % h;
                    int my = k / h;
                    t[k] = m[(mx * w) + my];
                }

                _transposed[i][c] = t;
            }
        }
    }

    /// <summary>Gets the default matrices (every entry its default parameters), as the encoder uses them.</summary>
    internal static JxlDequantMatrices Defaults => DefaultSet.Value;

    /// <summary>Gets the JPEG quantization tables when the DCT8 matrix is raw with the JPEG denominator (for jbrd), else null.</summary>
    internal int[][]? JpegTables { get; private set; }

    /// <summary>Gets the weights of a channel's matrix for a transform in the coefficient layout, wider than tall, as tokens are ordered.</summary>
    internal float[] GetWide(int channel, JxlTransformType t) => _matrices[JxlTransforms8.MatrixIndex(t)][channel];

    /// <summary>Gets the weights of a channel's matrix for a transform, in its stored (possibly transposed) layout.</summary>
    internal float[] Get(int channel, JxlTransformType t) => JxlTransforms8.Transposed(t) ? _transposed[JxlTransforms8.MatrixIndex(t)][channel] : _matrices[JxlTransforms8.MatrixIndex(t)][channel];

    /// <summary>Reads the matrix set; raw matrices are Modular streams from <paramref name="stream"/> on.</summary>
    internal static JxlDequantMatrices Read(JxlBitReader r, JxlTree? globalTree, int bitDepth, int stream, SafetyLimits limits)
    {
        bool defaults = r.ReadBool();
        if (defaults)
        {
            // Most files use the default matrices; they are the same every time, so they are built once.
            return DefaultSet.Value;
        }

        float[][][] matrices = new float[17][][];
        int[][]? jpeg = null;
        for (int i = 0; i < 17; i++)
        {
            Encoding e = ReadEncoding(r, i, globalTree, bitDepth, stream + i, limits);
            if (e.IsDefault)
            {
                matrices[i] = DefaultSet.Value._matrices[i];
                continue;
            }

            if (i == 0 && e.Raw is { } raw && (int)Math.Round(1 / e.Denominator) == 2040)
            {
                jpeg = raw;
            }

            matrices[i] = Weights(r, i, e);
        }

        return new JxlDequantMatrices(matrices) { JpegTables = jpeg };
    }

    private static Encoding ReadEncoding(JxlBitReader r, int index, JxlTree? globalTree, int bitDepth, int stream, SafetyLimits limits)
    {
        uint mode = r.ReadBits(3);
        if (mode is >= 1 and <= 5 && index is not (0 or 1 or 2 or 3 or 9 or 10))
        {
            throw r.Corrupt($"Dequantization encoding {mode} does not suit matrix {index}.");
        }

        switch (mode)
        {
            case 0:
                return Default(index) with { IsDefault = true };
            case 1:
                return new Encoding(Kind.Hornuss) { Params = Fixed(r, 3) };
            case 2:
                return new Encoding(Kind.Dct2) { Params = Fixed(r, 6) };
            case 3:
                return new Encoding(Kind.Dct4) { Params = Fixed(r, 2), DctParams = DctParams(r) };
            case 4:
                return new Encoding(Kind.Dct4x8) { Params = Fixed(r, 1), DctParams = DctParams(r) };
            case 5:
                {
                    float[][] p = Fixed(r, 9);
                    foreach (float[] row in p)
                    {
                        for (int k = 0; k < 6; k++)
                        {
                            row[k] *= 64;
                        }
                    }

                    return new Encoding(Kind.Afv) { Params = p, DctParams = DctParams(r), Dct4x4Params = DctParams(r) };
                }

            case 6:
                return new Encoding(Kind.Dct) { DctParams = DctParams(r) };
            default:
                {
                    (int w, int h) = JxlTransforms8.MatrixSize(index);
                    float denominator = r.ReadF16();
                    List<JxlChannel> channels = [new(w, h, 0, 0), new(w, h, 0, 0), new(w, h, 0, 0)];
                    JxlModularImage image = JxlModularImage.Read(r, channels, globalTree, bitDepth, limits);
                    image.Decode(r, stream, 0, image.Channels.Count);
                    image.Inverse(r);
                    return new Encoding(Kind.Raw) { Denominator = denominator, Raw = [image.Channels[0].Data, image.Channels[1].Data, image.Channels[2].Data] };
                }
        }
    }

    private static float[][] Fixed(JxlBitReader r, int n)
    {
        float[][] p = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            p[c] = new float[n];
            for (int k = 0; k < n; k++)
            {
                p[c][k] = r.ReadF16();
            }
        }

        return p;
    }

    private static float[][] DctParams(JxlBitReader r)
    {
        int n = (int)r.ReadBits(4) + 1;
        float[][] p = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            p[c] = new float[n];
        }

        for (int c = 0; c < 3; c++)
        {
            for (int k = 0; k < n; k++)
            {
                p[c][k] = r.ReadF16();
            }
        }

        for (int c = 0; c < 3; c++)
        {
            p[c][0] *= 64;
        }

        return p;
    }

    private static Encoding Default(int index)
    {
        return index switch
        {
            0 => Banded([3150f, 0, -0.4f, -0.4f, -0.4f, -2f], [560f, 0, -0.3f, -0.3f, -0.3f, -0.3f], [512f, -2f, -1f, 0, -1f, -2f]),
            1 => new Encoding(Kind.Hornuss) { Params = [[280f, 3160f, 3160f], [60f, 864f, 864f], [18f, 200f, 200f]] },
            2 => new Encoding(Kind.Dct2) { Params = [[3840f, 2560f, 1280f, 640f, 480f, 300f], [960f, 640f, 320f, 180f, 140f, 120f], [640f, 320f, 128f, 64f, 32f, 16f]] },
            3 => new Encoding(Kind.Dct4) { Params = [[1, 1], [1, 1], [1, 1]], DctParams = Copy(Dct4Params) },
            4 => Banded([8996.873f, -1.3000778f, -0.4942453f, -0.43909377f, -0.6350102f, -0.9017726f, -1.6162099f], [3191.4836f, -0.67424583f, -0.80745816f, -0.4492584f, -0.3586544f, -0.3132239f, -0.37615025f], [1157.504f, -2.0531423f, -1.4f, -0.5068713f, -0.4270873f, -1.4856834f, -4.920914f]),
            5 => Banded([15718.408f, -1.025f, -0.98f, -0.9012f, -0.4f, -0.48819396f, -0.421064f, -0.27f], [7305.7637f, -0.8041958f, -0.76330364f, -0.5566038f, -0.49785304f, -0.43699592f, -0.40180868f, -0.27321684f], [3803.5317f, -3.0607336f, -2.041327f, -2.023565f, -0.54953897f, -0.4f, -0.4f, -0.3f]),
            6 => Banded([7240.7734f, -0.7f, -0.7f, -0.2f, -0.2f, -0.2f, -0.5f], [1448.1547f, -0.5f, -0.5f, -0.5f, -0.2f, -0.2f, -0.2f], [506.85413f, -1.4f, -0.2f, -0.5f, -0.5f, -1.5f, -3.6f]),
            7 => Banded([16283.249f, -1.7812846f, -1.6309059f, -1.0382179f, -0.85f, -0.7f, -0.9f, -1.2360638f], [5089.1577f, -0.3200494f, -0.3536285f, -0.3034f, -0.61f, -0.5f, -0.5f, -0.6f], [3397.7761f, -0.32132736f, -0.3450762f, -0.7034f, -0.9f, -1.0f, -1.0f, -1.1754606f]),
            8 => Banded([13844.971f, -0.971138f, -0.658f, -0.42026f, -0.22712f, -0.2206f, -0.226f, -0.6f], [4798.964f, -0.6112531f, -0.8377079f, -0.7901486f, -0.26927274f, -0.38272768f, -0.22924222f, -0.20719099f], [1807.2369f, -1.2f, -1.2f, -0.7f, -0.7f, -0.7f, -0.4f, -0.5f]),
            9 => new Encoding(Kind.Dct4x8) { Params = [[1], [1], [1]], DctParams = Copy(Dct4x8Params) },
            10 => new Encoding(Kind.Afv)
            {
                Params = [[3072f, 3072f, 256f, 256f, 256f, 414f, 0, 0, 0], [1024f, 1024f, 50f, 50f, 50f, 58f, 0, 0, 0], [384f, 384f, 12f, 12f, 12f, 22f, -0.25f, -0.25f, -0.25f]],
                DctParams = Copy(Dct4x8Params),
                Dct4x4Params = Copy(Dct4Params),
            },
            11 => Common(23966.166f, 8380.191f, 4493.024f),
            12 => Common(15358.898f, 5597.3604f, 2919.9617f),
            13 => Common(47932.332f, 16760.383f, 8986.048f),
            14 => Common(30717.797f, 11194.721f, 5839.9233f),
            15 => Common(95864.664f, 33520.766f, 17972.096f),
            _ => Common(61435.594f, 24209.441f, 12979.847f),
        };
    }

    private static Encoding Banded(float[] x, float[] y, float[] b) => new(Kind.Dct) { DctParams = [x, y, b] };

    private static Encoding Common(float a, float b, float c) => Banded([a, .. SeqA], [b, .. SeqB], [c, .. SeqC]);

    private static float[][] Copy(float[][] p) => [.. p.Select(row => row.ToArray())];

    private static float[][] Weights(JxlBitReader r, int index, Encoding e)
    {
        (int w, int h) = JxlTransforms8.MatrixSize(index);
        float[][] weights = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            float[] m;
            switch (e.Kind)
            {
                case Kind.Dct:
                    m = DctWeights(r, e.DctParams![c], w, h);
                    break;
                case Kind.Hornuss:
                    {
                        float[] p = e.Params![c];
                        m = new float[64];
                        Array.Fill(m, p[0]);
                        m[0] = 1;
                        m[1] = p[1];
                        m[8] = p[1];
                        m[9] = p[2];
                        break;
                    }

                case Kind.Dct2:
                    {
                        float[] p = e.Params![c];
                        m = new float[64];
                        m[0] = 1;
                        for (int k = 0; k < 6; k++)
                        {
                            int dim = 1 << (k / 2);
                            if (k % 2 == 0)
                            {
                                for (int y = 0; y < dim; y++)
                                {
                                    for (int x = dim; x < dim * 2; x++)
                                    {
                                        m[(y * 8) + x] = p[k];
                                        m[(x * 8) + y] = p[k];
                                    }
                                }
                            }
                            else
                            {
                                for (int y = dim; y < dim * 2; y++)
                                {
                                    for (int x = dim; x < dim * 2; x++)
                                    {
                                        m[(y * 8) + x] = p[k];
                                    }
                                }
                            }
                        }

                        break;
                    }

                case Kind.Dct4:
                    {
                        float[] p = e.Params![c];
                        float[] mat = DctWeights(r, e.DctParams![c], 4, 4);
                        m = new float[64];
                        for (int y = 0; y < 4; y++)
                        {
                            for (int x = 0; x < 4; x++)
                            {
                                float v = mat[(y * 4) + x];
                                m[(y * 16) + (x * 2)] = v;
                                m[(y * 16) + (x * 2) + 1] = v;
                                m[(((y * 2) + 1) * 8) + (x * 2)] = v;
                                m[(((y * 2) + 1) * 8) + (x * 2) + 1] = v;
                            }
                        }

                        m[1] /= p[0];
                        m[8] /= p[0];
                        m[9] /= p[1];
                        break;
                    }

                case Kind.Dct4x8:
                    {
                        float[] p = e.Params![c];
                        float[] mat = DctWeights(r, e.DctParams![c], 8, 4);
                        m = new float[64];
                        for (int y = 0; y < 4; y++)
                        {
                            for (int x = 0; x < 8; x++)
                            {
                                m[(2 * y * 8) + x] = mat[(y * 8) + x];
                                m[(((2 * y) + 1) * 8) + x] = mat[(y * 8) + x];
                            }
                        }

                        m[8] /= p[0];
                        break;
                    }

                case Kind.Afv:
                    m = AfvWeights(r, e.Params![c], e.DctParams![c], e.Dct4x4Params![c]);
                    break;
                default:
                    {
                        int[] raw = e.Raw![c];
                        m = new float[w * h];
                        for (int k = 0; k < m.Length; k++)
                        {
                            m[k] = raw[k] * e.Denominator;
                        }

                        break;
                    }
            }

            if (e.Kind != Kind.Raw)
            {
                for (int k = 0; k < m.Length; k++)
                {
                    m[k] = 1 / m[k];
                }
            }

            foreach (float v in m)
            {
                if (!(v > 0) || v >= 1e8f)
                {
                    throw r.Corrupt("A dequantization matrix has a non-positive or too large weight.");
                }
            }

            weights[c] = m;
        }

        return weights;
    }

    private static float[] AfvWeights(JxlBitReader r, float[] p, float[] dctParams, float[] dct4x4Params)
    {
        float[] w4x8 = DctWeights(r, dctParams, 8, 4);
        float[] w4x4 = DctWeights(r, dct4x4Params, 4, 4);
        float[] bands = [p[5], 0, 0, 0];
        for (int k = 1; k < 4; k++)
        {
            bands[k] = bands[k - 1] * Mult(p[5 + k]);
        }

        float freqLo = AfvFreqs[2];
        float freqHi = AfvFreqs[15];
        float[] m = new float[64];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                m[(16 * y) + (2 * x)] = (x, y) switch
                {
                    (0, 0) => 1,
                    (0, 1) => p[2],
                    (1, 0) => p[3],
                    (1, 1) => p[4],
                    _ => Interpolate(AfvFreqs[(y * 4) + x] - freqLo, freqHi - freqLo + 1e-6f, bands),
                };
            }
        }

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                m[(16 * y) + 8 + x] = y == 0 && x == 0 ? p[0] : w4x8[(y * 8) + x];
            }

            for (int x = 0; x < 4; x++)
            {
                m[(16 * y) + (2 * x) + 1] = y == 0 && x == 0 ? p[1] : w4x4[(y * 4) + x];
            }
        }

        return m;
    }

    private static float Mult(float x) => x > 0 ? 1 + x : 1 / (1 - x);

    private static float Interpolate(float pos, float max, float[] bands)
    {
        if (bands.Length == 1)
        {
            return bands[0];
        }

        float scaled = pos * (bands.Length - 1) / max;
        int index = (int)scaled;
        float frac = scaled - index;
        float a = bands[index];
        float b = bands[index + 1];
        return a * MathF.Pow(b / a, frac);
    }

    private static float[] DctWeights(JxlBitReader r, float[] p, int width, int height)
    {
        float[] bands = new float[p.Length];
        bands[0] = p[0];
        for (int k = 1; k < p.Length; k++)
        {
            bands[k] = bands[k - 1] * Mult(p[k]);
            if (!(bands[k] > 0))
            {
                throw r.Corrupt("A dequantization band is not positive.");
            }
        }

        float[] m = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dx = x / (float)(width - 1);
                float dy = y / (float)(height - 1);
                float distance = MathF.Sqrt((dx * dx) + (dy * dy));
                m[(y * width) + x] = Interpolate(distance, MathF.Sqrt(2) + 1e-6f, bands);
            }
        }

        return m;
    }

    private enum Kind
    {
        Dct,
        Hornuss,
        Dct2,
        Dct4,
        Dct4x8,
        Afv,
        Raw,
    }

    private sealed record Encoding(Kind Kind)
    {
        /// <summary>Gets whether the codestream asked for the default matrix, whose weights are built once.</summary>
        internal bool IsDefault { get; init; }

        internal float[][]? Params { get; init; }

        internal float[][]? DctParams { get; init; }

        internal float[][]? Dct4x4Params { get; init; }

        internal float Denominator { get; init; }

        internal int[][]? Raw { get; init; }
    }
}
