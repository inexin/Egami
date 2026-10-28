using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Audio.Codecs.Mp3;

/// <summary>What <see cref="Mp3FrameDecoder.Decode"/> made of a frame.</summary>
internal enum Mp3FrameResult
{
    /// <summary>The frame decoded.</summary>
    Decoded,

    /// <summary>
    /// The frame's main data starts in earlier frames that were not decoded (the first frames after a seek or a cut), so it
    /// came out as silence; its data was kept for the frames after it.
    /// </summary>
    NoReservoir,

    /// <summary>The frame's data breaks the format; nothing was decoded and the decoder was reset.</summary>
    Corrupt,
}

/// <summary>
/// Decodes MPEG audio frames (ISO/IEC 11172-3 and 13818-3, Layers I, II and III of MPEG-1, MPEG-2 and MPEG-2.5) to planar
/// float samples, keeping the state that runs across frames: the Layer III bit reservoir, the IMDCT overlap and the synthesis
/// filterbank's history. Closely translated from minimp3 (CC0, https://github.com/lieff/minimp3), whose structure and
/// arithmetic it keeps so the output matches.
/// </summary>
[SkipLocalsInit]
internal sealed class Mp3FrameDecoder
{
    /// <summary>The most samples per channel a frame decodes to.</summary>
    public const int MaxSamples = 1152;

    private const int MaxReservoir = 511;
    private const int MaxPayload = MpegAudioHeader.MaxFreeFormatBytes;
    private const int ShortBlock = 2;
    private const int StopBlock = 3;

    // minimp3's MAX_SCFI: (255 + BITS_DEQUANTIZER_OUT * 4 - 210 + 3) & ~3 with BITS_DEQUANTIZER_OUT = -1.
    private const int MaxScfi = 44;

    // Room past the main data for the Huffman decoder's look-ahead and for damaged big_values that run past part2_3_length.
    private const int MainDataSlack = 4096;

    private static readonly byte[] s_scfLong = Mp3Tables.ScfLong.ToArray();
    private static readonly byte[] s_scfShort = Mp3Tables.ScfShort.ToArray();
    private static readonly byte[] s_scfMixed = Mp3Tables.ScfMixed.ToArray();
    private static readonly float[] s_deqL12 = BuildLayer12Dequantizers();

    private readonly float[] _overlap = new float[2 * 9 * 32];
    private readonly float[] _qmf = new float[15 * 2 * 32];
    private readonly byte[] _reservoir = new byte[MaxReservoir];
    private readonly byte[] _mainData = new byte[MaxReservoir + MaxPayload + MainDataSlack];
    private readonly Granule[] _granules = new Granule[4];
    private readonly float[] _grbuf = new float[2 * 576];
    private readonly float[] _scf = new float[40];
    private readonly float[] _syn = new float[(18 + 15) * 64];
    private readonly byte[] _istPos = new byte[2 * 39];
    private readonly byte[] _iscf = new byte[40];
    private readonly float[] _l12Scf = new float[3 * 64];
    private readonly byte[] _bitAlloc = new byte[64];
    private readonly byte[] _scfCod = new byte[64];
    private int _reserv;
    private int _mainLength;
    private int _mainPos;
    private int _totalBands;
    private int _stereoBands;

    /// <summary>Gets how many bits after the CRC word the last frame's CRC covers (its side information or bit allocation).</summary>
    public int ProtectedBits { get; private set; }

    /// <summary>Drops the state carried between frames, as after a seek.</summary>
    public void Reset()
    {
        Array.Clear(_overlap);
        Array.Clear(_qmf);
        _reserv = 0;
    }

    /// <summary>
    /// Decodes one whole frame (header first, exactly its length) into <paramref name="left"/> and <paramref name="right"/>
    /// (the same span for mono), <see cref="MpegAudioHeader.SamplesPerFrame"/> samples each, scaled to ±1.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="left">The first channel's samples.</param>
    /// <param name="right">The second channel's samples; for mono, the same span as <paramref name="left"/>.</param>
    /// <returns>What came of it.</returns>
    public Mp3FrameResult Decode(ReadOnlySpan<byte> frame, Span<float> left, Span<float> right)
    {
        MpegAudioHeader h = new(frame);
        int channels = h.Channels;
        Bits bs = new(frame[MpegAudioHeader.Size..]);
        if (h.HasCrc)
        {
            bs.Get(16);
        }

        if (h.Layer == 3)
        {
            if (frame.Length - MpegAudioHeader.Size > MaxPayload)
            {
                Reset();
                return Mp3FrameResult.Corrupt;
            }

            int mainDataBegin = ReadSideInfo(ref bs, h);
            if (mainDataBegin < 0 || bs.Pos > bs.Limit)
            {
                Reset();
                return Mp3FrameResult.Corrupt;
            }

            ProtectedBits = bs.Pos - (h.HasCrc ? 16 : 0);
            bool success = RestoreReservoir(frame[MpegAudioHeader.Size..], ref bs, mainDataBegin);
            int granules = h.IsMpeg1 ? 2 : 1;
            if (success)
            {
                for (int igr = 0; igr < granules; igr++)
                {
                    Array.Clear(_grbuf);
                    DecodeLayer3(h, igr * channels, channels);
                    SynthGranule(_grbuf, 18, channels, left[(igr * 576)..], right[(igr * 576)..]);
                }
            }
            else
            {
                left[..(granules * 576)].Clear();
                right[..(granules * 576)].Clear();
            }

            SaveReservoir();
            return success ? Mp3FrameResult.Decoded : Mp3FrameResult.NoReservoir;
        }

        ProtectedBits = ReadScaleInfo(h, ref bs) - (h.HasCrc ? 16 : 0);
        Array.Clear(_grbuf);
        int layer = h.Layer;
        int offset = 0;
        for (int i = 0, igr = 0; igr < 3; igr++)
        {
            i += DequantizeGranule(i, ref bs, layer | 1);
            if (i == 12)
            {
                i = 0;
                ApplyScf384(igr);
                SynthGranule(_grbuf, 12, channels, left[offset..], right[offset..]);
                Array.Clear(_grbuf);
                offset += 384;
            }

            if (bs.Pos > bs.Limit)
            {
                Reset();
                return Mp3FrameResult.Corrupt;
            }
        }

        return Mp3FrameResult.Decoded;
    }

    private static float[] BuildLayer12Dequantizers()
    {
        // minimp3's g_deq_L12: 2^-20 / (2^b - 1) at the three scale factor fractions, for the 15 bit counts and the 3, 5 and 9 level groupings.
        ReadOnlySpan<int> divisors = [3, 7, 15, 31, 63, 127, 255, 511, 1023, 2047, 4095, 8191, 16383, 32767, 65535, 3, 5, 9];
        float[] table = new float[18 * 3];
        for (int i = 0; i < 18; i++)
        {
            table[(i * 3) + 0] = 9.53674316e-07f / divisors[i];
            table[(i * 3) + 1] = 7.56931807e-07f / divisors[i];
            table[(i * 3) + 2] = 6.00777173e-07f / divisors[i];
        }

        return table;
    }

    // ---- Layers I and II ----

    /// <summary>Reads the bit allocation, scale factor selection and scale factors; returns the position where the CRC's coverage ends.</summary>
    private int ReadScaleInfo(MpegAudioHeader h, ref Bits bs)
    {
        ReadOnlySpan<byte> codes = Mp3Tables.BitAllocCodes;
        int mode = h.Mode;
        int stereoBands = mode == 3 ? 0 : mode == 1 ? (h.ModeExtension << 2) + 4 : 32;

        // The allocation table: (code table offset, field width, band count) groups.
        ReadOnlySpan<byte> alloc;
        int bands;
        if (h.Layer == 1)
        {
            alloc = [76, 4, 32];
            bands = 32;
        }
        else if (!h.IsMpeg1)
        {
            alloc = [60, 4, 4, 44, 3, 7, 44, 2, 19];
            bands = 30;
        }
        else
        {
            int rateIndex = h.SampleRateIndex;
            int kbps = h.BitrateKbps >> (mode != 3 ? 1 : 0);
            if (kbps == 0)
            {
                kbps = 192;
            }

            alloc = [0, 4, 3, 16, 4, 8, 32, 3, 12, 40, 2, 7];
            bands = 27;
            if (kbps < 56)
            {
                alloc = [44, 4, 2, 44, 3, 10];
                bands = rateIndex == 2 ? 12 : 8;
            }
            else if (kbps >= 96 && rateIndex != 1)
            {
                bands = 30;
            }
        }

        _totalBands = bands;
        _stereoBands = Math.Min(stereoBands, bands);

        int k = 0;
        int width = 0;
        int table = 0;
        int group = 0;
        for (int i = 0; i < _totalBands; i++)
        {
            if (i == k)
            {
                k += alloc[group + 2];
                width = alloc[group + 1];
                table = alloc[group];
                group += 3;
            }

            byte ba = codes[table + (int)bs.Get(width)];
            _bitAlloc[2 * i] = ba;
            if (i < _stereoBands)
            {
                ba = codes[table + (int)bs.Get(width)];
            }

            _bitAlloc[(2 * i) + 1] = _stereoBands != 0 ? ba : (byte)0;
        }

        bool layer1 = h.Layer == 1;
        for (int i = 0; i < 2 * _totalBands; i++)
        {
            _scfCod[i] = _bitAlloc[i] != 0 ? (layer1 ? (byte)2 : (byte)bs.Get(2)) : (byte)6;
        }

        // The CRC covers everything up to here.
        int afterAllocation = bs.Pos;
        ReadScalefactors(ref bs, 2 * _totalBands);
        for (int i = _stereoBands; i < _totalBands; i++)
        {
            _bitAlloc[(2 * i) + 1] = 0;
        }

        return afterAllocation;
    }

    private void ReadScalefactors(ref Bits bs, int bands)
    {
        float[] deq = s_deqL12;
        int s0 = 0;
        for (int i = 0; i < bands; i++)
        {
            float s = 0;
            int ba = _bitAlloc[i];
            int mask = ba != 0 ? 4 + ((19 >> _scfCod[i]) & 3) : 0;
            for (int m = 4; m != 0; m >>= 1)
            {
                if ((mask & m) != 0)
                {
                    int b = (int)bs.Get(6);
                    s = deq[(ba * 3) - 6 + (b % 3)] * (1 << 21 >> (b / 3));
                }

                _l12Scf[s0++] = s;
            }
        }
    }

    private int DequantizeGranule(int start, ref Bits bs, int groupSize)
    {
        float[] g = _grbuf;
        for (int j = 0; j < 4; j++)
        {
            int dst = start + (groupSize * j);
            int choff = 576;
            for (int i = 0; i < 2 * _totalBands; i++)
            {
                int ba = _bitAlloc[i];
                if (ba != 0)
                {
                    if (ba < 17)
                    {
                        int half = (1 << (ba - 1)) - 1;
                        for (int k = 0; k < groupSize; k++)
                        {
                            g[dst + k] = (int)bs.Get(ba) - half;
                        }
                    }
                    else
                    {
                        uint mod = (2u << (ba - 17)) + 1;
                        uint code = bs.Get((int)(mod + 2 - (mod >> 3)));
                        for (int k = 0; k < groupSize; k++, code /= mod)
                        {
                            g[dst + k] = (int)(code % mod) - (int)(mod / 2);
                        }
                    }
                }

                dst += choff;
                choff = 18 - choff;
            }
        }

        return groupSize * 4;
    }

    private void ApplyScf384(int igr)
    {
        float[] g = _grbuf;
        int sb = _stereoBands * 18;
        Array.Copy(g, sb, g, 576 + sb, (_totalBands - _stereoBands) * 18);
        int dst = 0;
        int scf = igr;
        for (int i = 0; i < _totalBands; i++, dst += 18, scf += 6)
        {
            float l = _l12Scf[scf];
            float r = _l12Scf[scf + 3];
            for (int k = 0; k < 12; k++)
            {
                g[dst + k] *= l;
                g[dst + k + 576] *= r;
            }
        }
    }

    // ---- Layer III ----

    private int ReadSideInfo(ref Bits bs, MpegAudioHeader h)
    {
        uint scfsi = 0;
        int mainDataBegin;
        int part23Sum = 0;
        int srIdx = h.RateTableIndex;
        srIdx -= srIdx != 0 ? 1 : 0;
        int grCount = h.IsMono ? 1 : 2;
        bool mpeg1 = h.IsMpeg1;
        if (mpeg1)
        {
            grCount *= 2;
            mainDataBegin = (int)bs.Get(9);
            scfsi = bs.Get(7 + grCount);
        }
        else
        {
            mainDataBegin = (int)(bs.Get(8 + grCount) >> grCount);
        }

        int g = 0;
        do
        {
            ref Granule gr = ref _granules[g];
            if (h.IsMono)
            {
                scfsi <<= 4;
            }

            gr.Part23Length = (int)bs.Get(12);
            part23Sum += gr.Part23Length;
            gr.BigValues = (int)bs.Get(9);
            if (gr.BigValues > 288)
            {
                return -1;
            }

            gr.GlobalGain = (int)bs.Get(8);
            gr.ScalefacCompress = (int)bs.Get(mpeg1 ? 4 : 9);
            gr.Sfb = s_scfLong;
            gr.SfbStart = srIdx * 23;
            gr.NLongSfb = 22;
            gr.NShortSfb = 0;
            uint tables;
            if (bs.Get(1) != 0)
            {
                gr.BlockType = (int)bs.Get(2);
                if (gr.BlockType == 0)
                {
                    return -1;
                }

                gr.MixedBlockFlag = (int)bs.Get(1);
                gr.Region0 = 7;
                gr.Region1 = 255;
                gr.Region2 = 255;
                if (gr.BlockType == ShortBlock)
                {
                    scfsi &= 0x0F0F;
                    if (gr.MixedBlockFlag == 0)
                    {
                        gr.Region0 = 8;
                        gr.Sfb = s_scfShort;
                        gr.SfbStart = srIdx * 40;
                        gr.NLongSfb = 0;
                        gr.NShortSfb = 39;
                    }
                    else
                    {
                        gr.Sfb = s_scfMixed;
                        gr.SfbStart = srIdx * 40;
                        gr.NLongSfb = mpeg1 ? 8 : 6;
                        gr.NShortSfb = 30;
                    }
                }

                tables = bs.Get(10);
                tables <<= 5;
                gr.SubblockGain0 = (int)bs.Get(3);
                gr.SubblockGain1 = (int)bs.Get(3);
                gr.SubblockGain2 = (int)bs.Get(3);
            }
            else
            {
                gr.BlockType = 0;
                gr.MixedBlockFlag = 0;
                tables = bs.Get(15);
                gr.Region0 = (int)bs.Get(4);
                gr.Region1 = (int)bs.Get(3);
                gr.Region2 = 255;
            }

            gr.Table0 = (int)(tables >> 10);
            gr.Table1 = (int)((tables >> 5) & 31);
            gr.Table2 = (int)(tables & 31);
            gr.Preflag = mpeg1 ? (int)bs.Get(1) : gr.ScalefacCompress >= 500 ? 1 : 0;
            gr.ScalefacScale = (int)bs.Get(1);
            gr.Count1Table = (int)bs.Get(1);
            gr.Scfsi = (int)((scfsi >> 12) & 15);
            scfsi <<= 4;
            g++;
        }
        while (--grCount != 0);

        if (part23Sum + bs.Pos > bs.Limit + (mainDataBegin * 8))
        {
            return -1;
        }

        return mainDataBegin;
    }

    private bool RestoreReservoir(ReadOnlySpan<byte> payload, ref Bits bs, int mainDataBegin)
    {
        int frameBytes = (bs.Limit - bs.Pos) / 8;
        int have = Math.Min(_reserv, mainDataBegin);
        _reservoir.AsSpan(Math.Max(0, _reserv - mainDataBegin), have).CopyTo(_mainData);
        payload.Slice(bs.Pos / 8, frameBytes).CopyTo(_mainData.AsSpan(have));
        _mainLength = have + frameBytes;
        _mainPos = 0;
        return _reserv >= mainDataBegin;
    }

    private void SaveReservoir()
    {
        int pos = (_mainPos + 7) / 8;
        int remains = (_mainLength) - pos;
        if (remains > MaxReservoir)
        {
            pos += remains - MaxReservoir;
            remains = MaxReservoir;
        }

        if (remains > 0)
        {
            _mainData.AsSpan(pos, remains).CopyTo(_reservoir);
        }

        _reserv = Math.Max(remains, 0);
    }

    private void DecodeLayer3(MpegAudioHeader h, int first, int channels)
    {
        for (int ch = 0; ch < channels; ch++)
        {
            ref Granule gr = ref _granules[first + ch];
            int limit = _mainPos + gr.Part23Length;
            DecodeScalefactors(h, ch, ref gr);
            Huffman(ch * 576, ref gr, limit);
        }

        if (channels == 2)
        {
            if (h.IsIntensityStereo)
            {
                IntensityStereo(h, first);
            }
            else if (h.IsMidSide)
            {
                MidSide(0, 576);
            }
        }

        for (int ch = 0; ch < channels; ch++)
        {
            ref Granule gr = ref _granules[first + ch];
            int aaBands = 31;
            int longBands = (gr.MixedBlockFlag != 0 ? 2 : 0) << (h.RateTableIndex == 2 ? 1 : 0);
            int buf = ch * 576;
            if (gr.NShortSfb != 0)
            {
                aaBands = longBands - 1;
                Reorder(buf + (longBands * 18), gr.Sfb, gr.SfbStart + gr.NLongSfb);
            }

            Antialias(buf, aaBands);
            Imdct(buf, ch * 9 * 32, gr.BlockType, longBands);
            ChangeSign(buf);
        }
    }

    /// <summary>Reads bits from the main data at <see cref="_mainPos"/>, as minimp3's get_bits on the scratch bitstream.</summary>
    private uint MainBits(int n)
    {
        Bits bs = new(_mainData.AsSpan(0, _mainLength)) { Pos = _mainPos };
        uint v = bs.Get(n);
        _mainPos = bs.Pos;
        return v;
    }

    private void DecodeScalefactors(MpegAudioHeader h, int ch, ref Granule gr)
    {
        ReadOnlySpan<byte> partitions = Mp3Tables.ScfPartitions;
        int partition = 28 * ((gr.NShortSfb != 0 ? 1 : 0) + (gr.NLongSfb == 0 ? 1 : 0));
        Span<byte> size = stackalloc byte[4];
        byte[] iscf = _iscf;
        int scfShift = gr.ScalefacScale + 1;
        int scfsi = gr.Scfsi;
        if (h.IsMpeg1)
        {
            int part = Mp3Tables.ScfcDecode[gr.ScalefacCompress];
            size[1] = size[0] = (byte)(part >> 2);
            size[3] = size[2] = (byte)(part & 3);
        }
        else
        {
            ReadOnlySpan<byte> mod = Mp3Tables.Mpeg2Mod;
            int ist = h.IsIntensityStereo && ch != 0 ? 1 : 0;
            int sfc = gr.ScalefacCompress >> ist;
            int k;
            int modprod;
            for (k = ist * 3 * 4; sfc >= 0; sfc -= modprod, k += 4)
            {
                modprod = 1;
                for (int i = 3; i >= 0; i--)
                {
                    size[i] = (byte)(sfc / modprod % mod[k + i]);
                    modprod *= mod[k + i];
                }
            }

            partition += k;
            scfsi = -16;
        }

        // L3_read_scalefactors.
        Span<byte> istPos = _istPos.AsSpan(ch * 39, 39);
        int sp = 0;
        int ip = 0;
        for (int i = 0; i < 4 && partitions[partition + i] != 0; i++, scfsi *= 2)
        {
            int cnt = partitions[partition + i];
            if ((scfsi & 8) != 0)
            {
                istPos.Slice(ip, cnt).CopyTo(iscf.AsSpan(sp));
            }
            else
            {
                int bits = size[i];
                if (bits == 0)
                {
                    iscf.AsSpan(sp, cnt).Clear();
                    istPos.Slice(ip, cnt).Clear();
                }
                else
                {
                    int maxScf = scfsi < 0 ? (1 << bits) - 1 : -1;
                    for (int k = 0; k < cnt; k++)
                    {
                        int s = (int)MainBits(bits);
                        istPos[ip + k] = (byte)(s == maxScf ? -1 : s);
                        iscf[sp + k] = (byte)s;
                    }
                }
            }

            ip += cnt;
            sp += cnt;
        }

        iscf[sp] = iscf[sp + 1] = iscf[sp + 2] = 0;

        if (gr.NShortSfb != 0)
        {
            int sh = 3 - scfShift;
            for (int i = 0; i < gr.NShortSfb; i += 3)
            {
                iscf[gr.NLongSfb + i + 0] += (byte)(gr.SubblockGain0 << sh);
                iscf[gr.NLongSfb + i + 1] += (byte)(gr.SubblockGain1 << sh);
                iscf[gr.NLongSfb + i + 2] += (byte)(gr.SubblockGain2 << sh);
            }
        }
        else if (gr.Preflag != 0)
        {
            ReadOnlySpan<byte> preamp = Mp3Tables.Preamp;
            for (int i = 0; i < 10; i++)
            {
                iscf[11 + i] += preamp[i];
            }
        }

        int gainExp = gr.GlobalGain - 4 - 210 - (h.IsMidSide ? 2 : 0);
        float gain = LdexpQ2(1 << (MaxScfi / 4), MaxScfi - gainExp);
        int n = gr.NLongSfb + gr.NShortSfb;
        for (int i = 0; i < n; i++)
        {
            _scf[i] = LdexpQ2(gain, iscf[i] << scfShift);
        }
    }

    private static float LdexpQ2(float y, int expQ2)
    {
        ReadOnlySpan<float> frac = Mp3Tables.ExpFrac;
        int e;
        do
        {
            e = Math.Min(30 * 4, expQ2);
            y *= frac[e & 3] * (1 << 30 >> (e >> 2));
        }
        while ((expQ2 -= e) > 0);
        return y;
    }

    private static float Pow43(int x)
    {
        ReadOnlySpan<float> pow43 = Mp3Tables.Pow43;
        if (x < 129)
        {
            return pow43[16 + x];
        }

        int mult = 256;
        if (x < 1024)
        {
            mult = 16;
            x <<= 3;
        }

        int sign = 2 * x & 64;
        float frac = (float)((x & 63) - sign) / ((x & ~63) + sign);
        return pow43[16 + ((x + sign) >> 6)] * (1f + (frac * ((4f / 3) + (frac * (2f / 9))))) * mult;
    }

    private void Huffman(int dst, ref Granule gr, int limit)
    {
        // The code tables are complete, so every lookup stays inside its table and those reads skip the bounds checks; the
        // reads of the main data and the writes of the granule stay checked.
        ref short tabs = ref MemoryMarshal.GetReference(Mp3Tables.Huffman);
        ref float pow43 = ref MemoryMarshal.GetReference(Mp3Tables.Pow43);
        byte[] buf = _mainData;
        float[] g = _grbuf;
        float[] scfs = _scf;
        byte[] sfbs = gr.Sfb;
        int sfb = gr.SfbStart;
        int scf = 0;
        float one = 0f;
        int ireg = 0;
        int bigValCnt = gr.BigValues;
        int pos = _mainPos;
        int next = pos / 8;
        uint cache = ((((((buf[next] * 256u) + buf[next + 1]) * 256u) + buf[next + 2]) * 256u) + buf[next + 3]) << (pos & 7);
        int sh = (pos & 7) - 8;
        next += 4;

        while (bigValCnt > 0)
        {
            int tabNum = ireg switch { 0 => gr.Table0, 1 => gr.Table1, _ => gr.Table2 };
            int sfbCnt = ireg switch { 0 => gr.Region0, 1 => gr.Region1, _ => gr.Region2 };
            ireg++;
            ref short codebook = ref Unsafe.Add(ref tabs, Mp3Tables.HuffmanIndex[tabNum]);
            int linbits = Mp3Tables.LinBits[tabNum];
            int np;
            do
            {
                np = sfbs[sfb++] / 2;
                int pairs = Math.Min(bigValCnt, np);
                one = scfs[scf++];
                if (pairs <= 0)
                {
                    // Only damaged side info gets here (the band list ended); minimp3 relies on big_values staying in range.
                    bigValCnt = 0;
                    break;
                }

                if (linbits == 0)
                {
                    // Tables 0 to 15: no escape, so each value is a pow43 entry with its sign.
                    do
                    {
                        int w = 5;
                        int leaf = Unsafe.Add(ref codebook, (int)(cache >> (32 - w)));
                        while (leaf < 0)
                        {
                            cache <<= w;
                            sh += w;
                            w = leaf & 7;
                            leaf = Unsafe.Add(ref codebook, (int)(cache >> (32 - w)) - (leaf >> 3));
                        }

                        int length = leaf >> 8;
                        cache <<= length;
                        sh += length;
                        for (int j = 0; j < 2; j++, dst++, leaf >>= 4)
                        {
                            int lsb = leaf & 0x0F;
                            g[dst] = Unsafe.Add(ref pow43, 16 + lsb - (16 * (int)(cache >> 31))) * one;
                            if (lsb != 0)
                            {
                                cache <<= 1;
                                sh += 1;
                            }
                        }

                        while (sh >= 0)
                        {
                            cache |= (uint)buf[next++] << sh;
                            sh -= 8;
                        }
                    }
                    while (--pairs != 0);
                    continue;
                }

                do
                {
                    int w = 5;
                    int leaf = Unsafe.Add(ref codebook, (int)(cache >> (32 - w)));
                    while (leaf < 0)
                    {
                        cache <<= w;
                        sh += w;
                        w = leaf & 7;
                        leaf = Unsafe.Add(ref codebook, (int)(cache >> (32 - w)) - (leaf >> 3));
                    }

                    int length = leaf >> 8;
                    cache <<= length;
                    sh += length;
                    for (int j = 0; j < 2; j++, dst++, leaf >>= 4)
                    {
                        int lsb = leaf & 0x0F;
                        if (lsb == 15)
                        {
                            lsb += (int)(cache >> (32 - linbits));
                            cache <<= linbits;
                            sh += linbits;
                            while (sh >= 0)
                            {
                                cache |= (uint)buf[next++] << sh;
                                sh -= 8;
                            }

                            g[dst] = one * Pow43(lsb) * ((int)cache < 0 ? -1 : 1);
                        }
                        else
                        {
                            g[dst] = Unsafe.Add(ref pow43, 16 + lsb - (16 * (int)(cache >> 31))) * one;
                        }

                        if (lsb != 0)
                        {
                            cache <<= 1;
                            sh += 1;
                        }
                    }

                    while (sh >= 0)
                    {
                        cache |= (uint)buf[next++] << sh;
                        sh -= 8;
                    }
                }
                while (--pairs != 0);
            }
            while ((bigValCnt -= np) > 0 && --sfbCnt >= 0);
        }

        ReadOnlySpan<byte> count1 = gr.Count1Table != 0 ? Mp3Tables.Count1B : Mp3Tables.Count1A;
        for (int np = 1 - bigValCnt; ; dst += 4)
        {
            int leaf = count1[(int)(cache >> 28)];
            if ((leaf & 8) == 0)
            {
                leaf = count1[(leaf >> 3) + (int)(cache << 4 >> (32 - (leaf & 3)))];
            }

            cache <<= leaf & 7;
            sh += leaf & 7;
            if ((next * 8) - 24 + sh > limit)
            {
                break;
            }

            if (--np == 0)
            {
                np = sfbs[sfb++] / 2;
                if (np == 0)
                {
                    break;
                }

                one = scfs[scf++];
            }

            if ((leaf & 128) != 0)
            {
                g[dst] = (int)cache < 0 ? -one : one;
                cache <<= 1;
                sh++;
            }

            if ((leaf & 64) != 0)
            {
                g[dst + 1] = (int)cache < 0 ? -one : one;
                cache <<= 1;
                sh++;
            }

            if (--np == 0)
            {
                np = sfbs[sfb++] / 2;
                if (np == 0)
                {
                    break;
                }

                one = scfs[scf++];
            }

            if ((leaf & 32) != 0)
            {
                g[dst + 2] = (int)cache < 0 ? -one : one;
                cache <<= 1;
                sh++;
            }

            if ((leaf & 16) != 0)
            {
                g[dst + 3] = (int)cache < 0 ? -one : one;
                cache <<= 1;
                sh++;
            }

            while (sh >= 0)
            {
                cache |= (uint)buf[next++] << sh;
                sh -= 8;
            }
        }

        _mainPos = limit;
    }

    private void MidSide(int left, int n)
    {
        float[] g = _grbuf;
        int right = left + 576;
        for (int i = 0; i < n; i++)
        {
            float a = g[left + i];
            float b = g[right + i];
            g[left + i] = a + b;
            g[right + i] = a - b;
        }
    }

    private void IntensityStereo(MpegAudioHeader h, int first)
    {
        ref Granule gr = ref _granules[first];
        float[] g = _grbuf;
        byte[] sfbs = gr.Sfb;
        int sfb0 = gr.SfbStart;
        Span<byte> istPos = _istPos.AsSpan(39, 39);
        Span<int> maxBand = stackalloc int[3];
        int nSfb = gr.NLongSfb + gr.NShortSfb;
        int maxBlocks = gr.NShortSfb != 0 ? 3 : 1;

        // L3_stereo_top_band over the right channel.
        maxBand[0] = maxBand[1] = maxBand[2] = -1;
        int right = 576;
        for (int i = 0; i < nSfb; i++)
        {
            int width = sfbs[sfb0 + i];
            for (int k = 0; k < width; k += 2)
            {
                if (g[right + k] != 0 || g[right + k + 1] != 0)
                {
                    maxBand[i % 3] = i;
                    break;
                }
            }

            right += width;
        }

        if (gr.NLongSfb != 0)
        {
            maxBand[0] = maxBand[1] = maxBand[2] = Math.Max(Math.Max(maxBand[0], maxBand[1]), maxBand[2]);
        }

        for (int i = 0; i < maxBlocks; i++)
        {
            int defaultPos = h.IsMpeg1 ? 3 : 0;
            int itop = nSfb - maxBlocks + i;
            int prev = itop - maxBlocks;
            istPos[itop] = maxBand[i] >= prev ? (byte)defaultPos : istPos[prev];
        }

        // L3_stereo_process.
        ReadOnlySpan<float> pan = Mp3Tables.Pan;
        int mpeg2Shift = _granules[first + 1].ScalefacCompress & 1;
        uint maxPos = h.IsMpeg1 ? 7u : 64u;
        bool ms = (h.B3 & 0x20) != 0;
        int left = 0;
        for (int i = 0; sfbs[sfb0 + i] != 0; i++)
        {
            int width = sfbs[sfb0 + i];
            uint ipos = istPos[i];
            if (i > maxBand[i % 3] && ipos < maxPos)
            {
                float kl;
                float kr;
                float s = ms ? 1.41421356f : 1;
                if (h.IsMpeg1)
                {
                    kl = pan[(int)(2 * ipos)];
                    kr = pan[(int)((2 * ipos) + 1)];
                }
                else
                {
                    kl = 1;
                    kr = LdexpQ2(1, (int)((ipos + 1) >> 1) << mpeg2Shift);
                    if ((ipos & 1) != 0)
                    {
                        kl = kr;
                        kr = 1;
                    }
                }

                kl *= s;
                kr *= s;
                for (int k = 0; k < width; k++)
                {
                    g[left + k + 576] = g[left + k] * kr;
                    g[left + k] = g[left + k] * kl;
                }
            }
            else if (ms)
            {
                MidSide(left, width);
            }

            left += width;
        }
    }

    private void Reorder(int start, byte[] sfbs, int sfb)
    {
        float[] g = _grbuf;
        float[] scratch = _syn;
        int src = start;
        int dst = 0;
        int len;
        for (; (len = sfbs[sfb]) != 0; sfb += 3, src += 2 * len)
        {
            for (int i = 0; i < len; i++, src++)
            {
                scratch[dst++] = g[src];
                scratch[dst++] = g[src + len];
                scratch[dst++] = g[src + (2 * len)];
            }
        }

        Array.Copy(scratch, 0, g, start, dst);
    }

    private void Antialias(int buf, int nbands)
    {
        ReadOnlySpan<float> aa = Mp3Tables.Antialias;
        float[] g = _grbuf;
        for (; nbands > 0; nbands--, buf += 18)
        {
            for (int i = 0; i < 8; i++)
            {
                float u = g[buf + 18 + i];
                float d = g[buf + 17 - i];
                g[buf + 18 + i] = (u * aa[i]) - (d * aa[8 + i]);
                g[buf + 17 - i] = (u * aa[8 + i]) + (d * aa[i]);
            }
        }
    }

    private static void Dct3x9(Span<float> y)
    {
        float s0 = y[0], s2 = y[2], s4 = y[4], s6 = y[6], s8 = y[8];
        float t0 = s0 + (s6 * 0.5f);
        s0 -= s6;
        float t4 = (s4 + s2) * 0.93969262f;
        float t2 = (s8 + s2) * 0.76604444f;
        s6 = (s4 - s8) * 0.17364818f;
        s4 += s8 - s2;

        s2 = s0 - (s4 * 0.5f);
        y[4] = s4 + s0;
        s8 = t0 - t2 + s6;
        s0 = t0 - t4 + t2;
        s4 = t0 + t4 - s6;

        float s1 = y[1], s3 = y[3], s5 = y[5], s7 = y[7];

        s3 *= 0.86602540f;
        t0 = (s5 + s1) * 0.98480775f;
        t4 = (s5 - s7) * 0.34202014f;
        t2 = (s1 + s7) * 0.64278761f;
        s1 = (s1 - s5 - s7) * 0.86602540f;

        s5 = t0 - s3 - t2;
        s7 = t4 - s3 - t0;
        s3 = t4 + s3 - t2;

        y[0] = s4 - s7;
        y[1] = s2 + s1;
        y[2] = s0 - s3;
        y[3] = s8 + s5;
        y[5] = s8 - s5;
        y[6] = s0 + s3;
        y[7] = s2 - s1;
        y[8] = s4 + s7;
    }

    private void Imdct36(int buf, int overlap, ReadOnlySpan<float> window, int nbands)
    {
        ReadOnlySpan<float> twid = Mp3Tables.Twiddle9;
        float[] g = _grbuf;
        float[] ov = _overlap;
        Span<float> co = stackalloc float[9];
        Span<float> si = stackalloc float[9];
        for (int j = 0; j < nbands; j++, buf += 18, overlap += 9)
        {
            co[0] = -g[buf];
            si[0] = g[buf + 17];
            for (int i = 0; i < 4; i++)
            {
                si[8 - (2 * i)] = g[buf + (4 * i) + 1] - g[buf + (4 * i) + 2];
                co[1 + (2 * i)] = g[buf + (4 * i) + 1] + g[buf + (4 * i) + 2];
                si[7 - (2 * i)] = g[buf + (4 * i) + 4] - g[buf + (4 * i) + 3];
                co[2 + (2 * i)] = -(g[buf + (4 * i) + 3] + g[buf + (4 * i) + 4]);
            }

            Dct3x9(co);
            Dct3x9(si);

            si[1] = -si[1];
            si[3] = -si[3];
            si[5] = -si[5];
            si[7] = -si[7];

            for (int i = 0; i < 9; i++)
            {
                float ovl = ov[overlap + i];
                float sum = (co[i] * twid[9 + i]) + (si[i] * twid[i]);
                ov[overlap + i] = (co[i] * twid[i]) - (si[i] * twid[9 + i]);
                g[buf + i] = (ovl * window[i]) - (sum * window[9 + i]);
                g[buf + 17 - i] = (ovl * window[9 + i]) + (sum * window[i]);
            }
        }
    }

    private static void Idct3(float x0, float x1, float x2, Span<float> dst)
    {
        float m1 = x1 * 0.86602540f;
        float a1 = x0 - (x2 * 0.5f);
        dst[1] = x0 + x2;
        dst[0] = a1 + m1;
        dst[2] = a1 - m1;
    }

    private static void Imdct12(ReadOnlySpan<float> x, Span<float> dst, Span<float> overlap)
    {
        ReadOnlySpan<float> twid = Mp3Tables.Twiddle3;
        Span<float> co = stackalloc float[3];
        Span<float> si = stackalloc float[3];
        Idct3(-x[0], x[6] + x[3], x[12] + x[9], co);
        Idct3(x[15], x[12] - x[9], x[6] - x[3], si);
        si[1] = -si[1];

        for (int i = 0; i < 3; i++)
        {
            float ovl = overlap[i];
            float sum = (co[i] * twid[3 + i]) + (si[i] * twid[i]);
            overlap[i] = (co[i] * twid[i]) - (si[i] * twid[3 + i]);
            dst[i] = (ovl * twid[2 - i]) - (sum * twid[5 - i]);
            dst[5 - i] = (ovl * twid[5 - i]) + (sum * twid[2 - i]);
        }
    }

    private void ImdctShort(int buf, int overlap, int nbands)
    {
        Span<float> tmp = stackalloc float[18];
        for (; nbands > 0; nbands--, overlap += 9, buf += 18)
        {
            Span<float> g = _grbuf.AsSpan(buf, 18);
            Span<float> ov = _overlap.AsSpan(overlap, 9);
            g.CopyTo(tmp);
            ov[..6].CopyTo(g);
            Imdct12(tmp, g[6..], ov[6..]);
            Imdct12(tmp[1..], g[12..], ov[6..]);
            Imdct12(tmp[2..], ov, ov[6..]);
        }
    }

    private void ChangeSign(int buf)
    {
        float[] g = _grbuf;
        buf += 18;
        for (int b = 0; b < 32; b += 2, buf += 36)
        {
            for (int i = 1; i < 18; i += 2)
            {
                g[buf + i] = -g[buf + i];
            }
        }
    }

    private void Imdct(int buf, int overlap, int blockType, int longBands)
    {
        ReadOnlySpan<float> windows = Mp3Tables.MdctWindow;
        if (longBands != 0)
        {
            Imdct36(buf, overlap, windows[..18], longBands);
            buf += 18 * longBands;
            overlap += 9 * longBands;
        }

        if (blockType == ShortBlock)
        {
            ImdctShort(buf, overlap, 32 - longBands);
        }
        else
        {
            Imdct36(buf, overlap, windows.Slice(blockType == StopBlock ? 18 : 0, 18), 32 - longBands);
        }
    }

    // ---- Synthesis filterbank ----

    private void SynthGranule(float[] grbuf, int nbands, int channels, Span<float> left, Span<float> right)
    {
        for (int i = 0; i < channels; i++)
        {
            Mp3Synthesis.DctII(grbuf.AsSpan(576 * i), nbands);
        }

        float[] lins = _syn;
        Array.Copy(_qmf, lins, 15 * 64);
        for (int i = 0; i < nbands; i += 2)
        {
            Mp3Synthesis.Synth(grbuf.AsSpan(i), left[(32 * i)..], right[(32 * i)..], channels, lins.AsSpan(i * 64));
        }

        if (channels == 1)
        {
            for (int i = 0; i < 15 * 64; i += 2)
            {
                _qmf[i] = lins[(nbands * 64) + i];
            }
        }
        else
        {
            Array.Copy(lins, nbands * 64, _qmf, 0, 15 * 64);
        }
    }

    /// <summary>minimp3's bs_t: a bit reader over a byte span that returns 0 past its end.</summary>
    private ref struct Bits
    {
        private readonly ReadOnlySpan<byte> _buf;

        public Bits(ReadOnlySpan<byte> buf)
        {
            _buf = buf;
            Limit = buf.Length * 8;
        }

        public int Pos { get; set; }

        public int Limit { get; }

        public uint Get(int n)
        {
            int s = Pos & 7;
            int shl = n + s;
            int p = Pos >> 3;
            if (n == 0 || (Pos += n) > Limit)
            {
                return 0;
            }

            uint cache = 0;
            uint next = (uint)(_buf[p++] & (255 >> s));
            while ((shl -= 8) > 0)
            {
                cache |= next << shl;
                next = _buf[p++];
            }

            return cache | (next >> -shl);
        }
    }

    /// <summary>minimp3's L3_gr_info_t: one granule's side information for one channel.</summary>
    private struct Granule
    {
        public byte[] Sfb;
        public int SfbStart;
        public int Part23Length;
        public int BigValues;
        public int ScalefacCompress;
        public int GlobalGain;
        public int BlockType;
        public int MixedBlockFlag;
        public int NLongSfb;
        public int NShortSfb;
        public int Table0;
        public int Table1;
        public int Table2;
        public int Region0;
        public int Region1;
        public int Region2;
        public int SubblockGain0;
        public int SubblockGain1;
        public int SubblockGain2;
        public int Preflag;
        public int ScalefacScale;
        public int Count1Table;
        public int Scfsi;
    }
}
