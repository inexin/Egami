using System.Buffers.Binary;

using Egami.Heic.Hevc;
using Egami.Pixels;
using Egami.Video.Codecs.Encoding;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// Codes the core's decisions as HEVC access units of one slice each, through the HEIC encoder's CTU search: a keyframe is an
/// IDR picture (with the parameter sets in Annex B streams), every other frame a trailing P picture with its picture order
/// count and short-term reference picture set, predicting from the last few reference pictures, nearest first in list 0, which
/// the coder keeps with their search planes. B pictures come later.
/// </summary>
internal sealed class HevcFrameCoder : IFrameCoder
{
    /// <summary>The step of each QP, 0.625 · 2^(QP / 6) in sixteenths, for the core's bit model: HEVC's quantizer scale is H.264's.</summary>
    internal static readonly short[] QpSteps = [.. Enumerable.Range(0, 52).Select(qp => (short)Math.Round(10 * Math.Pow(2, qp / 6.0)))];

    /// <summary>The QP the PPS starts every slice at; each picture's own QP goes in its slice_qp_delta.</summary>
    internal const int PpsQp = 26;

    /// <summary>log2_max_pic_order_cnt_lsb: the order counts' low 8 bits are written, ample for the core's groups.</summary>
    internal const int Log2MaxPocLsb = 8;

    private readonly HevcEncoderSettings _settings;
    private readonly bool _annexB;
    private readonly ushort[][] _planes;
    private readonly int[] _strides;
    private readonly HevcInterProfile _profile;
    private readonly List<HevcReference> _references = [];
    private int _idrIndex;
    private int _nextId;

    /// <summary>Makes a coder for a stream's settings (the QP is set per picture).</summary>
    /// <param name="settings">The stream's size, bit depth, colour, timing, level and coding tools.</param>
    /// <param name="annexB">Whether access units are Annex B byte streams rather than length-prefixed.</param>
    public HevcFrameCoder(HevcEncoderSettings settings, bool annexB)
    {
        _profile = HevcInterProfile.For(settings.Effort);
        _settings = HevcEncoder.Resolve(settings with
        {
            PpsQp = PpsQp,
            Log2MaxPocLsb = Log2MaxPocLsb,
            TemporalMvp = true,
            MaxTransformDepthInter = _profile.TransformDepth,
            MaxDecPicBufferingMinus1 = Math.Max(settings.MaxDecPicBufferingMinus1, _profile.References),
        }).Settings;
        _annexB = annexB;
        Vps = HevcParameterWriter.Vps(_settings);
        Sps = HevcParameterWriter.Sps(_settings);
        Pps = HevcParameterWriter.Pps(_settings);
        int width = settings.Width, height = settings.Height;
        _planes = [new ushort[width * height], new ushort[width * height / 4], new ushort[width * height / 4]];
        _strides = [width, width / 2, width / 2];
    }

    /// <summary>Gets the stream's VPS NAL unit.</summary>
    internal byte[] Vps { get; }

    /// <summary>Gets the stream's SPS NAL unit.</summary>
    internal byte[] Sps { get; }

    /// <summary>Gets the stream's PPS NAL unit.</summary>
    internal byte[] Pps { get; }

    /// <summary>Gets the stream's settings, for tests.</summary>
    internal HevcEncoderSettings Settings => _settings;

    /// <summary>Gets the hvcC record of the stream's parameter sets, for the track's <see cref="TrackInfo.CodecPrivate"/>.</summary>
    internal byte[] ConfigurationRecord() => HevcParameterWriter.ConfigurationRecord(_settings, Vps, Sps, Pps);

    /// <summary>Gets or sets what runs after each frame is coded, with its reconstruction and display index, for tests.</summary>
    internal Action<HevcPicture, int>? Coded { get; set; }

    public short[] Steps => QpSteps;

    private int CtuCount => ((_settings.Width + (1 << _settings.CtbLog2Size) - 1) >> _settings.CtbLog2Size)
        * ((_settings.Height + (1 << _settings.CtbLog2Size) - 1) >> _settings.CtbLog2Size);

    public CodedFrame Code(YuvRaster<byte> picture, FrameDecision decision) => Code(picture, null, decision);

    public CodedFrame Code(YuvRaster<byte> picture, YuvRaster? full, FrameDecision decision)
    {
        bool idr = decision.Type == FrameType.Key || _references.Count == 0;
        if (idr)
        {
            _idrIndex = decision.Index;
            _references.Clear();
        }

        int qp = Math.Clamp(decision.Quantizer, 0, 51);
        Load(picture, full);
        HevcEncodeStats.Picture(idr ? 2 : 0);
        HevcEncodeStats.Add(HevcEncodeStat.Ctus, CtuCount);
        HevcEncodedPicture coded = Encode(qp, decision.Index - _idrIndex, idr, decision.Referenced, _references);
        Coded?.Invoke(coded.Reconstruction, decision.Index);
        byte[] data = idr && _annexB ? AccessUnit([Vps, Sps, Pps, coded.Slice]) : AccessUnit([coded.Slice]);
        AddPicture(idr ? 0 : 1, coded);
        return new CodedFrame(data, null, qp);
    }

    public (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer)
    {
        int qp = Math.Clamp(quantizer, 0, 51);
        Load(key, null);
        HevcEncodedPicture first = Encode(qp, 0, true, true, []);
        long nextBits = 0;
        if (next is not null)
        {
            // The next frame as the P picture it will be, predicted from the keyframe.
            Load(next, null);
            List<HevcReference> references = [new HevcReference(new HevcDpbPicture(first.Reconstruction, 0, -1))];
            nextBits = Encode(qp, 1, false, false, references).Slice.Length * 8L;
        }

        return (first.Slice.Length * 8L, nextBits);
    }

    /// <summary>
    /// Codes the loaded picture: an IDR picture, or a P picture predicting from <paramref name="references"/> (nearest first, all
    /// in its reference picture set); a referenced picture joins them, with its motion kept for temporal prediction.
    /// </summary>
    private HevcEncodedPicture Encode(int qp, int poc, bool idr, bool referenced, List<HevcReference> references)
    {
        HevcEncoderSettings settings = _settings with { Qp = qp };
        HevcEncodedPicture coded;
        if (idr)
        {
            coded = HevcEncoder.Encode(settings, _planes, _strides, new HevcSliceInfo { NalType = HevcNal.IdrNLp }, CancellationToken.None);
        }
        else
        {
            int count = Math.Min(references.Count, _profile.References);
            HevcSliceInfo info = new()
            {
                NalType = referenced ? HevcNal.TrailR : HevcNal.TrailN,
                Poc = poc,
                SliceType = HevcSliceHeader.P,
                Before = [.. references.Select(r => (r.Picture.Poc - poc, true))],
                NumRefIdxL0 = count,
                TemporalMvp = true,
                MaxNumMergeCand = _profile.MergeCandidates,
            };
            HevcReference[] active = [.. references.Take(count)];
            HevcSliceRefs refs = new([.. active.Select(r => r.Picture)], [], new bool[count], [], poc);
            coded = HevcEncoder.Encode(settings, _planes, _strides, info, CancellationToken.None, (picture, slice) =>
            {
                Array.Fill(picture.CtbRefs, refs);
                HevcInterContext context = new(refs, active, _profile, picture.CtbRefs.Length);
                return search => search.InterContext = context;
            }, _profile.IntraProfile);
        }

        HevcPicture reconstruction = coded.Reconstruction;
        reconstruction.ColMotion = new HevcColMotion(reconstruction);
        if (referenced && references == _references)
        {
            _references.Insert(0, new HevcReference(new HevcDpbPicture(reconstruction, poc, _nextId++)));
            if (_references.Count > _profile.References)
            {
                _references.RemoveAt(_references.Count - 1);
            }
        }

        return coded;
    }

    /// <summary>Adds a picture to the counting build's statistics: its bits and the 8x8 blocks skipped, merged, searched and intra.</summary>
    [System.Diagnostics.Conditional("EGAMI_HEVC_STATS")]
    private static void AddPicture(int kind, HevcEncodedPicture coded)
    {
        long[] shares = new long[4];
        foreach (List<HevcCodingUnit> ctu in coded.Ctus)
        {
            foreach (HevcCodingUnit cu in ctu)
            {
                HevcBlockShare share = !cu.Inter ? HevcBlockShare.Intra : cu.Skip ? HevcBlockShare.Skip
                    : cu.PartMode == HevcCodingUnit.Part2Nx2N && cu.Units[0].Merge ? HevcBlockShare.Merge : HevcBlockShare.Inter;
                shares[(int)share] += 1L << (2 * (cu.Log2 - 3));
            }
        }

        HevcEncodeStats.AddPicture(kind, coded.Slice.Length * 8L, shares);
    }

    /// <summary>Joins NAL units into an access unit: each after a start code (Annex B), or after its 4-byte size.</summary>
    internal byte[] AccessUnit(ReadOnlySpan<byte[]> nals)
    {
        if (!_annexB)
        {
            return HevcParameterWriter.LengthPrefixed(nals);
        }

        int length = 0;
        foreach (byte[] nal in nals)
        {
            length += 4 + nal.Length;
        }

        byte[] data = new byte[length];
        int at = 0;
        foreach (byte[] nal in nals)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(at), 1);
            nal.CopyTo(data, at + 4);
            at += 4 + nal.Length;
        }

        return data;
    }

    /// <summary>
    /// Copies the picture into the coder's 16-bit planes: the stream's own format when the core keeps it (10-bit planes for Main
    /// 10), else the core's 8-bit picture, shifted up two bits for Main 10.
    /// </summary>
    private void Load(YuvRaster<byte> picture, YuvRaster? full)
    {
        if (full is YuvRaster<ushort> words)
        {
            Copy(words.Y, _planes[0], _strides[0]);
            Copy(words.U!, _planes[1], _strides[1]);
            Copy(words.V!, _planes[2], _strides[2]);
            return;
        }

        int shift = _settings.BitDepth - 8;
        Copy(picture.Y, _planes[0], _strides[0], shift);
        Copy(picture.U!, _planes[1], _strides[1], shift);
        Copy(picture.V!, _planes[2], _strides[2], shift);
    }

    private static void Copy(YuvPlane<ushort> from, ushort[] to, int stride)
    {
        int rows = to.Length / stride;
        for (int y = 0; y < rows; y++)
        {
            from.Row(y)[..stride].CopyTo(to.AsSpan(y * stride, stride));
        }
    }

    private static void Copy(YuvPlane<byte> from, ushort[] to, int stride, int shift)
    {
        int rows = to.Length / stride;
        for (int y = 0; y < rows; y++)
        {
            ReadOnlySpan<byte> row = from.Row(y)[..stride];
            Span<ushort> target = to.AsSpan(y * stride, stride);
            for (int x = 0; x < row.Length; x++)
            {
                target[x] = (ushort)(row[x] << shift);
            }
        }
    }

    public void Dispose()
    {
    }
}
