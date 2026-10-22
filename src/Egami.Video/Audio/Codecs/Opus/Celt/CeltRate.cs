// Translated from libopus 1.5.2 celt/rate.c (clt_compute_allocation and interp_bits2pulses, decoder side).
namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>CELT's bit allocation: splits the frame's bits between the bands' PVQ and fine energy, all in integer eighth bits.</summary>
internal static class CeltRate
{
    private const int AllocSteps = 6;

    private static ReadOnlySpan<byte> Log2FracTable =>
    [
        0, 8, 13, 16, 19, 21, 23, 24, 26, 27, 28, 29, 30, 31, 32, 32, 33, 34, 34, 35, 36, 36, 37, 37,
    ];

    /// <summary>
    /// clt_compute_allocation for the decoder: reads the skip, intensity and dual stereo symbols and fills
    /// <paramref name="pulses"/>, <paramref name="ebits"/> and <paramref name="finePriority"/>; returns the number of coded bands.
    /// </summary>
    public static int ComputeAllocation(int start, int end, ReadOnlySpan<int> offsets, ReadOnlySpan<int> cap, int allocTrim, out int intensity,
        out bool dualStereo, int total, out int balance, Span<int> pulses, Span<int> ebits, Span<int> finePriority, int c, int lm, OpusRangeDecoder dec)
    {
        const int BitRes = CeltTables.BitRes;
        const int Len = CeltTables.NbEBands;
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        ReadOnlySpan<byte> allocVectors = CeltTables.BandAllocation;
        total = Math.Max(total, 0);
        int skipStart = start;
        // Reserve a bit to signal the end of manually skipped bands.
        int skipRsv = total >= 1 << BitRes ? 1 << BitRes : 0;
        total -= skipRsv;
        // Reserve bits for the intensity and dual stereo parameters.
        int intensityRsv = 0;
        int dualStereoRsv = 0;
        if (c == 2)
        {
            intensityRsv = Log2FracTable[end - start];
            if (intensityRsv > total)
            {
                intensityRsv = 0;
            }
            else
            {
                total -= intensityRsv;
                dualStereoRsv = total >= 1 << BitRes ? 1 << BitRes : 0;
                total -= dualStereoRsv;
            }
        }

        Span<int> bits1 = stackalloc int[Len];
        Span<int> bits2 = stackalloc int[Len];
        Span<int> thresh = stackalloc int[Len];
        Span<int> trimOffset = stackalloc int[Len];
        for (int j = start; j < end; j++)
        {
            // Below this threshold, we're sure not to allocate any PVQ bits.
            thresh[j] = Math.Max(c << BitRes, (3 * (eBands[j + 1] - eBands[j]) << lm << BitRes) >> 4);
            // Tilt of the allocation curve.
            trimOffset[j] = c * (eBands[j + 1] - eBands[j]) * (allocTrim - 5 - lm) * (end - j - 1) * (1 << (lm + BitRes)) >> 6;
            // Giving less resolution to single-coefficient bands because they get more benefit from having one coarse value per
            // coefficient.
            if ((eBands[j + 1] - eBands[j]) << lm == 1)
            {
                trimOffset[j] -= c << BitRes;
            }
        }

        int lo = 1;
        int hi = CeltTables.NbAllocVectors - 1;
        do
        {
            bool done = false;
            int psum = 0;
            int mid = (lo + hi) >> 1;
            for (int j = end; j-- > start;)
            {
                int n = eBands[j + 1] - eBands[j];
                int bitsj = c * n * allocVectors[(mid * Len) + j] << lm >> 2;
                if (bitsj > 0)
                {
                    bitsj = Math.Max(0, bitsj + trimOffset[j]);
                }

                bitsj += offsets[j];
                if (bitsj >= thresh[j] || done)
                {
                    done = true;
                    // Don't allocate more than we can actually use.
                    psum += Math.Min(bitsj, cap[j]);
                }
                else if (bitsj >= c << BitRes)
                {
                    psum += c << BitRes;
                }
            }

            if (psum > total)
            {
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        while (lo <= hi);
        hi = lo--;
        for (int j = start; j < end; j++)
        {
            int n = eBands[j + 1] - eBands[j];
            int bits1j = c * n * allocVectors[(lo * Len) + j] << lm >> 2;
            int bits2j = hi >= CeltTables.NbAllocVectors ? cap[j] : c * n * allocVectors[(hi * Len) + j] << lm >> 2;
            if (bits1j > 0)
            {
                bits1j = Math.Max(0, bits1j + trimOffset[j]);
            }

            if (bits2j > 0)
            {
                bits2j = Math.Max(0, bits2j + trimOffset[j]);
            }

            if (lo > 0)
            {
                bits1j += offsets[j];
            }

            bits2j += offsets[j];
            if (offsets[j] > 0)
            {
                skipStart = j;
            }

            bits2j = Math.Max(0, bits2j - bits1j);
            bits1[j] = bits1j;
            bits2[j] = bits2j;
        }

        return InterpBits2Pulses(start, end, skipStart, bits1, bits2, thresh, cap, total, out balance, skipRsv, out intensity, intensityRsv,
            out dualStereo, dualStereoRsv, pulses, ebits, finePriority, c, lm, dec);
    }

    private static int InterpBits2Pulses(int start, int end, int skipStart, ReadOnlySpan<int> bits1, ReadOnlySpan<int> bits2, ReadOnlySpan<int> thresh,
        ReadOnlySpan<int> cap, int total, out int outBalance, int skipRsv, out int intensity, int intensityRsv, out bool dualStereo, int dualStereoRsv,
        Span<int> bits, Span<int> ebits, Span<int> finePriority, int c, int lm, OpusRangeDecoder dec)
    {
        const int BitRes = CeltTables.BitRes;
        ReadOnlySpan<short> eBands = CeltTables.EBands;
        ReadOnlySpan<short> logN = CeltTables.LogN400;
        int allocFloor = c << BitRes;
        int stereo = c > 1 ? 1 : 0;
        int logM = lm << BitRes;
        int lo = 0;
        int hi = 1 << AllocSteps;
        int psum;
        bool done;
        for (int i = 0; i < AllocSteps; i++)
        {
            int mid = (lo + hi) >> 1;
            psum = 0;
            done = false;
            for (int j = end; j-- > start;)
            {
                int tmp = bits1[j] + (mid * bits2[j] >> AllocSteps);
                if (tmp >= thresh[j] || done)
                {
                    done = true;
                    // Don't allocate more than we can actually use.
                    psum += Math.Min(tmp, cap[j]);
                }
                else if (tmp >= allocFloor)
                {
                    psum += allocFloor;
                }
            }

            if (psum > total)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        psum = 0;
        done = false;
        for (int j = end; j-- > start;)
        {
            int tmp = bits1[j] + (lo * bits2[j] >> AllocSteps);
            if (tmp < thresh[j] && !done)
            {
                tmp = tmp >= allocFloor ? allocFloor : 0;
            }
            else
            {
                done = true;
            }

            // Don't allocate more than we can actually use.
            tmp = Math.Min(tmp, cap[j]);
            bits[j] = tmp;
            psum += tmp;
        }

        // Decide which bands to skip, working backwards from the end.
        int codedBands;
        int left;
        int percoeff;
        for (codedBands = end; ; codedBands--)
        {
            int j = codedBands - 1;
            // Never skip the first band, nor a band that has been boosted by dynalloc.
            if (j <= skipStart)
            {
                // Give the bit we reserved to end skipping back.
                total += skipRsv;
                break;
            }

            // Figure out how many left-over bits we would be adding to this band, including bits stolen back from higher,
            // skipped bands.
            left = total - psum;
            percoeff = (int)((uint)left / (uint)(eBands[codedBands] - eBands[start]));
            left -= (eBands[codedBands] - eBands[start]) * percoeff;
            int rem = Math.Max(left - (eBands[j] - eBands[start]), 0);
            int bandWidth = eBands[codedBands] - eBands[j];
            int bandBits = bits[j] + (percoeff * bandWidth) + rem;
            // Only code a skip decision if we're above the threshold for this band; otherwise it is force-skipped.
            if (bandBits >= Math.Max(thresh[j], allocFloor + (1 << BitRes)))
            {
                if (dec.DecodeBitLogp(1))
                {
                    break;
                }

                // We used a bit to skip this band.
                psum += 1 << BitRes;
                bandBits -= 1 << BitRes;
            }

            // Reclaim the bits originally allocated to this band.
            psum -= bits[j] + intensityRsv;
            if (intensityRsv > 0)
            {
                intensityRsv = Log2FracTable[j - start];
            }

            psum += intensityRsv;
            if (bandBits >= allocFloor)
            {
                // If we have enough for a fine energy bit per channel, use it.
                psum += allocFloor;
                bits[j] = allocFloor;
            }
            else
            {
                // Otherwise this band gets nothing at all.
                bits[j] = 0;
            }
        }

        // Code the intensity and dual stereo parameters.
        intensity = intensityRsv > 0 ? start + (int)dec.DecodeUint((uint)(codedBands + 1 - start)) : 0;
        if (intensity <= start)
        {
            total += dualStereoRsv;
            dualStereoRsv = 0;
        }

        dualStereo = dualStereoRsv > 0 && dec.DecodeBitLogp(1);

        // Allocate the remaining bits.
        left = total - psum;
        percoeff = (int)((uint)left / (uint)(eBands[codedBands] - eBands[start]));
        left -= (eBands[codedBands] - eBands[start]) * percoeff;
        for (int j = start; j < codedBands; j++)
        {
            bits[j] += percoeff * (eBands[j + 1] - eBands[j]);
        }

        for (int j = start; j < codedBands; j++)
        {
            int tmp = Math.Min(left, eBands[j + 1] - eBands[j]);
            bits[j] += tmp;
            left -= tmp;
        }

        int balance = 0;
        int jj;
        for (jj = start; jj < codedBands; jj++)
        {
            int n0 = eBands[jj + 1] - eBands[jj];
            int n = n0 << lm;
            int bit = bits[jj] + balance;
            int excess;
            if (n > 1)
            {
                excess = Math.Max(bit - cap[jj], 0);
                bits[jj] = bit - excess;
                // Compensate for the extra DoF in stereo.
                int den = (c * n) + (c == 2 && n > 2 && !dualStereo && jj < intensity ? 1 : 0);
                int nClogN = den * (logN[jj] + logM);
                // Offset for the number of fine bits by log2(N)/2 + FINE_OFFSET compared to their "fair share" of total/N.
                int offset = (nClogN >> 1) - (den * CeltTables.FineOffset);
                // N=2 is the only point that doesn't match the curve.
                if (n == 2)
                {
                    offset += den << BitRes >> 2;
                }

                // Changing the offset for allocating the second and third fine energy bit.
                if (bits[jj] + offset < den * 2 << BitRes)
                {
                    offset += nClogN >> 2;
                }
                else if (bits[jj] + offset < den * 3 << BitRes)
                {
                    offset += nClogN >> 3;
                }

                // Divide with rounding.
                ebits[jj] = Math.Max(0, bits[jj] + offset + (den << (BitRes - 1)));
                ebits[jj] = (int)((uint)ebits[jj] / (uint)den) >> BitRes;
                // Make sure not to bust.
                if (c * ebits[jj] > (bits[jj] >> BitRes))
                {
                    ebits[jj] = bits[jj] >> stereo >> BitRes;
                }

                // More than that is useless because that's about as far as PVQ can go.
                ebits[jj] = Math.Min(ebits[jj], CeltTables.MaxFineBits);
                // If we rounded down or capped this band, make it a candidate for the final fine energy pass.
                finePriority[jj] = ebits[jj] * (den << BitRes) >= bits[jj] + offset ? 1 : 0;
                // Remove the allocated fine bits; the rest are assigned to PVQ.
                bits[jj] -= c * ebits[jj] << BitRes;
            }
            else
            {
                // For N=1, all bits go to fine energy except for a single sign bit.
                excess = Math.Max(0, bit - (c << BitRes));
                bits[jj] = bit - excess;
                ebits[jj] = 0;
                finePriority[jj] = 1;
            }

            // Fine energy can't take advantage of the re-balancing in quant_all_bands(); do it here instead.
            if (excess > 0)
            {
                int extraFine = Math.Min(excess >> (stereo + BitRes), CeltTables.MaxFineBits - ebits[jj]);
                ebits[jj] += extraFine;
                int extraBits = extraFine * c << BitRes;
                finePriority[jj] = extraBits >= excess - balance ? 1 : 0;
                excess -= extraBits;
            }

            balance = excess;
        }

        // Save any remaining bits over the cap for the rebalancing in quant_all_bands().
        outBalance = balance;
        // The skipped bands use all their bits for fine energy.
        for (; jj < end; jj++)
        {
            ebits[jj] = bits[jj] >> stereo >> BitRes;
            bits[jj] = 0;
            finePriority[jj] = ebits[jj] < 1 ? 1 : 0;
        }

        return codedBands;
    }
}
