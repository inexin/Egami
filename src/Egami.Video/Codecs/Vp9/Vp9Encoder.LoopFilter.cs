namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// The loop filter level search (vp9_picklpf.c's search_filter_level is the idea): the coded frame filtered at a few levels
/// around a guess, keeping the level whose luma lands closest to the source, with lower levels preferred on near ties.
/// </summary>
internal sealed partial class Vp9Encoder<T>
{
    private static readonly int[] DefaultRefDeltas = [1, 0, -1, -1];
    private static readonly int[] DefaultModeDeltas = [0, 0];
    private readonly Vp9LoopFilter<T> _loopFilter = new();
    private Vp9Frame<T>? _filtered;

    /// <summary>Searches the loop filter level once the frame's blocks are coded, unless the plan fixes it.</summary>
    private void PickFilterLevel(Vp9FramePlan plan)
    {
        if (plan.LoopFilterLevel >= 0 || _lossless || plan.Effort < 1)
        {
            return;
        }

        _filtered ??= new Vp9Frame<T>(_width, _height, _subX, _subY, _bitDepth, _memory);
        Span<long> errors = stackalloc long[Vp9Tables.MaxLoopFilter + 1];
        errors.Fill(-1);
        int best = _filterLevel;
        long bestError = FilterError(best, errors);
        int step = Math.Max(1, best / 4);
        if (best < 8)
        {
            step = 2;
        }

        for (int round = 0; step >= 1 && round < 64; round++)
        {
            // The neighbours a step away against the centre: a lower level is kept on a near tie, as libvpx's bias does
            // (filtering costs detail), and a higher one must win by the same margin, so the search never swings back.
            int center = best;
            long centerError = bestError;
            long margin = (centerError >> (15 - (center / 8))) * step;
            long bestScore = centerError;
            for (int d = -1; d <= 1; d += 2)
            {
                int level = center + (d * step);
                if (level < 0 || level > Vp9Tables.MaxLoopFilter)
                {
                    continue;
                }

                long error = FilterError(level, errors);
                long score = error + (d < 0 ? -margin : margin);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = level;
                    bestError = error;
                }
            }

            if (best == center)
            {
                step >>= 1;
            }
        }

        _filterLevel = best;
    }

    /// <summary>The luma's squared error against the source after filtering the coded frame at <paramref name="level"/>.</summary>
    private long FilterError(int level, Span<long> errors)
    {
        if (errors[level] >= 0)
        {
            return errors[level];
        }

        Vp9Frame<T> f = _filtered!;
        for (int p = 0; p < 3; p++)
        {
            _recon.Plane(p).CopyTo(f.Plane(p));
        }

        if (level > 0)
        {
            _loopFilter.FrameInit(level, 0, true, DefaultRefDeltas, DefaultModeDeltas, SegmentFeatureActive, (s, feature) => _featureData[(s * 4) + feature],
                false, _miRows, _miCols);
            for (int miRow = 0; miRow < _miRows; miRow++)
            {
                for (int miCol = 0; miCol < _miCols; miCol++)
                {
                    int index = (miRow * _miCols) + miCol;
                    if (_miGrid[index] != index)
                    {
                        continue;
                    }

                    ref Vp9ModeInfo mi = ref _mi[index];
                    int bw = Vp9Tables.Num8x8BlocksWideLookup[mi.SbType], bh = Vp9Tables.Num8x8BlocksHighLookup[mi.SbType];
                    _loopFilter.BuildMask(ref mi, miRow, miCol, bw, bh);
                }
            }

            _loopFilter.FilterFrame(f, _miRows, _miCols, _mi, _miGrid, MaxParallelism);
        }

        T[] source = _source[0];
        long error = 0;
        Span<T> plane = f.Plane(0);
        int stride = f.Strides[0];
        for (int y = 0; y < _height; y++)
        {
            error += Vp9EncoderSimd.SseRow<T>(source.AsSpan(y * _sourceStride[0], _width), plane.Slice(y * stride, _width));
        }

        errors[level] = error;
        return error;
    }
}
