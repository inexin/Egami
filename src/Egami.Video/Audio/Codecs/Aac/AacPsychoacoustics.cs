namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// The AAC encoder's psychoacoustic model: for each scalefactor band of a block it gives the energy and the masking threshold,
/// the noise energy the band can hold without it being heard. The threshold starts from the band's energy lowered by a
/// signal-to-mask ratio that runs from 12 dB for noise to at most 24 dB for a pure tone, after Johnston's tonality rule
/// (IEEE JSAC 6(2), 1988) with the spectral flatness of the band as the measure; it then spreads to the neighbouring bands with
/// the slopes of the masking curve on the Bark scale (Zwicker and Fastl, Psychoacoustics, 1990: about 15 dB per Bark toward
/// higher frequencies and 30 dB per Bark toward lower ones), is raised to the threshold in quiet (Terhardt, JASA 65(2), 1979,
/// with full scale taken as 105 dB SPL and the curve capped at 5 dB SPL, so the quietest bands of loud material are not dropped), and for long blocks may rise at most 3 dB over the last block's, which keeps the noise
/// of a block from spreading ahead of an onset. The perceptual entropy, about the bits a block needs at its thresholds, steers
/// how the bit rate is spread over blocks.
/// </summary>
internal sealed class AacPsychoacoustics
{
    private const float NoiseSnrDb = 12;
    private const float TonalSnrMaxDb = 24;
    private const double FullScaleDb = 105;
    private const double QuietCapDb = 5;
    private const float SlopeUpDb = 15;
    private const float SlopeDownDb = 30;
    private const float RiseLimit = 2;
    private const float FallLimit = 0.01f;

    private readonly short[] _longOffsets;
    private readonly short[] _shortOffsets;
    private readonly float[] _longSnrTonal;
    private readonly float[] _shortSnrTonal;
    private readonly float[] _longAth;
    private readonly float[] _shortAth;
    private readonly float[] _longUp;
    private readonly float[] _longDown;
    private readonly float[] _shortUp;
    private readonly float[] _shortDown;

    /// <summary>Initializes the model for a sampling rate.</summary>
    /// <param name="rateIndex">The sampling_frequency_index, which picks the band tables.</param>
    /// <param name="sampleRate">The sampling rate in hertz.</param>
    public AacPsychoacoustics(int rateIndex, int sampleRate)
    {
        _longOffsets = AacTables.LongOffsets.Slice(AacTables.LongStart[rateIndex], AacTables.LongBands[rateIndex] + 1).ToArray();
        _shortOffsets = AacTables.ShortOffsets.Slice(AacTables.ShortStart[rateIndex], AacTables.ShortBands[rateIndex] + 1).ToArray();
        (_longSnrTonal, _longAth, _longUp, _longDown) = Prepare(_longOffsets, 1024, sampleRate);
        (_shortSnrTonal, _shortAth, _shortUp, _shortDown) = Prepare(_shortOffsets, 128, sampleRate);
    }

    /// <summary>Gets the long-window band offsets.</summary>
    public ReadOnlySpan<short> LongOffsets => _longOffsets;

    /// <summary>Gets the short-window band offsets.</summary>
    public ReadOnlySpan<short> ShortOffsets => _shortOffsets;

    /// <summary>
    /// Gives the energy and threshold of every band of one window's spectrum, in the encoder's units (16-bit sample scale).
    /// </summary>
    /// <param name="spectrum">The window's lines in frequency order: 1024 for a long window, 128 for a short one.</param>
    /// <param name="bands">The bands to analyse, from the lowest.</param>
    /// <param name="energy">Each band's energy.</param>
    /// <param name="threshold">Each band's masking threshold.</param>
    /// <param name="previous">For a long window, the last long window's thresholds, which this one may not exceed by more than 3 dB, updated to this one's; else empty.</param>
    /// <returns>The perceptual entropy of the window in bits.</returns>
    public float Analyze(ReadOnlySpan<float> spectrum, int bands, Span<float> energy, Span<float> threshold, Span<float> previous)
    {
        bool shortWindow = spectrum.Length == 128;
        ReadOnlySpan<short> offsets = shortWindow ? _shortOffsets : _longOffsets;
        ReadOnlySpan<float> snrTonal = shortWindow ? _shortSnrTonal : _longSnrTonal;
        ReadOnlySpan<float> ath = shortWindow ? _shortAth : _longAth;
        ReadOnlySpan<float> up = shortWindow ? _shortUp : _longUp;
        ReadOnlySpan<float> down = shortWindow ? _shortDown : _longDown;
        for (int b = 0; b < bands; b++)
        {
            int start = offsets[b], end = offsets[b + 1];
            float e = 0, logSum = 0;
            for (int k = start; k < end; k++)
            {
                float p = spectrum[k] * spectrum[k];
                e += p;
                logSum += FastLog2(p + 1);
            }

            energy[b] = e;
            if (e <= 0)
            {
                threshold[b] = 0;
                continue;
            }

            // Spectral flatness in dB: 0 for white noise, very negative for a tone. -30 dB counts as fully tonal.
            int width = end - start;
            float flatness = 3.0103f * ((logSum / width) - FastLog2((e / width) + 1));
            float tonality = Math.Clamp(flatness / -30f, 0, 1);
            float snr = (tonality * snrTonal[b]) + ((1 - tonality) * NoiseSnrDb);
            threshold[b] = e * MathF.Pow(10, -snr / 10);
        }

        for (int b = 1; b < bands; b++)
        {
            threshold[b] = Math.Max(threshold[b], threshold[b - 1] * up[b - 1]);
        }

        for (int b = bands - 2; b >= 0; b--)
        {
            threshold[b] = Math.Max(threshold[b], threshold[b + 1] * down[b]);
        }

        float pe = 0;
        for (int b = 0; b < bands; b++)
        {
            float t = Math.Max(threshold[b], ath[b]);
            if (!previous.IsEmpty)
            {
                float last = previous[b];
                if (last > 0)
                {
                    t = Math.Max(FallLimit * last, Math.Min(t, RiseLimit * last));
                    t = Math.Max(t, ath[b]);
                }

                previous[b] = t;
            }

            threshold[b] = t;
            if (energy[b] > t)
            {
                pe += 0.5f * (offsets[b + 1] - offsets[b]) * FastLog2(energy[b] / t);
            }
        }

        return pe;
    }

    /// <summary>A log2 good to about 0.01, from the float's exponent and a quadratic on its mantissa.</summary>
    public static float FastLog2(float x)
    {
        int bits = BitConverter.SingleToInt32Bits(x);
        float exponent = ((bits >> 23) & 255) - 128;
        float m = BitConverter.Int32BitsToSingle((bits & 0x007FFFFF) | 0x3F800000);
        return exponent + (((-0.34484843f * m) + 2.02466578f) * m) - 0.67487759f;
    }

    private static (float[] SnrTonal, float[] Ath, float[] Up, float[] Down) Prepare(short[] offsets, int lines, int sampleRate)
    {
        int bands = offsets.Length - 1;
        float[] snr = new float[bands];
        float[] ath = new float[bands];
        float[] up = new float[bands];
        float[] down = new float[bands];
        double lineHz = sampleRate / 2.0 / lines;

        // A full-scale sine's mean energy in its MDCT line: (32768 N 2 / pi)^2 / 2 for a block of N = 2 lines samples.
        double fullScale = 0.5 * Math.Pow(32768.0 * 2 * lines * 2 / Math.PI, 2);
        double[] bark = new double[bands];
        for (int b = 0; b < bands; b++)
        {
            double centre = (offsets[b] + offsets[b + 1]) / 2.0 * lineHz;
            bark[b] = Bark(centre);
            snr[b] = (float)Math.Min(14.5 + bark[b], TonalSnrMaxDb);

            // The quietest line of the band sets its threshold in quiet.
            double quietest = double.MaxValue;
            for (int k = offsets[b]; k < offsets[b + 1]; k++)
            {
                quietest = Math.Min(quietest, Terhardt(Math.Max((k + 0.5) * lineHz, 20)));
            }

            ath[b] = (float)(fullScale * Math.Pow(10, (Math.Min(quietest, QuietCapDb) - FullScaleDb) / 10));
        }

        for (int b = 0; b + 1 < bands; b++)
        {
            double distance = bark[b + 1] - bark[b];
            up[b] = (float)Math.Pow(10, -SlopeUpDb * distance / 10);
            down[b] = (float)Math.Pow(10, -SlopeDownDb * distance / 10);
        }

        return (snr, ath, up, down);
    }

    private static double Bark(double hz) => (13 * Math.Atan(0.00076 * hz)) + (3.5 * Math.Atan(hz / 7500 * (hz / 7500)));

    private static double Terhardt(double hz)
    {
        double f = hz / 1000;
        return (3.64 * Math.Pow(f, -0.8)) - (6.5 * Math.Exp(-0.6 * (f - 3.3) * (f - 3.3))) + (1e-3 * f * f * f * f);
    }
}
