namespace Egami.Formats.Av1;

/// <summary>
/// Chooses a key frame's in-loop filters once its blocks are decided: the deblocking levels (from the quantizer alone at efforts
/// 4 and below, as libaom's faster speeds pick them), then CDEF strengths for each 64x64 unit, each tried on the frame with the
/// decoder's own filters (<see cref="Av1LoopFilter"/>, <see cref="Av1Cdef"/>) and kept by the squared error it leaves, CDEF's
/// index bits counted at the frame's lambda. Leaves the frame filtered and its header, and the parameters to write, set to the
/// choice.
/// </summary>
internal static class Av1FilterSearch
{
    /// <summary>The CDEF primary and secondary strengths each effort tries, as (primary, secondary) pairs; secondary is 0, 1, 2 or 4.</summary>
    private static readonly (int Pri, int Sec)[][] CdefSets =
    [
        [(0, 0), (2, 0), (4, 1), (7, 2)],
        [(0, 0), (1, 0), (2, 1), (4, 1), (6, 2), (8, 2), (11, 4), (15, 4)],
        [(0, 0), (1, 0), (2, 0), (2, 1), (3, 1), (4, 1), (5, 2), (6, 2), (8, 2), (9, 4), (11, 4), (13, 4), (15, 4), (4, 0), (8, 0), (12, 2)],
    ];

    /// <summary>Searches and applies the filters.</summary>
    internal static void Search(Av1Frame frame, Av1EncoderSource source, Av1SequenceHeader sequence, Av1FrameParameters parameters, int effort, long rdmult,
        bool reference = true, bool video = false)
    {
        bool exact = video && parameters.LoopFilterSharpness == 0 && (reference ? effort >= 3 : effort >= 5);
        // A frame no other predicts from searches its filters as two efforts lower do.
        effort = reference ? effort : Math.Max(0, effort - 2);
        Av1FrameHeader header = frame.Header;
        // Intra block copy turns every loop filter off.
        if (header.CodedLossless || header.AllowIntrabc)
        {
            return;
        }

        int planes = header.NumPlanes;
        ushort[][] prefilter = [.. frame.Planes!.Select(p => (ushort[])p.Clone())];
        if (exact)
        {
            DeblockExact(frame, source, sequence, parameters, prefilter);
        }
        else
        {
            Deblock(frame, source, sequence, parameters, prefilter, search: effort >= 5);
        }

        bool restore = video && reference && effort >= 3 && sequence.EnableRestoration;
        ushort[][]? deblocked = restore ? [.. frame.Planes!.Select(p => (ushort[])((ushort[])p).Clone())] : null;

        // Tune IQ's adaptive CDEF: off at the finest quantizers, where it blurs (pickcdef.c).
        if (sequence.EnableCdef && parameters.BaseQIdx > 32)
        {
            Cdef(frame, source, sequence, parameters, effort, rdmult, video && !reference ? ReferenceStrengths(parameters) : null);
        }

        if (restore)
        {
            frame.Header = Reparse(sequence, parameters);
            Av1RestorationSearch.Search(frame, source, sequence, parameters, deblocked!, rdmult);
        }

        frame.Header = Reparse(sequence, parameters);
    }

    /// <summary>
    /// Finds the deblocking level of each plane that leaves the least squared error, climbing from libaom's guess for the
    /// quantizer (picklpf.c) by halving steps; the luma level serves both edge directions.
    /// </summary>
    private static void Deblock(Av1Frame frame, Av1EncoderSource source, Av1SequenceHeader sequence, Av1FrameParameters parameters, ushort[][] prefilter, bool search)
    {
        int planes = frame.Header.NumPlanes;
        int q = Av1Tables.AcQLookup8[0];
        int bitDepth = sequence.BitDepth;
        q = bitDepth == 8 ? Av1Tables.AcQLookup8[parameters.BaseQIdx] : bitDepth == 10 ? Av1Tables.AcQLookup10[parameters.BaseQIdx] : Av1Tables.AcQLookup12[parameters.BaseQIdx];
        int guess = bitDepth switch
        {
            8 => (int)(((q * 17563L) - 421574 + (1 << 17)) >> 18),
            10 => (int)(((q * 20723L) + 4060632 + (1 << 19)) >> 20),
            _ => (int)(((q * 20723L) + 16242526 + (1 << 21)) >> 22),
        };
        // A key frame above 8 bits filters a little less (picklpf.c).
        guess = Math.Clamp(bitDepth == 8 ? guess : guess - 4, 0, 63);
        if (!search)
        {
            // libaom's pick from the quantizer alone, as its faster speeds do.
            Array.Fill(parameters.LoopFilterLevel, guess);
            if (frame.Header.NumPlanes == 1)
            {
                parameters.LoopFilterLevel[2] = parameters.LoopFilterLevel[3] = 0;
            }

            frame.Header = Reparse(sequence, parameters);
            Av1LoopFilter.Apply<ushort>(frame);
            return;
        }

        Dictionary<int, long[]> tried = [];
        long[] Try(int level)
        {
            if (!tried.TryGetValue(level, out long[]? errors))
            {
                errors = new long[planes];
                for (int plane = 0; plane < planes; plane++)
                {
                    prefilter[plane].CopyTo(frame.Plane<ushort>(plane), 0);
                }

                Array.Fill(parameters.LoopFilterLevel, level);
                frame.Header = Reparse(sequence, parameters);
                Av1LoopFilter.Apply<ushort>(frame);
                for (int plane = 0; plane < planes; plane++)
                {
                    errors[plane] = PlaneError(frame, source, plane);
                }

                tried[level] = errors;
            }

            return errors;
        }

        int[] best = new int[planes];
        for (int plane = 0; plane < planes; plane++)
        {
            int level = guess;
            for (int step = 8; step >= 1; step >>= 1)
            {
                while (true)
                {
                    long here = Try(level)[plane];
                    int down = Math.Max(level - step, 0), up = Math.Min(level + step, 63);
                    if (down != level && Try(down)[plane] < here)
                    {
                        level = down;
                    }
                    else if (up != level && Try(up)[plane] < here)
                    {
                        level = up;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            best[plane] = level;
        }

        parameters.LoopFilterLevel[0] = parameters.LoopFilterLevel[1] = best[0];
        if (planes > 1)
        {
            (parameters.LoopFilterLevel[2], parameters.LoopFilterLevel[3]) = (best[1], best[2]);
        }

        if (best[0] == 0)
        {
            // Without luma filtering the header codes no chroma levels.
            parameters.LoopFilterLevel[2] = parameters.LoopFilterLevel[3] = 0;
        }

        for (int plane = 0; plane < planes; plane++)
        {
            prefilter[plane].CopyTo(frame.Plane<ushort>(plane), 0);
        }

        frame.Header = Reparse(sequence, parameters);
        Av1LoopFilter.Apply<ushort>(frame);
    }

    /// <summary>
    /// Finds the deblocking levels that leave the least squared error by the single pass over every level
    /// (<see cref="Av1LoopFilter.LevelErrors"/>): luma across vertical edges, then, those filtered at their level, across
    /// horizontal ones; each chroma plane's one level for both directions from both on the unfiltered plane.
    /// </summary>
    private static void DeblockExact(Av1Frame frame, Av1EncoderSource source, Av1SequenceHeader sequence, Av1FrameParameters parameters, ushort[][] prefilter)
    {
        int planes = frame.Header.NumPlanes;
        int vertical = Best(Av1LoopFilter.LevelErrors(frame, 0, true, source.Planes[0]));
        int[] levels = new int[4];
        levels[0] = vertical;
        if (vertical > 0)
        {
            Array.Fill(parameters.LoopFilterLevel, 0);
            parameters.LoopFilterLevel[0] = vertical;
            parameters.LoopFilterLevel[1] = 1;
            frame.Header = Reparse(sequence, parameters);
            Av1LoopFilter.ApplyPlane<ushort>(frame, 0, true);
        }

        levels[1] = Best(Av1LoopFilter.LevelErrors(frame, 0, false, source.Planes[0]));
        prefilter[0].CopyTo(frame.Plane<ushort>(0), 0);
        for (int plane = 1; plane < planes && (levels[0] > 0 || levels[1] > 0); plane++)
        {
            long[] v = Av1LoopFilter.LevelErrors(frame, plane, true, source.Planes[plane]);
            long[] h = Av1LoopFilter.LevelErrors(frame, plane, false, source.Planes[plane]);
            for (int i = 0; i < v.Length; i++)
            {
                v[i] += h[i];
            }

            levels[plane + 1] = Best(v);
        }

        levels.CopyTo(parameters.LoopFilterLevel, 0);
        frame.Header = Reparse(sequence, parameters);
        Av1LoopFilter.Apply<ushort>(frame);

        // The lowest level of the least error.
        static int Best(long[] errors)
        {
            int best = 0;
            for (int level = 1; level < errors.Length; level++)
            {
                if (errors[level] < errors[best])
                {
                    best = level;
                }
            }

            return best;
        }
    }

    /// <summary>
    /// Chooses up to eight CDEF strength pairs and each 64x64 unit's pair: every candidate strength is applied to the whole
    /// deblocked frame once, each unit's error measured under it, then pairs are added greedily while they pay for their index
    /// bits.
    /// </summary>
    private static void Cdef(Av1Frame frame, Av1EncoderSource source, Av1SequenceHeader sequence, Av1FrameParameters parameters, int effort, long rdmult,
        (int Pri, int Sec)[]? candidates = null)
    {
        int planes = frame.Header.NumPlanes;
        (int Pri, int Sec)[] strengths = candidates ?? CdefSets[effort <= 4 ? 0 : effort <= 6 ? 1 : 2];
        int units = frame.CdefIndex.Length;
        bool[] coded = new bool[units];
        int codedUnits = 0;
        for (int u = 0; u < units; u++)
        {
            coded[u] = frame.CdefIndex[u] >= 0;
            codedUnits += coded[u] ? 1 : 0;
        }

        if (codedUnits == 0)
        {
            return;
        }

        parameters.CdefDamping = 3 + (parameters.BaseQIdx >> 6);
        ushort[][] deblocked = [.. frame.Planes!.Select(p => (ushort[])p.Clone())];
        long[,] lumaError = new long[units, strengths.Length];
        long[,] chromaError = new long[units, strengths.Length];
        for (int k = 0; k < strengths.Length; k++)
        {
            for (int plane = 0; plane < planes; plane++)
            {
                deblocked[plane].CopyTo(frame.Plane<ushort>(plane), 0);
            }

            parameters.CdefBits = 0;
            (parameters.CdefYPriStrength[0], parameters.CdefYSecStrength[0]) = strengths[k];
            (parameters.CdefUvPriStrength[0], parameters.CdefUvSecStrength[0]) = strengths[k];
            for (int u = 0; u < units; u++)
            {
                frame.CdefIndex[u] = (sbyte)(coded[u] ? 0 : -1);
            }

            frame.Header = Reparse(sequence, parameters);
            Av1Cdef.Apply<ushort>(frame);
            int columns = frame.CdefStride;
            frame.For((units + columns - 1) / columns, row =>
            {
                for (int u = row * columns; u < Math.Min(units, (row + 1) * columns); u++)
                {
                    if (coded[u])
                    {
                        lumaError[u, k] = UnitError(frame, source, 0, u);
                        for (int plane = 1; plane < planes; plane++)
                        {
                            chromaError[u, k] += UnitError(frame, source, plane, u);
                        }
                    }
                }
            });
        }

        // Greedy: start from the best single pair, then add the pair (luma strength, chroma strength) that lowers the total most.
        List<(int Y, int Uv)> chosen = [];
        long bestTotal = long.MaxValue;
        int maxBits = effort <= 3 ? 1 : effort <= 6 ? 2 : 3;
        for (int bits = 0; bits <= maxBits; bits++)
        {
            int count = 1 << bits;
            while (chosen.Count < count)
            {
                (int, int) bestPair = (0, 0);
                long bestPairTotal = long.MaxValue;
                for (int y = 0; y < strengths.Length; y++)
                {
                    for (int uv = 0; uv < strengths.Length; uv++)
                    {
                        if (planes == 1 && uv != 0)
                        {
                            continue;
                        }

                        chosen.Add((y, uv));
                        long total = Total(chosen, coded, lumaError, chromaError);
                        chosen.RemoveAt(chosen.Count - 1);
                        if (total < bestPairTotal)
                        {
                            (bestPairTotal, bestPair) = (total, (y, uv));
                        }
                    }
                }

                chosen.Add(bestPair);
            }

            long withRate = Total(chosen, coded, lumaError, chromaError) + Av1Rate.RdCost(rdmult, (long)codedUnits * bits * Av1CostSink.BitCost, 0);
            if (withRate < bestTotal)
            {
                bestTotal = withRate;
                parameters.CdefBits = bits;
                for (int i = 0; i < count; i++)
                {
                    (parameters.CdefYPriStrength[i], parameters.CdefYSecStrength[i]) = strengths[chosen[i].Y];
                    (parameters.CdefUvPriStrength[i], parameters.CdefUvSecStrength[i]) = strengths[chosen[i].Uv];
                }
            }
            else
            {
                chosen.RemoveRange(chosen.Count - (count / 2), count / 2);
                break;
            }
        }

        // Each unit takes its best pair of the set.
        int pairs = 1 << parameters.CdefBits;
        for (int u = 0; u < units; u++)
        {
            if (!coded[u])
            {
                frame.CdefIndex[u] = -1;
                continue;
            }

            long best = long.MaxValue;
            for (int i = 0; i < pairs; i++)
            {
                long error = lumaError[u, chosen[i].Y] + chromaError[u, chosen[i].Uv];
                if (error < best)
                {
                    best = error;
                    frame.CdefIndex[u] = (sbyte)i;
                }
            }
        }

        for (int plane = 0; plane < planes; plane++)
        {
            deblocked[plane].CopyTo(frame.Plane<ushort>(plane), 0);
        }

        frame.Header = Reparse(sequence, parameters);
        Av1Cdef.Apply<ushort>(frame);

        static long Total(List<(int Y, int Uv)> set, bool[] coded, long[,] luma, long[,] chroma)
        {
            long total = 0;
            for (int u = 0; u < coded.Length; u++)
            {
                if (!coded[u])
                {
                    continue;
                }

                long best = long.MaxValue;
                foreach ((int y, int uv) in set)
                {
                    best = Math.Min(best, luma[u, y] + chroma[u, uv]);
                }

                total += best;
            }

            return total;
        }
    }

    /// <summary>
    /// Gets the CDEF strengths a frame no other predicts from tries: off, and those its nearest references, LAST and BWDREF, chose
    /// for luma and chroma (SVT-AV1's leaf frames reuse their references' strengths the same way).
    /// </summary>
    private static (int Pri, int Sec)[] ReferenceStrengths(Av1FrameParameters parameters)
    {
        List<(int Pri, int Sec)> list = [(0, 0)];
        foreach (int rf in (ReadOnlySpan<int>)[0, 4])
        {
            if (parameters.Slots?[parameters.RefFrameIdx[rf]]?.Header is not { } h)
            {
                continue;
            }

            for (int i = 0; i < 1 << h.CdefBits; i++)
            {
                foreach ((int, int) pair in (ReadOnlySpan<(int, int)>)[(h.CdefYPriStrength[i], h.CdefYSecStrength[i]), (h.CdefUvPriStrength[i], h.CdefUvSecStrength[i])])
                {
                    if (!list.Contains(pair) && list.Count < 8)
                    {
                        list.Add(pair);
                    }
                }
            }
        }

        return [.. list];
    }

    /// <summary>Gets the frame header the parameters write, as the decoder reads it.</summary>
    internal static Av1FrameHeader Reparse(Av1SequenceHeader sequence, Av1FrameParameters parameters)
    {
        Av1BitWriter bits = new();
        Av1ObuWriter.FrameHeader(bits, sequence, parameters);
        return Av1FrameHeader.Parse(bits.ToArray(), sequence, 0, 0, "AV1", parameters.Slots is null ? null : (Av1Frame?[])parameters.Slots.Clone());
    }

    private static long PlaneError(Av1Frame frame, Av1EncoderSource source, int plane)
    {
        return Av1MotionSearch.SquaredError(frame.Plane<ushort>(plane), source.Planes[plane], 0, frame.Strides[plane], source.Widths[plane], source.Heights[plane]);
    }

    /// <summary>Gets a plane's squared error over one 64x64 luma unit, at 8-bit scale times 16, as the block search counts distortion.</summary>
    private static long UnitError(Av1Frame frame, Av1EncoderSource source, int plane, int unit)
    {
        int ssx = plane > 0 ? frame.Sequence.SubsamplingX : 0, ssy = plane > 0 ? frame.Sequence.SubsamplingY : 0;
        int fbr = unit / frame.CdefStride, fbc = unit % frame.CdefStride;
        int x0 = (fbc * 64) >> ssx, y0 = (fbr * 64) >> ssy;
        int x1 = Math.Min(x0 + (64 >> ssx), source.Widths[plane]), y1 = Math.Min(y0 + (64 >> ssy), source.Heights[plane]);
        int stride = frame.Strides[plane];
        long error = x1 > x0 && y1 > y0 ? Av1MotionSearch.SquaredError(frame.Plane<ushort>(plane), source.Planes[plane], (y0 * stride) + x0, stride, x1 - x0, y1 - y0) : 0;
        return frame.Sequence.BitDepth switch
        {
            10 => error,
            12 => (error + 8) >> 4,
            _ => error << 4,
        };
    }
}
