using System.Buffers.Binary;

using Egami.Heic.Hevc;
using Egami.Pixels;
using Egami.Video.Codecs.Encoding;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// Codes the core's decisions as HEVC access units of one slice each, through the HEIC encoder's CTU search: a keyframe is an
/// IDR picture (with the parameter sets in Annex B streams), every other frame a trailing picture with its picture order count
/// and short-term reference picture set. So far every picture is coded intra; P and B pictures come with inter coding.
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
    private int _idrIndex;

    /// <summary>Makes a coder for a stream's settings (the QP is set per picture).</summary>
    /// <param name="settings">The stream's size, bit depth, colour, timing, level and coding tools.</param>
    /// <param name="annexB">Whether access units are Annex B byte streams rather than length-prefixed.</param>
    public HevcFrameCoder(HevcEncoderSettings settings, bool annexB)
    {
        _settings = HevcEncoder.Resolve(settings with { PpsQp = PpsQp, Log2MaxPocLsb = Log2MaxPocLsb }).Settings;
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
        bool idr = decision.Type == FrameType.Key;
        if (idr)
        {
            _idrIndex = decision.Index;
        }

        int qp = Math.Clamp(decision.Quantizer, 0, 51);
        HevcSliceInfo info = idr
            ? new HevcSliceInfo { NalType = HevcNal.IdrNLp }
            : new HevcSliceInfo { NalType = decision.Referenced ? HevcNal.TrailR : HevcNal.TrailN, Poc = decision.Index - _idrIndex };
        Load(picture, full);
        HevcEncodeStats.Picture(2);
        HevcEncodeStats.Add(HevcEncodeStat.Ctus, CtuCount);
        HevcEncodedPicture coded = HevcEncoder.Encode(_settings with { Qp = qp }, _planes, _strides, info, CancellationToken.None);
        Coded?.Invoke(coded.Reconstruction, decision.Index);
        byte[] data = idr && _annexB ? AccessUnit([Vps, Sps, Pps, coded.Slice]) : AccessUnit([coded.Slice]);
        HevcEncodeStats.AddPicture(0, coded.Slice.Length * 8L, new long[4]);
        return new CodedFrame(data, null, qp);
    }

    public (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer)
    {
        HevcEncoderSettings settings = _settings with { Qp = Math.Clamp(quantizer, 0, 51) };
        Load(key, null);
        long keyBits = HevcEncoder.Encode(settings, _planes, _strides, new HevcSliceInfo { NalType = HevcNal.IdrNLp }, CancellationToken.None).Slice.Length * 8L;
        long nextBits = 0;
        if (next is not null)
        {
            Load(next, null);
            nextBits = HevcEncoder.Encode(settings, _planes, _strides, new HevcSliceInfo { NalType = HevcNal.TrailR, Poc = 1 }, CancellationToken.None).Slice.Length * 8L;
        }

        return (keyBits, nextBits);
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
