// Translated to C# from oxideav-aac (MIT), modified: the TNS decision of its src/encoder_tns.rs (autocorrelation, Levinson
// recursion, prediction-gain and coefficient-trim rules), the temporal gate of src/encoder.rs (tns_temporal_permits) and the
// coefficient quantizer of src/tns_coef.rs, over float spans; the analysis filter is the exact inverse of AacFrameDecoder's
// synthesis. Checked against ISO/IEC 14496-3 4.6.9.

namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// Temporal noise shaping for the encoder (ISO/IEC 14496-3 4.6.9): decides per window whether a prediction filter over the
/// spectrum pays (a high prediction gain on a signal whose energy is uneven in time), quantizes its reflection coefficients
/// to four bits, and runs the moving-average analysis filter that the decoder's all-pole filter undoes, so the quantization
/// noise follows the signal's envelope in time.
/// </summary>
internal sealed class AacTnsEncoder
{
    /// <summary>The highest filter order the encoder uses.</summary>
    public const int MaxOrder = 8;

    private const double GainMin = 1.4;
    private const double TrimBelow = 0.1;
    private const double TemporalRatio = 3.0;
    private const double TemporalFloor = 128.0 * 90.0 * 90.0;

    private readonly double[] _autocorrelation = new double[MaxOrder + 1];
    private readonly double[] _parcor = new double[MaxOrder];
    private readonly double[] _a = new double[MaxOrder + 1];
    private readonly double[] _b = new double[MaxOrder + 1];
    private readonly float[] _history = new float[MaxOrder];

    /// <summary>Gets the filter order of each window, 0 for none.</summary>
    public int[] Order { get; } = new int[8];

    /// <summary>Gets the four-bit coefficients of each window's filter, <see cref="MaxOrder"/> per window.</summary>
    public int[] Coefficients { get; } = new int[8 * MaxOrder];

    /// <summary>Gets the band count each window's filter covers, written as its length.</summary>
    public int[] Length { get; } = new int[8];

    /// <summary>Gets whether any window has a filter.</summary>
    public bool Present { get; private set; }

    /// <summary>
    /// Whether a block of time samples is uneven enough in time for TNS to help: of its 16 equal parts, the loudest holds more
    /// than three times the mean energy, and the mean is above a floor that keeps quiet passages out.
    /// </summary>
    /// <param name="samples">The block, scaled to 16-bit sample units.</param>
    /// <returns>Whether TNS may be used.</returns>
    public static bool Permits(ReadOnlySpan<float> samples)
    {
        int sub = samples.Length / 16;
        double total = 0, max = 0;
        for (int j = 0; j < 16; j++)
        {
            double e = 0;
            foreach (float v in samples.Slice(j * sub, sub))
            {
                e += (double)v * v;
            }

            total += e;
            max = Math.Max(max, e);
        }

        double mean = total / 16;
        return mean > TemporalFloor * sub / 128 && max > TemporalRatio * mean;
    }

    /// <summary>Clears every window's filter.</summary>
    public void Clear()
    {
        Present = false;
        Array.Clear(Order);
    }

    /// <summary>
    /// Decides the filter of one window over the lines [0, <paramref name="end"/>) and, when it pays, filters them in place.
    /// </summary>
    /// <param name="window">The window, 0 for a long block.</param>
    /// <param name="spectrum">The window's lines.</param>
    /// <param name="end">The end of the filtered range: the offset of min(TNS_MAX_BANDS, max_sfb).</param>
    /// <param name="bands">The filter length to write: the window's band count, so that the filter starts at line 0.</param>
    /// <param name="shortWindow">Whether the window is one of eight short ones (order at most 7).</param>
    public void Analyze(int window, Span<float> spectrum, int end, int bands, bool shortWindow)
    {
        Order[window] = 0;
        int maxOrder = Math.Min(MaxOrder, shortWindow ? 7 : 12);
        if (end < 2 * maxOrder)
        {
            return;
        }

        ReadOnlySpan<float> region = spectrum[..end];
        Span<double> r = _autocorrelation.AsSpan(0, maxOrder + 1);
        for (int lag = 0; lag <= maxOrder; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < region.Length; i++)
            {
                sum += (double)region[i] * region[i + lag];
            }

            r[lag] = sum;
        }

        if (r[0] <= 0)
        {
            return;
        }

        // Levinson-Durbin: the reflection coefficients and the prediction error.
        Span<double> a = _a;
        Span<double> b = _b;
        a.Clear();
        a[0] = 1;
        double error = r[0];
        int order = 0;
        for (int m = 1; m <= maxOrder; m++)
        {
            double acc = r[m];
            for (int i = 1; i < m; i++)
            {
                acc += a[i] * r[m - i];
            }

            double k = -acc / error;
            if (!double.IsFinite(k) || Math.Abs(k) >= 1)
            {
                break;
            }

            for (int i = 1; i < m; i++)
            {
                b[i] = a[i] + (k * a[m - i]);
            }

            b[1..m].CopyTo(a[1..m]);
            a[m] = k;
            _parcor[order++] = k;
            error *= 1 - (k * k);
            if (error <= 0)
            {
                break;
            }
        }

        if (order == 0 || error <= 0 || r[0] / error < GainMin)
        {
            return;
        }

        while (order > 0 && Math.Abs(_parcor[order - 1]) < TrimBelow)
        {
            order--;
        }

        if (order == 0)
        {
            return;
        }

        // Four-bit coefficients (coef_res 1, no compression), and the direct-form filter the decoder rebuilds from them.
        double iq = 7.5 / (Math.PI / 2);
        double iqNegative = 8.5 / (Math.PI / 2);
        Span<float> lpc = stackalloc float[MaxOrder + 1];
        Span<float> step = stackalloc float[MaxOrder + 1];
        lpc.Clear();
        lpc[0] = 1;
        for (int m = 1; m <= order; m++)
        {
            double k = _parcor[m - 1];
            int c = Math.Clamp((int)Math.Round(Math.Asin(k) * (k >= 0 ? iq : iqNegative), MidpointRounding.AwayFromZero), -8, 7);
            Coefficients[(window * MaxOrder) + m - 1] = c;
            float q = (float)Math.Sin(c / (c >= 0 ? iq : iqNegative));
            for (int i = 1; i < m; i++)
            {
                step[i] = lpc[i] + (q * lpc[m - i]);
            }

            step[1..m].CopyTo(lpc[1..m]);
            lpc[m] = q;
        }

        Order[window] = order;
        Length[window] = bands;
        Present = true;

        // The analysis filter, upward: e[n] = s[n] + sum lpc[k] s[n - k], which the decoder's all-pole filter inverts.
        Span<float> history = _history.AsSpan(0, order);
        history.Clear();
        for (int n = 0; n < end; n++)
        {
            float s = spectrum[n];
            float e = s;
            for (int k = 1; k <= order; k++)
            {
                e += lpc[k] * history[k - 1];
            }

            for (int k = order - 1; k > 0; k--)
            {
                history[k] = history[k - 1];
            }

            if (order > 0)
            {
                history[0] = s;
            }

            spectrum[n] = e;
        }
    }

    /// <summary>Gets the bits of the tns_data the windows' filters need (Table 4.48).</summary>
    /// <param name="shortWindows">Whether the block has eight short windows.</param>
    /// <returns>The bit count.</returns>
    public int Bits(bool shortWindows)
    {
        int windows = shortWindows ? 8 : 1;
        int bits = 0;
        for (int w = 0; w < windows; w++)
        {
            bits += shortWindows ? 1 : 2;
            if (Order[w] > 0)
            {
                bits += 1 + (shortWindows ? 4 + 3 : 6 + 5) + 2 + (4 * Order[w]);
            }
        }

        return bits;
    }

    /// <summary>Writes tns_data (Table 4.48): one filter per window that has one, upward, four-bit coefficients.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="shortWindows">Whether the block has eight short windows.</param>
    public void Write(AacBitWriter writer, bool shortWindows)
    {
        int windows = shortWindows ? 8 : 1;
        for (int w = 0; w < windows; w++)
        {
            int order = Order[w];
            writer.Write(order > 0 ? 1u : 0u, shortWindows ? 1 : 2);
            if (order == 0)
            {
                continue;
            }

            writer.Write(1, 1); // coef_res: four-bit coefficients
            writer.Write((uint)Length[w], shortWindows ? 4 : 6);
            writer.Write((uint)order, shortWindows ? 3 : 5);
            writer.Write(0, 1); // direction: upward
            writer.Write(0, 1); // coef_compress: none
            for (int i = 0; i < order; i++)
            {
                writer.Write((uint)Coefficients[(w * MaxOrder) + i] & 15, 4);
            }
        }
    }
}
