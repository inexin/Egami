using Egami.Colors;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// The last stage of a develop, per pixel: white-balanced camera RGB to the output colour space, with exposure and, when the
/// file has them, the DNG camera profile's HueSatMap, LookTable and ProfileToneCurve (applied in linear ProPhoto RGB, as the
/// DNG specification places them), then the output's transfer function.
/// </summary>
internal sealed class RawLook
{
    private const int CurveSize = 4096;
    private readonly float[] _direct;
    private readonly float[]? _toProPhoto;
    private readonly float[]? _fromProPhoto;
    private readonly HsvTable? _hueSat;
    private readonly HsvTable? _look;
    private readonly float[]? _curve;
    private readonly float _gain;
    private readonly float[] _encode;

    /// <summary>
    /// The default tone curve, as (input, output) pairs of linear values: measured from Adobe Camera Raw's default rendering (a
    /// full-size preview Adobe DNG Converter wrote for a linear neutral ramp DNG with no profile tone curve), smoothed, its
    /// shadows below 0.4 % a straight line through black where 8-bit output cannot resolve them.
    /// </summary>
    internal static readonly float[] DefaultCurve =
    [
        0f, 0f, 0.003861f, 0.002580f, 0.005051f, 0.003370f, 0.006836f, 0.004596f, 0.009277f, 0.006529f, 0.012100f, 0.009134f,
        0.016403f, 0.013964f, 0.022248f, 0.022002f, 0.030167f, 0.034742f, 0.039368f, 0.051439f, 0.053391f, 0.079200f,
        0.072389f, 0.121020f, 0.094469f, 0.173814f, 0.128099f, 0.259378f, 0.173678f, 0.367273f, 0.226688f, 0.473480f,
        0.307362f, 0.602878f, 0.416754f, 0.730328f, 0.565057f, 0.849116f, 0.737530f, 0.935005f, 1f, 1f,
    ];

    public RawLook(RawFrame frame, RawCamera camera, ColorProfile target, double exposure, bool profile, bool defaultCurve = false)
    {
        ColorPrimaries primaries = target.Primaries ?? ColorProfile.Srgb.Primaries!;
        _encode = EncodeTable(target.Transfer ?? TransferFunction.Srgb);
        double[] toTarget = RawColor.Multiply(RawColor.XyzToRgb(primaries), RawColor.Adaptation(RawColor.D50, (primaries.White.X, primaries.White.Y)));
        _gain = (float)Math.Pow(2, exposure);
        RawProfile p = frame.Profile;
        if (profile)
        {
            if (p.HueSatDims is int[] hs && p.HueSat1 is float[] data1)
            {
                float[] data = data1;
                if (p.HueSat2 is float[] data2 && camera.Weight1 < 1)
                {
                    float w = (float)camera.Weight1;
                    data = [.. data1.Zip(data2, (a, b) => (a * w) + (b * (1 - w)))];
                }

                _hueSat = new HsvTable(hs, data, p.HueSatSrgb);
            }

            if (p.LookDims is int[] ld && p.Look is float[] look)
            {
                _look = new HsvTable(ld, look, p.LookSrgb);
            }

            if (p.ToneCurve is float[] points)
            {
                _curve = Curve(points);
            }
        }

        if (_curve is null && defaultCurve)
        {
            _curve = Curve(DefaultCurve);
        }

        if (_hueSat is not null || _look is not null || _curve is not null)
        {
            _toProPhoto = Floats(RawColor.Multiply(RawColor.XyzToRgb(RawColor.ProPhoto), camera.CameraToXyz));
            _fromProPhoto = Floats(RawColor.Multiply(toTarget, RawColor.RgbToXyz(RawColor.ProPhoto)));
            _direct = [];
        }
        else
        {
            _direct = Floats(RawColor.Scale(RawColor.Multiply(toTarget, camera.CameraToXyz), _gain));
        }
    }

    /// <summary>Converts one row of white-balanced camera RGB to encoded output RGB, clipped to 0 to 1.</summary>
    public void Apply(ReadOnlySpan<float> source, Span<float> output)
    {
        if (_toProPhoto is null)
        {
            float[] m = _direct;
            for (int i = 0; i < source.Length; i += 3)
            {
                float r = source[i], g = source[i + 1], b = source[i + 2];
                output[i] = Clip((m[0] * r) + (m[1] * g) + (m[2] * b));
                output[i + 1] = Clip((m[3] * r) + (m[4] * g) + (m[5] * b));
                output[i + 2] = Clip((m[6] * r) + (m[7] * g) + (m[8] * b));
            }
        }
        else
        {
            float[] a = _toProPhoto, z = _fromProPhoto!;
            for (int i = 0; i < source.Length; i += 3)
            {
                float r0 = source[i], g0 = source[i + 1], b0 = source[i + 2];
                float r = MathF.Max(0, (a[0] * r0) + (a[1] * g0) + (a[2] * b0));
                float g = MathF.Max(0, (a[3] * r0) + (a[4] * g0) + (a[5] * b0));
                float b = MathF.Max(0, (a[6] * r0) + (a[7] * g0) + (a[8] * b0));
                _hueSat?.Apply(ref r, ref g, ref b);
                r *= _gain;
                g *= _gain;
                b *= _gain;
                _look?.Apply(ref r, ref g, ref b);
                if (_curve is not null)
                {
                    Tone(_curve, ref r, ref g, ref b);
                }

                output[i] = Clip((z[0] * r) + (z[1] * g) + (z[2] * b));
                output[i + 1] = Clip((z[3] * r) + (z[4] * g) + (z[5] * b));
                output[i + 2] = Clip((z[6] * r) + (z[7] * g) + (z[8] * b));
            }
        }

        Encode(output[..source.Length]);
    }

    private const int EncodeSize = 1 << 16;

    /// <summary>
    /// Returns the transfer function sampled at 65,537 even steps of 0 to 1; linear interpolation between them is within a
    /// thousandth of a 16-bit step of the function itself.
    /// </summary>
    private static float[] EncodeTable(TransferFunction transfer) => Tables.GetValue(transfer, MakeTable);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TransferFunction, float[]> Tables = [];

    private static float[] MakeTable(TransferFunction transfer)
    {
        float[] linear = new float[EncodeSize + 1];
        for (int i = 0; i <= EncodeSize; i++)
        {
            linear[i] = i / (float)EncodeSize;
        }

        float[] table = new float[EncodeSize + 1];
        transfer.FromLinear(linear, table);
        return table;
    }

    /// <summary>Encodes clipped linear values in place through the table.</summary>
    private void Encode(Span<float> values)
    {
        float[] table = _encode;
        for (int i = 0; i < values.Length; i++)
        {
            float f = values[i] * EncodeSize;
            int k = (int)f;
            if (k >= EncodeSize)
            {
                values[i] = table[EncodeSize];
                continue;
            }

            values[i] = table[k] + ((table[k + 1] - table[k]) * (f - k));
        }
    }

    private static float Clip(float v) => v > 0 ? (v < 1 ? v : 1) : 0;

    private static float[] Floats(double[] m) => [.. m.Select(v => (float)v)];

    /// <summary>
    /// Applies a tone curve the way that keeps hue: the curve maps the largest and smallest channel, and the middle one keeps its
    /// place between them.
    /// </summary>
    private static void Tone(float[] curve, ref float r, ref float g, ref float b)
    {
        r = Math.Clamp(r, 0, 1);
        g = Math.Clamp(g, 0, 1);
        b = Math.Clamp(b, 0, 1);
        if (r >= g)
        {
            if (g >= b)
            {
                Tone3(curve, ref r, ref g, ref b);
            }
            else if (b >= r)
            {
                Tone3(curve, ref b, ref r, ref g);
            }
            else
            {
                Tone3(curve, ref r, ref b, ref g);
            }
        }
        else if (r >= b)
        {
            Tone3(curve, ref g, ref r, ref b);
        }
        else if (b >= g)
        {
            Tone3(curve, ref b, ref g, ref r);
        }
        else
        {
            Tone3(curve, ref g, ref b, ref r);
        }
    }

    private static void Tone3(float[] curve, ref float high, ref float mid, ref float low)
    {
        float h = Look(curve, high), l = Look(curve, low);
        mid = high > low ? l + ((h - l) * (mid - low) / (high - low)) : h;
        high = h;
        low = l;
    }

    private static float Look(float[] curve, float v)
    {
        float f = v * (CurveSize - 1);
        int i = Math.Clamp((int)f, 0, CurveSize - 2);
        float t = f - i;
        return curve[i] + ((curve[i + 1] - curve[i]) * t);
    }

    /// <summary>Builds a table of ProfileToneCurve's points joined by a natural cubic spline.</summary>
    private static float[] Curve(float[] points)
    {
        int n = points.Length / 2;
        double[] x = new double[n], y = new double[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = points[i * 2];
            y[i] = points[(i * 2) + 1];
        }

        double[] second = new double[n];
        if (n > 2)
        {
            double[] u = new double[n];
            for (int i = 1; i < n - 1; i++)
            {
                double sig = (x[i] - x[i - 1]) / Math.Max(x[i + 1] - x[i - 1], 1e-12);
                double p = (sig * second[i - 1]) + 2;
                second[i] = (sig - 1) / p;
                double d = ((y[i + 1] - y[i]) / Math.Max(x[i + 1] - x[i], 1e-12)) - ((y[i] - y[i - 1]) / Math.Max(x[i] - x[i - 1], 1e-12));
                u[i] = ((6 * d / Math.Max(x[i + 1] - x[i - 1], 1e-12)) - (sig * u[i - 1])) / p;
            }

            for (int k = n - 2; k >= 0; k--)
            {
                second[k] = (second[k] * second[k + 1]) + u[k];
            }
        }

        float[] table = new float[CurveSize];
        int seg = 0;
        for (int i = 0; i < CurveSize; i++)
        {
            double v = i / (double)(CurveSize - 1);
            while (seg < n - 2 && v > x[seg + 1])
            {
                seg++;
            }

            double h = x[seg + 1] - x[seg];
            double value;
            if (v <= x[0])
            {
                value = y[0];
            }
            else if (v >= x[n - 1])
            {
                value = y[n - 1];
            }
            else if (h <= 1e-12)
            {
                value = y[seg];
            }
            else
            {
                double a = (x[seg + 1] - v) / h, b = (v - x[seg]) / h;
                value = (a * y[seg]) + (b * y[seg + 1]) + ((((a * a * a) - a) * second[seg]) + (((b * b * b) - b) * second[seg + 1])) * h * h / 6;
            }

            table[i] = (float)Math.Clamp(value, 0, 1);
        }

        return table;
    }

    /// <summary>
    /// A DNG HueSatMap or LookTable: hue shifts in degrees and saturation and value scales, on a grid of hue × saturation ×
    /// value divisions (value outermost, saturation innermost), looked up trilinearly in HSV of linear ProPhoto RGB, hue wrapping
    /// around and value optionally indexed sRGB-encoded.
    /// </summary>
    private sealed class HsvTable
    {
        private readonly int _hues;
        private readonly int _sats;
        private readonly int _vals;
        private readonly float[] _data;
        private readonly bool _srgb;

        public HsvTable(int[] dims, float[] data, bool srgb)
        {
            _hues = dims[0];
            _sats = dims[1];
            _vals = dims[2];
            _data = data;
            _srgb = srgb;
        }

        public void Apply(ref float r, ref float g, ref float b)
        {
            float max = MathF.Max(r, MathF.Max(g, b));
            if (max <= 0)
            {
                return;
            }

            float min = MathF.Min(r, MathF.Min(g, b));
            float delta = max - min;
            float s = delta / max;
            float h = 0;
            if (delta > 0)
            {
                h = max == r ? (g - b) / delta : max == g ? 2 + ((b - r) / delta) : 4 + ((r - g) / delta);
                if (h < 0)
                {
                    h += 6;
                }
            }

            float hf = h * _hues / 6;
            int h0 = (int)hf;
            float ht = hf - h0;
            h0 %= _hues;
            int h1 = (h0 + 1) % _hues;
            float sf = s * (_sats - 1);
            int s0 = Math.Min((int)sf, Math.Max(_sats - 2, 0));
            float st = _sats > 1 ? sf - s0 : 0;
            int s1 = Math.Min(s0 + 1, _sats - 1);
            float v = Math.Clamp(max, 0, 1);
            if (_srgb)
            {
                v = v <= 0.0031308f ? v * 12.92f : (1.055f * MathF.Pow(v, 1 / 2.4f)) - 0.055f;
            }

            float vf = v * (_vals - 1);
            int v0 = Math.Min((int)vf, Math.Max(_vals - 2, 0));
            float vt = _vals > 1 ? vf - v0 : 0;
            int v1 = Math.Min(v0 + 1, _vals - 1);

            float shift = 0, satScale = 0, valScale = 0;
            for (int k = 0; k < 8; k++)
            {
                int hi = (k & 1) == 0 ? h0 : h1;
                int si = (k & 2) == 0 ? s0 : s1;
                int vi = (k & 4) == 0 ? v0 : v1;
                float w = ((k & 1) == 0 ? 1 - ht : ht) * ((k & 2) == 0 ? 1 - st : st) * ((k & 4) == 0 ? 1 - vt : vt);
                if (w == 0)
                {
                    continue;
                }

                int at = ((((vi * _hues) + hi) * _sats) + si) * 3;
                shift += w * _data[at];
                satScale += w * _data[at + 1];
                valScale += w * _data[at + 2];
            }

            h += shift * 6 / 360;
            h = h < 0 ? h + 6 : h >= 6 ? h - 6 : h;
            s = Math.Clamp(s * satScale, 0, 1);
            float value = max * valScale;

            // Back to RGB through the hexcone.
            int sector = Math.Clamp((int)h, 0, 5);
            float f = h - sector;
            float p = value * (1 - s);
            float q = value * (1 - (s * f));
            float t = value * (1 - (s * (1 - f)));
            (r, g, b) = sector switch
            {
                0 => (value, t, p),
                1 => (q, value, p),
                2 => (p, value, t),
                3 => (p, q, value),
                4 => (t, p, value),
                _ => (value, p, q),
            };
        }
    }
}
