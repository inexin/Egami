using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Heic.Hevc;
using Egami.Nal;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Hardware.Vulkan;
using Egami.Video.Hardware.Vulkan.Native;

namespace Egami.Video.Hardware.Decoding;

/// <summary>
/// Decodes HEVC on the GPU's video engine through Vulkan Video (<c>VK_KHR_video_decode_h265</c>). Egami reads the stream with
/// its software decoder's parser and does what Vulkan leaves to the application: picture boundaries, picture order count, the
/// reference picture sets, the decoded picture buffer's slots and the output order (H.265 8.1.3, 8.3.1, 8.3.2 and C.5.2). The
/// driver decodes each picture's slice segments into a DPB slot, which is copied to host memory in the same submission. Up to
/// <see cref="InFlight"/> pictures run on the GPU while the next ones are read. 4:2:0 streams in 8 and 10 bits are decoded;
/// other formats and the range extensions' coding tools are refused.
/// </summary>
internal sealed unsafe class HevcHardwareDecoder : IVideoDecoder
{
    /// <summary>The pictures that may be on the GPU at once.</summary>
    internal const int InFlight = 4;

    private readonly VideoTrackInfo _track;
    private readonly EgamiContext _egami;
    private readonly DecodeErrorMode _errors;
    private readonly VulkanDevice _device;
    private readonly VulkanQueue _queue;
    private readonly ProfileBlock* _profiles;
    private readonly Capabilities?[] _caps = new Capabilities?[2];
    private readonly VulkanCommandPool _pool;
    private readonly Job[] _jobs = new Job[InFlight];
    private readonly Queue<int> _submitted = new();
    private readonly Queue<Picture> _emitted = new();
    private readonly PriorityQueue<(MediaTime Time, MediaTime? Duration, Hdr10PlusMetadata? Hdr10Plus), MediaTime> _times = new();
    private readonly Dictionary<HevcPps, (HevcSps Sps, HevcVps? Vps, VulkanObjectHandle Handle)> _parameters = [];
    private readonly HevcVps?[] _vps = new HevcVps?[16];
    private readonly HevcSps?[] _sps = new HevcSps?[16];
    private readonly HevcPps?[] _pps = new HevcPps?[64];
    private readonly byte[]?[] _vpsRbsp = new byte[]?[16];
    private readonly byte[]?[] _spsRbsp = new byte[]?[16];
    private readonly byte[]?[] _ppsRbsp = new byte[]?[64];
    private readonly NalBumpingBuffer<Picture> _dpb = new();
    private readonly SliceBuffer _slices = new();
    private readonly List<Picture> _untimed = [];
    private readonly List<Picture> _stCurrBefore = [];
    private readonly List<Picture> _stCurrAfter = [];
    private readonly List<Picture> _ltCurr = [];
    private readonly HashSet<Picture> _keep = [];
    private readonly int _lengthSize;

    // The session and the pictures of the current size and bit depth.
    private VulkanVideoSession? _session;
    private VulkanObjectHandle? _queryPool;
    private VulkanImage[] _slots = [];
    private VulkanBuffer? _bitstream;
    private ulong _regionSize;
    private ulong _alignment = 1;
    private uint _codedWidth;
    private uint _codedHeight;
    private int _sessionWidth;
    private int _sessionHeight;
    private int _sessionBitDepth;
    private int _sessionSlots;
    private bool _needsReset;
    private bool _slotsReady;

    // The picture being read: its first slice segment's header and NAL unit, and its parameter sets.
    private Picture? _cur;
    private HevcSliceHeader? _first;
    private HevcNal? _firstNal;
    private HevcSliceHeader? _lastIndependent;
    private HevcSps? _activeSps;
    private bool _skipping;
    private bool _waitingForIrap;

    // The decoding process's state (8.1.3, 8.3.1).
    private bool _firstPicture = true;
    private bool _afterEndOfSequence;
    private bool _skipRasl;
    private int _prevTid0Poc;
    private Hdr10PlusMetadata? _hdr10Plus;
    private int _nextJob;
    private int _packet = -1;
    private HdrMetadata? _hdr;
    private DiagnosticReporter? _diagnostics;
    private bool _disposed;

    public HevcHardwareDecoder(VideoTrackInfo track, VideoDecoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        _track = track;
        _egami = context.Egami;
        _errors = options.Errors;
        _hdr = track.Hdr;
        _device = VulkanVideoContext.GetDevice(VkVideoCodecOperationFlagBitsKHR.DECODE_H265_BIT_KHR, "Main");
        _queue = _device.DecodeQueue ?? throw new HardwareVideoException($"{_device.Physical.Name} has no video decode queue.");
        if ((_device.Physical.QueueFamilies[_queue.Family].Flags & VkQueueFlagBits.TRANSFER_BIT) == 0)
        {
            throw new HardwareVideoException($"{_device.Physical.Name}'s decode queue cannot copy pictures out (no transfer support).");
        }

        _profiles = (ProfileBlock*)NativeMemory.AllocZeroed((nuint)(sizeof(ProfileBlock) * 2));
        try
        {
            ProfileBlock.Fill(&_profiles[0], 8);
            ProfileBlock.Fill(&_profiles[1], 10);
            Capabilities caps = CapabilitiesFor(8) ?? throw new HardwareVideoException($"{_device.Physical.Name} does not decode HEVC Main.");
            if ((caps.Decode.flags & VkVideoDecodeCapabilityFlagBitsKHR.DPB_AND_OUTPUT_COINCIDE_BIT_KHR) == 0)
            {
                throw new HardwareVideoException($"{_device.Physical.Name} decodes HEVC only into separate output pictures, which Egami does not use yet.");
            }

            _pool = _device.CreateCommandPool(_queue);
            for (int i = 0; i < InFlight; i++)
            {
                _jobs[i] = new Job(_pool.Allocate(), _device.CreateFence());
            }
        }
        catch
        {
            DisposeResources();
            throw;
        }

        ReadOnlyMemory<byte> config = string.Equals(track.CodecId, CodecIds.QuickTimeVideo, StringComparison.Ordinal)
            ? DolbyVisionConfiguration(track.CodecPrivate)
            : track.CodecPrivate;
        if (!config.IsEmpty)
        {
            try
            {
                _lengthSize = ReadConfiguration(config.Span);
            }
            catch (CorruptDataException e)
            {
                Report("Hevc.CorruptFrame", $"The track's hvcC record cannot be read ({e.Message}).");
                if (_errors == DecodeErrorMode.Throw)
                {
                    DisposeResources();
                    throw;
                }
            }
        }
    }

    /// <summary>Gets the DPB slots of the current session, for tests.</summary>
    internal int Slots => _slots.Length;

    /// <summary>Gets how many pictures have started so far, for the packet timestamps.</summary>
    private int PicturesStarted { get; set; }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _packet++;
        int started = PicturesStarted;
        _hdr10Plus = null;
        try
        {
            foreach (ReadOnlyMemory<byte> nal in Split(packet.Data))
            {
                DecodeNal(nal);
            }
        }
        catch (CorruptDataException e) when (_errors != DecodeErrorMode.Throw)
        {
            // As the software decoder: what was decoded comes out, and decoding starts again at the next IRAP picture.
            Report("Hevc.CorruptFrame", $"Packet {_packet} cannot be decoded ({e.Message}); frames up to the next IRAP picture are dropped.");
            StartOver();
            return;
        }
        finally
        {
            for (int i = started; i < PicturesStarted; i++)
            {
                _times.Enqueue((packet.Timestamp, packet.Duration, _hdr10Plus), packet.Timestamp);
            }

            TakeTimes();
        }
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            FinishPicture();
        }
        catch (CorruptDataException e) when (_errors != DecodeErrorMode.Throw)
        {
            Report("Hevc.CorruptFrame", $"The last picture cannot be decoded ({e.Message}).");
        }

        while (_dpb.TryFlush(out Picture picture, out _))
        {
            Emit(picture);
        }

        TakeTimes();
        WaitAll();
        _times.Clear();
    }

    public bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_submitted.Count > 0 && _device.Commands.vkGetFenceStatus(_device.Raw, _jobs[_submitted.Peek()].Fence.Value) == VkResult.SUCCESS)
        {
            Collect(_submitted.Dequeue());
        }

        while (_emitted.TryPeek(out Picture? picture) && picture.Job < 0)
        {
            _emitted.Dequeue();
            if (picture.Raster is { } raster)
            {
                picture.Raster = null;
                raster.Metadata.Hdr = _hdr;
                ColorCodePoints color = raster.Metadata.CodePoints ?? new ColorCodePoints(2, 2, 2, false);
                frame = new YuvVideoFrame(raster, picture.Time, picture.Duration, color) { Hdr = _hdr, Hdr10Plus = picture.Hdr10Plus };
                return true;
            }
        }

        frame = null;
        return false;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WaitAll();
        while (_emitted.TryDequeue(out Picture? picture))
        {
            picture.Raster?.Dispose();
        }

        ClearBuffer();
        _cur = null;
        _first = null;
        _firstNal = null;
        _slices.Clear();
        _firstPicture = true;
        _afterEndOfSequence = false;
        _waitingForIrap = false;
        _skipping = false;
        _times.Clear();
        _untimed.Clear();
        _needsReset = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_submitted.Count > 0)
        {
            _ = _device.Commands.vkQueueWaitIdle(_queue.Handle);
            _submitted.Clear();
        }

        foreach (Job? job in _jobs)
        {
            job?.Picture?.Raster?.Dispose();
        }

        while (_emitted.TryDequeue(out Picture? picture))
        {
            picture.Raster?.Dispose();
        }

        foreach (Picture picture in _dpb.Pictures())
        {
            picture.Raster?.Dispose();
        }

        DisposeResources();
        _diagnostics?.Dispose();
    }

    /// <summary>
    /// The hvcC record of a Dolby Vision sample entry (dvh1 or dvhe, ETSI TS 103 572), the whole stsd entry with its size and type
    /// first, or empty for any other entry; as the software codec reads it.
    /// </summary>
    internal static ReadOnlyMemory<byte> DolbyVisionConfiguration(ReadOnlyMemory<byte> entry)
    {
        ReadOnlySpan<byte> box = entry.Span;
        if (box.Length < 86 || !(box[4..8].SequenceEqual("dvh1"u8) || box[4..8].SequenceEqual("dvhe"u8)))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        for (int at = 86; at + 8 <= box.Length;)
        {
            int size = (int)Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(box[at..]), int.MaxValue);
            if (size < 8 || size > box.Length - at)
            {
                break;
            }

            if (box.Slice(at + 4, 4).SequenceEqual("hvcC"u8))
            {
                return entry.Slice(at + 8, size - 8);
            }

            at += size;
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>Reads the parameter sets of an hvcC record and returns its NAL unit length size.</summary>
    private int ReadConfiguration(ReadOnlySpan<byte> hvcC)
    {
        if (hvcC.Length < 23 || hvcC[0] != 1)
        {
            throw HevcErrors.Corrupt("The hvcC record is cut off or not version 1.");
        }

        int lengthSize = (hvcC[21] & 3) + 1;
        if (lengthSize == 3)
        {
            throw HevcErrors.Corrupt("The hvcC record's lengthSizeMinusOne is 2; NAL unit sizes take 1, 2 or 4 bytes.");
        }

        int at = 23;
        for (int array = 0; array < hvcC[22]; array++)
        {
            if (at + 3 > hvcC.Length)
            {
                throw HevcErrors.Corrupt("The hvcC record's NAL unit arrays are cut off.");
            }

            int count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(hvcC[(at + 1)..]);
            at += 3;
            for (int i = 0; i < count; i++)
            {
                int length = at + 2 <= hvcC.Length ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(hvcC[at..]) : -1;
                if (length < 0 || at + 2 + length > hvcC.Length)
                {
                    throw HevcErrors.Corrupt("An hvcC NAL unit is cut off.");
                }

                DecodeNal(hvcC.Slice(at + 2, length).ToArray());
                at += 2 + length;
            }
        }

        return lengthSize;
    }

    private List<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> packet)
    {
        if (_lengthSize == 0)
        {
            return NalFraming.SplitAnnexB(packet);
        }

        try
        {
            return NalFraming.SplitLengthPrefixed(packet, _lengthSize);
        }
        catch (CorruptDataException) when (NalFraming.StartsWithStartCode(packet.Span))
        {
            return NalFraming.SplitAnnexB(packet);
        }
    }

    private void DecodeNal(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        if (span.Length < 3)
        {
            return;
        }

        int type = (span[0] >> 1) & 63, layer = ((span[0] & 1) << 5) | (span[1] >> 3);
        if (layer != 0)
        {
            // A layer above the base (Dolby Vision's enhancement layer, MV-HEVC): the base layer is decoded alone.
            return;
        }

        if (type >= 32)
        {
            DecodeNonVcl(type, span);
            return;
        }

        if (type >= 22)
        {
            // Reserved VCL types are ignored, as H.265 asks of decoders.
            return;
        }

        DecodeSlice(data);
    }

    private void DecodeNonVcl(int type, ReadOnlySpan<byte> span)
    {
        if (type is HevcNal.Vps or HevcNal.Sps or HevcNal.Pps or HevcNal.Aud or HevcNal.PrefixSei or HevcNal.Eos or HevcNal.Eob)
        {
            // 7.4.2.4.4: these start a new access unit, so the current picture is complete.
            FinishPicture();
        }

        switch (type)
        {
            case HevcNal.Vps:
                {
                    HevcNal nal = HevcNal.Parse(span);
                    int id = nal.Rbsp.Length > 2 ? nal.Rbsp[2] >> 4 : 0;
                    if (_vpsRbsp[id] is not { } old || !old.AsSpan().SequenceEqual(nal.Rbsp))
                    {
                        HevcVps vps = HevcVps.Read(nal.Reader(null));
                        _vps[vps.Id] = vps;
                        _vpsRbsp[vps.Id] = nal.Rbsp;
                    }

                    break;
                }

            case HevcNal.Sps:
                {
                    HevcNal nal = HevcNal.Parse(span);
                    HevcSps sps = HevcSps.Read(nal.Reader(null));
                    if (_spsRbsp[sps.Id] is not { } old || !old.AsSpan().SequenceEqual(nal.Rbsp))
                    {
                        _sps[sps.Id] = sps;
                        _spsRbsp[sps.Id] = nal.Rbsp;
                    }

                    break;
                }

            case HevcNal.Pps:
                {
                    HevcNal nal = HevcNal.Parse(span);
                    HevcPps pps = HevcPps.Read(nal.Reader(null));
                    if (_ppsRbsp[pps.Id] is not { } old || !old.AsSpan().SequenceEqual(nal.Rbsp))
                    {
                        _pps[pps.Id] = pps;
                        _ppsRbsp[pps.Id] = nal.Rbsp;
                    }

                    break;
                }

            case HevcNal.Eos or HevcNal.Eob:
                _afterEndOfSequence = true;
                break;
            case HevcNal.PrefixSei:
                {
                    HevcSeiMessages sei = HevcSeiMessages.Read(NalFraming.Unescape(span), _hdr);
                    _hdr10Plus ??= sei.Hdr10Plus;
                    if (sei.Hdr is { } hdr)
                    {
                        _hdr = hdr;
                    }

                    break;
                }
        }
    }

    private (HevcSps Sps, HevcPps Pps) Active(int ppsId)
    {
        HevcPps pps = _pps[ppsId] ?? throw HevcErrors.Corrupt($"A slice refers to PPS {ppsId}, which the stream does not have.");
        HevcSps sps = _sps[pps.SpsId] ?? throw HevcErrors.Corrupt($"PPS {ppsId} refers to SPS {pps.SpsId}, which the stream does not have.");
        return (sps, pps);
    }

    private void DecodeSlice(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> span = data.Span;
        int type = (span[0] >> 1) & 63;
        bool firstInPicture = (span[2] & 0x80) != 0;
        if (_waitingForIrap)
        {
            if (!(firstInPicture && type is >= HevcNal.BlaWLp and <= 23))
            {
                return;
            }

            _waitingForIrap = false;
        }

        if (!firstInPicture && _skipping)
        {
            return;
        }

        HevcNal nal = HevcNal.Parse(span);
        HevcSliceHeader h = HevcSliceHeader.Read(nal.Reader(null), nal, Active, _lastIndependent);
        if (!h.Dependent)
        {
            _lastIndependent = h;
        }

        if (h.FirstSliceSegmentInPic)
        {
            FinishPicture();
            _skipping = Skips(nal);
            if (_skipping)
            {
                return;
            }

            StartPicture(nal, h);
        }
        else if (_cur is null)
        {
            throw HevcErrors.Corrupt("The first slice segment of a picture is missing.");
        }

        _slices.Add(data);
    }

    /// <summary>
    /// Whether a picture is to be left out: a RASL picture of an IRAP picture that starts decoding (8.1.3), whose references were
    /// never decoded.
    /// </summary>
    private bool Skips(HevcNal nal)
    {
        if (nal.IsIrap)
        {
            _skipRasl = _firstPicture || _afterEndOfSequence || nal.Type is >= HevcNal.BlaWLp and <= HevcNal.IdrNLp;
            return false;
        }

        return _skipRasl && nal.Type is HevcNal.RaslN or HevcNal.RaslR;
    }

    /// <summary>Throws for what the hardware decoder does not take: other chroma formats and bit depths, and the range extensions.</summary>
    private void CheckSupported(HevcSps sps, HevcPps pps)
    {
        sps.CheckSupported();
        if (sps.ChromaFormatIdc != 1)
        {
            throw HevcErrors.Unsupported(sps.ChromaFormatIdc switch { 0 => "The stream is monochrome.", 2 => "The stream is 4:2:2.", _ => "The stream is 4:4:4." });
        }

        if (sps.BitDepthLuma != sps.BitDepthChroma || sps.BitDepthLuma is not (8 or 10))
        {
            throw HevcErrors.Unsupported($"The stream has {sps.BitDepthLuma}-bit luma and {sps.BitDepthChroma}-bit chroma.");
        }

        if (sps.TransformSkipRotation || sps.TransformSkipContext || sps.ImplicitRdpcm || sps.ExplicitRdpcm || sps.IntraSmoothingDisabled
            || sps.HighPrecisionOffsets || sps.PersistentRiceAdaptation || pps.CrossComponentPrediction || pps.ChromaQpOffsetListEnabled
            || pps.Log2MaxTransformSkipSize != 2 || pps.Log2SaoOffsetScaleLuma != 0 || pps.Log2SaoOffsetScaleChroma != 0)
        {
            throw HevcErrors.Unsupported("The stream uses coding tools of the format range extensions.");
        }

        if (sps.BitDepthLuma == 10 && CapabilitiesFor(10) is null)
        {
            throw HevcErrors.Unsupported($"The stream is 10-bit, which {_device.Physical.Name} does not decode.");
        }
    }

    /// <summary>
    /// Starts a picture from its first slice segment header: its order count, the marking by its reference picture set, the
    /// pictures generated for missing references, and the pictures that leave the buffer for output before it is decoded.
    /// </summary>
    private void StartPicture(HevcNal nal, HevcSliceHeader h)
    {
        (HevcSps sps, HevcPps pps) = Active(h.PpsId);
        CheckSupported(sps, pps);
        Egami.Video.Codecs.H264.H264Sps.CheckSize(_egami.Limits, sps.Width, sps.Height);
        bool noRaslOutput = nal.IsIrap && (_firstPicture || _afterEndOfSequence || nal.Type is >= HevcNal.BlaWLp and <= HevcNal.IdrNLp);
        if (!ReferenceEquals(sps, _activeSps) && (_session is null || sps.Width != _sessionWidth || sps.Height != _sessionHeight
            || sps.BitDepthLuma != _sessionBitDepth || SlotsFor(sps) > _sessionSlots))
        {
            if (!nal.IsIrap)
            {
                throw HevcErrors.Corrupt("The picture size changes at a picture that is not an IRAP picture.");
            }

            // A new size: the frames before are output and the pictures start over.
            while (_dpb.TryFlush(out Picture picture, out _))
            {
                Emit(picture);
            }

            ClearBuffer();
            CreateSession(sps);
        }

        _activeSps = sps;
        int poc = PictureOrderCount(nal, h, sps, noRaslOutput);
        if (nal.TemporalIdPlus1 == 1 && !IsRaslRadlOrSubLayerNonReference(nal.Type))
        {
            _prevTid0Poc = poc;
        }

        ApplyReferencePictureSet(nal, h, sps, poc, noRaslOutput);
        _dpb.Capacity = sps.MaxDecPicBuffering;
        _dpb.MaxNumReorder = sps.MaxNumReorder;
        _dpb.MaxLatencyPictures = sps.MaxLatencyPictures;
        if (noRaslOutput && !_firstPicture)
        {
            // C.5.2.2: an IRAP picture that starts a sequence outputs or drops what waits (a CRA infers NoOutputOfPriorPicsFlag 1).
            if (nal.Type != HevcNal.CraNut && !h.NoOutputOfPriorPics)
            {
                while (_dpb.TryFlush(out Picture leaving, out _))
                {
                    Emit(leaving);
                }
            }

            ClearBuffer();
        }
        else
        {
            while (_dpb.TryBumpBeforeInsert(out Picture leaving, out _))
            {
                Emit(leaving);
            }
        }

        _firstPicture = false;
        _afterEndOfSequence = false;
        Picture pic = new() { Poc = poc, Sps = sps, Pps = pps, Output = h.PicOutput };
        pic.Slot = FreeSlot();
        _cur = pic;
        _first = h;
        _firstNal = nal;
        _slices.Clear();
        PicturesStarted++;
    }

    /// <summary>PicOrderCntVal (8.3.1).</summary>
    private int PictureOrderCount(HevcNal nal, HevcSliceHeader h, HevcSps sps, bool noRaslOutput)
    {
        int maxLsb = 1 << sps.Log2MaxPocLsb;
        int lsb = h.PocLsb, msb;
        if (nal.IsIrap && noRaslOutput)
        {
            msb = 0;
        }
        else
        {
            int prevLsb = _prevTid0Poc & (maxLsb - 1), prevMsb = _prevTid0Poc - prevLsb;
            msb = lsb < prevLsb && prevLsb - lsb >= maxLsb / 2 ? prevMsb + maxLsb
                : lsb > prevLsb && lsb - prevLsb > maxLsb / 2 ? prevMsb - maxLsb
                : prevMsb;
        }

        return msb + lsb;
    }

    // RASL_N, RASL_R, RADL_N, RADL_R and the sub-layer non-reference types TRAIL_N to RSV_VCL_N14 (even types up to 14).
    private static bool IsRaslRadlOrSubLayerNonReference(int type) => type is >= 6 and <= 9 || (type <= 14 && (type & 1) == 0);

    /// <summary>
    /// The reference picture set (8.3.2): finds each picture the current one lists, generates missing ones (8.3.3), marks the
    /// long-term ones and marks every other picture unused for reference.
    /// </summary>
    private void ApplyReferencePictureSet(HevcNal nal, HevcSliceHeader h, HevcSps sps, int poc, bool noRaslOutput)
    {
        _stCurrBefore.Clear();
        _stCurrAfter.Clear();
        _ltCurr.Clear();
        _keep.Clear();
        if (nal.IsIrap && noRaslOutput)
        {
            foreach (Picture p in _dpb.Pictures())
            {
                p.Marking = Picture.Unused;
            }
        }

        if (nal.IsIdr)
        {
            _dpb.RemoveUnused();
            return;
        }

        int maxLsb = 1 << sps.Log2MaxPocLsb;
        List<Picture> ltFoll = [];

        // Long-term pictures first: any reference picture may become one (8-5).
        foreach ((int pocLsb, bool used, int msbCycle) in h.LongTerm)
        {
            int ltPoc = msbCycle < 0 ? pocLsb : pocLsb + poc - (msbCycle * maxLsb) - (poc & (maxLsb - 1));
            Picture? found = null;
            foreach (Picture p in _dpb.Pictures())
            {
                if (p.IsReference && (msbCycle < 0 ? (p.Poc & (maxLsb - 1)) == ltPoc : p.Poc == ltPoc))
                {
                    found = p;
                    break;
                }
            }

            if (found is null && used)
            {
                found = Generate(ltPoc, Picture.LongTerm);
            }

            if (found is not null)
            {
                (used ? _ltCurr : ltFoll).Add(found);
                _keep.Add(found);
            }
        }

        // Short-term pictures (8-6): only short-term reference pictures that are not now long-term.
        HevcShortTermRps rps = h.ShortTermRps;
        for (int i = 0; i < rps.Deltas.Length; i++)
        {
            int stPoc = poc + rps.Deltas[i];
            Picture? found = null;
            foreach (Picture p in _dpb.Pictures())
            {
                if (p.Marking == Picture.ShortTerm && p.Poc == stPoc && !_keep.Contains(p))
                {
                    found = p;
                    break;
                }
            }

            if (found is null && rps.Used[i])
            {
                found = Generate(stPoc, Picture.ShortTerm);
            }

            if (found is null)
            {
                continue;
            }

            _keep.Add(found);
            if (rps.Used[i])
            {
                (i < rps.NumNegative ? _stCurrBefore : _stCurrAfter).Add(found);
            }
        }

        foreach (Picture p in _ltCurr)
        {
            p.Marking = Picture.LongTerm;
        }

        foreach (Picture p in ltFoll)
        {
            p.Marking = Picture.LongTerm;
        }

        foreach (Picture p in _dpb.Pictures())
        {
            if (!_keep.Contains(p))
            {
                p.Marking = Picture.Unused;
            }
        }

        _dpb.RemoveUnused();
    }

    /// <summary>
    /// A picture for a missing reference (8.3.3.2), never output. Its slot holds whatever it held: the GPU's picture memory cannot
    /// be filled with the middle value the software decoder uses, so pictures predicted from it differ from the software decoder's.
    /// </summary>
    private Picture Generate(int poc, int marking)
    {
        Report("Hevc.MissingReference", $"The reference picture with order count {poc} is missing; a stand-in is used.");
        Picture generated = new() { Poc = poc, Marking = marking, Generated = true };
        generated.Slot = FreeSlot();
        _dpb.Insert(generated, poc, needsOutput: false);
        return generated;
    }

    /// <summary>Decodes the current picture on the GPU and stores it for output (C.5.2.3).</summary>
    private void FinishPicture()
    {
        if (_cur is not { } pic || _first is not { } h || _firstNal is not { } nal)
        {
            return;
        }

        _cur = null;
        _first = null;
        _firstNal = null;
        try
        {
            Submit(pic, h, nal);
        }
        finally
        {
            _slices.Clear();
        }

        pic.Marking = Picture.ShortTerm;
        _dpb.Insert(pic, pic.Poc, pic.Output);
        while (_dpb.TryBumpAfterInsert(out Picture leaving, out _))
        {
            Emit(leaving);
        }
    }

    private void Emit(Picture picture)
    {
        picture.Emitted = true;
        _untimed.Add(picture);
        _emitted.Enqueue(picture);
    }

    /// <summary>Gives the frames output since the last call the smallest packet timestamps waiting, in output order.</summary>
    private void TakeTimes()
    {
        foreach (Picture picture in _untimed)
        {
            (picture.Time, picture.Duration, picture.Hdr10Plus) = _times.TryDequeue(out var t, out _) ? t : (MediaTime.Zero, null, null);
        }

        _untimed.Clear();
    }

    /// <summary>
    /// Empties the buffer without output; pictures that waited for it are dropped. Pictures already output stay in the output
    /// queue with their frames (a flush outputs pictures that are still references, and leaves them in the buffer).
    /// </summary>
    private void ClearBuffer()
    {
        foreach (Picture p in _dpb.Pictures())
        {
            if (p.Emitted)
            {
                continue;
            }

            p.Output = false;
            p.Raster?.Dispose();
            p.Raster = null;
        }

        _dpb.Clear();
    }

    /// <summary>After damage: the pictures decoded come out, and decoding starts again at the next IRAP picture.</summary>
    private void StartOver()
    {
        _cur = null;
        _first = null;
        _firstNal = null;
        _slices.Clear();
        while (_dpb.TryFlush(out Picture picture, out _))
        {
            Emit(picture);
        }

        TakeTimes();
        _dpb.Clear();
        _times.Clear();
        _firstPicture = true;
        _skipping = false;
        _waitingForIrap = true;
    }

    /// <summary>The slots the stream needs: one per picture the buffer may hold, the current one included, and one spare.</summary>
    private static int SlotsFor(HevcSps sps) => Math.Max(sps.MaxDecPicBuffering, 1) + 1;

    /// <summary>A DPB slot no reference picture holds.</summary>
    private int FreeSlot()
    {
        Span<bool> used = stackalloc bool[_slots.Length];
        foreach (Picture p in _dpb.Pictures())
        {
            if (p.IsReference && p.Slot >= 0)
            {
                used[p.Slot] = true;
            }
        }

        int free = used.IndexOf(false);
        return free >= 0 ? free : throw HevcErrors.Corrupt("The stream keeps more reference pictures than its sps_max_dec_pic_buffering.");
    }

    private Capabilities? CapabilitiesFor(int bitDepth)
    {
        int index = bitDepth == 8 ? 0 : 1;
        if (_caps[index] is null)
        {
            _caps[index] = Capabilities.Query(_device.Physical, &_profiles[index]);
        }

        return _caps[index]!.Value.Supported ? _caps[index] : null;
    }

    /// <summary>Makes the video session and its pictures for a sequence's size, after the GPU has finished with the old ones.</summary>
    private void CreateSession(HevcSps sps)
    {
        WaitAll();
        DisposeSession();
        int bitDepth = sps.BitDepthLuma;
        ProfileBlock* profile = &_profiles[bitDepth == 8 ? 0 : 1];
        Capabilities capabilities = CapabilitiesFor(bitDepth)!.Value;
        VkVideoCapabilitiesKHR caps = capabilities.Video;
        uint width = (uint)sps.Width, height = (uint)sps.Height;
        if (width > caps.maxCodedExtent.width || height > caps.maxCodedExtent.height)
        {
            throw HevcErrors.Beyond(
                $"{_device.Physical.Name} decodes HEVC up to {caps.maxCodedExtent.width}x{caps.maxCodedExtent.height}; {width}x{height} is larger.");
        }

        // Level 8.5 (general_level_idc 255) puts no limits on the stream; the GPU is tried at its own highest level.
        if (sps.ProfileTierLevel.LevelIdc != 255 && LevelIndex(sps.ProfileTierLevel.LevelIdc) > (int)capabilities.H265.maxLevelIdc)
        {
            throw HevcErrors.Beyond($"{_device.Physical.Name} decodes HEVC up to level index {(int)capabilities.H265.maxLevelIdc}; this stream has general_level_idc {sps.ProfileTierLevel.LevelIdc}.");
        }

        int slots = (int)Math.Min((uint)SlotsFor(sps), caps.maxDpbSlots);
        if (slots < sps.MaxDecPicBuffering || slots - 1 > caps.maxActiveReferencePictures + 1)
        {
            throw HevcErrors.Beyond($"{_device.Physical.Name} keeps {caps.maxDpbSlots} pictures; the stream needs {SlotsFor(sps)}.");
        }

        VkFormat format = bitDepth == 8 ? VkFormat.G8_B8R8_2PLANE_420_UNORM : VkFormat.G10X6_B10X6R10X6_2PLANE_420_UNORM_3PACK16;
        // A picture smaller than the GPU's smallest is decoded into pictures of the smallest size: the SPS gives the decoder its size.
        _codedWidth = AlignUp(Math.Max(width, caps.minCodedExtent.width), Math.Max(1u, caps.pictureAccessGranularity.width));
        _codedHeight = AlignUp(Math.Max(height, caps.minCodedExtent.height), Math.Max(1u, caps.pictureAccessGranularity.height));
        VkExtensionProperties header = caps.stdHeaderVersion;
        VkVideoSessionCreateInfoKHR info = new()
        {
            queueFamilyIndex = (uint)_queue.Family,
            pVideoProfile = &profile->Profile,
            pictureFormat = format,
            maxCodedExtent = new VkExtent2D { width = _codedWidth, height = _codedHeight },
            referencePictureFormat = format,
            maxDpbSlots = (uint)slots,
            maxActiveReferencePictures = (uint)Math.Min(slots - 1, (int)caps.maxActiveReferencePictures),
            pStdHeaderVersion = &header,
        };
        _session = _device.CreateVideoSession(info);
        VkQueryPoolCreateInfo queryInfo = new() { pNext = &profile->Profile, queryType = VkQueryType.RESULT_STATUS_ONLY_KHR, queryCount = InFlight };
        ulong queryPool;
        VkCheck.ThrowIfFailed(_device.Commands.vkCreateQueryPool(_device.Raw, &queryInfo, null, &queryPool), "vkCreateQueryPool");
        _queryPool = new VulkanObjectHandle(_device.Handle, queryPool, _device.Commands.vkDestroyQueryPool);
        _slots = new VulkanImage[slots];
        for (int i = 0; i < slots; i++)
        {
            _slots[i] = CreatePicture(profile, format);
        }

        int sampleBytes = bitDepth == 8 ? 1 : 2;
        ulong frameBytes = (ulong)_codedWidth * _codedHeight * 3 / 2 * (ulong)sampleBytes;
        foreach (Job job in _jobs)
        {
            job.Readback = _device.CreateBuffer(frameBytes, VkBufferUsageFlagBits.TRANSFER_DST_BIT, VkMemoryPropertyFlagBits.HOST_VISIBLE_BIT);
        }

        _alignment = Math.Max(Math.Max(caps.minBitstreamBufferOffsetAlignment, caps.minBitstreamBufferSizeAlignment), 1);
        CreateBitstream(AlignUp((ulong)_codedWidth * _codedHeight * 3 / 4, _alignment), profile);
        _sessionWidth = sps.Width;
        _sessionHeight = sps.Height;
        _sessionBitDepth = bitDepth;
        _sessionSlots = slots;
        _needsReset = true;
        _slotsReady = false;
    }

    private void CreateBitstream(ulong regionSize, ProfileBlock* profile)
    {
        _bitstream?.Dispose();
        _bitstream = null;
        _regionSize = regionSize;
        _bitstream = _device.CreateBuffer(_regionSize * InFlight, VkBufferUsageFlagBits.VIDEO_DECODE_SRC_BIT_KHR, VkMemoryPropertyFlagBits.HOST_VISIBLE_BIT, &profile->List);
    }

    private VulkanImage CreatePicture(ProfileBlock* profile, VkFormat format)
    {
        VkImageCreateInfo info = new()
        {
            pNext = &profile->List,
            imageType = VkImageType._2D,
            format = format,
            extent = new VkExtent3D { width = _codedWidth, height = _codedHeight, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlagBits._1_BIT,
            tiling = VkImageTiling.OPTIMAL,
            usage = VkImageUsageFlagBits.VIDEO_DECODE_DPB_BIT_KHR | VkImageUsageFlagBits.VIDEO_DECODE_DST_BIT_KHR | VkImageUsageFlagBits.TRANSFER_SRC_BIT,
            sharingMode = VkSharingMode.EXCLUSIVE,
            initialLayout = VkImageLayout.UNDEFINED,
        };
        return _device.CreateImage(info);
    }

    /// <summary>The session parameters holding a picture's VPS, SPS and PPS, made the first time the PPS is used with its SPS.</summary>
    private ulong Parameters(HevcSps sps, HevcPps pps)
    {
        HevcVps? vps = _vps[sps.VpsId];
        if (_parameters.TryGetValue(pps, out var made) && ReferenceEquals(made.Sps, sps) && ReferenceEquals(made.Vps, vps))
        {
            return made.Handle.Value;
        }

        if (made.Handle is not null || _parameters.Count >= 16)
        {
            // Parameter sets that were replaced: drop them once the GPU is done with them.
            WaitAll();
            DisposeParameters();
        }

        StdVideoH265ProfileTierLevel ptl = new()
        {
            general_profile_idc = sps.ProfileTierLevel.ProfileIdc is 1 or 2 ? (StdVideoH265ProfileIdc)sps.ProfileTierLevel.ProfileIdc
                : sps.BitDepthLuma == 8 ? StdVideoH265ProfileIdc.MAIN : StdVideoH265ProfileIdc.MAIN_10,
            general_level_idc = (StdVideoH265LevelIdc)Math.Min(LevelIndex(sps.ProfileTierLevel.LevelIdc), (int)CapabilitiesFor(sps.BitDepthLuma)!.Value.H265.maxLevelIdc),
        };
        ptl.flags.general_tier_flag = sps.ProfileTierLevel.Tier;
        ptl.flags.general_progressive_source_flag = true;
        ptl.flags.general_frame_only_constraint_flag = true;
        StdVideoH265DecPicBufMgr dpb = default;
        for (int i = 0; i < 7; i++)
        {
            dpb.max_dec_pic_buffering_minus1[i] = (byte)(sps.MaxDecPicBuffering - 1);
            dpb.max_num_reorder_pics[i] = (byte)sps.MaxNumReorder;
            dpb.max_latency_increase_plus1[i] = (uint)sps.MaxLatencyIncreasePlus1;
        }

        StdVideoH265VideoParameterSet stdVps = new()
        {
            vps_video_parameter_set_id = (byte)sps.VpsId,
            vps_max_sub_layers_minus1 = (byte)(vps?.MaxSubLayersMinus1 ?? sps.MaxSubLayersMinus1),
            pDecPicBufMgr = &dpb,
            pProfileTierLevel = &ptl,
        };
        stdVps.flags.vps_temporal_id_nesting_flag = vps?.TemporalIdNesting ?? sps.TemporalIdNesting;
        stdVps.flags.vps_sub_layer_ordering_info_present_flag = true;

        int setCount = sps.ShortTermRps.Count;
        StdVideoH265ShortTermRefPicSet* sets = stackalloc StdVideoH265ShortTermRefPicSet[Math.Max(setCount, 1)];
        for (int i = 0; i < setCount; i++)
        {
            sets[i] = ShortTermSet(sps.ShortTermRps[i]);
        }

        StdVideoH265LongTermRefPicsSps longTerm = default;
        for (int i = 0; i < sps.NumLongTermRefPicsSps; i++)
        {
            longTerm.used_by_curr_pic_lt_sps_flag |= sps.UsedByCurrPicLtSps[i] ? 1u << i : 0;
            longTerm.lt_ref_pic_poc_lsb_sps[i] = (uint)sps.LtRefPicPocLsbSps[i];
        }

        StdVideoH265ScalingLists spsLists = default, ppsLists = default;
        bool spsListData = sps.ScalingListEnabled && sps.ScalingList is { } spsList && !ReferenceEquals(spsList, HevcScalingList.Default);
        if (spsListData)
        {
            FillScalingLists(sps.ScalingList!, &spsLists);
        }

        StdVideoH265SequenceParameterSet stdSps = new()
        {
            chroma_format_idc = (StdVideoH265ChromaFormatIdc)sps.ChromaFormatIdc,
            pic_width_in_luma_samples = (uint)sps.Width,
            pic_height_in_luma_samples = (uint)sps.Height,
            sps_video_parameter_set_id = (byte)sps.VpsId,
            sps_max_sub_layers_minus1 = (byte)sps.MaxSubLayersMinus1,
            sps_seq_parameter_set_id = (byte)sps.Id,
            bit_depth_luma_minus8 = (byte)(sps.BitDepthLuma - 8),
            bit_depth_chroma_minus8 = (byte)(sps.BitDepthChroma - 8),
            log2_max_pic_order_cnt_lsb_minus4 = (byte)(sps.Log2MaxPocLsb - 4),
            log2_min_luma_coding_block_size_minus3 = (byte)(sps.MinCbLog2Size - 3),
            log2_diff_max_min_luma_coding_block_size = (byte)(sps.CtbLog2Size - sps.MinCbLog2Size),
            log2_min_luma_transform_block_size_minus2 = (byte)(sps.MinTbLog2Size - 2),
            log2_diff_max_min_luma_transform_block_size = (byte)(sps.MaxTbLog2Size - sps.MinTbLog2Size),
            max_transform_hierarchy_depth_inter = (byte)sps.MaxTransformHierarchyDepthInter,
            max_transform_hierarchy_depth_intra = (byte)sps.MaxTransformHierarchyDepthIntra,
            num_short_term_ref_pic_sets = (byte)setCount,
            num_long_term_ref_pics_sps = (byte)sps.NumLongTermRefPicsSps,
            pcm_sample_bit_depth_luma_minus1 = (byte)(sps.PcmEnabled ? sps.PcmBitDepthLuma - 1 : 0),
            pcm_sample_bit_depth_chroma_minus1 = (byte)(sps.PcmEnabled ? sps.PcmBitDepthChroma - 1 : 0),
            log2_min_pcm_luma_coding_block_size_minus3 = (byte)(sps.PcmEnabled ? sps.Log2MinPcmCbSize - 3 : 0),
            log2_diff_max_min_pcm_luma_coding_block_size = (byte)(sps.PcmEnabled ? sps.Log2MaxPcmCbSize - sps.Log2MinPcmCbSize : 0),
            conf_win_left_offset = (uint)(sps.Conformance.Left / sps.SubWidthC),
            conf_win_right_offset = (uint)(sps.Conformance.Right / sps.SubWidthC),
            conf_win_top_offset = (uint)(sps.Conformance.Top / sps.SubHeightC),
            conf_win_bottom_offset = (uint)(sps.Conformance.Bottom / sps.SubHeightC),
            pProfileTierLevel = &ptl,
            pDecPicBufMgr = &dpb,
            pScalingLists = spsListData ? &spsLists : null,
            pShortTermRefPicSet = setCount > 0 ? sets : null,
            pLongTermRefPicsSps = sps.LongTermRefsPresent ? &longTerm : null,
        };
        stdSps.flags.sps_temporal_id_nesting_flag = sps.TemporalIdNesting;
        stdSps.flags.conformance_window_flag = (sps.Conformance.Left | sps.Conformance.Right | sps.Conformance.Top | sps.Conformance.Bottom) != 0;
        stdSps.flags.sps_sub_layer_ordering_info_present_flag = true;
        stdSps.flags.scaling_list_enabled_flag = sps.ScalingListEnabled;
        stdSps.flags.sps_scaling_list_data_present_flag = spsListData;
        stdSps.flags.amp_enabled_flag = sps.AmpEnabled;
        stdSps.flags.sample_adaptive_offset_enabled_flag = sps.SaoEnabled;
        stdSps.flags.pcm_enabled_flag = sps.PcmEnabled;
        stdSps.flags.pcm_loop_filter_disabled_flag = sps.PcmLoopFilterDisabled;
        stdSps.flags.long_term_ref_pics_present_flag = sps.LongTermRefsPresent;
        stdSps.flags.sps_temporal_mvp_enabled_flag = sps.TemporalMvpEnabled;
        stdSps.flags.strong_intra_smoothing_enabled_flag = sps.StrongIntraSmoothing;

        if (pps.ScalingList is { } ppsList)
        {
            FillScalingLists(ppsList, &ppsLists);
        }

        StdVideoH265PictureParameterSet stdPps = new()
        {
            pps_pic_parameter_set_id = (byte)pps.Id,
            pps_seq_parameter_set_id = (byte)sps.Id,
            sps_video_parameter_set_id = (byte)sps.VpsId,
            num_extra_slice_header_bits = (byte)pps.NumExtraSliceHeaderBits,
            num_ref_idx_l0_default_active_minus1 = (byte)(pps.NumRefIdxL0Default - 1),
            num_ref_idx_l1_default_active_minus1 = (byte)(pps.NumRefIdxL1Default - 1),
            init_qp_minus26 = (sbyte)pps.InitQpMinus26,
            diff_cu_qp_delta_depth = (byte)pps.DiffCuQpDeltaDepth,
            pps_cb_qp_offset = (sbyte)pps.CbQpOffset,
            pps_cr_qp_offset = (sbyte)pps.CrQpOffset,
            pps_beta_offset_div2 = (sbyte)pps.BetaOffsetDiv2,
            pps_tc_offset_div2 = (sbyte)pps.TcOffsetDiv2,
            log2_parallel_merge_level_minus2 = (byte)(pps.Log2ParMrgLevel - 2),
            log2_max_transform_skip_block_size_minus2 = (byte)(pps.Log2MaxTransformSkipSize - 2),
            num_tile_columns_minus1 = (byte)(pps.TileColumns - 1),
            num_tile_rows_minus1 = (byte)(pps.TileRows - 1),
            pScalingLists = pps.ScalingList is not null ? &ppsLists : null,
        };
        for (int i = 0; i < pps.ColumnWidths.Length && i < 19; i++)
        {
            stdPps.column_width_minus1[i] = (ushort)(pps.ColumnWidths[i] - 1);
        }

        for (int i = 0; i < pps.RowHeights.Length && i < 21; i++)
        {
            stdPps.row_height_minus1[i] = (ushort)(pps.RowHeights[i] - 1);
        }

        stdPps.flags.dependent_slice_segments_enabled_flag = pps.DependentSliceSegmentsEnabled;
        stdPps.flags.output_flag_present_flag = pps.OutputFlagPresent;
        stdPps.flags.sign_data_hiding_enabled_flag = pps.SignDataHiding;
        stdPps.flags.cabac_init_present_flag = pps.CabacInitPresent;
        stdPps.flags.constrained_intra_pred_flag = pps.ConstrainedIntraPred;
        stdPps.flags.transform_skip_enabled_flag = pps.TransformSkipEnabled;
        stdPps.flags.cu_qp_delta_enabled_flag = pps.CuQpDeltaEnabled;
        stdPps.flags.pps_slice_chroma_qp_offsets_present_flag = pps.SliceChromaQpOffsetsPresent;
        stdPps.flags.weighted_pred_flag = pps.WeightedPred;
        stdPps.flags.weighted_bipred_flag = pps.WeightedBipred;
        stdPps.flags.transquant_bypass_enabled_flag = pps.TransquantBypassEnabled;
        stdPps.flags.tiles_enabled_flag = pps.TilesEnabled;
        stdPps.flags.entropy_coding_sync_enabled_flag = pps.EntropyCodingSync;
        stdPps.flags.uniform_spacing_flag = pps.UniformSpacing;
        stdPps.flags.loop_filter_across_tiles_enabled_flag = pps.LoopFilterAcrossTiles;
        stdPps.flags.pps_loop_filter_across_slices_enabled_flag = pps.LoopFilterAcrossSlices;
        stdPps.flags.deblocking_filter_control_present_flag = pps.DeblockingControlPresent;
        stdPps.flags.deblocking_filter_override_enabled_flag = pps.DeblockingOverrideEnabled;
        stdPps.flags.pps_deblocking_filter_disabled_flag = pps.DeblockingDisabled;
        stdPps.flags.pps_scaling_list_data_present_flag = pps.ScalingList is not null;
        stdPps.flags.lists_modification_present_flag = pps.ListsModificationPresent;
        stdPps.flags.slice_segment_header_extension_present_flag = pps.SliceHeaderExtensionPresent;

        VkVideoDecodeH265SessionParametersAddInfoKHR add = new()
        {
            stdVPSCount = 1,
            pStdVPSs = &stdVps,
            stdSPSCount = 1,
            pStdSPSs = &stdSps,
            stdPPSCount = 1,
            pStdPPSs = &stdPps,
        };
        VkVideoDecodeH265SessionParametersCreateInfoKHR h265Info = new() { maxStdVPSCount = 1, maxStdSPSCount = 1, maxStdPPSCount = 1, pParametersAddInfo = &add };
        VkVideoSessionParametersCreateInfoKHR info = new() { pNext = &h265Info, videoSession = _session!.Handle };
        ulong raw;
        VkCheck.ThrowIfFailed(_device.Commands.vkCreateVideoSessionParametersKHR(_device.Raw, &info, null, &raw), "vkCreateVideoSessionParametersKHR");
        _parameters[pps] = (sps, vps, new VulkanObjectHandle(_device.Handle, raw, _device.Commands.vkDestroyVideoSessionParametersKHR));
        return raw;
    }

    /// <summary>
    /// A short-term reference picture set in the Std form: the syntax of a predicted set, and for every set the pictures it
    /// resolves to as explicit deltas (num_negative_pics, delta_poc_s0_minus1 and the used flags).
    /// </summary>
    private static StdVideoH265ShortTermRefPicSet ShortTermSet(HevcShortTermRps rps)
    {
        StdVideoH265ShortTermRefPicSet set = new()
        {
            num_negative_pics = (byte)rps.NumNegative,
            num_positive_pics = (byte)(rps.Deltas.Length - rps.NumNegative),
        };
        if (rps.Predicted)
        {
            set.flags.inter_ref_pic_set_prediction_flag = true;
            set.flags.delta_rps_sign = rps.DeltaRpsSign;
            set.delta_idx_minus1 = (uint)rps.DeltaIdxMinus1;
            set.abs_delta_rps_minus1 = (ushort)rps.AbsDeltaRpsMinus1;
            set.used_by_curr_pic_flag = (ushort)rps.UsedByCurrPicFlags;
            set.use_delta_flag = (ushort)rps.UseDeltaFlags;
        }

        int previous = 0;
        for (int j = 0; j < rps.NumNegative; j++)
        {
            set.delta_poc_s0_minus1[j] = (ushort)(previous - rps.Deltas[j] - 1);
            previous = rps.Deltas[j];
            set.used_by_curr_pic_s0_flag |= (ushort)(rps.Used[j] ? 1 << j : 0);
        }

        previous = 0;
        for (int j = 0; j < rps.Deltas.Length - rps.NumNegative; j++)
        {
            int delta = rps.Deltas[rps.NumNegative + j];
            set.delta_poc_s1_minus1[j] = (ushort)(delta - previous - 1);
            previous = delta;
            set.used_by_curr_pic_s1_flag |= (ushort)(rps.Used[rps.NumNegative + j] ? 1 << j : 0);
        }

        return set;
    }

    /// <summary>The Std scaling lists: the coded lists in the order of the syntax (up-right diagonal) and the DC values.</summary>
    private static void FillScalingLists(HevcScalingList source, StdVideoH265ScalingLists* target)
    {
        for (int m = 0; m < 6; m++)
        {
            source.Coded(0, m).CopyTo(new Span<byte>(target->ScalingList4x4 + (m * 16), 16));
            source.Coded(1, m).CopyTo(new Span<byte>(target->ScalingList8x8 + (m * 64), 64));
            source.Coded(2, m).CopyTo(new Span<byte>(target->ScalingList16x16 + (m * 64), 64));
            target->ScalingListDCCoef16x16[m] = (byte)source.Dc(2, m);
        }

        for (int m = 0; m < 2; m++)
        {
            source.Coded(3, m * 3).CopyTo(new Span<byte>(target->ScalingList32x32 + (m * 64), 64));
            target->ScalingListDCCoef32x32[m] = (byte)source.Dc(3, m * 3);
        }
    }

    /// <summary>The Std level enumerator of a general_level_idc (30 times the level number).</summary>
    private static int LevelIndex(int levelIdc) => levelIdc switch
    {
        <= 30 => 0,
        <= 60 => 1,
        <= 63 => 2,
        <= 90 => 3,
        <= 93 => 4,
        <= 120 => 5,
        <= 123 => 6,
        <= 150 => 7,
        <= 153 => 8,
        <= 156 => 9,
        <= 180 => 10,
        <= 183 => 11,
        _ => 12,
    };

    /// <summary>Records and submits the decoding of the current picture into its slot, and the copy of the slot to host memory.</summary>
    private void Submit(Picture pic, HevcSliceHeader h, HevcNal nal)
    {
        int jobIndex = _nextJob;
        _nextJob = (_nextJob + 1) % InFlight;
        Job job = _jobs[jobIndex];
        while (job.Picture is not null)
        {
            // The oldest job: everything submitted before it is done first.
            Collect(_submitted.Dequeue());
        }

        HevcSps sps = pic.Sps!;
        HevcPps pps = pic.Pps!;
        ulong parameters = Parameters(sps, pps);

        // The slice segments with start codes, at offsets from the picture's start.
        int size = 0;
        foreach (ReadOnlyMemory<byte> slice in _slices)
        {
            size += 3 + slice.Length;
        }

        if ((ulong)size + _alignment > _regionSize)
        {
            WaitAll();
            CreateBitstream(AlignUp((ulong)size * 2, _alignment), &_profiles[_sessionBitDepth == 8 ? 0 : 1]);
        }

        Span<byte> region = _bitstream!.Span.Slice((int)((ulong)jobIndex * _regionSize), (int)_regionSize);
        uint* offsets = stackalloc uint[_slices.Count];
        int at = 0;
        for (int i = 0; i < _slices.Count; i++)
        {
            offsets[i] = (uint)at;
            region[at] = 0;
            region[at + 1] = 0;
            region[at + 2] = 1;
            _slices[i].Span.CopyTo(region[(at + 3)..]);
            at += 3 + _slices[i].Length;
        }

        ulong range = AlignUp((ulong)at, _alignment);
        region[at..(int)range].Clear();

        // The reference pictures in the buffer, each in its slot.
        List<Picture> refs = [];
        foreach (Picture p in _dpb.Pictures())
        {
            if (p.IsReference && p.Slot >= 0)
            {
                refs.Add(p);
            }
        }

        VkDeviceCommands vk = _device.Commands;
        nint cb = job.Commands;
        _pool.Begin(cb);
        vk.vkCmdResetQueryPool(cb, _queryPool!.Value, (uint)jobIndex, 1);
        int slotCount = _slots.Length;
        VkImageMemoryBarrier2* barriers = stackalloc VkImageMemoryBarrier2[slotCount];
        int barrierCount = 0;
        if (!_slotsReady)
        {
            _slotsReady = true;
            for (int i = 0; i < slotCount; i++)
            {
                barriers[barrierCount++] = Barrier(_slots[i].Handle, VkImageLayout.UNDEFINED, VkImageLayout.VIDEO_DECODE_DPB_KHR, VkPipelineStageFlagBits2.NONE, 0, VkPipelineStageFlagBits2.VIDEO_DECODE_BIT_KHR, VkAccessFlagBits2.VIDEO_DECODE_READ_BIT_KHR | VkAccessFlagBits2.VIDEO_DECODE_WRITE_BIT_KHR);
            }
        }

        // The pictures decoded before this one wrote the references it reads.
        VkMemoryBarrier2 dpbHazard = new()
        {
            srcStageMask = VkPipelineStageFlagBits2.VIDEO_DECODE_BIT_KHR,
            srcAccessMask = VkAccessFlagBits2.VIDEO_DECODE_WRITE_BIT_KHR,
            dstStageMask = VkPipelineStageFlagBits2.VIDEO_DECODE_BIT_KHR,
            dstAccessMask = VkAccessFlagBits2.VIDEO_DECODE_READ_BIT_KHR | VkAccessFlagBits2.VIDEO_DECODE_WRITE_BIT_KHR,
        };
        PipelineBarrier(cb, barriers, barrierCount, &dpbHazard);

        int n = refs.Count;
        VkExtent2D extent = new() { width = _codedWidth, height = _codedHeight };
        VkVideoPictureResourceInfoKHR* resources = stackalloc VkVideoPictureResourceInfoKHR[n + 1];
        StdVideoDecodeH265ReferenceInfo* refInfos = stackalloc StdVideoDecodeH265ReferenceInfo[n + 1];
        VkVideoDecodeH265DpbSlotInfoKHR* slotInfos = stackalloc VkVideoDecodeH265DpbSlotInfoKHR[n + 1];
        VkVideoReferenceSlotInfoKHR* slots = stackalloc VkVideoReferenceSlotInfoKHR[n + 1];
        VkVideoReferenceSlotInfoKHR* beginSlots = stackalloc VkVideoReferenceSlotInfoKHR[n + 1];
        for (int i = 0; i <= n; i++)
        {
            bool setup = i == n;
            Picture p = setup ? pic : refs[i];
            resources[i] = new VkVideoPictureResourceInfoKHR { codedExtent = extent, imageViewBinding = _slots[p.Slot].View };
            refInfos[i] = new StdVideoDecodeH265ReferenceInfo { PicOrderCntVal = p.Poc };
            refInfos[i].flags.used_for_long_term_reference = !setup && p.Marking == Picture.LongTerm;
            slotInfos[i] = new VkVideoDecodeH265DpbSlotInfoKHR { pStdReferenceInfo = &refInfos[i] };
            slots[i] = new VkVideoReferenceSlotInfoKHR { pNext = &slotInfos[i], slotIndex = p.Slot, pPictureResource = &resources[i] };
            beginSlots[i] = slots[i];
        }

        // The picture being decoded is bound without a slot until the decode sets it up.
        beginSlots[n].slotIndex = -1;
        beginSlots[n].pNext = null;
        VkVideoBeginCodingInfoKHR begin = new()
        {
            videoSession = _session!.Handle,
            videoSessionParameters = parameters,
            referenceSlotCount = (uint)n + 1,
            pReferenceSlots = beginSlots,
        };
        vk.vkCmdBeginVideoCodingKHR(cb, &begin);
        if (_needsReset)
        {
            _needsReset = false;
            VkVideoCodingControlInfoKHR control = new() { flags = VkVideoCodingControlFlagBitsKHR.RESET_BIT_KHR };
            vk.vkCmdControlVideoCodingKHR(cb, &control);
        }

        StdVideoDecodeH265PictureInfo std = new()
        {
            sps_video_parameter_set_id = (byte)sps.VpsId,
            pps_seq_parameter_set_id = (byte)sps.Id,
            pps_pic_parameter_set_id = (byte)pps.Id,
            NumDeltaPocsOfRefRpsIdx = (byte)(!h.ShortTermRpsFromSps && h.ShortTermRps.Predicted ? h.ShortTermRps.RefNumDeltaPocs : 0),
            PicOrderCntVal = pic.Poc,
            NumBitsForSTRefPicSetInSlice = (ushort)(h.ShortTermRpsFromSps ? 0 : h.ShortTermRpsBits),
        };
        std.flags.IrapPicFlag = nal.IsIrap;
        std.flags.IdrPicFlag = nal.IsIdr;
        std.flags.IsReference = true;
        std.flags.short_term_ref_pic_set_sps_flag = h.ShortTermRpsFromSps;
        new Span<byte>(std.RefPicSetStCurrBefore, 8).Fill(0xFF);
        new Span<byte>(std.RefPicSetStCurrAfter, 8).Fill(0xFF);
        new Span<byte>(std.RefPicSetLtCurr, 8).Fill(0xFF);
        for (int i = 0; i < _stCurrBefore.Count && i < 8; i++)
        {
            std.RefPicSetStCurrBefore[i] = (byte)_stCurrBefore[i].Slot;
        }

        for (int i = 0; i < _stCurrAfter.Count && i < 8; i++)
        {
            std.RefPicSetStCurrAfter[i] = (byte)_stCurrAfter[i].Slot;
        }

        for (int i = 0; i < _ltCurr.Count && i < 8; i++)
        {
            std.RefPicSetLtCurr[i] = (byte)_ltCurr[i].Slot;
        }

        VkVideoDecodeH265PictureInfoKHR h265Picture = new() { pStdPictureInfo = &std, sliceSegmentCount = (uint)_slices.Count, pSliceSegmentOffsets = offsets };
        VkVideoDecodeInfoKHR decode = new()
        {
            pNext = &h265Picture,
            srcBuffer = _bitstream.Handle,
            srcBufferOffset = (ulong)jobIndex * _regionSize,
            srcBufferRange = range,
            dstPictureResource = resources[n],
            pSetupReferenceSlot = &slots[n],
            referenceSlotCount = (uint)n,
            pReferenceSlots = n > 0 ? slots : null,
        };
        vk.vkCmdBeginQuery(cb, _queryPool.Value, (uint)jobIndex, 0);
        vk.vkCmdDecodeVideoKHR(cb, &decode);
        vk.vkCmdEndQuery(cb, _queryPool.Value, (uint)jobIndex);
        VkVideoEndCodingInfoKHR end = new();
        vk.vkCmdEndVideoCodingKHR(cb, &end);

        // The decoded picture out to host memory, its slot back to the DPB layout for the pictures after.
        ulong image = _slots[pic.Slot].Handle;
        barriers[0] = Barrier(image, VkImageLayout.VIDEO_DECODE_DPB_KHR, VkImageLayout.TRANSFER_SRC_OPTIMAL, VkPipelineStageFlagBits2.VIDEO_DECODE_BIT_KHR, VkAccessFlagBits2.VIDEO_DECODE_WRITE_BIT_KHR, VkPipelineStageFlagBits2.ALL_TRANSFER_BIT, VkAccessFlagBits2.TRANSFER_READ_BIT);
        PipelineBarrier(cb, barriers, 1, null);
        ulong sampleBytes = _sessionBitDepth == 8 ? 1u : 2u;
        VkBufferImageCopy* regions = stackalloc VkBufferImageCopy[2];
        regions[0] = new VkBufferImageCopy
        {
            imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlagBits.PLANE_0_BIT, layerCount = 1 },
            imageExtent = new VkExtent3D { width = _codedWidth, height = _codedHeight, depth = 1 },
        };
        regions[1] = new VkBufferImageCopy
        {
            bufferOffset = (ulong)_codedWidth * _codedHeight * sampleBytes,
            imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlagBits.PLANE_1_BIT, layerCount = 1 },
            imageExtent = new VkExtent3D { width = _codedWidth / 2, height = _codedHeight / 2, depth = 1 },
        };
        vk.vkCmdCopyImageToBuffer(cb, image, VkImageLayout.TRANSFER_SRC_OPTIMAL, job.Readback!.Handle, 2, regions);
        barriers[0] = Barrier(image, VkImageLayout.TRANSFER_SRC_OPTIMAL, VkImageLayout.VIDEO_DECODE_DPB_KHR, VkPipelineStageFlagBits2.ALL_TRANSFER_BIT, VkAccessFlagBits2.TRANSFER_READ_BIT, VkPipelineStageFlagBits2.VIDEO_DECODE_BIT_KHR, VkAccessFlagBits2.VIDEO_DECODE_READ_BIT_KHR | VkAccessFlagBits2.VIDEO_DECODE_WRITE_BIT_KHR);
        VkMemoryBarrier2 toHost = new()
        {
            srcStageMask = VkPipelineStageFlagBits2.ALL_TRANSFER_BIT,
            srcAccessMask = VkAccessFlagBits2.TRANSFER_WRITE_BIT,
            dstStageMask = VkPipelineStageFlagBits2.HOST_BIT,
            dstAccessMask = VkAccessFlagBits2.HOST_READ_BIT,
        };
        PipelineBarrier(cb, barriers, 1, &toHost);
        _pool.End(cb);
        _device.Submit(_queue, cb, job.Fence);
        job.Picture = pic;
        pic.Job = jobIndex;
        _submitted.Enqueue(jobIndex);
    }

    /// <summary>Waits for a job, checks how the decode went and copies its picture out when it is to be output.</summary>
    private void Collect(int jobIndex)
    {
        Job job = _jobs[jobIndex];
        Picture pic = job.Picture!;
        _device.WaitAndReset(job.Fence);
        job.Picture = null;
        pic.Job = -1;
        long status = 0;

        // The fence has signalled, so the status is there unless the driver gave up on the picture (a broken stream can make it
        // time out): a status that is not ready is a failed decode, where waiting for it would never end.
        VkResult queried = _device.Commands.vkGetQueryPoolResults(_device.Raw, _queryPool!.Value, (uint)jobIndex, 1, 8, &status, 8, VkQueryResultFlagBits.WITH_STATUS_BIT_KHR | VkQueryResultFlagBits._64_BIT);
        if (queried != VkResult.NOT_READY)
        {
            VkCheck.ThrowIfFailed(queried, "vkGetQueryPoolResults");
        }
        else
        {
            status = 0;
        }

        if (status <= 0)
        {
            string message = $"The GPU failed to decode the picture with order count {pic.Poc} (status {status}).";
            Report("Hevc.CorruptFrame", message);
            if (_errors == DecodeErrorMode.Throw)
            {
                throw HevcErrors.Corrupt(message);
            }

            if (_errors == DecodeErrorMode.Skip)
            {
                return;
            }
        }

        if (pic.Output && pic.Sps is { } sps)
        {
            pic.Raster = ReadPicture(job.Readback!, sps);
        }
    }

    private void WaitAll()
    {
        while (_submitted.Count > 0)
        {
            Collect(_submitted.Dequeue());
        }
    }

    /// <summary>Copies the picture's conformance window out of a readback buffer's two planes.</summary>
    private YuvRaster ReadPicture(VulkanBuffer readback, HevcSps sps)
    {
        int width = sps.OutputWidth, height = sps.OutputHeight;
        ColorCodePoints color = ColorOf(sps, _track.Color);
        ChromaSiting siting = _track.ChromaSiting ?? ChromaSiting.Left;
        YuvColor yuvColor = color.TryGetYuvColor(siting, out YuvColor c) ? c
            : new YuvColor(height <= 576 ? YuvMatrix.Bt601 : YuvMatrix.Bt709, color.FullRange ? YuvRange.Full : YuvRange.Limited, siting);
        int stride = (int)_codedWidth, left = sps.Conformance.Left, top = sps.Conformance.Top;
        if (sps.BitDepthLuma == 8)
        {
            YuvRaster<byte> raster = YuvRaster.Create(width, height, ChromaFormat.Yuv420, yuvColor, false, _egami);
            raster.Metadata.CodePoints = color;
            ReadOnlySpan<byte> nv12 = readback.Span;
            for (int y = 0; y < height; y++)
            {
                nv12.Slice(((y + top) * stride) + left, width).CopyTo(raster.Y.Row(y));
            }

            ReadOnlySpan<byte> uv = nv12[(stride * (int)_codedHeight)..];
            YuvPlane<byte> u = raster.U!, v = raster.V!;
            int cw = u.Width;
            for (int y = 0; y < u.Height; y++)
            {
                ReadOnlySpan<byte> row = uv.Slice(((y + (top / 2)) * stride) + left, cw * 2);
                Span<byte> ur = u.Row(y), vr = v.Row(y);
                for (int x = 0; x < cw; x++)
                {
                    ur[x] = row[2 * x];
                    vr[x] = row[(2 * x) + 1];
                }
            }

            return raster;
        }

        YuvRaster<ushort> words = YuvRaster.Create(width, height, ChromaFormat.Yuv420, sps.BitDepthLuma, yuvColor, false, _egami);
        words.Metadata.CodePoints = color;

        // P010: the 10 bits of each sample in the high bits of 16.
        ReadOnlySpan<ushort> p010 = MemoryMarshal.Cast<byte, ushort>(readback.Span);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<ushort> row = p010.Slice(((y + top) * stride) + left, width);
            Span<ushort> target = words.Y.Row(y);
            for (int x = 0; x < width; x++)
            {
                target[x] = (ushort)(row[x] >> 6);
            }
        }

        ReadOnlySpan<ushort> uv16 = p010[(stride * (int)_codedHeight)..];
        YuvPlane<ushort> u16 = words.U!, v16 = words.V!;
        for (int y = 0; y < u16.Height; y++)
        {
            ReadOnlySpan<ushort> row = uv16.Slice(((y + (top / 2)) * stride) + left, u16.Width * 2);
            Span<ushort> ur = u16.Row(y), vr = v16.Row(y);
            for (int x = 0; x < u16.Width; x++)
            {
                ur[x] = (ushort)(row[2 * x] >> 6);
                vr[x] = (ushort)(row[(2 * x) + 1] >> 6);
            }
        }

        return words;
    }

    /// <summary>
    /// The frame's color, as Egami's software decoder gives it: the VUI's colour description wins over the container's; without
    /// either, the matrix is BT.601 for standard definition and BT.709 above.
    /// </summary>
    private static ColorCodePoints ColorOf(HevcSps sps, ColorCodePoints? container)
    {
        HevcVui? vui = sps.Vui;
        ColorCodePoints stream = vui is not null && (vui.ColourPrimaries != 2 || vui.TransferCharacteristics != 2 || vui.MatrixCoefficients != 2)
            ? new ColorCodePoints((byte)vui.ColourPrimaries, (byte)vui.TransferCharacteristics, (byte)vui.MatrixCoefficients, vui.FullRange)
            : new ColorCodePoints(2, 2, (byte)(sps.OutputHeight <= 576 ? 6 : 1), vui?.FullRange ?? false);
        bool unspecified = stream.Primaries == 2 && stream.TransferCharacteristics == 2 && stream.MatrixCoefficients == (sps.OutputHeight <= 576 ? 6 : 1);
        return unspecified && container is { } c ? (stream.FullRange ? c with { FullRange = true } : c) : stream;
    }

    private void Report(string id, string message)
    {
        if (_egami.Diagnostics is null)
        {
            return;
        }

        _diagnostics ??= _egami.StartDiagnostics();
        _diagnostics.Report(DiagnosticLevel.Warning, id, message, frame: _packet);
    }

    private static VkImageMemoryBarrier2 Barrier(
        ulong image, VkImageLayout from, VkImageLayout to, VkPipelineStageFlagBits2 srcStage, VkAccessFlagBits2 srcAccess, VkPipelineStageFlagBits2 dstStage, VkAccessFlagBits2 dstAccess) => new()
        {
            srcStageMask = srcStage,
            srcAccessMask = srcAccess,
            dstStageMask = dstStage,
            dstAccessMask = dstAccess,
            oldLayout = from,
            newLayout = to,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
            image = image,
            subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlagBits.COLOR_BIT, levelCount = 1, layerCount = 1 },
        };

    private void PipelineBarrier(nint cb, VkImageMemoryBarrier2* images, int imageCount, VkMemoryBarrier2* memory)
    {
        VkDependencyInfo dependency = new()
        {
            memoryBarrierCount = memory is null ? 0u : 1u,
            pMemoryBarriers = memory,
            imageMemoryBarrierCount = (uint)imageCount,
            pImageMemoryBarriers = images,
        };
        _device.Commands.vkCmdPipelineBarrier2(cb, &dependency);
    }

    private void DisposeParameters()
    {
        foreach ((_, _, VulkanObjectHandle handle) in _parameters.Values)
        {
            handle.Dispose();
        }

        _parameters.Clear();
    }

    private void DisposeSession()
    {
        DisposeParameters();
        foreach (VulkanImage image in _slots)
        {
            image.Dispose();
        }

        _slots = [];
        foreach (Job? job in _jobs)
        {
            job?.Readback?.Dispose();
            job?.Readback = null;
        }

        _bitstream?.Dispose();
        _bitstream = null;
        _queryPool?.Dispose();
        _queryPool = null;
        _session?.Dispose();
        _session = null;
    }

    private void DisposeResources()
    {
        DisposeSession();
        foreach (Job? job in _jobs)
        {
            job?.Fence.Dispose();
        }

        _pool?.Dispose();
        if (_profiles != null)
        {
            NativeMemory.Free(_profiles);
        }
    }

    private static uint AlignUp(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>A picture of the stream: its order count, reference marking, slot and, once read back, its planes.</summary>
    private sealed class Picture : INalDpbPicture
    {
        public const int Unused = 0;
        public const int ShortTerm = 1;
        public const int LongTerm = 2;

        public int Poc { get; set; }

        /// <summary>Gets or sets the marking: <see cref="Unused"/>, <see cref="ShortTerm"/> or <see cref="LongTerm"/>.</summary>
        public int Marking { get; set; } = ShortTerm;

        public bool IsReference => Marking != Unused;

        public bool Generated { get; set; }

        public bool Output { get; set; }

        /// <summary>Gets or sets whether the picture is in the output queue.</summary>
        public bool Emitted { get; set; }

        public int Slot { get; set; } = -1;

        public int Job { get; set; } = -1;

        public HevcSps? Sps { get; set; }

        public HevcPps? Pps { get; set; }

        public YuvRaster? Raster { get; set; }

        public MediaTime Time { get; set; }

        public MediaTime? Duration { get; set; }

        public Hdr10PlusMetadata? Hdr10Plus { get; set; }
    }

    /// <summary>One picture's GPU resources: its command buffer, fence and readback buffer; the query and bitstream share go by its index.</summary>
    private sealed class Job(nint commands, VulkanObjectHandle fence)
    {
        public nint Commands { get; } = commands;

        public VulkanObjectHandle Fence { get; } = fence;

        public VulkanBuffer? Readback { get; set; }

        public Picture? Picture { get; set; }
    }

    /// <summary>A decode profile and its list, in native memory: images, buffers and the session are made against them.</summary>
    private struct ProfileBlock
    {
        public VkVideoProfileInfoKHR Profile;
        public VkVideoDecodeH265ProfileInfoKHR H265;
        public VkVideoDecodeUsageInfoKHR Usage;
        public VkVideoProfileListInfoKHR List;

        public static void Fill(ProfileBlock* self, int bitDepth)
        {
            VkVideoComponentBitDepthFlagBitsKHR depth = bitDepth == 8 ? VkVideoComponentBitDepthFlagBitsKHR._8_BIT_KHR : VkVideoComponentBitDepthFlagBitsKHR._10_BIT_KHR;
            self->Usage = new VkVideoDecodeUsageInfoKHR { videoUsageHints = VkVideoDecodeUsageFlagBitsKHR.TRANSCODING_BIT_KHR };
            self->H265 = new VkVideoDecodeH265ProfileInfoKHR
            {
                pNext = &self->Usage,
                stdProfileIdc = bitDepth == 8 ? StdVideoH265ProfileIdc.MAIN : StdVideoH265ProfileIdc.MAIN_10,
            };
            self->Profile = new VkVideoProfileInfoKHR
            {
                pNext = &self->H265,
                videoCodecOperation = VkVideoCodecOperationFlagBitsKHR.DECODE_H265_BIT_KHR,
                chromaSubsampling = VkVideoChromaSubsamplingFlagBitsKHR._420_BIT_KHR,
                lumaBitDepth = depth,
                chromaBitDepth = depth,
            };
            self->List = new VkVideoProfileListInfoKHR { profileCount = 1, pProfiles = &self->Profile };
        }
    }

    /// <summary>The capabilities of a decode profile on the device.</summary>
    private struct Capabilities
    {
        public VkVideoCapabilitiesKHR Video;
        public VkVideoDecodeCapabilitiesKHR Decode;
        public VkVideoDecodeH265CapabilitiesKHR H265;
        public bool Supported;

        public static Capabilities Query(VulkanPhysicalDevice physical, ProfileBlock* profile)
        {
            VkVideoDecodeH265CapabilitiesKHR h265 = new();
            VkVideoDecodeCapabilitiesKHR decode = new() { pNext = &h265 };
            VkVideoCapabilitiesKHR video = new() { pNext = &decode };
            VkResult result = physical.Instance.Commands.vkGetPhysicalDeviceVideoCapabilitiesKHR(physical.Handle, &profile->Profile, &video);
            if (result < 0)
            {
                // Some drivers refuse usage hints; ask again without them.
                profile->H265.pNext = null;
                result = physical.Instance.Commands.vkGetPhysicalDeviceVideoCapabilitiesKHR(physical.Handle, &profile->Profile, &video);
            }

            video.pNext = null;
            decode.pNext = null;
            return new Capabilities { Video = video, Decode = decode, H265 = h265, Supported = result >= 0 };
        }
    }
}
