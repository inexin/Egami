using System.Buffers.Binary;

using Egami.Heic.Hevc;
using Egami.Pixels;
using Egami.Video.Codecs.Encoding;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// Codes the core's decisions as HEVC access units of one slice each, through the HEIC encoder's CTU search: a keyframe is an
/// IDR picture (with the parameter sets in Annex B streams), a P picture predicts from the last few P and I pictures, nearest
/// first in list 0, and a B picture from the nearest reference on each side (list 0 before it, list 1 after it, a pyramid's
/// middle B picture among them). Each picture's short-term reference picture set names every reference picture still to be
/// used, so the decoder's buffer holds what the coder's does: the P and I pictures a P picture may predict from, and while a
/// pyramid's run is coded its middle B picture.
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
    private readonly int _references;

    // The reference pictures the decoder's buffer holds after the last picture, P and I pictures marked as anchors.
    private readonly List<HevcReference> _dpb = [];
    private HevcReference? _lastAnchor;
    private int _idrIndex;
    private int _nextId;

    /// <summary>Makes a coder for a stream's settings (the QP is set per picture).</summary>
    /// <param name="settings">The stream's size, bit depth, colour, timing, level and coding tools.</param>
    /// <param name="annexB">Whether access units are Annex B byte streams rather than length-prefixed.</param>
    /// <param name="bFrames">The most B-frames the core puts between two references.</param>
    /// <param name="pyramid">Whether a run's middle B-frame is a reference.</param>
    /// <param name="references">How many P and I pictures a P picture predicts from, or null for the effort's.</param>
    public HevcFrameCoder(HevcEncoderSettings settings, bool annexB, int bFrames = 0, bool pyramid = false, int? references = null)
    {
        _profile = HevcInterProfile.For(settings.Effort);
        _references = Math.Max(1, references ?? _profile.References);

        // While a run of B pictures is decoded the buffer holds the anchors the last P picture predicted from, that P picture, a
        // pyramid's middle B picture and the picture decoded (sps_max_dec_pic_buffering_minus1 leaves the last out); a P picture
        // is decoded before the B pictures shown ahead of it, two of them in a pyramid of three, which comes before the first.
        int buffering = _references + (bFrames > 0 ? 1 : 0) + (bFrames > 0 && pyramid ? 1 : 0);
        _settings = HevcEncoder.Resolve(settings with
        {
            PpsQp = PpsQp,
            Log2MaxPocLsb = Log2MaxPocLsb,
            TemporalMvp = true,
            MaxTransformDepthInter = _profile.TransformDepth,
            MaxDecPicBufferingMinus1 = Math.Max(settings.MaxDecPicBufferingMinus1, buffering),
            MaxNumReorderPics = bFrames == 0 ? 0 : pyramid ? 2 : 1,
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

    /// <summary>Gets how many B pictures were coded, for tests.</summary>
    internal int BPictures { get; private set; }

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
        bool idr = decision.Type == FrameType.Key || _dpb.Count == 0;
        if (idr)
        {
            _idrIndex = decision.Index;
            _dpb.Clear();
        }

        int qp = Math.Clamp(decision.Quantizer, 0, 51);
        int poc = decision.Index - _idrIndex;
        bool b = !idr && decision.Type == FrameType.Bidirectional && _dpb.Exists(r => r.Picture.Poc > poc);
        Load(picture, full);
        HevcEncodeStats.Picture(idr ? 2 : b ? 1 : 0);
        HevcEncodeStats.Add(HevcEncodeStat.Ctus, CtuCount);
        HevcEncodedPicture coded;
        if (idr)
        {
            coded = EncodeIdr(qp);
            Keep([], coded, poc, anchor: true);
        }
        else if (b)
        {
            coded = EncodeB(qp, poc, decision.Referenced);
            BPictures++;
        }
        else
        {
            coded = EncodeP(qp, poc, decision.Referenced);
        }

        Coded?.Invoke(coded.Reconstruction, decision.Index);
        byte[] data = idr && _annexB ? AccessUnit([Vps, Sps, Pps, coded.Slice]) : AccessUnit([coded.Slice]);
        AddPicture(idr ? 0 : !b ? 1 : decision.Referenced ? 2 : 3, coded);
        return new CodedFrame(data, null, qp);
    }

    public (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer)
    {
        int qp = Math.Clamp(quantizer, 0, 51);
        Load(key, null);
        HevcEncodedPicture first = EncodeIdr(qp);
        long nextBits = 0;
        if (next is not null)
        {
            // The next frame as the P picture it will be, predicted from the keyframe.
            Load(next, null);
            HevcReference keyRef = new(new HevcDpbPicture(first.Reconstruction, 0, -1)) { Anchor = true };
            nextBits = EncodeInter(qp, 1, HevcSliceHeader.P, false, [keyRef], [keyRef], [], null).Slice.Length * 8L;
        }

        return (first.Slice.Length * 8L, nextBits);
    }

    private HevcEncodedPicture EncodeIdr(int qp)
    {
        HevcEncodedPicture coded = HevcEncoder.Encode(_settings with { Qp = qp }, _planes, _strides, new HevcSliceInfo { NalType = HevcNal.IdrNLp },
            CancellationToken.None);
        coded.Reconstruction.ColMotion = new HevcColMotion(coded.Reconstruction);
        return coded;
    }

    /// <summary>
    /// A P picture: predicts from the last few anchors, nearest first, and keeps only them; a pyramid's middle B picture, whose
    /// run is coded, leaves the buffer.
    /// </summary>
    private HevcEncodedPicture EncodeP(int qp, int poc, bool referenced)
    {
        HevcReference[] anchors = [.. _dpb.Where(r => r.Anchor).OrderByDescending(r => r.Picture.Poc).Take(_references)];
        HevcEncodedPicture coded = EncodeInter(qp, poc, HevcSliceHeader.P, referenced, anchors, anchors, [], null);
        Keep(anchors, referenced ? coded : null, poc, anchor: true);
        return coded;
    }

    /// <summary>
    /// A B picture: list 0 the nearest reference before it (two from effort 5), list 1 the nearest after it; every reference
    /// picture stays, as later pictures of the run predict from them.
    /// </summary>
    private HevcEncodedPicture EncodeB(int qp, int poc, bool referenced)
    {
        HevcReference[] kept = [.. _dpb];
        HevcReference[] before = [.. kept.Where(r => r.Picture.Poc < poc).OrderByDescending(r => r.Picture.Poc)];
        HevcReference[] after = [.. kept.Where(r => r.Picture.Poc > poc).OrderBy(r => r.Picture.Poc)];
        HevcReference[] list0 = before.Length > 0 ? [.. before.Take(_profile.BReferences)] : [after[0]];
        HevcEncodedPicture coded = EncodeInter(qp, poc, HevcSliceHeader.B, referenced, kept, list0, [after[0]], _lastAnchor);
        Keep(kept, referenced ? coded : null, poc, anchor: false);

        return coded;
    }

    /// <summary>Makes the buffer the reference set kept and the picture just coded, when it is a reference.</summary>
    private void Keep(HevcReference[] kept, HevcEncodedPicture? coded, int poc, bool anchor)
    {
        _dpb.Clear();
        _dpb.AddRange(kept);
        if (coded is null)
        {
            return;
        }

        HevcReference current = new(new HevcDpbPicture(coded.Reconstruction, poc, _nextId++)) { Anchor = anchor };
        _dpb.Add(current);
        if (anchor)
        {
            _lastAnchor = current;
        }
    }

    /// <summary>
    /// Codes the loaded picture as a P or B picture: its reference set the pictures of <paramref name="kept"/> (used where they
    /// are in a list), list 0 and list 1 as given; a B picture takes temporal motion from list 1's picture, and the vectors of
    /// <paramref name="seeds"/>, the last anchor coded, as search starts.
    /// </summary>
    private HevcEncodedPicture EncodeInter(int qp, int poc, int sliceType, bool referenced, HevcReference[] kept, HevcReference[] list0, HevcReference[] list1,
        HevcReference? seeds)
    {
        bool b = sliceType == HevcSliceHeader.B;
        (int, bool)[] Set(IEnumerable<HevcReference> pictures) =>
            [.. pictures.Select(r => (r.Picture.Poc - poc, list0.Contains(r) || list1.Contains(r)))];
        HevcSliceInfo info = new()
        {
            NalType = referenced ? HevcNal.TrailR : HevcNal.TrailN,
            Poc = poc,
            SliceType = sliceType,
            Before = Set(kept.Where(r => r.Picture.Poc < poc).OrderByDescending(r => r.Picture.Poc)),
            After = Set(kept.Where(r => r.Picture.Poc > poc).OrderBy(r => r.Picture.Poc)),
            NumRefIdxL0 = list0.Length,
            NumRefIdxL1 = Math.Max(1, list1.Length),
            TemporalMvp = true,
            CollocatedFromL0 = !b,
            MaxNumMergeCand = _profile.MergeCandidates,
        };
        HevcSliceRefs refs = new([.. list0.Select(r => r.Picture)], [.. list1.Select(r => r.Picture)], new bool[list0.Length], new bool[list1.Length], poc);
        HevcEncodedPicture coded = HevcEncoder.Encode(_settings with { Qp = qp }, _planes, _strides, info, CancellationToken.None, (picture, slice) =>
        {
            Array.Fill(picture.CtbRefs, refs);
            HevcInterContext context = new(refs, list0, list1, _profile, picture.CtbRefs.Length)
            {
                Seeds = b && _profile.BSeeds ? seeds : null,
                Referenced = referenced,
            };
            return search => search.InterContext = context;
        }, _profile.IntraProfile);
        coded.Reconstruction.ColMotion = new HevcColMotion(coded.Reconstruction);
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
