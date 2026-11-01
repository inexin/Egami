using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>
/// Rate-distortion optimized quantization (Karczewicz et al., "Rate distortion optimized quantization", VCEG-AH21, as the JCT-VC
/// encoder description applies it to HEVC): each level, in the order residual coding codes them, is the rounded level, one less
/// or zero, whichever gives the least distortion plus lambda times its bins at the contexts and Rice state the coding will be in;
/// then whole sub-blocks are dropped where coding them costs more than they give, and the last position is moved down where
/// that pays.
/// </summary>
internal sealed partial class HevcCtuSearch
{
    private readonly double[] _rdZero = new double[32 * 32];
    private readonly double[] _rdChosen = new double[32 * 32];
    private readonly double[] _rdSig = new double[32 * 32];
    private readonly double[] _rdDistortion = new double[32 * 32];
    private readonly int[] _rdRaster = new int[32 * 32];
    private readonly double[] _lastCostX = new double[32];
    private readonly double[] _lastCostY = new double[32];

    /// <summary>The raster position of each position in scan order (sub-blocks, then positions in them), by scanIdx and log2.</summary>
    private static readonly int[][] ScanRasters = MakeScanRasters();

    private static int[][] MakeScanRasters()
    {
        int[][] tables = new int[3 * 6][];
        for (int scanIdx = 0; scanIdx < 3; scanIdx++)
        {
            for (int log2 = 2; log2 <= 5; log2++)
            {
                byte[] inner = HevcScan.Get(scanIdx, 2), outer = HevcScan.Get(scanIdx, log2 - 2);
                int n = 1 << log2;
                int[] table = new int[n * n];
                for (int i = 0; i < n * n / 16; i++)
                {
                    for (int p = 0; p < 16; p++)
                    {
                        table[(i << 4) + p] = ((((outer[(2 * i) + 1] << 2) + inner[(2 * p) + 1])) * n) + (outer[2 * i] << 2) + inner[2 * p];
                    }
                }

                tables[(scanIdx * 6) + log2] = table;
            }
        }

        return tables;
    }

    /// <summary>Quantizes a block's coefficients for the least J; returns the number of levels that are not zero.</summary>
    /// <remarks>
    /// <paramref name="levels"/> is all zeros on entry; only the sub-blocks up to the last level's are written, and
    /// <paramref name="scanEnd"/> gives how far, in scan order.
    /// </remarks>
    /// <remarks>
    /// <paramref name="bits"/> gives the block's rate as RDOQ counts it (1/32768 bits, without coded_sub_block_flag), which the
    /// search uses instead of counting the residual's bins again.
    /// </remarks>
    private int Rdoq(int[] coefficients, short[] levels, int log2, int c, int scanIdx, int qp, int bitDepth, double lambda, out int scanEnd, out long bits)
    {
        int n = 1 << log2, count = n * n;
        int scale = HevcForward.QuantScale[qp % 6];
        int shift = HevcForward.QuantShift(qp, log2, bitDepth);
        double unit = 1L << shift;
        int ts = 15 - bitDepth - log2;
        double errScale = Math.ScaleB(1.0 / ((double)scale * scale), -2 * ts);
        double bitCost = lambda / CabacCost.Bit;
        byte[] contexts = _bins.Contexts;

        byte[] inner = HevcScan.Get(scanIdx, 2);
        int log2Sb = log2 - 2;
        byte[] outer = HevcScan.Get(scanIdx, log2Sb);
        int sbWidth = 1 << log2Sb, subBlocks = 1 << (2 * log2Sb);

        // The last position, in scan order, that rounds to a level.
        int[] rasters = ScanRasters[(scanIdx * 6) + log2];
        long half = 1L << (shift - 1);
        long threshold = ((1L << shift) - half + scale - 1) / scale;
        int last = -1;
        for (int s = count - 1; s >= 0; s--)
        {
            if (Math.Abs((long)coefficients[rasters[s]]) >= threshold)
            {
                last = s;
                break;
            }
        }

        scanEnd = (last + 16) & ~15;
        bits = 0;
        if (last < 0)
        {
            scanEnd = 0;
            return 0;
        }

        // The rate of each last position's x and y.
        LastPositionCosts(log2, c, bitCost);

        int sigBase = HevcContexts.SigCoeff;
        int table = (((((c == 0 ? 0 : 1) * 4) + log2 - 2) * 3) + scanIdx) * 4;
        byte[] offsets = HevcScan.Offsets(scanIdx, log2);
        ulong codedSubBlock = 0;
        int greater1State = 1;
        double uncoded = 0;

        int lastSubBlock = last >> 4;
        for (int i = lastSubBlock; i >= 0; i--)
        {
            int xS = outer[2 * i], yS = outer[(2 * i) + 1];
            bool right = xS + 1 < sbWidth && (codedSubBlock & (1UL << ((yS * sbWidth) + xS + 1))) != 0;
            bool below = yS + 1 < sbWidth && (codedSubBlock & (1UL << (((yS + 1) * sbWidth) + xS))) != 0;
            byte[] sigTable = HevcSliceDecoder.SigContexts[table + (right ? 1 : 0) + (below ? 2 : 0)];
            int corner = (yS << (2 + log2)) + (xS << 2);
            int ctxSet = (i == 0 || c > 0 ? 0 : 2) + (greater1State == 0 ? 1 : 0);
            int g1Base = HevcContexts.Greater1 + (ctxSet * 4) + (c > 0 ? 16 : 0);
            int g2 = HevcContexts.Greater2 + ctxSet + (c > 0 ? 4 : 0);
            int state = 1, k = 0, rice = 0;
            bool firstCoeff2 = true;
            double codedCost = 0, zeroCost = 0;
            int nonZero = 0;
            int top = i == lastSubBlock ? last & 15 : 15;
            for (int p = top; p >= 0; p--)
            {
                int s = (i << 4) + p;
                int raster = rasters[s];
                _rdRaster[s] = raster;
                long exact = Math.Abs((long)coefficients[raster]) * scale;
                double zero = exact * (double)exact * errScale;
                _rdZero[s] = zero;
                uncoded += zero;
                int maxAbs = (int)Math.Min((exact + half) >> shift, short.MaxValue);
                bool isLast = s == last;
                byte sigState = contexts[sigBase + (p == 0 ? sigTable[corner] : sigTable[corner + offsets[p]])];
                double sig0 = isLast ? 0 : CabacCost.Of(sigState, 0) * bitCost;
                int bestLevel = 0;
                double best = isLast ? double.MaxValue : zero + sig0;
                double bestSig = sig0, bestDistortion = zero;
                if (maxAbs > 0)
                {
                    double sig1 = isLast ? 0 : CabacCost.Of(sigState, 1) * bitCost;
                    for (int level = Math.Max(maxAbs - 1, 1); level <= maxAbs; level++)
                    {
                        double error = exact - (level * unit);
                        double distortion = error * error * errScale;
                        double cost = distortion + sig1 + (LevelRate(level, k, state, firstCoeff2, rice, contexts, g1Base, g2) * bitCost);
                        if (cost < best)
                        {
                            (best, bestLevel, bestSig, bestDistortion) = (cost, level, sig1, distortion);
                        }
                    }
                }

                _rdChosen[s] = best;
                _rdSig[s] = bestSig;
                _rdDistortion[s] = bestDistortion;
                levels[raster] = (short)(coefficients[raster] < 0 ? -bestLevel : bestLevel);
                codedCost += best;
                zeroCost += zero;
                if (bestLevel != 0)
                {
                    nonZero++;

                    // The coding's states after the level, as residual coding moves them.
                    int baseLevel = k < 8 ? (firstCoeff2 ? 3 : 2) : 1;
                    if (k < 8)
                    {
                        if (bestLevel > 1)
                        {
                            state = 0;
                        }
                        else if (state is > 0 and < 3)
                        {
                            state++;
                        }
                    }

                    if (bestLevel >= baseLevel && bestLevel > 3 << rice)
                    {
                        rice = Math.Min(rice + 1, 4);
                    }

                    if (bestLevel >= 2)
                    {
                        firstCoeff2 = false;
                    }

                    k++;
                }
            }

            // A sub-block between the first and the last may be dropped whole (coded_sub_block_flag 0).
            if (i < lastSubBlock && i > 0 && nonZero > 0)
            {
                int flag = HevcContexts.CodedSubBlock + Math.Min((right ? 1 : 0) + (below ? 1 : 0), 1) + (c == 0 ? 0 : 2);
                double keep = codedCost + (CabacCost.Of(contexts[flag], 1) * bitCost);
                double drop = zeroCost + (CabacCost.Of(contexts[flag], 0) * bitCost);
                if (drop <= keep)
                {
                    for (int p = 0; p < 16; p++)
                    {
                        int s = (i << 4) + p;
                        levels[_rdRaster[s]] = 0;
                        _rdChosen[s] = _rdZero[s];
                        _rdDistortion[s] = _rdZero[s];
                        _rdSig[s] = 0;
                    }

                    nonZero = 0;
                }
            }

            if (nonZero > 0)
            {
                codedSubBlock |= 1UL << ((yS * sbWidth) + xS);
                greater1State = state;
            }
        }

        // The last position: the one with the least total, everything after it uncoded.
        double below0 = 0;
        int bestLast = -1;
        double bestTotal = uncoded;
        double prefix = 0;
        for (int s = 0; s <= last; s++)
        {
            int raster = _rdRaster[s];
            if (levels[raster] != 0)
            {
                int x = raster & (n - 1), y = raster >> log2;
                if (scanIdx == 2)
                {
                    (x, y) = (y, x);
                }

                double total = prefix + (_rdChosen[s] - _rdSig[s]) + _lastCostX[x] + _lastCostY[y] + (uncoded - below0 - _rdZero[s]);
                if (total < bestTotal)
                {
                    (bestTotal, bestLast) = (total, s);
                }
            }

            prefix += _rdChosen[s];
            below0 += _rdZero[s];
        }

        int result = 0;
        double distortionTotal = 0;
        for (int s = 0; s <= last; s++)
        {
            int raster = _rdRaster[s];
            if (s > bestLast)
            {
                levels[raster] = 0;
                distortionTotal += _rdZero[s];
            }
            else
            {
                distortionTotal += _rdDistortion[s];
                result += levels[raster] != 0 ? 1 : 0;
            }
        }

        bits = result == 0 ? 0 : (long)((bestTotal - distortionTotal) / bitCost);
        return result;
    }

    /// <summary>The bins of a level after its significance: greater1 and greater2 flags, coeff_abs_level_remaining and the sign.</summary>
    private static int LevelRate(int level, int k, int state, bool firstCoeff2, int rice, byte[] contexts, int g1Base, int g2)
    {
        int bits = CabacCost.Bit;
        if (k < 8)
        {
            bits += CabacCost.Of(contexts[g1Base + state], level > 1 ? 1 : 0);
            if (level > 1 && firstCoeff2)
            {
                bits += CabacCost.Of(contexts[g2], level > 2 ? 1 : 0);
            }

            int baseLevel = firstCoeff2 ? 3 : 2;
            if (level >= baseLevel)
            {
                bits += HevcSyntaxWriter.RemainingLength(level - baseLevel, rice) * CabacCost.Bit;
            }
        }
        else
        {
            bits += HevcSyntaxWriter.RemainingLength(level - 1, rice) * CabacCost.Bit;
        }

        return bits;
    }

    /// <summary>The cost, in distortion units, of each value of last_sig_coeff_x and _y: prefix bins at their contexts, suffix bits.</summary>
    private void LastPositionCosts(int log2, int c, double bitCost)
    {
        int ctxOffset, ctxShift;
        if (c == 0)
        {
            ctxOffset = (3 * (log2 - 2)) + ((log2 - 1) >> 2);
            ctxShift = (log2 + 1) >> 2;
        }
        else
        {
            ctxOffset = 15;
            ctxShift = log2 - 2;
        }

        int maxPrefix = (log2 << 1) - 1;
        byte[] contexts = _bins.Contexts;
        for (int position = 0; position < 1 << log2; position++)
        {
            int prefix = HevcSyntaxWriter.Prefix(position, out _, out int bits);
            long x = (long)bits * CabacCost.Bit, y = x;
            for (int i = 0; i < prefix; i++)
            {
                x += CabacCost.Of(contexts[HevcContexts.LastXPrefix + ctxOffset + (i >> ctxShift)], 1);
                y += CabacCost.Of(contexts[HevcContexts.LastYPrefix + ctxOffset + (i >> ctxShift)], 1);
            }

            if (prefix < maxPrefix)
            {
                x += CabacCost.Of(contexts[HevcContexts.LastXPrefix + ctxOffset + (prefix >> ctxShift)], 0);
                y += CabacCost.Of(contexts[HevcContexts.LastYPrefix + ctxOffset + (prefix >> ctxShift)], 0);
            }

            _lastCostX[position] = x * bitCost;
            _lastCostY[position] = y * bitCost;
        }
    }
}
