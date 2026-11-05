using System.Numerics;

namespace Egami.Formats.Jpeg2000;

/// <summary>
/// The HT block decoder of High-Throughput JPEG 2000 (T.814 clause 7): the HT cleanup pass, with its MEL, VLC and MagSgn
/// bit-streams, then the HT SigProp and MagRef passes over the HT refinement segment.
/// </summary>
internal sealed partial class J2kTier1
{
    // Per-sample state in the HT grid.
    private const byte HtSig = 1;
    private const byte HtNeg = 2;
    private const byte HtRefined = 4;
    private const byte HtBit = 8;

    /// <summary>MEL_E, the MEL exponent table (Table 2).</summary>
    private static ReadOnlySpan<byte> MelExponents => [0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 5];

    // The HT grid: the code-block padded to whole quads with a border of one sample, magnitudes and states.
    private uint[] _htMagnitudes = [];
    private byte[] _htStates = [];
    private int[] _htExponents = [];
    private int _htStride;

    // The cleanup segment: where it starts in _bytes, its length Lcup and prefix length Pcup.
    private int _cupStart;
    private int _lcup;
    private int _pcup;

    // MagSgn bit-stream: bits from the front of the segment, little-endian (7.1.2).
    private ulong _msBits;
    private int _msCount;
    private int _msPos;
    private int _msLast;

    // MEL bit-stream: bits from byte Pcup on, big-endian (7.1.3), and the MEL decoder state (7.3.3).
    private int _melPos;
    private int _melBits;
    private int _melTmp;
    private int _melK;
    private int _melRun;
    private bool _melOne;

    // VLC bit-stream: bits from the end of the segment backwards, little-endian (7.1.4).
    private ulong _vlcBits;
    private int _vlcCount;
    private int _vlcPos;
    private int _vlcLast;

    // SigProp (forward) and MagRef (backward) bit-streams of the refinement segment (7.1.5 and 7.1.6).
    private int _refStart;
    private int _lref;
    private int _spPos;
    private int _spBits;
    private int _spTmp;
    private int _spLast;
    private int _mrPos;
    private int _mrBits;
    private int _mrTmp;
    private int _mrLast;

    // Set when a bit-stream runs past its bytes or breaks a rule of clause 7.1: the data is damaged.
    private bool _htDamage;

    /// <summary>Decodes an HT code-block and writes its coefficients as <see cref="Decode"/> does.</summary>
    private void DecodeHt(J2kCodeBlock block, J2kBand band, J2kTileComponent tc, Span<int> integers, Span<float> floats, int offset, int stride)
    {
        int width = block.Width;
        int height = block.Height;
        bool reversible = tc.Style.Reversible;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // The last HT set with a cleanup segment (B.1 to B.3): segments run cleanup, refinement, cleanup, refinement...
        List<J2kSegment> segments = block.Segments;
        int set = -1;
        for (int s = 0; s < segments.Count; s += 2)
        {
            if (segments[s].KeptPasses > 0 && segments[s].Length > 0)
            {
                set = s / 2;
            }
        }

        if (block.Kind != J2kBlockKind.Ht || set < 0)
        {
            Clear(integers, floats, offset, stride, width, height, reversible);
            return;
        }

        J2kSegment cleanup = segments[2 * set];
        J2kSegment refinement = 2 * set + 1 < segments.Count ? segments[(2 * set) + 1] : default;
        int z = refinement.KeptPasses > 0 && refinement.Length > 0 ? 1 + Math.Min(refinement.KeptPasses, 2) : 1;
        int skipped = block.ZeroPlanes + (block.Placeholders / 3) + set;
        int planes = band.MagnitudePlanes + tc.RoiShift;

        Prepare(width, height, (tc.Style.BlockStyle & 8) != 0, band.Orientation);
        Gather(block);
        int at = StatesOffset;
        for (int s = 0; s < 2 * set; s++)
        {
            at += segments[s].Length + 2;
        }

        _htDamage = false;
        PrepareHt(width, height);
        if (cleanup.Length < 2 || !Cleanup(at, cleanup.Length, width, height))
        {
            Overrun++;
            Clear(integers, floats, offset, stride, width, height, reversible);
            return;
        }

        if (z > 1)
        {
            int refStart = at + cleanup.Length + 2;
            SigProp(refStart, refinement.Length, width, height);
            if (z > 2)
            {
                MagRef(width, height);
            }
        }

        if (_htDamage)
        {
            Overrun++;
        }

        Output(width, height, skipped, planes);
        Write(band, tc, integers, floats, offset, stride, reversible);
    }

    private void PrepareHt(int width, int height)
    {
        int quadsWide = (width + 1) >> 1;
        int quadsHigh = (height + 1) >> 1;
        _htStride = (2 * quadsWide) + 2;
        int size = _htStride * ((2 * quadsHigh) + 2);
        if (_htStates.Length < size)
        {
            _htStates = new byte[size];
            _htMagnitudes = new uint[size];
        }
        else
        {
            _htStates.AsSpan(0, size).Clear();
            _htMagnitudes.AsSpan(0, size).Clear();
        }

        if (_htExponents.Length < 2 * _htStride)
        {
            _htExponents = new int[2 * _htStride];
        }
        else
        {
            _htExponents.AsSpan(0, 2 * _htStride).Clear();
        }
    }

    /// <summary>
    /// Decodes the HT cleanup pass (7.3): significance, EMB patterns and unsigned residuals quad-pair by quad-pair from the MEL
    /// and VLC bit-streams, then each significant sample's magnitude and sign from the MagSgn bit-stream. Returns false when the
    /// segment's suffix length is impossible.
    /// </summary>
    private bool Cleanup(int start, int length, int width, int height)
    {
        byte[] data = _bytes;
        int scup = (data[start + length - 1] << 4) + (data[start + length - 2] & 0x0F);
        if (scup < 2 || scup > length || scup > 4079)
        {
            return false;
        }

        // modDcup (7.1.1): the last byte reads as 0xFF and the second last with its four low bits set.
        data[start + length - 1] = 0xFF;
        data[start + length - 2] |= 0x0F;
        _cupStart = start;
        _lcup = length;
        _pcup = length - scup;
        _msBits = 0;
        _msCount = 0;
        _msPos = 0;
        _msLast = 0;
        _melPos = _pcup;
        _melBits = 0;
        _melTmp = 0;
        _melK = 0;
        _melRun = 0;
        _melOne = false;
        _vlcPos = length - 3;
        _vlcLast = data[start + length - 2];
        int head = _vlcLast >> 4;
        _vlcCount = (head & 7) < 7 ? 4 : 3;
        _vlcBits = (ulong)(head & ((1 << _vlcCount) - 1));

        int quadsWide = (width + 1) >> 1;
        int quadsHigh = (height + 1) >> 1;
        int stride = _htStride;
        Span<int> above = _htExponents.AsSpan(0, stride);
        Span<int> below = _htExponents.AsSpan(stride, stride);
        Span<int> rho = stackalloc int[2];
        Span<int> offsets = stackalloc int[2];
        Span<int> known = stackalloc int[2];
        Span<int> ones = stackalloc int[2];
        Span<int> residuals = stackalloc int[2];
        for (int qy = 0; qy < quadsHigh; qy++)
        {
            bool initial = qy == 0;
            int top = ((2 * qy) + 1) * stride;
            below.Clear();
            for (int qx = 0; qx < quadsWide; qx += 2)
            {
                int count = Math.Min(2, quadsWide - qx);
                for (int i = 0; i < count; i++)
                {
                    int x = (2 * (qx + i)) + 1;
                    int context = Context(top + x, initial, qx + i == 0);
                    int entry = 0;
                    if (context != 0 || MelSymbol() != 0)
                    {
                        RefillVlc();
                        entry = J2kHtTables.Vlc[((initial ? 0 : 1) << 10) | (context << 7) | (int)(_vlcBits & 0x7F)];
                        int used = entry & 7;
                        _vlcBits >>= used;
                        _vlcCount -= used;
                    }

                    rho[i] = (entry >> 4) & 0xF;
                    offsets[i] = (entry >> 3) & 1;
                    ones[i] = (entry >> 8) & 0xF;
                    known[i] = (entry >> 12) & 0xF;
                    int q = rho[i];
                    _htStates[top + x] = (byte)(q & 1);
                    _htStates[top + stride + x] = (byte)((q >> 1) & 1);
                    _htStates[top + x + 1] = (byte)((q >> 2) & 1);
                    _htStates[top + stride + x + 1] = (byte)((q >> 3) & 1);
                }

                Residuals(initial, count, offsets, residuals);
                for (int i = 0; i < count; i++)
                {
                    int x = (2 * (qx + i)) + 1;
                    int q = rho[i];
                    if (q == 0)
                    {
                        continue;
                    }

                    int kappa = 1;
                    if (!initial && (q & (q - 1)) != 0)
                    {
                        int e = Math.Max(Math.Max(above[x - 1], above[x]), Math.Max(above[x + 1], above[x + 2]));
                        kappa = Math.Max(1, e - 1);
                    }

                    int bound = kappa + residuals[i];
                    for (int j = 0; j < 4; j++)
                    {
                        if (((q >> j) & 1) == 0)
                        {
                            continue;
                        }

                        int index = top + x + ((j >> 1) & 1) + ((j & 1) * stride);
                        int bits = bound - ((known[i] >> j) & 1);
                        if (bits > 32)
                        {
                            _htDamage = true;
                            return false;
                        }

                        ulong v = MagSgn(bits) + ((ulong)((ones[i] >> j) & 1) << bits);
                        ulong magnitude = (v >> 1) + 1;
                        if (magnitude > uint.MaxValue)
                        {
                            _htDamage = true;
                            return false;
                        }

                        _htMagnitudes[index] = (uint)magnitude;
                        _htStates[index] |= (byte)((v & 1) != 0 ? HtSig | HtNeg : HtSig);
                        if ((j & 1) != 0)
                        {
                            below[x + (j >> 1)] = 64 - BitOperations.LeadingZeroCount((2 * magnitude) - 1);
                        }
                    }
                }
            }

            below.CopyTo(above);
        }

        // Padded samples are never significant (7.2).
        if ((width & 1) != 0 || (height & 1) != 0)
        {
            for (int y = 0; y < 2 * quadsHigh; y++)
            {
                for (int x = 0; x < 2 * quadsWide; x++)
                {
                    if (x >= width || y >= height)
                    {
                        _htStates[((y + 1) * stride) + x + 1] = 0;
                        _htMagnitudes[((y + 1) * stride) + x + 1] = 0;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>Returns a quad's context c_q from its neighbours' significance (formulas 1 and 2 of 7.3.5).</summary>
    private int Context(int at, bool initial, bool left)
    {
        byte[] s = _htStates;
        int stride = _htStride;
        if (initial)
        {
            if (left)
            {
                return 0;
            }

            int f = s[at - 2] & HtSig;
            int sf = s[at - 2 + stride] & HtSig;
            int w = s[at - 1] & HtSig;
            int sw = s[at - 1 + stride] & HtSig;
            return (f | sf) + (2 * w) + (4 * sw);
        }

        int nw = s[at - stride - 1] & HtSig;
        int n = s[at - stride] & HtSig;
        int ne = s[at - stride + 1] & HtSig;
        int nf = s[at - stride + 2] & HtSig;
        int west = (s[at - 1] | s[at - 1 + stride]) & HtSig;
        return (nw | n) + (2 * west) + (4 * (ne | nf));
    }

    /// <summary>Decodes the unsigned residuals of a quad-pair (7.3.6), U-VLC prefixes, suffixes and extensions interleaved.</summary>
    private void Residuals(bool initial, int count, ReadOnlySpan<int> offsets, Span<int> residuals)
    {
        residuals.Clear();
        bool both = count == 2 && offsets[0] != 0 && offsets[1] != 0;
        if (initial && both)
        {
            int prefix1;
            int prefix2;
            int add = 0;
            if (MelSymbol() == 1)
            {
                prefix1 = UPrefix();
                prefix2 = UPrefix();
                add = 2;
            }
            else
            {
                prefix1 = UPrefix();
                prefix2 = prefix1 > 2 ? VlcBits(1) + 1 : UPrefix();
            }

            int suffix1 = USuffix(prefix1);
            int suffix2 = USuffix(prefix2);
            int extension1 = UExtension(suffix1);
            int extension2 = UExtension(suffix2);
            residuals[0] = add + prefix1 + suffix1 + (4 * extension1);
            residuals[1] = add + prefix2 + suffix2 + (4 * extension2);
            return;
        }

        int p1 = offsets[0] != 0 ? UPrefix() : 0;
        int p2 = count == 2 && offsets[1] != 0 ? UPrefix() : 0;
        int s1 = USuffix(p1);
        int s2 = USuffix(p2);
        int e1 = UExtension(s1);
        int e2 = UExtension(s2);
        residuals[0] = p1 + s1 + (4 * e1);
        residuals[1] = p2 + s2 + (4 * e2);
    }

    private int UPrefix()
    {
        if (VlcBits(1) != 0)
        {
            return 1;
        }

        if (VlcBits(1) != 0)
        {
            return 2;
        }

        return VlcBits(1) != 0 ? 3 : 5;
    }

    private int USuffix(int prefix) => prefix < 3 ? 0 : prefix == 3 ? VlcBits(1) : VlcBits(5);

    private int UExtension(int suffix) => suffix < 28 ? 0 : VlcBits(4);

    /// <summary>Takes <paramref name="count"/> bits (at most 7) from the VLC bit-stream, the first in the least significant bit.</summary>
    private int VlcBits(int count)
    {
        RefillVlc();
        int value = (int)(_vlcBits & ((1UL << count) - 1));
        _vlcBits >>= count;
        _vlcCount -= count;
        return value;
    }

    /// <summary>Fills the VLC bit buffer from the bytes before the last one read (importVLCBit, 7.1.4).</summary>
    private void RefillVlc()
    {
        while (_vlcCount <= 56)
        {
            // Bytes before the VLC byte-stream read as 0: the buffer runs ahead of the bits the codewords take.
            int b = _vlcPos >= _pcup ? _bytes[_cupStart + _vlcPos] : 0;

            int count = _vlcLast > 0x8F && (b & 0x7F) == 0x7F ? 7 : 8;
            _vlcBits |= (ulong)(b & ((1 << count) - 1)) << _vlcCount;
            _vlcCount += count;
            _vlcLast = b;
            _vlcPos--;
        }
    }

    /// <summary>Takes <paramref name="count"/> bits (at most 32) from the MagSgn bit-stream (importMagSgnBit, 7.1.2).</summary>
    private ulong MagSgn(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        while (_msCount < count)
        {
            int bits = _msLast == 0xFF ? 7 : 8;
            int b;
            if (_msPos < _pcup)
            {
                b = _bytes[_cupStart + _msPos];
            }
            else if (_msPos == _pcup)
            {
                b = 0xFF;
            }
            else
            {
                b = 0;
                _htDamage = true;
            }

            _msBits |= (ulong)(b & ((1 << bits) - 1)) << _msCount;
            _msCount += bits;
            _msLast = b;
            _msPos++;
        }

        ulong value = _msBits & ((1UL << count) - 1);
        _msBits >>= count;
        _msCount -= count;
        return value;
    }

    /// <summary>Decodes the next MEL symbol (decodeMELSym, 7.3.3).</summary>
    private int MelSymbol()
    {
        if (_melRun == 0 && !_melOne)
        {
            int exponent = MelExponents[_melK];
            if (MelBit() == 1)
            {
                _melRun = 1 << exponent;
                _melK = Math.Min(12, _melK + 1);
            }
            else
            {
                _melRun = 0;
                while (exponent > 0)
                {
                    _melRun = (2 * _melRun) + MelBit();
                    exponent--;
                }

                _melK = Math.Max(0, _melK - 1);
                _melOne = true;
            }
        }

        if (_melRun > 0)
        {
            _melRun--;
            return 0;
        }

        _melOne = false;
        return 1;
    }

    /// <summary>Takes the next MEL bit (importMELBit, 7.1.3).</summary>
    private int MelBit()
    {
        if (_melBits == 0)
        {
            _melBits = _melTmp == 0xFF ? 7 : 8;
            if (_melPos < _lcup)
            {
                _melTmp = _bytes[_cupStart + _melPos];
                _melPos++;
            }
            else
            {
                _melTmp = 0xFF;
            }
        }

        _melBits--;
        return (_melTmp >> _melBits) & 1;
    }

    /// <summary>
    /// Decodes the HT SigProp pass (7.4): in stripes of four rows and groups of four columns, each insignificant sample with a
    /// significant neighbour, or a newly significant one earlier in the scan, takes a refinement bit; then the newly significant
    /// samples of the group take their signs.
    /// </summary>
    private void SigProp(int start, int length, int width, int height)
    {
        _refStart = start;
        _lref = length;
        _spPos = 0;
        _spBits = 0;
        _spTmp = 0;
        _spLast = 0;
        _mrPos = length - 1;
        _mrBits = 0;
        _mrTmp = 0;
        _mrLast = 0xFF;
        byte[] s = _htStates;
        int stride = _htStride;
        for (int y0 = 0; y0 < height; y0 += 4)
        {
            int rows = Math.Min(4, height - y0);
            for (int x0 = 0; x0 < width; x0 += 4)
            {
                int columns = Math.Min(4, width - x0);
                for (int x = x0; x < x0 + columns; x++)
                {
                    for (int y = y0; y < y0 + rows; y++)
                    {
                        int at = ((y + 1) * stride) + x + 1;
                        if ((s[at] & HtSig) != 0)
                        {
                            continue;
                        }

                        // Significance from the cleanup pass anywhere around, except below the stripe in vertically causal mode.
                        int sig = s[at - stride - 1] | s[at - stride] | s[at - stride + 1] | s[at - 1] | s[at + 1];
                        bool south = y + 1 < height && (!_causal || y + 1 < y0 + 4);
                        if (south)
                        {
                            sig |= s[at + stride - 1] | s[at + stride] | s[at + stride + 1];
                        }

                        // Refinement bits already decoded in this pass: the column to the left, the samples above in this
                        // column, and the row above the stripe.
                        int refined = s[at - 1] | s[at - stride - 1] | s[at - stride];
                        if (y == y0)
                        {
                            refined |= s[at - stride + 1];
                        }

                        if (south && y + 1 < y0 + 4)
                        {
                            refined |= s[at + stride - 1];
                        }

                        if ((sig & HtSig) != 0 || (refined & HtBit) != 0)
                        {
                            s[at] |= (byte)(SigPropBit() != 0 ? HtRefined | HtBit : HtRefined);
                        }
                    }
                }

                for (int x = x0; x < x0 + columns; x++)
                {
                    for (int y = y0; y < y0 + rows; y++)
                    {
                        int at = ((y + 1) * stride) + x + 1;
                        if ((s[at] & (HtSig | HtBit)) == HtBit && SigPropBit() != 0)
                        {
                            s[at] |= HtNeg;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Decodes the HT MagRef pass (7.5): each sample significant in the cleanup pass takes one more magnitude bit.</summary>
    private void MagRef(int width, int height)
    {
        byte[] s = _htStates;
        int stride = _htStride;
        for (int y0 = 0; y0 < height; y0 += 4)
        {
            int rows = Math.Min(4, height - y0);
            for (int x = 0; x < width; x++)
            {
                for (int y = y0; y < y0 + rows; y++)
                {
                    int at = ((y + 1) * stride) + x + 1;
                    if ((s[at] & HtSig) != 0)
                    {
                        s[at] |= (byte)(MagRefBit() != 0 ? HtRefined | HtBit : HtRefined);
                    }
                }
            }
        }
    }

    /// <summary>Takes the next SigProp bit (importSigPropBit, 7.1.5).</summary>
    private int SigPropBit()
    {
        if (_spBits == 0)
        {
            _spBits = _spLast == 0xFF ? 7 : 8;
            if (_spPos < _lref)
            {
                _spTmp = _bytes[_refStart + _spPos];
                _spPos++;
                _htDamage |= (_spTmp & (1 << _spBits)) != 0;
            }
            else
            {
                _spTmp = 0;
            }

            _spLast = _spTmp;
        }

        int bit = _spTmp & 1;
        _spTmp >>= 1;
        _spBits--;
        return bit;
    }

    /// <summary>Takes the next MagRef bit (importMagRefBit, 7.1.6).</summary>
    private int MagRefBit()
    {
        if (_mrBits == 0)
        {
            if (_mrPos >= 0)
            {
                _mrTmp = _bytes[_refStart + _mrPos];
                _mrPos--;
            }
            else
            {
                _mrTmp = 0;
            }

            _mrBits = _mrLast > 0x8F && (_mrTmp & 0x7F) == 0x7F ? 7 : 8;
            _mrLast = _mrTmp;
        }

        int bit = _mrTmp & 1;
        _mrTmp >>= 1;
        _mrBits--;
        return bit;
    }

    /// <summary>
    /// Turns the decoded magnitudes, refinement bits and signs into the doubled magnitudes <see cref="Write"/> takes (7.6): the
    /// cleanup magnitude covers the top S_blk + 1 of the band's bit-planes, a refinement bit the next one, and the value sits a
    /// half step above the last plane decoded.
    /// </summary>
    private void Output(int width, int height, int skipped, int planes)
    {
        int stride = _htStride;
        for (int y = 0; y < height; y++)
        {
            Span<Coefficient> row = _coefficients.AsSpan(((y + 1) * _stride) + 1, width);
            for (int x = 0; x < width; x++)
            {
                int at = ((y + 1) * stride) + x + 1;
                byte state = _htStates[at];
                ulong value = _htMagnitudes[at];
                int decoded = skipped + 1;
                if ((state & HtRefined) != 0)
                {
                    value = (value << 1) | (uint)((state >> 3) & 1);
                    decoded++;
                }

                if (value == 0)
                {
                    continue;
                }

                int low = planes - decoded;
                if (low < 0)
                {
                    value >>= -low;
                    low = 0;
                    _htDamage = true;
                }

                ulong magnitude = ((2 * value) + 1) << Math.Min(low, 32);
                if (magnitude > int.MaxValue)
                {
                    magnitude = int.MaxValue;
                    _htDamage = true;
                }

                row[x].Magnitude = (int)magnitude;
                row[x].Flags = (state & HtNeg) != 0 ? Neg : 0;
            }
        }
    }
}
