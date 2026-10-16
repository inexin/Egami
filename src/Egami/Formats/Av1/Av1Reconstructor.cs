namespace Egami.Formats.Av1;

/// <summary>
/// Reconstructs a tile's blocks into the frame's planes before the loop filters: intra prediction (libaom's reconintra.c and
/// aom_dsp/intrapred.c), chroma from luma (cfl.c), palettes, intra block copy (the intra case of reconinter.c with
/// convolve.c's bilinear half-sample filter) and the residual (BSD 2-clause, see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed partial class Av1Reconstructor : IAv1Reconstructor
{
    private const int CflLine = 32;

    private readonly Av1Frame _frame;
    private readonly int _bitDepth;
    private readonly int _ssx, _ssy;
    private readonly ushort[] _cflRecon = new ushort[CflLine * CflLine];
    private readonly short[] _cflAc = new short[CflLine * CflLine];
    private int _cflWidth, _cflHeight;
    private bool _cflComputed;

    /// <summary>Starts reconstructing a tile of a frame.</summary>
    internal Av1Reconstructor(Av1Frame frame)
    {
        _frame = frame;
        _bitDepth = frame.Sequence.BitDepth;
        _ssx = frame.Sequence.SubsamplingX;
        _ssy = frame.Sequence.SubsamplingY;
    }

    /// <inheritdoc/>
    public void IntraBlock(Av1TileParser tile, int plane, int x, int y, int txSize, int txType, int eob, int[] coeffs)
    {
        Av1BlockInfo b = tile.Block;
        ushort[] dst = _frame.Planes![plane];
        int stride = _frame.Strides[plane];
        int offset = (y * stride) + x;
        (int col4, int row4) = tile.PlaneOrigin(plane);
        int colOff = (x >> 2) - col4, rowOff = (y >> 2) - row4;
        int mode = plane == 0 ? b.YMode : b.UvModeAsY;
        bool palette = b.PaletteSize[plane > 0 ? 1 : 0] > 0;
        int filterMode = plane == 0 && b.UseFilterIntra ? b.FilterIntraMode : -1;
        int angleDelta = (plane == 0 ? b.AngleDeltaY : b.AngleDeltaUv) * 3;
        PredictIntra(tile, plane, colOff, rowOff, txSize, mode, angleDelta, palette, filterMode, dst, offset, stride);
        if (plane > 0 && b.UvMode == Pred.UvCfl)
        {
            PredictCfl(b, plane, txSize, dst, offset, stride);
        }

        if (eob > 0)
        {
            Av1InverseTransform.Add(coeffs, txSize, txType, _frame.Header.LosslessArray[b.SegmentId], eob, dst, offset, stride, _bitDepth);
        }

        if (plane == 0 && StoreCflRequired(tile))
        {
            StoreCfl(tile, rowOff, colOff, txSize, dst, offset, stride);
        }
    }

    /// <inheritdoc/>
    public void InterPrediction(Av1TileParser tile)
    {
        Av1BlockInfo b = tile.Block;
        for (int plane = 0; plane < _frame.Header.NumPlanes; plane++)
        {
            if (plane > 0 && !tile.IsChromaRef)
            {
                break;
            }

            PredictIntrabc(tile, b, plane);
        }
    }

    /// <inheritdoc/>
    public void InterDone(Av1TileParser tile)
    {
        Av1BlockInfo b = tile.Block;
        if (StoreCflRequired(tile))
        {
            // cfl_store_block: the whole block's luma, for a chroma from luma block that covers it.
            int row = 0, col = 0;
            int bsize = b.Size;
            if (Av1Tables.BlockSizeHigh[bsize] == 4 || Av1Tables.BlockSizeWide[bsize] == 4)
            {
                row = (tile.MiRow & 1) != 0 && _ssy != 0 ? 1 : 0;
                col = (tile.MiCol & 1) != 0 && _ssx != 0 ? 1 : 0;
            }

            int width = AlignTo(tile.MaxBlockWide(bsize, 0) << 2, Av1Tables.TxSizeWideLog2[b.TxSize]);
            int height = AlignTo(tile.MaxBlockHigh(bsize, 0) << 2, Av1Tables.TxSizeHighLog2[b.TxSize]);
            int stride = _frame.Strides[0];
            StoreCflArea(row, col, width, height, _frame.Planes![0], (tile.MiRow * 4 * stride) + (tile.MiCol * 4), stride);
        }

        static int AlignTo(int value, int log2) => (value + (1 << log2) - 1) & ~((1 << log2) - 1);
    }

    /// <inheritdoc/>
    public void InterResidual(Av1TileParser tile, int plane, int x, int y, int txSize, int txType, int eob, int[] coeffs)
    {
        if (eob == 0)
        {
            return;
        }

        int stride = _frame.Strides[plane];
        Av1InverseTransform.Add(coeffs, txSize, txType, _frame.Header.LosslessArray[tile.Block.SegmentId], eob, _frame.Planes![plane], (y * stride) + x, stride, _bitDepth);
    }

    private bool StoreCflRequired(Av1TileParser tile) =>
        !_frame.Sequence.Monochrome && (!tile.IsChromaRef || (!tile.Block.IsInter && tile.Block.UvMode == Pred.UvCfl));

    /// <summary>cfl_store_tx: stores a luma transform block, subsampled, for chroma from luma.</summary>
    private void StoreCfl(Av1TileParser tile, int row, int col, int txSize, ushort[] src, int offset, int stride)
    {
        int bsize = tile.Block.Size;
        if (Av1Tables.BlockSizeHigh[bsize] == 4 || Av1Tables.BlockSizeWide[bsize] == 4)
        {
            row += (tile.MiRow & 1) != 0 && _ssy != 0 ? 1 : 0;
            col += (tile.MiCol & 1) != 0 && _ssx != 0 ? 1 : 0;
        }

        StoreCflArea(row, col, Av1Tables.TxSizeWide[txSize], Av1Tables.TxSizeHigh[txSize], src, offset, stride);
    }

    private void StoreCflArea(int row, int col, int width, int height, ushort[] src, int offset, int stride)
    {
        int storeRow = row << (2 - _ssy), storeCol = col << (2 - _ssx);
        int storeHeight = height >> _ssy, storeWidth = width >> _ssx;
        _cflComputed = false;
        if (col == 0 && row == 0)
        {
            _cflWidth = storeWidth;
            _cflHeight = storeHeight;
        }
        else
        {
            _cflWidth = Math.Max(storeCol + storeWidth, _cflWidth);
            _cflHeight = Math.Max(storeRow + storeHeight, _cflHeight);
        }

        int output = (storeRow * CflLine) + storeCol;
        for (int j = 0; j < height; j += 1 << _ssy)
        {
            int input = offset + (j * stride);
            for (int i = 0; i < width; i += 1 << _ssx)
            {
                int value = (_ssx, _ssy) switch
                {
                    (1, 1) => (src[input + i] + src[input + i + 1] + src[input + i + stride] + src[input + i + stride + 1]) << 1,
                    (1, _) => (src[input + i] + src[input + i + 1]) << 2,
                    _ => src[input + i] << 3,
                };
                _cflRecon[output + (i >> _ssx)] = (ushort)value;
            }

            output += CflLine;
        }
    }

    /// <summary>av1_cfl_predict_block: adds the scaled luma AC to the DC prediction.</summary>
    private void PredictCfl(Av1BlockInfo b, int plane, int txSize, ushort[] dst, int offset, int stride)
    {
        int width = Av1Tables.TxSizeWide[txSize], height = Av1Tables.TxSizeHigh[txSize];
        if (!_cflComputed)
        {
            // cfl_pad: repeat the last column and row the luma reached.
            if (width > _cflWidth)
            {
                for (int j = 0; j < _cflHeight; j++)
                {
                    int rowStart = j * CflLine;
                    _cflRecon.AsSpan(rowStart + _cflWidth, width - _cflWidth).Fill(_cflRecon[rowStart + _cflWidth - 1]);
                }

                _cflWidth = width;
            }

            if (height > _cflHeight)
            {
                for (int j = _cflHeight; j < height; j++)
                {
                    _cflRecon.AsSpan((j - 1) * CflLine, width).CopyTo(_cflRecon.AsSpan(j * CflLine));
                }

                _cflHeight = height;
            }

            // subtract_average
            int log2 = Av1Tables.TxSizeWideLog2[txSize] + Av1Tables.TxSizeHighLog2[txSize];
            int sum = 1 << (log2 - 1);
            for (int j = 0; j < height; j++)
            {
                for (int i = 0; i < width; i++)
                {
                    sum += _cflRecon[(j * CflLine) + i];
                }
            }

            int average = sum >> log2;
            for (int j = 0; j < height; j++)
            {
                for (int i = 0; i < width; i++)
                {
                    _cflAc[(j * CflLine) + i] = (short)(_cflRecon[(j * CflLine) + i] - average);
                }
            }

            _cflComputed = true;
        }

        int signs = b.CflAlphaSigns;
        int sign = plane == 1 ? ((signs + 1) * 11) >> 5 : signs + 1 - (3 * (((signs + 1) * 11) >> 5));
        int magnitude = plane == 1 ? b.CflAlphaIdx >> 4 : b.CflAlphaIdx & 15;
        int alpha = sign == 0 ? 0 : sign == 2 ? magnitude + 1 : -magnitude - 1;
        int max = (1 << _bitDepth) - 1;
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                int scaled = alpha * _cflAc[(j * CflLine) + i];
                int luma = scaled < 0 ? -((-scaled + 32) >> 6) : (scaled + 32) >> 6;
                int at = offset + (j * stride) + i;
                dst[at] = (ushort)Math.Clamp(luma + dst[at], 0, max);
            }
        }
    }

    /// <summary>
    /// Predicts an intra block copy block in a plane from the frame itself (build_inter_predictors_8x8_and_bigger with the
    /// identity scale and the 2-tap filter).
    /// </summary>
    private void PredictIntrabc(Av1TileParser tile, Av1BlockInfo b, int plane)
    {
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        int bw = Math.Max((tile.BlockWidth4 * 4) >> ssx, 4), bh = Math.Max((tile.BlockHeight4 * 4) >> ssy, 4);
        int rowStart = Av1Tables.BlockSizeHigh[b.Size] == 4 && ssy != 0 ? -1 : 0;
        int colStart = Av1Tables.BlockSizeWide[b.Size] == 4 && ssx != 0 ? -1 : 0;
        int preX = ((tile.MiCol * 4) + (4 * colStart)) >> ssx;
        int preY = ((tile.MiRow * 4) + (4 * rowStart)) >> ssy;

        // clamp_mv_to_umv_border_sb, in sixteenth samples of the plane.
        int spelLeft = (4 + bw) << 4, spelRight = spelLeft - 16, spelTop = (4 + bh) << 4, spelBottom = spelTop - 16;
        int mvRow = Math.Clamp(b.MvRow * (1 << (1 - ssy)), (tile.ToTopEdge * (1 << (1 - ssy))) - spelTop, (tile.ToBottomEdge * (1 << (1 - ssy))) + spelBottom);
        int mvCol = Math.Clamp(b.MvCol * (1 << (1 - ssx)), (tile.ToLeftEdge * (1 << (1 - ssx))) - spelLeft, (tile.ToRightEdge * (1 << (1 - ssx))) + spelRight);
        int posX = (preX << 4) + mvCol, posY = (preY << 4) + mvRow;
        int subX = mvCol & 15, subY = mvRow & 15;
        int srcX = posX >> 4, srcY = posY >> 4;

        ushort[] planeData = _frame.Planes![plane];
        int stride = _frame.Strides[plane];
        int src = (srcY * stride) + srcX, dst = (preY * stride) + preX;
        int bd = _bitDepth, max = (1 << bd) - 1;
        if (subX == 0 && subY == 0)
        {
            for (int y = 0; y < bh; y++)
            {
                planeData.AsSpan(src + (y * stride), bw).CopyTo(planeData.AsSpan(dst + (y * stride)));
            }

            return;
        }

        if (subY == 0)
        {
            int round0 = bd == 12 ? 5 : 3;
            int bits = 7 - round0;
            Span<ushort> row = stackalloc ushort[128];
            for (int y = 0; y < bh; y++)
            {
                int s = src + (y * stride);
                for (int x = 0; x < bw; x++)
                {
                    int res = Round(64 * (planeData[s + x] + planeData[s + x + 1]), round0);
                    row[x] = (ushort)Math.Clamp(Round(res, bits), 0, max);
                }

                row[..bw].CopyTo(planeData.AsSpan(dst + (y * stride)));
            }

            return;
        }

        if (subX == 0)
        {
            Span<ushort> block = stackalloc ushort[128 * 128];
            for (int y = 0; y < bh; y++)
            {
                int s = src + (y * stride);
                for (int x = 0; x < bw; x++)
                {
                    block[(y * bw) + x] = (ushort)Math.Clamp(Round(planeData[s + x] + planeData[s + stride + x], 1), 0, max);
                }
            }

            for (int y = 0; y < bh; y++)
            {
                block.Slice(y * bw, bw).CopyTo(planeData.AsSpan(dst + (y * stride)));
            }

            return;
        }

        {
            int round0 = bd == 12 ? 5 : 3;
            int round1 = bd == 12 ? 9 : 11;
            int offsetBits = bd + 14 - round0;
            Span<short> im = stackalloc short[(128 + 1) * 128];
            for (int y = 0; y < bh + 1; y++)
            {
                int s = src + (y * stride);
                for (int x = 0; x < bw; x++)
                {
                    im[(y * bw) + x] = (short)Round((1 << (bd + 6)) + (64 * (planeData[s + x] + planeData[s + x + 1])), round0);
                }
            }

            int subtract = (1 << (offsetBits - round1)) + (1 << (offsetBits - round1 - 1));
            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < bw; x++)
                {
                    int sum = (1 << offsetBits) + (64 * (im[(y * bw) + x] + im[((y + 1) * bw) + x]));
                    int res = Round(sum, round1) - subtract;
                    planeData[dst + (y * stride) + x] = (ushort)Math.Clamp(res, 0, max);
                }
            }
        }

        static int Round(int value, int bits) => bits == 0 ? value : (value + (1 << (bits - 1))) >> bits;
    }
}
