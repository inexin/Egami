namespace Egami.Video.Audio.Codecs.Alac;

/// <summary>
/// ALAC's adaptive linear predictor, translated from Apple's dp_enc.c (pc_block) and dp_dec.c (unpc_block), Apache-2.0: a
/// prediction from the last <c>numActive</c> samples relative to the oldest, whose coefficients follow the sign of each
/// residual. Both directions update the coefficients the same way, so the decoder tracks the encoder.
/// </summary>
internal static class AlacPredictor
{
    /// <summary>The coefficient scale the encoder always uses.</summary>
    public const int DenShiftDefault = 9;

    /// <summary>The coefficients the encoder starts every channel with (init_coefs at <see cref="DenShiftDefault"/>).</summary>
    public static void InitCoefficients(Span<short> coefs)
    {
        const int den = 1 << DenShiftDefault;
        coefs.Clear();
        coefs[0] = (short)((38 * den) >> 4);
        coefs[1] = (short)((-29 * den) >> 4);
        coefs[2] = (short)((-2 * den) >> 4);
    }

    /// <summary>
    /// Turns residuals back into samples (unpc_block). <paramref name="residuals"/> and <paramref name="output"/> may be the
    /// same array when <paramref name="numActive"/> is 31 (first differences, no coefficients). Both arrays must hold at least
    /// <paramref name="numActive"/> + 1 values even when <paramref name="count"/> is smaller, as Apple's buffers do.
    /// </summary>
    public static void Unpredict(int[] residuals, int[] output, int count, Span<short> coefs, int numActive, int chanBits, int denShift)
    {
        int chanShift = 32 - chanBits;
        int denHalf = 1 << (denShift - 1);
        output[0] = residuals[0];
        if (numActive == 0)
        {
            if (count > 1 && residuals != output)
            {
                residuals.AsSpan(1, count - 1).CopyTo(output.AsSpan(1));
            }

            return;
        }

        if (numActive == 31)
        {
            int prev = output[0];
            for (int j = 1; j < count; j++)
            {
                int d = residuals[j] + prev;
                prev = (d << chanShift) >> chanShift;
                output[j] = prev;
            }

            return;
        }

        for (int j = 1; j <= numActive; j++)
        {
            int d = residuals[j] + output[j - 1];
            output[j] = (d << chanShift) >> chanShift;
        }

        int lim = numActive + 1;
        for (int j = lim; j < count; j++)
        {
            int top = output[j - lim];
            int last = j - 1;
            int sum = 0;
            for (int k = 0; k < numActive; k++)
            {
                sum += coefs[k] * (output[last - k] - top);
            }

            int del = residuals[j];
            int del0 = del;
            int sg = Sign(del);
            del += top + ((sum + denHalf) >> denShift);
            output[j] = (del << chanShift) >> chanShift;
            if (sg > 0)
            {
                for (int k = numActive - 1; k >= 0; k--)
                {
                    int dd = top - output[last - k];
                    int sgn = Sign(dd);
                    coefs[k] = (short)(coefs[k] - sgn);
                    del0 -= (numActive - k) * ((sgn * dd) >> denShift);
                    if (del0 <= 0)
                    {
                        break;
                    }
                }
            }
            else if (sg < 0)
            {
                for (int k = numActive - 1; k >= 0; k--)
                {
                    int dd = top - output[last - k];
                    int sgn = Sign(dd);
                    coefs[k] = (short)(coefs[k] + sgn);
                    del0 -= (numActive - k) * ((-sgn * dd) >> denShift);
                    if (del0 >= 0)
                    {
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Turns samples into residuals (pc_block), adapting the coefficients. Both arrays must hold at least
    /// <paramref name="numActive"/> + 1 values even when <paramref name="count"/> is smaller, as Apple's buffers do.
    /// </summary>
    public static void Predict(int[] input, int[] residuals, int count, Span<short> coefs, int numActive, int chanBits, int denShift)
    {
        int chanShift = 32 - chanBits;
        int denHalf = 1 << (denShift - 1);
        residuals[0] = input[0];
        if (numActive == 0)
        {
            if (count > 1 && input != residuals)
            {
                input.AsSpan(1, count - 1).CopyTo(residuals.AsSpan(1));
            }

            return;
        }

        if (numActive == 31)
        {
            for (int j = 1; j < count; j++)
            {
                int d = input[j] - input[j - 1];
                residuals[j] = (d << chanShift) >> chanShift;
            }

            return;
        }

        for (int j = 1; j <= numActive; j++)
        {
            int d = input[j] - input[j - 1];
            residuals[j] = (d << chanShift) >> chanShift;
        }

        int lim = numActive + 1;
        if (numActive == 4)
        {
            Predict4(input, residuals, count, coefs, chanShift, denShift, denHalf);
            return;
        }

        if (numActive == 8)
        {
            Predict8(input, residuals, count, coefs, chanShift, denShift, denHalf);
            return;
        }

        for (int j = lim; j < count; j++)
        {
            int top = input[j - lim];
            int last = j - 1;
            int sum = 0;
            for (int k = 0; k < numActive; k++)
            {
                sum -= coefs[k] * (top - input[last - k]);
            }

            int del = input[j] - top - ((sum + denHalf) >> denShift);
            del = (del << chanShift) >> chanShift;
            residuals[j] = del;
            int del0 = del;
            int sg = Sign(del);
            if (sg > 0)
            {
                for (int k = numActive - 1; k >= 0; k--)
                {
                    int dd = top - input[last - k];
                    int sgn = Sign(dd);
                    coefs[k] = (short)(coefs[k] - sgn);
                    del0 -= (numActive - k) * ((sgn * dd) >> denShift);
                    if (del0 <= 0)
                    {
                        break;
                    }
                }
            }
            else if (sg < 0)
            {
                for (int k = numActive - 1; k >= 0; k--)
                {
                    int dd = top - input[last - k];
                    int sgn = Sign(dd);
                    coefs[k] = (short)(coefs[k] + sgn);
                    del0 -= (numActive - k) * ((-sgn * dd) >> denShift);
                    if (del0 >= 0)
                    {
                        break;
                    }
                }
            }
        }
    }

    /// <summary>The sign of a value: -1, 0 or 1 (sign_of_int).</summary>
    private static int Sign(int i) => (int)((uint)-i >> 31) | (i >> 31);

    /// <summary>Predict with four coefficients held in locals, the encoder's most common case.</summary>
    private static void Predict4(int[] input, int[] residuals, int count, Span<short> coefs, int chanShift, int denShift, int denHalf)
    {
        short a0 = coefs[0], a1 = coefs[1], a2 = coefs[2], a3 = coefs[3];
        for (int j = 5; j < count; j++)
        {
            int top = input[j - 5];
            int b0 = top - input[j - 1];
            int b1 = top - input[j - 2];
            int b2 = top - input[j - 3];
            int b3 = top - input[j - 4];
            int sum = (denHalf - (a0 * b0) - (a1 * b1) - (a2 * b2) - (a3 * b3)) >> denShift;
            int del = input[j] - top - sum;
            del = (del << chanShift) >> chanShift;
            residuals[j] = del;
            int del0 = del;
            if (del > 0)
            {
                int sgn = Sign(b3);
                a3 = (short)(a3 - sgn);
                del0 -= (sgn * b3) >> denShift;
                if (del0 <= 0)
                {
                    continue;
                }

                sgn = Sign(b2);
                a2 = (short)(a2 - sgn);
                del0 -= 2 * ((sgn * b2) >> denShift);
                if (del0 <= 0)
                {
                    continue;
                }

                sgn = Sign(b1);
                a1 = (short)(a1 - sgn);
                del0 -= 3 * ((sgn * b1) >> denShift);
                if (del0 <= 0)
                {
                    continue;
                }

                a0 = (short)(a0 - Sign(b0));
            }
            else if (del < 0)
            {
                int sgn = -Sign(b3);
                a3 = (short)(a3 - sgn);
                del0 -= (sgn * b3) >> denShift;
                if (del0 >= 0)
                {
                    continue;
                }

                sgn = -Sign(b2);
                a2 = (short)(a2 - sgn);
                del0 -= 2 * ((sgn * b2) >> denShift);
                if (del0 >= 0)
                {
                    continue;
                }

                sgn = -Sign(b1);
                a1 = (short)(a1 - sgn);
                del0 -= 3 * ((sgn * b1) >> denShift);
                if (del0 >= 0)
                {
                    continue;
                }

                a0 = (short)(a0 + Sign(b0));
            }
        }

        coefs[0] = a0;
        coefs[1] = a1;
        coefs[2] = a2;
        coefs[3] = a3;
    }

    /// <summary>Predict with eight coefficients, as <see cref="Predict4"/>.</summary>
    private static void Predict8(int[] input, int[] residuals, int count, Span<short> coefs, int chanShift, int denShift, int denHalf)
    {
        Span<short> a = stackalloc short[8];
        Span<int> b = stackalloc int[8];
        coefs[..8].CopyTo(a);
        for (int j = 9; j < count; j++)
        {
            int top = input[j - 9];
            int sum = denHalf;
            for (int k = 0; k < 8; k++)
            {
                b[k] = top - input[j - 1 - k];
                sum -= a[k] * b[k];
            }

            sum >>= denShift;
            int del = input[j] - top - sum;
            del = (del << chanShift) >> chanShift;
            residuals[j] = del;
            int del0 = del;
            if (del > 0)
            {
                for (int k = 7; k > 0; k--)
                {
                    int sgn = Sign(b[k]);
                    a[k] = (short)(a[k] - sgn);
                    del0 -= (8 - k) * ((sgn * b[k]) >> denShift);
                    if (del0 <= 0)
                    {
                        goto Next;
                    }
                }

                a[0] = (short)(a[0] - Sign(b[0]));
            }
            else if (del < 0)
            {
                for (int k = 7; k > 0; k--)
                {
                    int sgn = -Sign(b[k]);
                    a[k] = (short)(a[k] - sgn);
                    del0 -= (8 - k) * ((sgn * b[k]) >> denShift);
                    if (del0 >= 0)
                    {
                        goto Next;
                    }
                }

                a[0] = (short)(a[0] + Sign(b[0]));
            }

        Next:
            ;
        }

        a.CopyTo(coefs);
    }
}
