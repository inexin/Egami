using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Shading;

/// <summary>
/// SVG 1.1's feTurbulence, written from the algorithm in the specification (section 15.21): a Park–Miller generator seeds a
/// 256-entry lattice permutation and four tables of unit gradients (one per channel); each octave is gradient noise on the
/// lattice with an s-curve blend, and stitching adjusts the frequencies and wraps the lattice so the noise repeats over the tile.
/// </summary>
internal sealed class TurbulenceShader : Shader
{
    private const int BSize = 0x100;
    private const int BMask = 0xff;
    private const int PerlinN = 0x1000;
    private const int RandM = 2147483647;
    private const int RandA = 16807;
    private const int RandQ = 127773;
    private const int RandR = 2836;

    private readonly int[] _lattice = new int[BSize + BSize + 2];
    private readonly double[] _gradients = new double[4 * (BSize + BSize + 2) * 2];
    private readonly Matrix3x2 _inverse;
    private readonly bool _fractal;
    private readonly int _octaves;
    private readonly double _frequencyX;
    private readonly double _frequencyY;
    private readonly bool _stitch;
    private readonly int _stitchWidth;
    private readonly int _stitchHeight;
    private readonly int _wrapX;
    private readonly int _wrapY;

    internal TurbulenceShader(NoisePaint paint, Matrix3x2 inverse)
    {
        _inverse = inverse;
        _fractal = paint.Type == NoiseType.FractalNoise;
        _octaves = paint.Octaves;
        Init(paint.Seed);

        double fx = paint.BaseFrequencyX, fy = paint.BaseFrequencyY;
        if (paint.TileSize is Size tile)
        {
            // The frequencies move to the nearest that fit a whole number of cycles in the tile.
            double width = tile.Width, height = tile.Height;
            fx = Fit(fx, width);
            fy = Fit(fy, height);
            _stitch = true;
            _stitchWidth = (int)((width * fx) + 0.5);
            _wrapX = (int)(PerlinN + _stitchWidth);
            _stitchHeight = (int)((height * fy) + 0.5);
            _wrapY = (int)(PerlinN + _stitchHeight);
        }

        _frequencyX = fx;
        _frequencyY = fy;
    }

    public override void Shade(int y, int x, Span<PremulRgbaF32> row)
    {
        // Sampled one pixel past each pixel's top-left corner, where SkiaSharp's noise shaders sample: unstitched noise then matches
        // it within one level (the egami-compare gate); at the pixel center it is several levels off.
        Vector2 p = Vector2.Transform(new Vector2(x + 1, y + 1), _inverse), step = new(_inverse.M11, _inverse.M12);
        for (int i = 0; i < row.Length; i++)
        {
            Vector2 q = p + (step * i);
            float r = Channel(0, q), g = Channel(1, q), b = Channel(2, q), a = Channel(3, q);
            row[i] = new PremulRgbaF32(r * a, g * a, b * a, a);
        }
    }

    private static double Fit(double frequency, double size)
    {
        if (frequency == 0)
        {
            return 0;
        }

        double lo = Math.Floor(size * frequency) / size, hi = Math.Ceiling(size * frequency) / size;
        return lo > 0 && frequency / lo < hi / frequency ? lo : hi;
    }

    private static int SetupSeed(int seed)
    {
        long s = seed;
        if (s <= 0)
        {
            s = -(s % (RandM - 1)) + 1;
        }

        if (s > RandM - 1)
        {
            s = RandM - 1;
        }

        return (int)s;
    }

    private static int Random(int seed)
    {
        int result = (RandA * (seed % RandQ)) - (RandR * (seed / RandQ));
        return result <= 0 ? result + RandM : result;
    }

    private static double SCurve(double t) => t * t * (3 - (2 * t));

    private static double Lerp(double t, double a, double b) => a + (t * (b - a));

    private static int G(int channel, int index, int component) => (((channel * (BSize + BSize + 2)) + index) * 2) + component;

    private void Init(int seed)
    {
        seed = SetupSeed(seed);
        int i;
        for (int k = 0; k < 4; k++)
        {
            for (i = 0; i < BSize; i++)
            {
                _lattice[i] = i;
                for (int j = 0; j < 2; j++)
                {
                    seed = Random(seed);
                    _gradients[G(k, i, j)] = (double)((seed % (BSize + BSize)) - BSize) / BSize;
                }

                double gx = _gradients[G(k, i, 0)], gy = _gradients[G(k, i, 1)];
                double s = Math.Sqrt((gx * gx) + (gy * gy));
                _gradients[G(k, i, 0)] = gx / s;
                _gradients[G(k, i, 1)] = gy / s;
            }
        }

        for (i = BSize - 1; i > 0; i--)
        {
            int k = _lattice[i];
            seed = Random(seed);
            int j = seed % BSize;
            _lattice[i] = _lattice[j];
            _lattice[j] = k;
        }

        for (i = 0; i < BSize + 2; i++)
        {
            _lattice[BSize + i] = _lattice[i];
            for (int k = 0; k < 4; k++)
            {
                for (int j = 0; j < 2; j++)
                {
                    _gradients[G(k, BSize + i, j)] = _gradients[G(k, i, j)];
                }
            }
        }
    }

    private float Channel(int channel, Vector2 point)
    {
        double sum = 0, vx = point.X * _frequencyX, vy = point.Y * _frequencyY, ratio = 1;
        int width = _stitchWidth, height = _stitchHeight, wrapX = _wrapX, wrapY = _wrapY;
        for (int octave = 0; octave < _octaves; octave++)
        {
            double n = Noise2(channel, vx, vy, width, height, wrapX, wrapY);
            sum += (_fractal ? n : Math.Abs(n)) / ratio;
            vx *= 2;
            vy *= 2;
            ratio *= 2;
            if (_stitch)
            {
                width *= 2;
                wrapX = (2 * wrapX) - PerlinN;
                height *= 2;
                wrapY = (2 * wrapY) - PerlinN;
            }
        }

        double value = _fractal ? ((sum * 255) + 255) / 2 : sum * 255;
        return (float)(Math.Clamp(value, 0, 255) / 255);
    }

    private double Noise2(int channel, double vx, double vy, int width, int height, int wrapX, int wrapY)
    {
        double t = vx + PerlinN;
        int bx0 = (int)t;
        double rx0 = t - (int)t, rx1 = rx0 - 1;
        t = vy + PerlinN;
        int by0 = (int)t;
        double ry0 = t - (int)t, ry1 = ry0 - 1;

        // Stitching wraps the lattice before it is masked, as Skia and browsers do: the specification's code masks first, so its
        // wrap test can never pass.
        if (_stitch)
        {
            if (bx0 >= wrapX)
            {
                bx0 -= width;
            }

            if (by0 >= wrapY)
            {
                by0 -= height;
            }
        }

        int bx1 = (bx0 + 1) & BMask, by1 = (by0 + 1) & BMask;
        bx0 &= BMask;
        by0 &= BMask;
        int i = _lattice[bx0], j = _lattice[bx1];
        int b00 = _lattice[i + by0], b10 = _lattice[j + by0], b01 = _lattice[i + by1], b11 = _lattice[j + by1];
        double sx = SCurve(rx0), sy = SCurve(ry0);
        double u = (rx0 * _gradients[G(channel, b00, 0)]) + (ry0 * _gradients[G(channel, b00, 1)]);
        double v = (rx1 * _gradients[G(channel, b10, 0)]) + (ry0 * _gradients[G(channel, b10, 1)]);
        double a = Lerp(sx, u, v);
        u = (rx0 * _gradients[G(channel, b01, 0)]) + (ry1 * _gradients[G(channel, b01, 1)]);
        v = (rx1 * _gradients[G(channel, b11, 0)]) + (ry1 * _gradients[G(channel, b11, 1)]);
        double b = Lerp(sx, u, v);
        return Lerp(sy, a, b);
    }
}

/// <summary>
/// Simplex noise on the skewed triangular lattice (Perlin's 2001 design, as Gustavson's public write-up explains it), with a
/// permutation shuffled from the seed by SplitMix64. Tiled noise runs 4D simplex noise around a torus, so it repeats exactly.
/// Octaves are normalized by their summed amplitudes, so every channel stays within 0 to 1.
/// </summary>
internal sealed class SimplexShader : Shader
{
    private static readonly double F2 = 0.5 * (Math.Sqrt(3) - 1);
    private static readonly double G2 = (3 - Math.Sqrt(3)) / 6;
    private static readonly double F4 = (Math.Sqrt(5) - 1) / 4;
    private static readonly double G4 = (5 - Math.Sqrt(5)) / 20;

    private static readonly sbyte[] Grad2 = [1, 1, -1, 1, 1, -1, -1, -1, 1, 0, -1, 0, 0, 1, 0, -1];

    private static readonly sbyte[] Grad4 =
    [
        0, 1, 1, 1, 0, 1, 1, -1, 0, 1, -1, 1, 0, 1, -1, -1, 0, -1, 1, 1, 0, -1, 1, -1, 0, -1, -1, 1, 0, -1, -1, -1,
        1, 0, 1, 1, 1, 0, 1, -1, 1, 0, -1, 1, 1, 0, -1, -1, -1, 0, 1, 1, -1, 0, 1, -1, -1, 0, -1, 1, -1, 0, -1, -1,
        1, 1, 0, 1, 1, 1, 0, -1, 1, -1, 0, 1, 1, -1, 0, -1, -1, 1, 0, 1, -1, 1, 0, -1, -1, -1, 0, 1, -1, -1, 0, -1,
        1, 1, 1, 0, 1, 1, -1, 0, 1, -1, 1, 0, 1, -1, -1, 0, -1, 1, 1, 0, -1, 1, -1, 0, -1, -1, 1, 0, -1, -1, -1, 0,
    ];

    private readonly byte[][] _perm = new byte[4][];
    private readonly Matrix3x2 _inverse;
    private readonly double _frequency;
    private readonly int _octaves;
    private readonly Size? _tile;
    private readonly double _norm;

    internal SimplexShader(NoisePaint paint, Matrix3x2 inverse)
    {
        _inverse = inverse;
        _frequency = paint.BaseFrequencyX;
        _octaves = paint.Octaves;
        _tile = paint.TileSize;
        double amplitude = 1, total = 0;
        for (int i = 0; i < _octaves; i++)
        {
            total += amplitude;
            amplitude /= 2;
        }

        _norm = total > 0 ? 1 / total : 0;
        ulong state = (ulong)(uint)paint.Seed;
        for (int channel = 0; channel < 4; channel++)
        {
            byte[] p = new byte[512];
            for (int i = 0; i < 256; i++)
            {
                p[i] = (byte)i;
            }

            for (int i = 255; i > 0; i--)
            {
                int j = (int)(SplitMix(ref state) % (ulong)(i + 1));
                (p[i], p[j]) = (p[j], p[i]);
            }

            for (int i = 0; i < 256; i++)
            {
                p[256 + i] = p[i];
            }

            _perm[channel] = p;
        }
    }

    public override void Shade(int y, int x, Span<PremulRgbaF32> row)
    {
        Vector2 p = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), _inverse), step = new(_inverse.M11, _inverse.M12);
        for (int i = 0; i < row.Length; i++)
        {
            Vector2 q = p + (step * i);
            float r = Channel(0, q), g = Channel(1, q), b = Channel(2, q), a = Channel(3, q);
            row[i] = new PremulRgbaF32(r * a, g * a, b * a, a);
        }
    }

    /// <summary>SplitMix64 (Steele, Lea and Flood, 2014): the next value of a 64-bit state.</summary>
    internal static ulong SplitMix(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private float Channel(int channel, Vector2 point)
    {
        if (_octaves == 0)
        {
            return 0;
        }

        byte[] perm = _perm[channel];
        double sum = 0, amplitude = 1, frequency = _frequency;
        for (int octave = 0; octave < _octaves; octave++)
        {
            double n;
            if (_tile is Size tile)
            {
                // Around a torus: each axis a circle whose circumference is the tile at this frequency, so the noise repeats exactly.
                double ax = 2 * Math.PI * point.X / tile.Width, ay = 2 * Math.PI * point.Y / tile.Height;
                double rx = tile.Width * frequency / (2 * Math.PI), ry = tile.Height * frequency / (2 * Math.PI);
                n = Noise4(perm, rx * Math.Cos(ax), rx * Math.Sin(ax), ry * Math.Cos(ay), ry * Math.Sin(ay));
            }
            else
            {
                n = Noise2(perm, point.X * frequency, point.Y * frequency);
            }

            sum += n * amplitude;
            amplitude /= 2;
            frequency *= 2;
        }

        return (float)Math.Clamp(0.5 + (0.5 * sum * _norm), 0, 1);
    }

    private static double Noise2(byte[] perm, double xin, double yin)
    {
        double s = (xin + yin) * F2;
        int i = (int)Math.Floor(xin + s), j = (int)Math.Floor(yin + s);
        double t = (i + j) * G2;
        double x0 = xin - (i - t), y0 = yin - (j - t);
        int i1 = x0 > y0 ? 1 : 0, j1 = 1 - i1;
        double x1 = x0 - i1 + G2, y1 = y0 - j1 + G2, x2 = x0 - 1 + (2 * G2), y2 = y0 - 1 + (2 * G2);
        int ii = i & 255, jj = j & 255;
        double n = Corner2(perm[ii + perm[jj]], x0, y0) + Corner2(perm[ii + i1 + perm[jj + j1]], x1, y1) + Corner2(perm[ii + 1 + perm[jj + 1]], x2, y2);
        return Math.Clamp(70 * n, -1, 1);
    }

    private static double Corner2(int hash, double x, double y)
    {
        double t = 0.5 - (x * x) - (y * y);
        if (t < 0)
        {
            return 0;
        }

        int g = (hash & 7) * 2;
        t *= t;
        return t * t * ((Grad2[g] * x) + (Grad2[g + 1] * y));
    }

    private static double Noise4(byte[] perm, double x, double y, double z, double w)
    {
        double s = (x + y + z + w) * F4;
        int i = (int)Math.Floor(x + s), j = (int)Math.Floor(y + s), k = (int)Math.Floor(z + s), l = (int)Math.Floor(w + s);
        double t = (i + j + k + l) * G4;
        double x0 = x - (i - t), y0 = y - (j - t), z0 = z - (k - t), w0 = w - (l - t);

        // Rank the coordinates to find which of the 24 simplices holds the point.
        int rx = 0, ry = 0, rz = 0, rw = 0;
        if (x0 > y0)
        {
            rx++;
        }
        else
        {
            ry++;
        }

        if (x0 > z0)
        {
            rx++;
        }
        else
        {
            rz++;
        }

        if (x0 > w0)
        {
            rx++;
        }
        else
        {
            rw++;
        }

        if (y0 > z0)
        {
            ry++;
        }
        else
        {
            rz++;
        }

        if (y0 > w0)
        {
            ry++;
        }
        else
        {
            rw++;
        }

        if (z0 > w0)
        {
            rz++;
        }
        else
        {
            rw++;
        }

        int ii = i & 255, jj = j & 255, kk = k & 255, ll = l & 255;
        double n = 0;
        for (int c = 0; c < 5; c++)
        {
            // Corner c steps by 1 on the coordinates ranked at least 4 − c; corner 4 is (1, 1, 1, 1).
            int threshold = 4 - c;
            int a = c == 0 ? 0 : (rx >= threshold ? 1 : 0), b = c == 0 ? 0 : (ry >= threshold ? 1 : 0);
            int d = c == 0 ? 0 : (rz >= threshold ? 1 : 0), e = c == 0 ? 0 : (rw >= threshold ? 1 : 0);
            if (c == 4)
            {
                a = b = d = e = 1;
            }

            double cx = x0 - a + (c * G4), cy = y0 - b + (c * G4), cz = z0 - d + (c * G4), cw = w0 - e + (c * G4);
            double tt = 0.6 - (cx * cx) - (cy * cy) - (cz * cz) - (cw * cw);
            if (tt < 0)
            {
                continue;
            }

            int hash = perm[ii + a + perm[jj + b + perm[kk + d + perm[ll + e]]]] & 31;
            int g = hash * 4;
            tt *= tt;
            n += tt * tt * ((Grad4[g] * cx) + (Grad4[g + 1] * cy) + (Grad4[g + 2] * cz) + (Grad4[g + 3] * cw));
        }

        return Math.Clamp(27 * n, -1, 1);
    }
}
