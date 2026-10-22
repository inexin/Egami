using System.Buffers;

namespace Egami.Formats.Webp;

/// <summary>
/// Token recording: every coefficient token of the frame is kept (with its probability slot or fixed probability) so the
/// probabilities, and whether to code skipped macroblocks, can be chosen once the whole frame is known.
/// </summary>
internal sealed partial class Vp8Encoder
{
    // A token is its bit (bit 15) and its probability slot; slots from here on are the fixed probabilities 0 to 255.
    private const int FixedProbability = Vp8Costs.ProbabilityCount;

    private ushort[] _tokens = [];
    private int _tokenCount;
    private readonly int[] _tokenStart;

    // Bit statistics per probability slot: all tokens, and the tokens of all-zero macroblocks alone.
    private readonly int[] _totals = new int[Vp8Costs.ProbabilityCount];
    private readonly int[] _ones = new int[Vp8Costs.ProbabilityCount];
    private readonly int[] _zeroTotals = new int[Vp8Costs.ProbabilityCount];
    private readonly int[] _zeroOnes = new int[Vp8Costs.ProbabilityCount];
    private bool _recordingZero;

    private readonly byte[] _finalProbs = new byte[Vp8Costs.ProbabilityCount];
    private bool _useSkip;
    private int _skipProb;

    private void ResetTokens()
    {
        if (_tokens.Length == 0)
        {
            _tokens = ArrayPool<ushort>.Shared.Rent(Math.Clamp(_mbw * _mbh * 64, 4096, 1 << 22));
        }

        _tokenCount = 0;
        Array.Clear(_totals);
        Array.Clear(_ones);
        Array.Clear(_zeroTotals);
        Array.Clear(_zeroOnes);
    }

    /// <summary>Records the macroblock's tokens with the committed non-zero contexts, and updates them.</summary>
    private void RecordMacroblock(int mbx, int mb)
    {
        _tokenStart[mb] = _tokenCount;
        _recordingZero = _zero[mb];
        ModeScore rd = _rd;
        Span<byte> top = _topNz.AsSpan(mbx * 9, 9);
        Span<byte> left = _leftNz;
        int type, first;
        if (_isI16[mb])
        {
            top[8] = left[8] = RecordBlock(top[8] + left[8], 1, 0, rd.YDc);
            type = 0;
            first = 1;
        }
        else
        {
            type = 3;
            first = 0;
        }

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                top[x] = left[y] = RecordBlock(top[x] + left[y], type, first, rd.YAc.AsSpan(((y * 4) + x) * 16, 16));
            }
        }

        for (int ch = 0; ch <= 2; ch += 2)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    int ctx = top[4 + ch + x] + left[4 + ch + y];
                    top[4 + ch + x] = left[4 + ch + y] = RecordBlock(ctx, 2, 0, rd.UvLevels.AsSpan(((ch * 2) + (y * 2) + x) * 16, 16));
                }
            }
        }
    }

    /// <summary>Records one block's tokens as the decoder will read them; returns 1 if it has a non-zero level.</summary>
    private byte RecordBlock(int ctx, int type, int first, ReadOnlySpan<short> levels)
    {
        int last = Vp8LevelCosts.LastNonZero(levels, first);
        ReadOnlySpan<byte> bands = Vp8Decoder.Bands;
        int n = first;
        int p = Slot(type, bands[n], ctx);
        if (!Token(last >= 0, p))
        {
            return 0;
        }

        while (n < 16)
        {
            int c = levels[n++];
            int v = Math.Abs(c);
            if (!Token(v != 0, p + 1))
            {
                p = Slot(type, bands[n], 0);
                continue;
            }

            if (!Token(v > 1, p + 2))
            {
                p = Slot(type, bands[n], 1);
            }
            else
            {
                if (!Token(v > 4, p + 3))
                {
                    if (Token(v != 2, p + 4))
                    {
                        Token(v == 4, p + 5);
                    }
                }
                else if (!Token(v > 10, p + 6))
                {
                    if (!Token(v > 6, p + 7))
                    {
                        Fixed(v == 6, 159);
                    }
                    else
                    {
                        Fixed(v >= 9, 165);
                        Fixed((v & 1) == 0, 145);
                    }
                }
                else
                {
                    ReadOnlySpan<byte> cat;
                    if (v < 19)
                    {
                        Token(false, p + 8);
                        Token(false, p + 9);
                        v -= 11;
                        cat = Vp8Decoder.Cat3;
                    }
                    else if (v < 35)
                    {
                        Token(false, p + 8);
                        Token(true, p + 9);
                        v -= 19;
                        cat = Vp8Decoder.Cat4;
                    }
                    else if (v < 67)
                    {
                        Token(true, p + 8);
                        Token(false, p + 10);
                        v -= 35;
                        cat = Vp8Decoder.Cat5;
                    }
                    else
                    {
                        Token(true, p + 8);
                        Token(true, p + 10);
                        v -= 67;
                        cat = Vp8Decoder.Cat6;
                    }

                    for (int i = 0; i < cat.Length; i++)
                    {
                        Fixed(((v >> (cat.Length - 1 - i)) & 1) != 0, cat[i]);
                    }
                }

                p = Slot(type, bands[n], 2);
            }

            Fixed(c < 0, 128);
            if (n == 16 || !Token(n <= last, p))
            {
                return 1;
            }
        }

        return 1;
    }

    private static int Slot(int type, int band, int ctx) => (((type * 8) + band) * 3 + ctx) * 11;

    private bool Token(bool bit, int slot)
    {
        Append((ushort)((bit ? 0x8000 : 0) | slot));
        int one = bit ? 1 : 0;
        _totals[slot]++;
        _ones[slot] += one;
        if (_recordingZero)
        {
            _zeroTotals[slot]++;
            _zeroOnes[slot] += one;
        }

        return bit;
    }

    private void Fixed(bool bit, int probability) => Append((ushort)((bit ? 0x8000 : 0) | (FixedProbability + probability)));

    private void Append(ushort token)
    {
        if (_tokenCount == _tokens.Length)
        {
            ushort[] larger = ArrayPool<ushort>.Shared.Rent(_tokens.Length * 2);
            _tokens.AsSpan(0, _tokenCount).CopyTo(larger);
            ArrayPool<ushort>.Shared.Return(_tokens);
            _tokens = larger;
        }

        _tokens[_tokenCount++] = token;
    }

    /// <summary>Refreshes the rate estimates from the statistics so far.</summary>
    private void RunningProbabilities()
    {
        Span<byte> probs = stackalloc byte[Vp8Costs.ProbabilityCount];
        ChooseProbabilities(_totals, _ones, null, probs);
        _costs.Update(probs);
    }

    /// <summary>
    /// Chooses each probability (updated where that saves bits) for the given statistics; returns the bits spent on the tokens
    /// and the updates, in 1/256 bit.
    /// </summary>
    private static long ChooseProbabilities(int[] totals, int[] ones, (int[] Totals, int[] Ones)? minus, Span<byte> probs)
    {
        ReadOnlySpan<byte> defaults = Vp8Tables.DefaultCoeffProbs;
        ReadOnlySpan<byte> update = Vp8Tables.CoeffUpdateProbs;
        long size = 0;
        for (int i = 0; i < Vp8Costs.ProbabilityCount; i++)
        {
            int total = totals[i], nb = ones[i];
            if (minus is { } m)
            {
                total -= m.Totals[i];
                nb -= m.Ones[i];
            }

            int oldP = defaults[i];
            int newP = nb != 0 ? 255 - (int)((long)nb * 255 / total) : 255;
            long oldCost = Branch(nb, total, oldP) + Vp8Costs.Bit(0, update[i]);
            long newCost = Branch(nb, total, newP) + Vp8Costs.Bit(1, update[i]) + (8 * 256);
            if (oldCost > newCost)
            {
                probs[i] = (byte)newP;
                size += newCost;
            }
            else
            {
                probs[i] = (byte)oldP;
                size += oldCost;
            }
        }

        return size;

        static long Branch(int nb, int total, int p) => ((long)nb * Vp8Costs.Bit(1, p)) + ((long)(total - nb) * Vp8Costs.Bit(0, p));
    }

    /// <summary>Chooses the final probabilities, and whether all-zero macroblocks are coded with a skip flag or with tokens.</summary>
    private void FinalizeProbabilities()
    {
        int count = _mbw * _mbh;
        int zero = 0;
        foreach (bool z in _zero)
        {
            zero += z ? 1 : 0;
        }

        Span<byte> withSkip = stackalloc byte[Vp8Costs.ProbabilityCount];
        long costWithout = ChooseProbabilities(_totals, _ones, null, _finalProbs);
        _useSkip = false;
        if (zero > 0)
        {
            int skipProb = Math.Max(1, (int)((long)(count - zero) * 255 / count));
            long costWith = ChooseProbabilities(_totals, _ones, (_zeroTotals, _zeroOnes), withSkip)
                + (8 * 256)
                + ((long)zero * Vp8Costs.Bit(1, skipProb))
                + ((long)(count - zero) * Vp8Costs.Bit(0, skipProb));
            if (costWith < costWithout)
            {
                _useSkip = true;
                _skipProb = skipProb;
                withSkip.CopyTo(_finalProbs);
            }
        }
    }

    /// <summary>Writes the recorded tokens with the final probabilities, leaving out skipped macroblocks, row by row into the partitions.</summary>
    private Vp8BoolEncoder[] EmitTokens()
    {
        int count = PartitionCount;
        Vp8BoolEncoder[] partitions = new Vp8BoolEncoder[count];
        for (int i = 0; i < count; i++)
        {
            partitions[i] = new Vp8BoolEncoder(Math.Max(4096, _tokenCount / (4 * count)));
        }

        Span<byte> probs = stackalloc byte[FixedProbability + 256];
        _finalProbs.CopyTo(probs);
        for (int p = 0; p < 256; p++)
        {
            probs[FixedProbability + p] = (byte)p;
        }

        ReadOnlySpan<ushort> tokens = _tokens;
        for (int mby = 0; mby < _mbh; mby++)
        {
            Vp8BoolEncoder bw = partitions[mby & (count - 1)];
            for (int mb = mby * _mbw, rowEnd = mb + _mbw; mb < rowEnd; mb++)
            {
                // Runs of coded macroblocks go out together.
                int end = mb;
                while (end < rowEnd && !(_useSkip && _zero[end]))
                {
                    end++;
                }

                bw.PutBits(tokens[_tokenStart[mb].._tokenStart[end]], probs);
                mb = end;
            }
        }

        return partitions;
    }

    /// <summary>Returns the token buffer to the pool.</summary>
    private void ReleaseTokens()
    {
        if (_tokens.Length > 0)
        {
            ArrayPool<ushort>.Shared.Return(_tokens);
            _tokens = [];
        }
    }
}
