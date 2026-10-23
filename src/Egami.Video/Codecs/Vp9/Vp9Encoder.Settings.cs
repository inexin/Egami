namespace Egami.Video.Codecs.Vp9;

/// <summary>How hard the encoder searches at each effort, and adaptive quantization by segments.</summary>
internal sealed partial class Vp9Encoder<T>
{
    private readonly byte[] _segmentTreeProbs = [255, 255, 255, 255, 255, 255, 255];
    private readonly int[] _txBlocks = new int[4];
    private int _filterLevel;
    private int _sadPerBit = 1;

    /// <summary>Gets the largest block the search codes whole.</summary>
    private const int MaxSearchBlock = Vp9Tables.Block64x64;

    /// <summary>Gets the smallest block the partition search reaches (8x8 holds the 4x4 intra blocks below it).</summary>
    private int MinBlock => _plan.Effort <= 0 ? Vp9Tables.Block16x16 : Vp9Tables.Block8x8;

    /// <summary>Gets whether 4x4 intra blocks are tried.</summary>
    private bool AllowSub8x8 => _frameIsIntra ? _plan.Effort >= 1 : _plan.Effort >= 6;

    /// <summary>Gets libvpx's partition search breakout distance for a 64x64 block, in 16 times the squared error.</summary>
    private long BreakoutDistance => _plan.Effort switch { <= 3 => 1L << 22, <= 6 => 1L << 21, _ => 1L << 20 };

    /// <summary>Gets libvpx's partition search breakout rate per log2 of the block's pixels, in 1/512 bits.</summary>
    private int BreakoutRate => _plan.Effort <= 3 ? 100 : 80;

    /// <summary>Gets the smallest block searched whole at a level: low efforts leave 64x64 blocks whole only on still frames.</summary>
    private int MinSearchBlock(int bsize) => _plan.Effort <= 1 && bsize == Vp9Tables.Block64x64 && _frameIsIntra ? Vp9Tables.BlockInvalid : 0;

    /// <summary>Gets how many transform sizes below the largest are tried.</summary>
    private int TxSearchDepth => _plan.Effort switch { <= 1 => 0, <= 3 => 1, <= 6 => 2, _ => 3 };

    /// <summary>Gets how many transform sizes below the largest intra blocks of inter frames try.</summary>
    private int InterFrameIntraTxDepth => _plan.Effort switch { <= 5 => 0, <= 7 => 1, _ => 3 };

    /// <summary>Gets how many intra modes the smaller transform sizes try, the best at the largest size.</summary>
    private int IntraModesKept => _plan.Effort switch { <= 3 => 2, <= 6 => 3, <= 8 => 5, _ => 10 };

    /// <summary>Gets the effort from which the RD search itself runs the trellis (the coded blocks always do from effort 1).</summary>
    private const int TrellisSearchEffort = 6;

    /// <summary>Gets how much worse than DC prediction, as a multiple of its error, the best inter prediction is before intra is tried.</summary>
    private int IntraGate => _plan.Effort <= 4 ? 2 : 3;

    /// <summary>Gets how many inter choices get a full RD search.</summary>
    private int FullInterCandidates => _plan.Effort switch { <= 1 => 1, <= 3 => 2, <= 5 => 3, <= 7 => 5, _ => 8 };

    /// <summary>Gets the first diamond step of the whole-pixel motion search.</summary>
    private int SearchRange => _plan.Effort switch { <= 2 => 8, <= 5 => 16, _ => 32 };

    /// <summary>Gets how many of LAST, GOLDEN and ALTREF inter blocks may use.</summary>
    private int ReferenceCount => _plan.Effort <= 0 ? 1 : 3;

    private byte[] _previousSizes = [];
    private bool _previousSizesValid;

    /// <summary>
    /// The square block sizes a superblock's partition search covers on inter frames at low and middle efforts: from one size
    /// below the smallest block the same place had in the previous frame to one above its largest (libvpx's relaxed
    /// auto_min_max_partition_size). Keyframes, the first frame after one and high efforts search every size.
    /// </summary>
    private (int Min, int Max) PartitionRange(int miRow, int miCol)
    {
        if (_frameIsIntra || !_previousSizesValid || _plan.Effort >= 7)
        {
            return (0, Vp9Tables.Block64x64);
        }

        int smallest = 4, largest = 0;
        for (int r = miRow; r < Math.Min(miRow + 8, _miRows); r++)
        {
            for (int c = miCol; c < Math.Min(miCol + 8, _miCols); c++)
            {
                int size = _previousSizes[(r * _miCols) + c];
                int wide = Math.Max(1, (int)Vp9Tables.BWidthLog2Lookup[size]), high = Math.Max(1, (int)Vp9Tables.BHeightLog2Lookup[size]);
                smallest = Math.Min(smallest, Math.Min(wide, high));
                largest = Math.Max(largest, Math.Max(wide, high));
            }
        }

        // Levels in 4x4 units: 1 is 8x8, 4 is 64x64; squares are 3, 6, 9 and 12.
        int min = Math.Max(1, smallest - 1), max = Math.Min(4, largest + 1);
        return (min == 1 ? 0 : (min * 3), max * 3);
    }

    /// <summary>Keeps each 8x8's block size of the frame just coded, for the next frame's partition range.</summary>
    private void KeepSizes()
    {
        if (_previousSizes.Length != _mi.Length)
        {
            _previousSizes = new byte[_mi.Length];
        }

        for (int i = 0; i < _mi.Length; i++)
        {
            _previousSizes[i] = _mi[_miGrid[i]].SbType;
        }

        _previousSizesValid = true;
    }

    /// <summary>Lists the luma intra modes tried for a block size.</summary>
    private int IntraModesFor(int bsize, Span<int> modes)
    {
        int effort = _plan.Effort;
        if (!_frameIsIntra && effort <= 5)
        {
            modes[0] = Vp9Tables.DcPred;
            modes[1] = Vp9Tables.VPred;
            modes[2] = Vp9Tables.HPred;
            modes[3] = Vp9Tables.TmPred;
            return 4;
        }

        if (effort >= 4 || (bsize <= Vp9Tables.Block16x16 && effort >= 2))
        {
            for (int i = 0; i < 10; i++)
            {
                modes[i] = i;
            }

            return 10;
        }

        // DC, V, H and TM carry most of the gain.
        modes[0] = Vp9Tables.DcPred;
        modes[1] = Vp9Tables.VPred;
        modes[2] = Vp9Tables.HPred;
        modes[3] = Vp9Tables.TmPred;
        return 4;
    }

    /// <summary>Lists the chroma intra modes tried.</summary>
    private int IntraChromaModes(Span<int> modes)
    {
        if (_plan.Effort >= 5)
        {
            for (int i = 0; i < 10; i++)
            {
                modes[i] = i;
            }

            return 10;
        }

        modes[0] = Vp9Tables.DcPred;
        modes[1] = Vp9Tables.VPred;
        modes[2] = Vp9Tables.HPred;
        modes[3] = Vp9Tables.TmPred;
        return 4;
    }

    // Segmentation: adaptive quantization by block energy (libvpx's aq-mode 1, vp9_aq_variance.c is the idea).

    private static ReadOnlySpan<double> AqSteps => [0.70, 0.80, 0.90, 1.0, 1.12];

    private readonly int[] _featureMask = new int[8];
    private readonly short[] _featureData = new short[8 * 4];

    private bool SegmentFeatureActive(int segment, int feature) => _segmentationEnabled && (_featureMask[segment] & (1 << feature)) != 0;

    private void SetupSegmentation(Vp9FramePlan plan)
    {
        Array.Clear(_featureMask);
        Array.Clear(_featureData);
        _segmentationEnabled = plan.AqMode == 1 && plan.Quantizer > 0;
        if (!_segmentationEnabled)
        {
            return;
        }

        // Segments 0 to 4 code low, middle and high energy blocks with finer or coarser steps than the frame's.
        int baseQ = plan.Quantizer;
        int baseStep = AcQuant(baseQ, 0);
        for (int s = 0; s < 5; s++)
        {
            double target = baseStep * AqSteps[s];
            int q = baseQ;
            if (s != 3)
            {
                // The index whose step is nearest the target.
                double best = double.MaxValue;
                for (int i = 1; i < 256; i++)
                {
                    double d = Math.Abs(AcQuant(i, 0) - target);
                    if (d < best)
                    {
                        best = d;
                        q = i;
                    }
                }
            }

            _segmentQuantizer[s] = q;
            _featureMask[s] |= 1 << Vp9Tables.SegLvlAltQ;
            _featureData[(s * 4) + Vp9Tables.SegLvlAltQ] = (short)(q - baseQ);
        }

        for (int s = 5; s < 8; s++)
        {
            _segmentQuantizer[s] = baseQ;
        }

        // Equal probabilities over the five segments used.
        _segmentTreeProbs[0] = 204;
        _segmentTreeProbs[1] = 128;
        _segmentTreeProbs[2] = 255;
        _segmentTreeProbs[3] = 128;
        _segmentTreeProbs[4] = 128;
        _segmentTreeProbs[5] = 255;
        _segmentTreeProbs[6] = 128;
        _costs.FillSegments(_segmentTreeProbs);
    }

    /// <summary>The segment of a block: its luma energy against the frame's (vp9_vaq_segment_id is the idea).</summary>
    private int SegmentFor(Tile tile, int miRow, int miCol, int bsize)
    {
        _ = tile;
        if (!_segmentationEnabled)
        {
            return 0;
        }

        int w = 4 * Vp9Tables.Num4x4BlocksWideLookup[bsize], h = 4 * Vp9Tables.Num4x4BlocksHighLookup[bsize];
        double variance = Vp9EncoderSimd.Variance(_source[0], (miRow * 8 * _sourceStride[0]) + (miCol * 8), _sourceStride[0], w, h) /
            (double)(1 << (2 * (_bitDepth - 8)));
        double energy = Math.Log(variance + 1.0) - 3.0;
        int segment = energy < -1.5 ? 0 : energy < -0.5 ? 1 : energy < 0.5 ? 2 : energy < 2.5 ? 3 : 4;
        return segment;
    }

    private void WriteSegmentation(ref Vp9BitWriter w)
    {
        w.Bit(_segmentationEnabled ? 1 : 0);
        if (!_segmentationEnabled)
        {
            return;
        }

        w.Bit(1); // update_map
        for (int i = 0; i < 7; i++)
        {
            bool coded = _segmentTreeProbs[i] != 255;
            w.Bit(coded ? 1 : 0);
            if (coded)
            {
                w.Literal(_segmentTreeProbs[i], 8);
            }
        }

        w.Bit(0); // temporal_update
        w.Bit(1); // update_data
        w.Bit(0); // abs_delta: deltas
        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                bool enabled = (_featureMask[i] & (1 << j)) != 0;
                w.Bit(enabled ? 1 : 0);
                if (!enabled)
                {
                    continue;
                }

                int max = Vp9Tables.SegFeatureDataMax[j];
                int bits = max == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)max);
                int value = _featureData[(i * 4) + j];
                w.Literal(Math.Abs(value), bits);
                if (Vp9Tables.SegFeatureSigned[j])
                {
                    w.Bit(value < 0 ? 1 : 0);
                }
            }
        }
    }
}
