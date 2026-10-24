namespace Egami.Video.Audio.Codecs.Ac3;

/// <summary>
/// The mix matrix from an AC-3 frame's channels to an output layout, by the general downmix procedure of ATSC A/52:2018 section
/// 7.8.1 for LoRo output: channels the output has are routed, the center goes to left and right at the stream's center mix level,
/// surrounds go to left and right at its surround mix level (a single surround also at -3 dB), and a center-only output takes left
/// and right at -3 dB. The coefficients are then scaled down together so no output sums to more than 1 (section 7.8.2).
/// </summary>
internal static class Ac3Downmix
{
    private const float Minus3 = 0.70710678f;

    /// <summary>Builds the matrix, [output, input], with the inputs in the stream's order and the LFE last.</summary>
    public static float[,] Matrix(int acmod, bool lfe, float clev, float slev, ChannelLayout output)
    {
        Speakers[] inputs = InputSpeakers(acmod);
        int count = inputs.Length + (lfe ? 1 : 0);
        float[,] m = new float[output.ChannelCount, count];
        Speakers o = output.Speakers;
        bool outL = (o & (Speakers.FrontLeft | Speakers.FrontRight)) == (Speakers.FrontLeft | Speakers.FrontRight);
        bool outC = (o & Speakers.FrontCenter) != 0;
        Speakers pairL = (o & Speakers.SideLeft) != 0 && (o & Speakers.SideRight) != 0 ? Speakers.SideLeft
            : (o & Speakers.BackLeft) != 0 && (o & Speakers.BackRight) != 0 ? Speakers.BackLeft : Speakers.None;
        Speakers pairR = pairL == Speakers.SideLeft ? Speakers.SideRight : pairL == Speakers.BackLeft ? Speakers.BackRight : Speakers.None;
        bool outPair = pairL != Speakers.None;
        bool outBack = (o & Speakers.BackCenter) != 0;

        void Add(Speakers to, int from, float gain)
        {
            int index = output.IndexOf(to);
            if (index >= 0)
            {
                m[index, from] += gain;
            }
        }

        int l = Array.IndexOf(inputs, Speakers.FrontLeft);
        int r = Array.IndexOf(inputs, Speakers.FrontRight);
        int c = Array.IndexOf(inputs, Speakers.FrontCenter);
        int s = Array.IndexOf(inputs, Speakers.BackCenter);
        int ls = Array.IndexOf(inputs, Speakers.SideLeft);
        int rs = Array.IndexOf(inputs, Speakers.SideRight);
        if (acmod == 0)
        {
            // Dual mono: both programs as left and right, or their sum at -6 dB into a lone center.
            if (outL)
            {
                Add(Speakers.FrontLeft, l, 1);
                Add(Speakers.FrontRight, r, 1);
            }
            else
            {
                Add(Speakers.FrontCenter, l, 0.5f);
                Add(Speakers.FrontCenter, r, 0.5f);
            }
        }
        else
        {
            int fronts = (l >= 0 ? 2 : 0) + (c >= 0 ? 1 : 0);
            if (outL && l >= 0)
            {
                Add(Speakers.FrontLeft, l, 1);
                Add(Speakers.FrontRight, r, 1);
            }

            if (outC && c >= 0)
            {
                Add(Speakers.FrontCenter, c, outL || fronts == 1 ? 1 : clev * 2 * Minus3);
            }

            if (outC && !outL)
            {
                // A center-only output.
                if (l >= 0)
                {
                    Add(Speakers.FrontCenter, l, Minus3);
                    Add(Speakers.FrontCenter, r, Minus3);
                }

                if (s >= 0)
                {
                    Add(Speakers.FrontCenter, s, slev * Minus3);
                }

                if (ls >= 0)
                {
                    Add(Speakers.FrontCenter, ls, slev * Minus3);
                    Add(Speakers.FrontCenter, rs, slev * Minus3);
                }
            }
            else if (outL)
            {
                if (c >= 0 && !outC)
                {
                    float gain = fronts == 1 ? Minus3 : clev;
                    Add(Speakers.FrontLeft, c, gain);
                    Add(Speakers.FrontRight, c, gain);
                }

                if (s >= 0)
                {
                    if (outBack)
                    {
                        Add(Speakers.BackCenter, s, 1);
                    }
                    else if (outPair)
                    {
                        Add(pairL, s, Minus3);
                        Add(pairR, s, Minus3);
                    }
                    else
                    {
                        Add(Speakers.FrontLeft, s, slev * Minus3);
                        Add(Speakers.FrontRight, s, slev * Minus3);
                    }
                }

                if (ls >= 0)
                {
                    if (outPair)
                    {
                        Add(pairL, ls, 1);
                        Add(pairR, rs, 1);
                    }
                    else if (outBack)
                    {
                        Add(Speakers.BackCenter, ls, Minus3);
                        Add(Speakers.BackCenter, rs, Minus3);
                    }
                    else
                    {
                        Add(Speakers.FrontLeft, ls, slev);
                        Add(Speakers.FrontRight, rs, slev);
                    }
                }
            }
        }

        // Scale so that full scale on every input cannot overload any output.
        float worst = 0;
        for (int i = 0; i < m.GetLength(0); i++)
        {
            float sum = 0;
            for (int j = 0; j < inputs.Length; j++)
            {
                sum += MathF.Abs(m[i, j]);
            }

            worst = MathF.Max(worst, sum);
        }

        if (worst > 1)
        {
            for (int i = 0; i < m.GetLength(0); i++)
            {
                for (int j = 0; j < inputs.Length; j++)
                {
                    m[i, j] /= worst;
                }
            }
        }

        if (lfe)
        {
            Add(Speakers.LowFrequency, inputs.Length, 1);
        }

        return m;
    }

    /// <summary>Gets the speakers of an audio coding mode's channels in the stream's order (Table 5.8).</summary>
    public static Speakers[] InputSpeakers(int acmod) => acmod switch
    {
        0 or 2 => [Speakers.FrontLeft, Speakers.FrontRight],
        1 => [Speakers.FrontCenter],
        3 => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight],
        4 => [Speakers.FrontLeft, Speakers.FrontRight, Speakers.BackCenter],
        5 => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight, Speakers.BackCenter],
        6 => [Speakers.FrontLeft, Speakers.FrontRight, Speakers.SideLeft, Speakers.SideRight],
        _ => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight, Speakers.SideLeft, Speakers.SideRight],
    };
}
