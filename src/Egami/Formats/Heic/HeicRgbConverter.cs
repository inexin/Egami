using System.Runtime.InteropServices;

using Egami.Formats.Heif;

namespace Egami.Formats.Heic;

/// <summary>
/// Converts decoded HEIC planes to RGB (or gray) with alpha the way libheif's heif-dec does, so Load equals its output sample for
/// sample (2.9.4's closeness gate). The rules were found by comparing outputs on the corpus, with libheif run as a black box:
/// chroma is taken from the nearest sample (no interpolation); 8-bit 4:2:0 at full range is converted in integers with 8
/// fraction bits, unless it is an overlay's layer; everything else in single precision, rounded half up, with limited range
/// scaled by 1.1689 (luma) and 1.1429 (chroma) rather than 255/219 and 255/224. The identity matrix and YCgCo are converted exactly. Samples come out at 8 bits for
/// 8-bit planes and at 16 bits (rescaled from the planes' depth) otherwise.
/// </summary>
internal sealed class HeicRgbConverter : IHeifRgbConverter
{
    private const float LimitedLuma = 1.1689f;
    private const float LimitedChroma = 1.1429f;

    private readonly HeifPlanes _color;
    private readonly HeifPlanes? _alpha;
    private readonly Mode _mode;
    private readonly int _max;
    private readonly int _half;
    private readonly int _lumaOffset;
    private readonly bool _limited;
    private readonly float _cr;
    private readonly float _cb;
    private readonly float _gu;
    private readonly float _gv;
    private readonly int _crInt;
    private readonly int _cbInt;
    private readonly int _guInt;
    private readonly int _gvInt;

    /// <summary>Prepares the conversion of <paramref name="color"/> (and <paramref name="alpha"/>'s luma as alpha).</summary>
    /// <param name="color">The color planes.</param>
    /// <param name="alpha">The alpha planes, the same size, or null.</param>
    /// <param name="codePoints">The color's H.273 code points and range.</param>
    /// <param name="layer">Whether the planes are a layer of an overlay, which heif-dec converts in single precision at any depth.</param>
    internal HeicRgbConverter(HeifPlanes color, HeifPlanes? alpha, Colors.ColorCodePoints codePoints, bool layer)
    {
        _color = color;
        _alpha = alpha;
        int depth = color.BitDepth;
        _max = (1 << depth) - 1;
        _half = 1 << (depth - 1);
        OutputDepth = depth > 8 ? 16 : 8;
        Channels = (color.Monochrome ? 1 : 3) + (alpha is null ? 0 : 1);
        _limited = !codePoints.FullRange;
        _lumaOffset = _limited ? 16 << (depth - 8) : 0;
        int matrix = codePoints.MatrixCoefficients;
        _mode = color.Monochrome ? Mode.Gray : matrix switch
        {
            0 => Mode.Identity,
            8 => Mode.YCgCo,
            _ => depth == 8 && color.Chroma == Pixels.ChromaFormat.Yuv420 && !_limited && !layer ? Mode.Integer : Mode.Float,
        };
        Supported = matrix is not (3 or 10 or 11 or 13 or 14 or 15 or 16) and < 17;
        (float kr, _, float kb) = HeifRgbConverter.Coefficients(matrix, codePoints.Primaries);

        // In single precision, in this order: other orders round the green coefficients differently, which shows at exact halves.
        float kg = 1f - kb - kr;
        _cr = 2f * (1f - kr);
        _cb = 2f * (1f - kb);
        _gu = kb * (2f * (1f - kb)) / kg;
        _gv = kr * (2f * (1f - kr)) / kg;
        double kgd = 1 - (double)kr - kb;
        _crInt = (int)Math.Round(2 * (1 - (double)kr) * 256);
        _cbInt = (int)Math.Round(2 * (1 - (double)kb) * 256);
        _guInt = (int)Math.Round(2 * (double)kb * (1 - (double)kb) / kgd * 256);
        _gvInt = (int)Math.Round(2 * (double)kr * (1 - (double)kr) / kgd * 256);
    }

    private enum Mode
    {
        Gray,
        Integer,
        Float,
        Identity,
        YCgCo,
    }

    /// <inheritdoc/>
    public int OutputDepth { get; }

    /// <inheritdoc/>
    public int Channels { get; }

    /// <summary>Gets whether the matrix is one heif-dec converts; others are converted with BT.601's.</summary>
    public bool Supported { get; }

    /// <inheritdoc/>
    public void Write(int y, int x, int width, Span<byte> target)
    {
        int[] samples = System.Buffers.ArrayPool<int>.Shared.Rent(width * Channels);
        try
        {
            Span<int> row = samples.AsSpan(0, width * Channels);
            Row(y, x, width, row);
            if (OutputDepth > 8)
            {
                Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(target);
                for (int i = 0; i < row.Length; i++)
                {
                    words[i] = (ushort)row[i];
                }
            }
            else
            {
                for (int i = 0; i < row.Length; i++)
                {
                    target[i] = (byte)row[i];
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(samples);
        }
    }

    /// <inheritdoc/>
    public void Row(int y, int x, int width, Span<int> row)
    {
        HeifPlanes c = _color;
        ushort[] yPlane = c.Samples[0];
        int yRow = y * c.Strides[0];
        bool mono = c.Monochrome;
        ushort[]? uPlane = mono ? null : c.Samples[1], vPlane = mono ? null : c.Samples[2];
        int uvRow = mono ? 0 : (y >> c.SubsamplingY) * c.Strides[1];
        int ssx = c.SubsamplingX;
        ushort[]? aPlane = _alpha?.Samples[0];
        int aRow = _alpha is null ? 0 : y * _alpha.Strides[0];
        int channels = Channels, max = _max, half = _half;
        for (int i = 0, at = 0; i < width; i++, at += channels)
        {
            int px = x + i;
            int luma = yPlane[yRow + px];
            int u = 0, v = 0;
            if (!mono)
            {
                int uv = uvRow + (px >> ssx);
                u = uPlane![uv] - half;
                v = vPlane![uv] - half;
            }

            int r, g, b;
            switch (_mode)
            {
                case Mode.Gray:
                    r = g = b = _limited ? Round((luma - _lumaOffset) * LimitedLuma) : luma;
                    break;
                case Mode.Identity:
                    (r, g, b) = (v + half, luma, u + half);
                    break;
                case Mode.YCgCo:
                    int t = luma - u;
                    (r, g, b) = (t + v, luma + u, t - v);
                    break;
                case Mode.Integer:
                    int y8 = luma << 8;
                    r = (y8 + (_crInt * v) + 128) >> 8;
                    g = (y8 - (_guInt * u) - (_gvInt * v) + 128) >> 8;
                    b = (y8 + (_cbInt * u) + 128) >> 8;
                    break;
                default:
                    // The chroma scale multiplies each term, after the coefficient (1 at full range).
                    float yf = _limited ? (luma - _lumaOffset) * LimitedLuma : luma;
                    float scale = _limited ? LimitedChroma : 1f;
                    float uf = u, vf = v;
                    r = Round(yf + (scale * (_cr * vf)));
                    g = Round(yf - (scale * (_gu * uf)) - (scale * (_gv * vf)));
                    b = Round(yf + (scale * (_cb * uf)));
                    break;
            }

            row[at] = Output(r, max);
            if (!mono)
            {
                row[at + 1] = Output(g, max);
                row[at + 2] = Output(b, max);
            }

            if (aPlane is not null)
            {
                int alphaMax = (1 << _alpha!.BitDepth) - 1;
                row[at + channels - 1] = Output(aPlane[aRow + px], alphaMax);
            }
        }
    }

    private static int Round(float value) => (int)MathF.Floor(value + 0.5f);

    /// <summary>Clamps a sample to its depth and rescales it to the output's: kept at 8 bits, else to 16.</summary>
    private int Output(int sample, int max)
    {
        sample = Math.Clamp(sample, 0, max);
        return OutputDepth == 8 && max == 255 ? sample
            : OutputDepth == 8 ? ((sample * 255) + (max / 2)) / max
            : (int)((((long)sample * 65535) + (max / 2)) / max);
    }
}
