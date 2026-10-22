namespace Egami.Video.Codecs.Vp9;

/// <summary>Inter prediction of a block (vp9_decodeframe.c's dec_build_inter_predictors and vp9_reconinter.c).</summary>
internal sealed partial class Vp9Decoder<T>
{
    internal sealed partial class Tile
    {
        private const int InterpExtend = 4;

        private void BuildInterPredictors(ref Vp9ModeInfo mi, int miRow, int miCol)
        {
            Vp9Decoder<T> d = _d;
            Vp9Frame<T> current = d._current!;
            int miX = miCol * 8, miY = miRow * 8;
            ReadOnlySpan<short> kernel = Kernel(mi.InterpFilter);
            bool compound = mi.HasSecondRef;
            for (int reference = 0; reference < (compound ? 2 : 1); reference++)
            {
                int frameIndex = mi.Ref(reference) - Vp9Tables.LastFrame;
                Vp9Frame<T> refFrame = d._activeRefs[frameIndex]!;
                Vp9ScaleFactors sf = d._scales[frameIndex];
                if (!sf.IsValid)
                {
                    throw new CorruptDataException("VP9", -1, "A reference frame has a size this frame cannot predict from.");
                }

                bool scaled = sf.IsScaled;
                for (int plane = 0; plane < 3; plane++)
                {
                    int n4wX4 = 4 * _n4W[plane], n4hX4 = 4 * _n4H[plane];
                    int dstStride = current.Strides[plane];
                    int dstOrigin = (((miRow * 8) >> _ssY[plane]) * dstStride) + ((miCol * 8) >> _ssX[plane]);
                    if (mi.SbType < Vp9Tables.Block8x8)
                    {
                        int i = 0;
                        for (int y = 0; y < _n4H[plane]; y++)
                        {
                            for (int x = 0; x < _n4W[plane]; x++)
                            {
                                int mv = AverageSplitMvs(ref mi, plane, reference, i++);
                                BuildInterPredictor(current, plane, n4wX4, n4hX4, 4 * x, 4 * y, 4, 4, miX, miY, kernel, sf, scaled,
                                    dstOrigin, dstStride, mv, refFrame, reference);
                            }
                        }
                    }
                    else
                    {
                        BuildInterPredictor(current, plane, n4wX4, n4hX4, 0, 0, n4wX4, n4hX4, miX, miY, kernel, sf, scaled,
                            dstOrigin, dstStride, mi.Mv(reference), refFrame, reference);
                    }
                }
            }
        }

        private static ReadOnlySpan<short> Kernel(int filter) => filter switch
        {
            Vp9Tables.EightTap => Vp9Tables.SubPelFilters8,
            Vp9Tables.EightTapSmooth => Vp9Tables.SubPelFilters8Lp,
            Vp9Tables.EightTapSharp => Vp9Tables.SubPelFilters8S,
            _ => Vp9Tables.BilinearFilters,
        };

        private int AverageSplitMvs(ref Vp9ModeInfo mi, int plane, int reference, int block)
        {
            int ssIdx = ((_ssX[plane] > 0 ? 1 : 0) << 1) | (_ssY[plane] > 0 ? 1 : 0);
            switch (ssIdx)
            {
                case 0:
                    return mi.Bmi[block].Mv(reference);
                case 1:
                    return MvPredQ2(ref mi, reference, block, block + 2);
                case 2:
                    return MvPredQ2(ref mi, reference, block, block + 1);
                default:
                    {
                        int row = 0, col = 0;
                        for (int b = 0; b < 4; b++)
                        {
                            int mv = mi.Bmi[b].Mv(reference);
                            row += Vp9ModeInfo.Row(mv);
                            col += Vp9ModeInfo.Col(mv);
                        }

                        return Vp9ModeInfo.Pack(RoundQ4(row), RoundQ4(col));
                    }
            }

            static int RoundQ4(int value) => (value < 0 ? value - 2 : value + 2) / 4;
        }

        private static int MvPredQ2(ref Vp9ModeInfo mi, int reference, int block0, int block1)
        {
            int a = mi.Bmi[block0].Mv(reference), b = mi.Bmi[block1].Mv(reference);
            return Vp9ModeInfo.Pack(RoundQ2(Vp9ModeInfo.Row(a) + Vp9ModeInfo.Row(b)), RoundQ2(Vp9ModeInfo.Col(a) + Vp9ModeInfo.Col(b)));

            static int RoundQ2(int value) => (value < 0 ? value - 1 : value + 1) / 2;
        }

        private void BuildInterPredictor(Vp9Frame<T> current, int plane, int bw, int bh, int x, int y, int w, int h, int miX, int miY,
            ReadOnlySpan<short> kernel, Vp9ScaleFactors sf, bool scaled, int dstOrigin, int dstStride, int mv, Vp9Frame<T> refFrame, int reference)
        {
            int ssX = _ssX[plane], ssY = _ssY[plane];
            int frameWidth = refFrame.CropWidth(plane), frameHeight = refFrame.CropHeight(plane);
            int refStride = refFrame.Strides[plane];
            int x0, y0, x016, y016, xs, ys, mvRow, mvCol;
            if (scaled)
            {
                int mvQ4 = ClampMvToUmvBorder(mv, bw, bh, ssX, ssY);
                int xStart = -_toLeftEdge >> (3 + ssX);
                int yStart = -_toTopEdge >> (3 + ssY);
                x016 = sf.ValueX((xStart + x) << 4);
                y016 = sf.ValueY((yStart + y) << 4);
                x0 = sf.ValueX(xStart + x);
                y0 = sf.ValueY(yStart + y);
                int xOffQ4 = sf.ScaledX((miX + x) << 4) & 15;
                int yOffQ4 = sf.ScaledY((miY + y) << 4) & 15;
                mvRow = sf.ScaledY(Vp9ModeInfo.Row(mvQ4)) + yOffQ4;
                mvCol = sf.ScaledX(Vp9ModeInfo.Col(mvQ4)) + xOffQ4;
                xs = sf.XStepQ4;
                ys = sf.YStepQ4;
            }
            else
            {
                x0 = (-_toLeftEdge >> (3 + ssX)) + x;
                y0 = (-_toTopEdge >> (3 + ssY)) + y;
                x016 = x0 << 4;
                y016 = y0 << 4;
                mvRow = Vp9ModeInfo.Row(mv) * (1 << (1 - ssY));
                mvCol = Vp9ModeInfo.Col(mv) * (1 << (1 - ssX));
                xs = ys = 16;
            }

            int subpelX = mvCol & 15, subpelY = mvRow & 15;
            x0 += mvCol >> 4;
            y0 += mvRow >> 4;
            x016 += mvCol;
            y016 += mvRow;

            int bufOffset = (y0 * refStride) + x0;
            Span<T> dstPlane = current.Plane(plane);
            int dst = dstOrigin + (y * dstStride) + x;
            Span<T> refPlane = refFrame.Plane(plane);
            int bd = _d._bitDepth;

            if (scaled || mvCol != 0 || mvRow != 0 || (frameWidth & 7) != 0 || (frameHeight & 7) != 0)
            {
                int y1 = ((y016 + ((h - 1) * ys)) >> 4) + 1;
                int x1 = ((x016 + ((w - 1) * xs)) >> 4) + 1;
                int xPad = 0, yPad = 0;
                if (subpelX != 0 || sf.XStepQ4 != 16)
                {
                    x0 -= InterpExtend - 1;
                    x1 += InterpExtend;
                    xPad = 1;
                }

                if (subpelY != 0 || sf.YStepQ4 != 16)
                {
                    y0 -= InterpExtend - 1;
                    y1 += InterpExtend;
                    yPad = 1;
                }

                if (x0 < 0 || x0 > frameWidth - 1 || x1 < 0 || x1 > frameWidth - 1 ||
                    y0 < 0 || y0 > frameHeight - 1 || y1 < 0 || y1 > frameHeight - 1)
                {
                    // Build the reference block with its edges repeated (build_mc_border), then predict from it.
                    int bW = x1 - x0 + 1, bH = y1 - y0 + 1;
                    Span<T> mc = _mcBuffer;
                    int leftCount = Math.Clamp(-x0, 0, bW);
                    int rightCount = Math.Clamp(x0 + bW - frameWidth, 0, bW - leftCount);
                    int copyCount = bW - leftCount - rightCount;
                    for (int r = 0; r < bH; r++)
                    {
                        int sy = Math.Clamp(y0 + r, 0, frameHeight - 1) * refStride;
                        Span<T> row = mc.Slice(r * bW, bW);
                        row[..leftCount].Fill(refPlane[sy]);
                        if (copyCount > 0)
                        {
                            refPlane.Slice(sy + x0 + leftCount, copyCount).CopyTo(row[leftCount..]);
                        }

                        row[(bW - rightCount)..].Fill(refPlane[sy + frameWidth - 1]);
                    }

                    int borderOffset = (yPad * 3 * bW) + (xPad * 3);
                    Vp9Convolve<T>.Predict(mc, borderOffset, bW, dstPlane, dst, dstStride, subpelX, xs, subpelY, ys, w, h, reference, kernel, bd,
                        _convolveTemp, _predictTemp);
                    return;
                }
            }

            Vp9Convolve<T>.Predict(refPlane, bufOffset, refStride, dstPlane, dst, dstStride, subpelX, xs, subpelY, ys, w, h, reference,
                kernel, bd, _convolveTemp, _predictTemp);
        }

        private int ClampMvToUmvBorder(int mv, int bw, int bh, int ssX, int ssY)
        {
            int spelLeft = (InterpExtend + bw) << 4;
            int spelRight = spelLeft - 16;
            int spelTop = (InterpExtend + bh) << 4;
            int spelBottom = spelTop - 16;
            int row = (short)(Vp9ModeInfo.Row(mv) * (1 << (1 - ssY)));
            int col = (short)(Vp9ModeInfo.Col(mv) * (1 << (1 - ssX)));
            col = Vp9Tables.Clamp(col, (_toLeftEdge * (1 << (1 - ssX))) - spelLeft, (_toRightEdge * (1 << (1 - ssX))) + spelRight);
            row = Vp9Tables.Clamp(row, (_toTopEdge * (1 << (1 - ssY))) - spelTop, (_toBottomEdge * (1 << (1 - ssY))) + spelBottom);
            return Vp9ModeInfo.Pack(row, col);
        }
    }
}
