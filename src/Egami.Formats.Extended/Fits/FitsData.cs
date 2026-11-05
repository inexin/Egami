using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Formats.Extended.Fits;

/// <summary>Turns FITS data into physical values, and physical values into a picture.</summary>
internal static class FitsData
{
    /// <summary>The softening of <see cref="FitsStretch.Asinh"/>.</summary>
    internal const double AsinhSoftening = 0.1;

    /// <summary>
    /// Decodes big-endian samples of <paramref name="bitpix"/> into physical values, <c>BZERO + BSCALE × stored</c>, with
    /// <c>BLANK</c> and float NaN as NaN. Values past the end of <paramref name="raw"/> are NaN. Returns how many decoded.
    /// </summary>
    public static long Decode<T>(ReadOnlySpan<byte> raw, int bitpix, double scale, double zero, long? blank, Span<T> dst)
        where T : unmanaged, IFloatingPointIeee754<T>
    {
        int width = Math.Abs(bitpix) / 8;
        int n = (int)Math.Min(dst.Length, raw.Length / width);
        bool plain = scale == 1 && zero == 0;
        switch (bitpix)
        {
            case 8:
                for (int i = 0; i < n; i++)
                {
                    byte s = raw[i];
                    dst[i] = s == blank ? T.NaN : plain ? T.CreateTruncating(s) : T.CreateTruncating((s * scale) + zero);
                }

                break;
            case 16:
                ReadOnlySpan<short> words = MemoryMarshal.Cast<byte, short>(raw[..(n * 2)]);
                for (int i = 0; i < n; i++)
                {
                    short s = BinaryPrimitives.ReverseEndianness(words[i]);
                    dst[i] = s == blank ? T.NaN : T.CreateTruncating((s * scale) + zero);
                }

                break;
            case 32:
                ReadOnlySpan<int> ints = MemoryMarshal.Cast<byte, int>(raw[..(n * 4)]);
                for (int i = 0; i < n; i++)
                {
                    int s = BinaryPrimitives.ReverseEndianness(ints[i]);
                    dst[i] = s == blank ? T.NaN : T.CreateTruncating((s * scale) + zero);
                }

                break;
            case 64:
                ReadOnlySpan<long> longs = MemoryMarshal.Cast<byte, long>(raw[..(n * 8)]);
                for (int i = 0; i < n; i++)
                {
                    long s = BinaryPrimitives.ReverseEndianness(longs[i]);
                    dst[i] = s == blank ? T.NaN : T.CreateTruncating((s * scale) + zero);
                }

                break;
            case -32:
                ReadOnlySpan<int> floats = MemoryMarshal.Cast<byte, int>(raw[..(n * 4)]);
                for (int i = 0; i < n; i++)
                {
                    float s = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReverseEndianness(floats[i]));
                    dst[i] = plain ? T.CreateTruncating(s) : T.CreateTruncating((s * scale) + zero);
                }

                break;
            default:
                ReadOnlySpan<long> doubles = MemoryMarshal.Cast<byte, long>(raw[..(n * 8)]);
                for (int i = 0; i < n; i++)
                {
                    double s = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReverseEndianness(doubles[i]));
                    dst[i] = T.CreateTruncating((s * scale) + zero);
                }

                break;
        }

        dst[n..].Fill(T.NaN);
        return n;
    }

    /// <summary>Returns the physical values a stretch puts at 0 and 1.</summary>
    public static (double Low, double High) Limits(FitsStretch stretch, ReadOnlySpan<float> values, FitsHeader header)
    {
        if (stretch == FitsStretch.None)
        {
            return (double.NaN, double.NaN);
        }

        if (stretch == FitsStretch.ZScale)
        {
            return ZScale(values);
        }

        double? dataMin = header.GetReal("DATAMIN");
        double? dataMax = header.GetReal("DATAMAX");
        if (dataMin is double lo && dataMax is double hi && double.IsFinite(lo) && double.IsFinite(hi) && hi > lo)
        {
            return (lo, hi);
        }

        // Finite values only: x - x is 0 for them and NaN for NaN and the infinities.
        Vector<float> lows = new(float.PositiveInfinity), highs = new(float.NegativeInfinity);
        int i = 0;
        for (; i <= values.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            Vector<float> v = new(values[i..]);
            Vector<int> finite = Vector.Equals(v - v, Vector<float>.Zero);
            lows = Vector.Min(lows, Vector.ConditionalSelect(finite, v, lows));
            highs = Vector.Max(highs, Vector.ConditionalSelect(finite, v, highs));
        }

        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (int lane = 0; lane < Vector<float>.Count; lane++)
        {
            min = Math.Min(min, lows[lane]);
            max = Math.Max(max, highs[lane]);
        }

        for (; i < values.Length; i++)
        {
            float v = values[i];
            if (float.IsFinite(v))
            {
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
        }

        return min > max ? (0, 1) : (min, max);
    }

    /// <summary>asinh(x / softening) / asinh(1 / softening) at 4096 steps over 0 to 1, with one more entry past the end.</summary>
    private static readonly Lazy<float[]> AsinhCurve = new(() =>
    {
        const int Steps = 4096;
        double norm = 1 / Math.Asinh(1 / AsinhSoftening);
        float[] curve = new float[Steps + 2];
        for (int i = 0; i < curve.Length; i++)
        {
            curve[i] = (float)(Math.Asinh(Math.Min(i, Steps) / (double)Steps / AsinhSoftening) * norm);
        }

        return curve;
    });

    /// <summary>Maps physical values into the picture's 0 to 1, NaN to 0.</summary>
    public static void Stretch(FitsStretch stretch, double low, double high, ReadOnlySpan<float> values, Span<float> dst)
    {
        if (stretch == FitsStretch.None)
        {
            values.CopyTo(dst);
            return;
        }

        float a = (float)low;
        float k = high > low ? (float)(1 / (high - low)) : 0;
        if (stretch == FitsStretch.Asinh)
        {
            // The curve from a table, linearly interpolated: within 1e-7 of the exact value.
            float[] curve = AsinhCurve.Value;
            for (int i = 0; i < values.Length; i++)
            {
                float v = Math.Clamp((values[i] - a) * k, 0, 1);
                if (float.IsNaN(v))
                {
                    dst[i] = 0;
                    continue;
                }

                float at = v * (curve.Length - 2);
                int n = (int)at;
                dst[i] = curve[n] + ((curve[n + 1] - curve[n]) * (at - n));
            }

            return;
        }

        Vector<float> offset = new(a), scale = new(k);
        int j = 0;
        for (; j <= values.Length - Vector<float>.Count; j += Vector<float>.Count)
        {
            Vector<float> v = (new Vector<float>(values[j..]) - offset) * scale;
            Vector<float> clamped = Vector.Min(Vector.Max(v, Vector<float>.Zero), Vector<float>.One);
            Vector.ConditionalSelect(Vector.Equals(v, v), clamped, Vector<float>.Zero).CopyTo(dst[j..]);
        }

        for (; j < values.Length; j++)
        {
            float v = (values[j] - a) * k;
            dst[j] = float.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
        }
    }

    /// <summary>
    /// IRAF's zscale: from a sorted sample of up to 1000 values taken evenly across the image, a line fitted with iterated
    /// rejection of values more than 2.5 sigma off (and their neighbours); its slope, divided by the contrast 0.25, gives the range
    /// around the median, inside the sample's own range. When too few values survive, the sample's range.
    /// </summary>
    internal static (double Low, double High) ZScale(ReadOnlySpan<float> values)
    {
        const int Samples = 1000;
        const double Contrast = 0.25, MaxReject = 0.5, Krej = 2.5;
        const int MinPixels = 5, MaxIterations = 5;
        int stride = Math.Max(1, values.Length / Samples);
        List<double> sample = new(Samples);
        for (int i = 0; i < values.Length && sample.Count < Samples; i += stride)
        {
            if (float.IsFinite(values[i]))
            {
                sample.Add(values[i]);
            }
        }

        if (sample.Count == 0)
        {
            return (0, 1);
        }

        sample.Sort();
        int npix = sample.Count;
        double zmin = sample[0], zmax = sample[^1];
        int center = (npix - 1) / 2;
        double median = npix % 2 == 1 ? sample[center] : 0.5 * (sample[center] + sample[center + 1]);
        int minPix = Math.Max(MinPixels, (int)(npix * MaxReject));
        int grow = Math.Max(1, (int)(npix * 0.01));
        bool[] bad = new bool[npix];
        int good = npix, lastGood = npix + 1;
        double slope = 0;
        for (int iteration = 0; iteration < MaxIterations && good < lastGood && good >= minPix; iteration++)
        {
            // Least-squares line through the good values against their index.
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            int m = 0;
            for (int i = 0; i < npix; i++)
            {
                if (!bad[i])
                {
                    sx += i;
                    sy += sample[i];
                    sxx += (double)i * i;
                    sxy += i * sample[i];
                    m++;
                }
            }

            double d = (m * sxx) - (sx * sx);
            slope = d == 0 ? 0 : ((m * sxy) - (sx * sy)) / d;
            double intercept = (sy - (slope * sx)) / m;
            double sum = 0, sum2 = 0;
            for (int i = 0; i < npix; i++)
            {
                if (!bad[i])
                {
                    double r = sample[i] - ((i * slope) + intercept);
                    sum += r;
                    sum2 += r * r;
                }
            }

            double mean = sum / m;
            double threshold = Krej * Math.Sqrt(Math.Max(0, (sum2 / m) - (mean * mean)));
            bool[] rejected = new bool[npix];
            for (int i = 0; i < npix; i++)
            {
                double r = sample[i] - ((i * slope) + intercept);
                if (bad[i] || r < -threshold || r > threshold)
                {
                    // Reject the value and its neighbours.
                    int from = Math.Max(0, i - (grow / 2)), to = Math.Min(npix - 1, i + ((grow - 1) / 2));
                    for (int j = from; j <= to; j++)
                    {
                        rejected[j] = true;
                    }
                }
            }

            bad = rejected;
            lastGood = good;
            good = bad.Count(b => !b);
        }

        if (good >= minPix)
        {
            slope /= Contrast;
            zmin = Math.Max(zmin, median - ((center - 1) * slope));
            zmax = Math.Min(zmax, median + ((npix - center) * slope));
        }

        return zmax > zmin ? (zmin, zmax) : (sample[0], sample[^1] > sample[0] ? sample[^1] : sample[0] + 1);
    }
}
