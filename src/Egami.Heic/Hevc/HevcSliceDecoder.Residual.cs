namespace Egami.Heic.Hevc;

/// <summary>residual_coding (7.3.8.11) and the reconstruction of one transform block from its coefficient levels.</summary>
internal sealed partial class HevcSliceDecoder
{
    /// <summary>
    /// Parses the residual of a transform block of component <paramref name="c"/> at (x0, y0) in component samples, scales and
    /// transforms it into <see cref="_residual"/>, applies the range extensions' modifications and adds it to the prediction.
    /// <paramref name="predMode"/> is the intra prediction mode, or -1 for an inter block.
    /// </summary>
    private void Residual(int x0, int y0, int log2, int c, int predMode, int resScale = 0)
    {
        // _coefficients is all zeros here: Reconstruct clears what it used.
        int n = 1 << log2;
        bool transformSkip = _pps.TransformSkipEnabled && !_cuBypass && log2 <= _pps.Log2MaxTransformSkipSize
            && _cabac.Decode(ref _contexts[HevcContexts.TransformSkip + (c == 0 ? 0 : 1)]) == 1;

        // Residual DPCM: implicit for intra blocks, explicit_rdpcm_flag for inter ones (7.3.8.11); and sign data hiding (7.4.9.11).
        bool inter = predMode < 0;
        bool rdpcm = _sps.ImplicitRdpcm && (transformSkip || _cuBypass) && (predMode == 10 || predMode == 26);
        bool rdpcmVertical = predMode == 26;
        if (inter && _sps.ExplicitRdpcm && (transformSkip || _cuBypass))
        {
            int cc = c == 0 ? 0 : 1;
            rdpcm = _cabac.Decode(ref _contexts[HevcContexts.ExplicitRdpcm + cc]) == 1;
            rdpcmVertical = rdpcm && _cabac.Decode(ref _contexts[HevcContexts.ExplicitRdpcmDir + cc]) == 1;
        }

        bool signHidingAllowed = _pps.SignDataHiding && !_cuBypass && !rdpcm;

        // Scan order (7.4.9.11): horizontal or vertical for 4x4 and 8x8 luma (and 8x8 chroma of 4:4:4) near-vertical or
        // near-horizontal intra modes.
        int scanIdx = 0;
        if (log2 == 2 || (log2 == 3 && (c == 0 || _sps.ChromaArrayType == 3)))
        {
            scanIdx = predMode is >= 6 and <= 14 ? 2 : predMode is >= 22 and <= 30 ? 1 : 0;
        }

        // last_sig_coeff_x/y_prefix and suffix.
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
        int prefixX = 0;
        while (prefixX < maxPrefix && _cabac.Decode(ref _contexts[HevcContexts.LastXPrefix + ctxOffset + (prefixX >> ctxShift)]) == 1)
        {
            prefixX++;
        }

        int prefixY = 0;
        while (prefixY < maxPrefix && _cabac.Decode(ref _contexts[HevcContexts.LastYPrefix + ctxOffset + (prefixY >> ctxShift)]) == 1)
        {
            prefixY++;
        }

        int lastX = LastPosition(prefixX), lastY = LastPosition(prefixY);
        if (scanIdx == 2)
        {
            (lastX, lastY) = (lastY, lastX);
        }

        // Positions inside a 4x4 sub-block and of the sub-blocks, for this scan.
        byte[] inner = HevcScan.Get(scanIdx, 2);
        int log2Sb = log2 - 2;
        byte[] outer = HevcScan.Get(scanIdx, log2Sb);
        int sbWidth = 1 << log2Sb;

        // Find the last sub-block and position in scan order.
        int lastSubBlock = -1, lastScanPos = -1;
        for (int i = (1 << (2 * log2Sb)) - 1; i >= 0; i--)
        {
            if (outer[2 * i] == lastX >> 2 && outer[(2 * i) + 1] == lastY >> 2)
            {
                lastSubBlock = i;
                break;
            }
        }

        for (int p = 15; p >= 0; p--)
        {
            if (inner[2 * p] == (lastX & 3) && inner[(2 * p) + 1] == (lastY & 3))
            {
                lastScanPos = p;
                break;
            }
        }

        if (lastSubBlock < 0)
        {
            throw HevcErrors.Corrupt("The last significant coefficient is outside its transform block.");
        }

        Span<bool> codedSubBlock = stackalloc bool[64];
        codedSubBlock.Clear();
        bool transformSkipContext = _sps.TransformSkipContext && (transformSkip || _cuBypass);
        int sbType = (c == 0 ? 2 : 0) + (transformSkip || _cuBypass ? 1 : 0);
        int greater1State = 1;
        Span<int> positions = stackalloc int[16];
        Span<int> levels = stackalloc int[16];
        int maxRow = 0, maxColumn = 0;
        for (int i = lastSubBlock; i >= 0; i--)
        {
            int xS = outer[2 * i], yS = outer[(2 * i) + 1];
            bool right = xS + 1 < sbWidth && codedSubBlock[(yS * sbWidth) + xS + 1];
            bool below = yS + 1 < sbWidth && codedSubBlock[((yS + 1) * sbWidth) + xS];
            bool inferSbDcSig = false;
            if (i < lastSubBlock && i > 0)
            {
                int ctxInc = Math.Min((right ? 1 : 0) + (below ? 1 : 0), 1) + (c == 0 ? 0 : 2);
                codedSubBlock[(yS * sbWidth) + xS] = _cabac.Decode(ref _contexts[HevcContexts.CodedSubBlock + ctxInc]) == 1;
                inferSbDcSig = true;
            }
            else
            {
                codedSubBlock[(yS * sbWidth) + xS] = true;
            }

            // sig_coeff_flag, in reverse scan order.
            int count16 = 0;
            int startPos = i == lastSubBlock ? lastScanPos - 1 : 15;
            if (i == lastSubBlock)
            {
                positions[count16++] = lastScanPos;
            }

            if (codedSubBlock[(yS * sbWidth) + xS])
            {
                int table = (((((c == 0 ? 0 : 1) * 4) + log2 - 2) * 3) + scanIdx) * 4;
                byte[] sigTable = SigContexts[table + (right ? 1 : 0) + (below ? 2 : 0)];
                for (int p = startPos; p >= 0; p--)
                {
                    int xP = inner[2 * p], yP = inner[(2 * p) + 1];
                    int xC = (xS << 2) + xP, yC = (yS << 2) + yP;
                    bool significant;
                    if (p == 0 && inferSbDcSig && count16 == 0)
                    {
                        significant = true;
                    }
                    else
                    {
                        int ctxInc = transformSkipContext ? (c == 0 ? 42 : 43) : sigTable[(yC << log2) + xC];
                        significant = _cabac.Decode(ref _contexts[HevcContexts.SigCoeff + ctxInc]) == 1;
                    }

                    if (significant)
                    {
                        positions[count16++] = p;
                    }
                }
            }

            if (count16 == 0)
            {
                continue;
            }

            // coeff_abs_level_greater1_flag (at most 8) and greater2_flag (the first greater than 1).
            int ctxSet = (i == 0 || c > 0 ? 0 : 2) + (greater1State == 0 ? 1 : 0);
            greater1State = 1;
            int firstGreater1 = -1;
            bool escape = count16 > 8;
            for (int k = 0; k < count16; k++)
            {
                levels[k] = 1;
            }

            for (int k = 0; k < Math.Min(count16, 8); k++)
            {
                int ctxInc = (ctxSet * 4) + greater1State + (c > 0 ? 16 : 0);
                int bin = _cabac.Decode(ref _contexts[HevcContexts.Greater1 + ctxInc]);
                if (bin == 1)
                {
                    greater1State = 0;
                    if (firstGreater1 < 0)
                    {
                        firstGreater1 = k;
                    }
                    else
                    {
                        escape = true;
                    }
                }
                else if (greater1State is > 0 and < 3)
                {
                    greater1State++;
                }

                levels[k] = bin + 1;
            }

            if (firstGreater1 >= 0)
            {
                int bin = _cabac.Decode(ref _contexts[HevcContexts.Greater2 + ctxSet + (c > 0 ? 4 : 0)]);
                levels[firstGreater1] = bin + 2;
                escape |= bin == 1;
            }

            // coeff_sign_flag: one bypass bin each, but the hidden sign (7.4.9.11).
            bool signHidden = signHidingAllowed && positions[0] - positions[count16 - 1] > 3;
            int signCount = signHidden ? count16 - 1 : count16;
            uint signs = _cabac.Bypass(signCount) << (32 - signCount);

            // coeff_abs_level_remaining.
            if (escape)
            {
                int rice = _sps.PersistentRiceAdaptation ? _statCoeff[sbType] / 4 : 0;
                bool updateStat = _sps.PersistentRiceAdaptation;
                bool firstCoeff2 = true;
                for (int k = 0; k < count16; k++)
                {
                    int baseLevel = k < 8 ? (firstCoeff2 ? 3 : 2) : 1;
                    if (levels[k] == baseLevel)
                    {
                        int remaining = Remaining(rice);
                        levels[k] = baseLevel + remaining;
                        if (levels[k] > 3 << rice)
                        {
                            rice = _sps.PersistentRiceAdaptation ? rice + 1 : Math.Min(rice + 1, 4);
                        }

                        if (updateStat)
                        {
                            int initial = _statCoeff[sbType] / 4;
                            if (remaining >= 3 << initial)
                            {
                                _statCoeff[sbType]++;
                            }
                            else if (2 * remaining < 1 << initial && _statCoeff[sbType] > 0)
                            {
                                _statCoeff[sbType]--;
                            }

                            updateStat = false;
                        }
                    }

                    if (levels[k] >= 2)
                    {
                        firstCoeff2 = false;
                    }
                }
            }

            // Place the levels with their signs.
            int sum = 0;
            for (int k = 0; k < count16; k++)
            {
                int p = positions[k];
                int xC = (xS << 2) + inner[2 * p], yC = (yS << 2) + inner[(2 * p) + 1];
                int level = levels[k];
                sum += level;
                if (k == count16 - 1 && signHidden)
                {
                    if ((sum & 1) != 0)
                    {
                        level = -level;
                    }
                }
                else
                {
                    if ((int)signs < 0)
                    {
                        level = -level;
                    }

                    signs <<= 1;
                }

                _coefficients[(yC * n) + xC] = level;
                maxRow = Math.Max(maxRow, yC);
                maxColumn = Math.Max(maxColumn, xC);
            }
        }

        Reconstruct(x0, y0, log2, c, predMode, transformSkip, rdpcm, rdpcmVertical, maxRow, maxColumn, resScale);
    }

    /// <summary>ctxIdxMap for sig_coeff_flag of 4x4 blocks (9.3.4.2.5); declared before <see cref="SigContexts"/>, built from it.</summary>
    private static readonly byte[] SigCtx4x4 = [0, 1, 4, 5, 2, 3, 4, 5, 6, 6, 8, 8, 7, 7, 8];

    /// <summary>
    /// The context index increment of sig_coeff_flag (9.3.4.2.5) without transform_skip_context_enabled_flag, by luma or chroma,
    /// log2TrafoSize, scanIdx and the coded sub-block flags to the right and below: one entry for each position in the block.
    /// </summary>
    private static readonly byte[][] SigContexts = MakeSigContexts();

    private static byte[][] MakeSigContexts()
    {
        byte[][] tables = new byte[2 * 4 * 3 * 4][];
        for (int chroma = 0; chroma < 2; chroma++)
        {
            for (int log2 = 2; log2 <= 5; log2++)
            {
                for (int scanIdx = 0; scanIdx < 3; scanIdx++)
                {
                    for (int prevCsbf = 0; prevCsbf < 4; prevCsbf++)
                    {
                        int n = 1 << log2;
                        byte[] table = new byte[n * n];
                        for (int yC = 0; yC < n; yC++)
                        {
                            for (int xC = 0; xC < n; xC++)
                            {
                                int xP = xC & 3, yP = yC & 3, xS = xC >> 2, yS = yC >> 2;
                                int sigCtx;
                                if (log2 == 2)
                                {
                                    // ctxIdxMap has 15 entries: the last position of a 4x4 block is never coded.
                                    sigCtx = (yC << 2) + xC < 15 ? SigCtx4x4[(yC << 2) + xC] : 0;
                                }
                                else if (xC + yC == 0)
                                {
                                    sigCtx = 0;
                                }
                                else
                                {
                                    sigCtx = prevCsbf switch
                                    {
                                        0 => xP + yP == 0 ? 2 : xP + yP < 3 ? 1 : 0,
                                        1 => yP == 0 ? 2 : yP == 1 ? 1 : 0,
                                        2 => xP == 0 ? 2 : xP == 1 ? 1 : 0,
                                        _ => 2,
                                    };
                                    if (chroma == 0)
                                    {
                                        if (xS + yS > 0)
                                        {
                                            sigCtx += 3;
                                        }

                                        sigCtx += log2 == 3 ? (scanIdx == 0 ? 9 : 15) : 21;
                                    }
                                    else
                                    {
                                        sigCtx += log2 == 3 ? 9 : 12;
                                    }
                                }

                                table[(yC * n) + xC] = (byte)(chroma == 0 ? sigCtx : 27 + sigCtx);
                            }
                        }

                        tables[(((((chroma * 4) + log2 - 2) * 3) + scanIdx) * 4) + prevCsbf] = table;
                    }
                }
            }
        }

        return tables;
    }

    /// <summary>LastSignificantCoeffX or Y from its prefix and, when the prefix is above 3, its suffix (7.4.9.11).</summary>
    private int LastPosition(int prefix)
    {
        if (prefix <= 3)
        {
            return prefix;
        }

        int bits = (prefix >> 1) - 1;
        int suffix = (int)_cabac.Bypass(bits);
        return ((1 << bits) * (2 + (prefix & 1))) + suffix;
    }

    /// <summary>coeff_abs_level_remaining (9.3.3.11): a Rice prefix of up to four, then Exp-Golomb of order rice + 1.</summary>
    private int Remaining(int rice)
    {
        int prefix = 0;
        while (_cabac.Bypass() == 1)
        {
            if (++prefix > 32)
            {
                throw HevcErrors.Corrupt("coeff_abs_level_remaining is too long.");
            }
        }

        if (prefix <= 3)
        {
            return (prefix << rice) + (int)_cabac.Bypass(rice);
        }

        int bits = prefix - 3 + rice;
        if (bits > 31)
        {
            throw HevcErrors.Corrupt("coeff_abs_level_remaining is too large.");
        }

        return (int)Math.Min(int.MaxValue, ((((1L << (prefix - 3)) + 3 - 1) << rice) + _cabac.Bypass(bits)));
    }

    /// <summary>Scales and transforms the levels (8.6.2), modifies the residual, and adds it to the predicted block.</summary>
    private void Reconstruct(int x0, int y0, int log2, int c, int predMode, bool transformSkip, bool rdpcm, bool rdpcmVertical, int maxRow, int maxColumn,
        int resScale)
    {
        int n = 1 << log2;
        int count = n * n;
        int bitDepth = c == 0 ? _sps.BitDepthLuma : _sps.BitDepthChroma;
        bool rotate = _sps.TransformSkipRotation && n == 4 && (transformSkip || _cuBypass);
        if (_cuBypass)
        {
            Array.Copy(_coefficients, _residual, count);
            if (rotate)
            {
                HevcTransform.Rotate(_residual, log2);
            }
        }
        else
        {
            HevcScalingList? lists = _pps.ScalingList ?? _sps.ScalingList;
            int[]? factors = null;
            if (_sps.ScalingListEnabled && lists is not null && !(transformSkip && n > 4))
            {
                // matrixId: intra Y, Cb, Cr (0 to 2), inter ones (3 to 5); 32x32 chroma only exists for 4:4:4 and uses them too.
                factors = lists.Factors[log2 - 2][c + (predMode < 0 ? 3 : 0)];
            }

            HevcTransform.Scale(_coefficients, log2, Qp(c), bitDepth, factors, maxRow, maxColumn);
            if (transformSkip)
            {
                if (rotate)
                {
                    HevcTransform.Rotate(_coefficients, log2);
                }

                HevcTransform.Skip(_coefficients, _residual, log2, bitDepth);
            }
            else
            {
                bool dst = c == 0 && n == 4 && predMode >= 0;
                if (rotate)
                {
                    maxRow = maxColumn = n - 1;
                }

                HevcTransform.Inverse(_coefficients, _residual, _scratch, log2, bitDepth, dst, maxRow, maxColumn);
            }
        }

        if (rdpcm)
        {
            HevcTransform.Dpcm(_residual, log2, rdpcmVertical);
        }

        if (resScale != 0)
        {
            AddCrossComponent(log2, resScale);
        }

        ushort[] plane = _picture.Planes[c];
        int stride = _picture.Widths[c];
        HevcTransform.Add(plane, (y0 * stride) + x0, stride, _residual, log2, bitDepth);

        // Leave the coefficients all zeros for the next block: the levels lie in rows 0 to maxRow and columns 0 to maxColumn,
        // anywhere after a rotation.
        if (rotate)
        {
            Array.Clear(_coefficients, 0, count);
        }
        else
        {
            for (int y = 0; y <= maxRow; y++)
            {
                Array.Clear(_coefficients, y * n, maxColumn + 1);
            }
        }
    }
}
