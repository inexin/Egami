using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Nal;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;
using Egami.Video.Hardware.Vulkan;
using Egami.Video.Hardware.Vulkan.Native;

namespace Egami.Video.Hardware.Decoding;

/// <summary>
/// Decodes H.264 on the GPU's video engine through Vulkan Video (<c>VK_KHR_video_decode_h264</c>). Egami reads the stream with
/// its software decoder's parser and does what Vulkan leaves to the application: picture boundaries, picture order count, frame
/// number gaps, reference marking, the decoded picture buffer's slots and the output order. The driver decodes each picture's
/// slices into a DPB slot, which is copied to host memory in the same submission. Up to <see cref="InFlight"/> pictures run on the
/// GPU while the next ones are read. Frame pictures in 8-bit 4:2:0 are decoded; field pictures, MBAFF and other formats are refused.
/// </summary>
internal sealed unsafe class H264HardwareDecoder : IVideoDecoder
{
    /// <summary>The pictures that may be on the GPU at once.</summary>
    internal const int InFlight = 4;

    private readonly VideoTrackInfo _track;
    private readonly EgamiContext _egami;
    private readonly DecodeErrorMode _errors;
    private readonly VulkanDevice _device;
    private readonly VulkanQueue _queue;
    private readonly ProfileBlock* _profile;
    private readonly VulkanCommandPool _pool;
    private readonly VulkanObjectHandle _queryPool;
    private readonly Job[] _jobs = new Job[InFlight];
    private readonly Queue<int> _submitted = new();
    private readonly Queue<Picture> _emitted = new();
    private readonly PriorityQueue<(MediaTime Time, MediaTime? Duration), MediaTime> _times = new();
    private readonly Dictionary<H264Pps, VulkanObjectHandle> _parameters = [];
    private readonly H264Sps?[] _sps = new H264Sps?[32];
    private readonly byte[]?[] _ppsRbsp = new byte[]?[256];
    private readonly H264Pps?[] _pps = new H264Pps?[256];
    private readonly NalBumpingBuffer<Picture> _dpb = new();
    private readonly SliceBuffer _slices = new();
    private readonly List<Picture> _untimed = [];

    // The session and the pictures of the current size.
    private VulkanVideoSession? _session;
    private VulkanImage[] _slots = [];
    private VulkanBuffer? _bitstream;
    private ulong _regionSize;
    private ulong _alignment = 1;
    private uint _codedWidth;
    private uint _codedHeight;
    private bool _needsReset;
    private bool _slotsReady;

    private H264Sps? _active;
    private H264Sps? _lastSps;
    private H264SliceHeader? _first;
    private Picture? _cur;
    private bool _curIntra;
    private H264Sei _sei = new();
    private int _curPocMsb;
    private int _curFrameNumOffset;
    private int _prevPocMsb;
    private int _prevPocLsb;
    private int _prevFrameNumOffset;
    private int _prevFrameNum;
    private int _prevRefFrameNum;
    private int _maxLongTermFrameIdx = -1;
    private bool _started;
    private int _recoveryFrameNum = -1;
    private int _nextJob;
    private readonly int _lengthSize;
    private int _packet = -1;
    private HdrMetadata? _hdr;
    private bool _prevInterlaced;
    private DiagnosticReporter? _diagnostics;
    private bool _disposed;

    public H264HardwareDecoder(VideoTrackInfo track, VideoDecoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        _track = track;
        _egami = context.Egami;
        _errors = options.Errors;
        _hdr = track.Hdr;
        _device = VulkanVideoContext.GetDevice(VkVideoCodecOperationFlagBitsKHR.DECODE_H264_BIT_KHR, "High");
        _queue = _device.DecodeQueue ?? throw new HardwareVideoException($"{_device.Physical.Name} has no video decode queue.");
        if ((_device.Physical.QueueFamilies[_queue.Family].Flags & VkQueueFlagBits.TRANSFER_BIT) == 0)
        {
            throw new HardwareVideoException($"{_device.Physical.Name}'s decode queue cannot copy pictures out (no transfer support).");
        }

        _profile = (ProfileBlock*)NativeMemory.AllocZeroed((nuint)sizeof(ProfileBlock));
        try
        {
            ProfileBlock.Fill(_profile);
            Caps = Capabilities.Query(_device.Physical, _profile);
            if ((Caps.Decode.flags & VkVideoDecodeCapabilityFlagBitsKHR.DPB_AND_OUTPUT_COINCIDE_BIT_KHR) == 0)
            {
                throw new HardwareVideoException($"{_device.Physical.Name} decodes H.264 only into separate output pictures, which Egami does not use yet.");
            }

            _pool = _device.CreateCommandPool(_queue);
            VkQueryPoolCreateInfo queryInfo = new() { pNext = &_profile->Profile, queryType = VkQueryType.RESULT_STATUS_ONLY_KHR, queryCount = InFlight };
            ulong queryPool;
            VkCheck.ThrowIfFailed(_device.Commands.vkCreateQueryPool(_device.Raw, &queryInfo, null, &queryPool), "vkCreateQueryPool");
            _queryPool = new VulkanObjectHandle(_device.Handle, queryPool, _device.Commands.vkDestroyQueryPool);
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

        if (!track.CodecPrivate.IsEmpty)
        {
            try
            {
                if (H264Config.Parse(track.CodecPrivate) is { } config)
                {
                    _lengthSize = config.LengthSize;
                    foreach (ReadOnlyMemory<byte> nal in config.ParameterSets)
                    {
                        DecodeNal(nal);
                    }
                }
            }
            catch (CorruptDataException e)
            {
                Report("H264.CorruptFrame", $"The track's avcC record cannot be read ({e.Message}).");
                if (_errors == DecodeErrorMode.Throw)
                {
                    DisposeResources();
                    throw;
                }
            }
        }
    }

    private Capabilities Caps { get; }

    /// <summary>Gets the DPB slots of the current session, for tests.</summary>
    internal int Slots => _slots.Length;

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _packet++;
        int started = PicturesStarted;
        try
        {
            foreach (ReadOnlyMemory<byte> nal in Split(packet.Data))
            {
                DecodeNal(nal);
            }

            FinishPicture();
        }
        catch (CorruptDataException e) when (_errors != DecodeErrorMode.Throw)
        {
            Report("H264.CorruptFrame", $"Packet {_packet} cannot be decoded ({e.Message}).");
        }
        finally
        {
            for (int i = started; i < PicturesStarted; i++)
            {
                _times.Enqueue((packet.Timestamp, packet.Duration), packet.Timestamp);
            }

            TakeTimes();
        }
    }

    /// <summary>Gives the frames output since the last call the smallest packet timestamps waiting, in output order.</summary>
    private void TakeTimes()
    {
        foreach (Picture picture in _untimed)
        {
            (picture.Time, picture.Duration) = _times.TryDequeue(out var t, out _) ? t : (MediaTime.Zero, null);
        }

        _untimed.Clear();
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FinishPicture();
        while (_dpb.TryFlush(out Picture picture, out _))
        {
            Emit(picture);
        }

        TakeTimes();
        while (_submitted.Count > 0)
        {
            Collect(_submitted.Dequeue());
        }

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
                frame = MakeFrame(picture, raster);
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

        _cur = null;
        _first = null;
        _slices.Clear();
        _dpb.Clear();
        _started = false;
        _recoveryFrameNum = -1;
        _maxLongTermFrameIdx = -1;
        _sei = new H264Sei();
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

        DisposeResources();
        _diagnostics?.Dispose();
    }

    /// <summary>Gets how many pictures have started so far, for the packet timestamps.</summary>
    private int PicturesStarted { get; set; }

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

    private void DecodeNal(ReadOnlyMemory<byte> nal)
    {
        ReadOnlySpan<byte> span = nal.Span;
        if (span.Length == 0 || (span[0] & 0x80) != 0)
        {
            return;
        }

        switch (span[0] & 31)
        {
            case 1 or 5:
                DecodeSlice(nal);
                break;
            case 6:
                FinishPictureBeforeNonVcl();
                _sei.Read(NalFraming.Unescape(span), _lastSps ?? _active);
                break;
            case 7:
                {
                    FinishPictureBeforeNonVcl();
                    byte[] rbsp = NalFraming.Unescape(span);
                    H264Sps sps = H264Sps.Read(rbsp, _egami.Limits);
                    if (_sps[sps.Id] is { } old && old.Rbsp.AsSpan().SequenceEqual(rbsp))
                    {
                        _lastSps = old;
                        break;
                    }

                    _sps[sps.Id] = sps;
                    _lastSps = sps;
                    break;
                }

            case 8:
                {
                    FinishPictureBeforeNonVcl();
                    byte[] rbsp = NalFraming.Unescape(span);
                    (int ppsId, _) = H264Pps.ReadIds(rbsp);
                    if (_ppsRbsp[ppsId] is { } old && old.AsSpan().SequenceEqual(rbsp))
                    {
                        break;
                    }

                    _ppsRbsp[ppsId] = rbsp;
                    _pps[ppsId] = null;
                    break;
                }

            case 9 or 10 or 11:
                FinishPictureBeforeNonVcl();
                break;
            case 2 or 3 or 4:
                throw H264Errors.Unsupported("The stream uses data partitioning (Extended profile).");
        }
    }

    private void FinishPictureBeforeNonVcl()
    {
        if (_cur is not null)
        {
            FinishPicture();
        }
    }

    private H264Pps? FindPps(int id)
    {
        if (id > 255 || _ppsRbsp[id] is not { } rbsp)
        {
            return null;
        }

        (_, int spsId) = H264Pps.ReadIds(rbsp);
        if (_sps[spsId] is not { } sps)
        {
            return null;
        }

        if (_pps[id] is not { } pps || !ReferenceEquals(pps.Sps, sps))
        {
            pps = _pps[id] = H264Pps.Read(rbsp, sps);
        }

        return pps;
    }

    private void DecodeSlice(ReadOnlyMemory<byte> nal)
    {
        // The header is read from the start of the slice: the slice data is the driver's.
        ReadOnlySpan<byte> span = nal.Span;
        byte[] rbsp = NalFraming.Unescape(span[..Math.Min(span.Length, 4096)]);
        H264SliceHeader h;
        try
        {
            h = H264SliceHeader.Read(rbsp, FindPps);
        }
        catch (CorruptDataException e)
        {
            if (_started)
            {
                Fail("H264.CorruptFrame", $"A slice header cannot be read ({e.Message}).", e);
            }

            return;
        }

        if (h.RedundantPicCnt > 0)
        {
            return;
        }

        if (_cur is not null && IsNewPicture(_first!, h))
        {
            FinishPicture();
        }

        if (_cur is null && !StartPicture(h))
        {
            return;
        }

        _curIntra &= h.IsI;
        _slices.Add(nal);
    }

    /// <summary>Whether a slice starts a new picture (7.4.1.2.4).</summary>
    private static bool IsNewPicture(H264SliceHeader a, H264SliceHeader b) =>
        b.FirstMb == 0 || a.FrameNum != b.FrameNum || a.Pps.Id != b.Pps.Id || (a.NalRefIdc == 0) != (b.NalRefIdc == 0)
        || a.FieldPic != b.FieldPic || a.BottomField != b.BottomField
        || a.PocLsb != b.PocLsb || a.DeltaPocBottom != b.DeltaPocBottom || a.DeltaPoc0 != b.DeltaPoc0 || a.DeltaPoc1 != b.DeltaPoc1
        || a.Idr != b.Idr || (a.Idr && a.IdrPicId != b.IdrPicId);

    private bool StartPicture(H264SliceHeader h)
    {
        H264Sps sps = h.Sps;
        if (!sps.FrameMbsOnly)
        {
            throw H264Errors.Unsupported("The stream is interlaced (field pictures or MBAFF frames).");
        }

        if (sps.ChromaFormatIdc != 1)
        {
            throw H264Errors.Unsupported(sps.ChromaFormatIdc == 0 ? "The stream is monochrome." : "The stream is 4:2:2.");
        }

        if (sps.BitDepthLuma != 8 || sps.BitDepthChroma != 8)
        {
            throw H264Errors.Unsupported($"The stream has {Math.Max(sps.BitDepthLuma, sps.BitDepthChroma)} bits per sample.");
        }

        if (!_started)
        {
            if (!h.Idr && _sei.RecoveryFrameCount < 0)
            {
                // Decoding starts at an IDR picture or a recovery point.
                _sei = new H264Sei();
                return false;
            }

            _started = true;
            if (!h.Idr)
            {
                _recoveryFrameNum = (h.FrameNum + _sei.RecoveryFrameCount) % sps.MaxFrameNum;
                _prevRefFrameNum = h.FrameNum == 0 ? 0 : h.FrameNum - 1;
                _prevFrameNum = _prevRefFrameNum;
            }
        }

        if (_active is null || sps.Width != _active.Width || sps.Height != _active.Height || Slots < SlotsFor(sps))
        {
            // A new size: the frames before are output and the pictures start over.
            while (_dpb.TryFlush(out Picture picture, out _))
            {
                Emit(picture);
            }

            _dpb.Clear();
            CreateSession(sps);
        }

        _active = sps;
        _dpb.Capacity = sps.DpbFrames;
        _dpb.MaxNumReorder = sps.MaxNumReorderFrames;
        if (!h.Idr && h.FrameNum != _prevRefFrameNum && h.FrameNum != (_prevRefFrameNum + 1) % sps.MaxFrameNum)
        {
            FillFrameNumGap(h);
        }

        Picture pic = new() { FrameNum = h.FrameNum, Sps = sps };
        ComputePoc(h, pic);
        _cur = pic;
        _curIntra = true;
        _first = h;
        _slices.Clear();
        PicturesStarted++;
        return true;
    }

    /// <summary>The picture order count (8.2.1) of a frame.</summary>
    private void ComputePoc(H264SliceHeader h, Picture pic)
    {
        H264Sps sps = h.Sps;
        if (h.Idr)
        {
            _prevPocMsb = _prevPocLsb = 0;
            _prevFrameNumOffset = 0;
        }

        int frameNumOffset = h.Idr ? 0 : _prevFrameNum > h.FrameNum ? _prevFrameNumOffset + sps.MaxFrameNum : _prevFrameNumOffset;
        _curFrameNumOffset = frameNumOffset;
        switch (sps.PocType)
        {
            case 0:
                {
                    int maxLsb = 1 << sps.Log2MaxPocLsb;
                    int lsb = h.PocLsb;
                    int msb = lsb < _prevPocLsb && _prevPocLsb - lsb >= maxLsb / 2 ? _prevPocMsb + maxLsb
                        : lsb > _prevPocLsb && lsb - _prevPocLsb > maxLsb / 2 ? _prevPocMsb - maxLsb
                        : _prevPocMsb;
                    _curPocMsb = msb;
                    pic.TopPoc = msb + lsb;
                    pic.BottomPoc = pic.TopPoc + h.DeltaPocBottom;
                    break;
                }

            case 1:
                {
                    int cycle = sps.OffsetForRefFrame.Length;
                    int absFrameNum = cycle != 0 ? frameNumOffset + h.FrameNum : 0;
                    if (h.NalRefIdc == 0 && absFrameNum > 0)
                    {
                        absFrameNum--;
                    }

                    long expected = 0;
                    if (absFrameNum > 0)
                    {
                        long deltaPerCycle = 0;
                        foreach (int offset in sps.OffsetForRefFrame)
                        {
                            deltaPerCycle += offset;
                        }

                        int cycleCount = (absFrameNum - 1) / cycle, inCycle = (absFrameNum - 1) % cycle;
                        expected = cycleCount * deltaPerCycle;
                        for (int i = 0; i <= inCycle; i++)
                        {
                            expected += sps.OffsetForRefFrame[i];
                        }
                    }

                    if (h.NalRefIdc == 0)
                    {
                        expected += sps.OffsetForNonRefPic;
                    }

                    pic.TopPoc = (int)(expected + h.DeltaPoc0);
                    pic.BottomPoc = pic.TopPoc + sps.OffsetForTopToBottomField + h.DeltaPoc1;
                    break;
                }

            default:
                {
                    int temp = h.Idr ? 0 : h.NalRefIdc == 0 ? (2 * (frameNumOffset + h.FrameNum)) - 1 : 2 * (frameNumOffset + h.FrameNum);
                    pic.TopPoc = pic.BottomPoc = temp;
                    break;
                }
        }
    }

    /// <summary>
    /// The decoding process for gaps in frame_num (8.2.5.2): "non-existing" short-term frames fill the gap. They hold DPB slots,
    /// marked as non-existing for the driver, and are never output.
    /// </summary>
    private void FillFrameNumGap(H264SliceHeader h)
    {
        H264Sps sps = h.Sps;
        if (!sps.GapsInFrameNumAllowed)
        {
            Report("H264.MissingReference", $"frame_num jumps from {_prevRefFrameNum} to {h.FrameNum}; the frames between are missing.");
        }

        int frameNum = (_prevRefFrameNum + 1) % sps.MaxFrameNum;
        int guard = 0;
        while (frameNum != h.FrameNum && guard++ < sps.MaxFrameNum)
        {
            int offset = _prevFrameNum > frameNum ? _prevFrameNumOffset + sps.MaxFrameNum : _prevFrameNumOffset;
            int poc = 2 * (offset + frameNum);
            SlidingWindow(sps, frameNum);
            _dpb.RemoveUnused();
            Picture gap = new() { FrameNum = frameNum, TopPoc = poc, BottomPoc = poc, NonExisting = true, IsShortTermReference = true };
            gap.Slot = FreeSlot();
            _dpb.Insert(gap, poc, false);
            _prevFrameNumOffset = offset;
            _prevFrameNum = frameNum;
            _prevRefFrameNum = frameNum;
            frameNum = (frameNum + 1) % sps.MaxFrameNum;
        }
    }

    /// <summary>Decodes the current picture on the GPU, marks references and stores it for output.</summary>
    private void FinishPicture()
    {
        if (_cur is not { } pic || _first is not { } h)
        {
            _sei = new H264Sei();
            return;
        }

        pic.Sei = _sei;
        _sei = new H264Sei();
        pic.Slot = FreeSlot();
        bool output = _recoveryFrameNum < 0 || h.Idr || pic.FrameNum == _recoveryFrameNum;
        if (output)
        {
            _recoveryFrameNum = -1;
        }

        pic.Output = output;
        Submit(pic, h);

        bool mmco5 = false;
        if (h.NalRefIdc != 0)
        {
            mmco5 = MarkReferences(h, pic);
        }

        if (h.Idr || mmco5)
        {
            if (!h.NoOutputOfPriorPics)
            {
                while (_dpb.TryFlush(out Picture prior, out _))
                {
                    Emit(prior);
                }
            }

            _dpb.Clear();
        }

        if (mmco5)
        {
            // tempPicOrderCnt (8.2.1): the picture's order count becomes 0, and its frame_num 0 (7.4.3).
            int temp = Math.Min(pic.TopPoc, pic.BottomPoc);
            pic.TopPoc -= temp;
            pic.BottomPoc -= temp;
            pic.FrameNum = 0;
        }

        _prevFrameNum = mmco5 ? 0 : h.FrameNum;
        _prevFrameNumOffset = mmco5 ? 0 : _curFrameNumOffset;
        if (h.NalRefIdc != 0)
        {
            _prevRefFrameNum = mmco5 ? 0 : h.FrameNum;
            _prevPocMsb = mmco5 ? 0 : _curPocMsb;
            _prevPocLsb = mmco5 ? pic.TopPoc : h.PocLsb;
        }

        _cur = null;
        _first = null;
        _slices.Clear();
        Store(pic, pic.Output);
    }

    /// <summary>Stores a decoded frame in the buffer and outputs what the bumping process lets go (C.4.4, C.4.5).</summary>
    private void Store(Picture pic, bool output)
    {
        _dpb.RemoveUnused();
        if (!pic.IsReference && !output)
        {
            return;
        }

        _dpb.Insert(pic, pic.Poc, output);
        while (_dpb.Count > _dpb.Capacity && _dpb.Waiting > 0 && _dpb.TryFlush(out Picture bumped, out _))
        {
            Emit(bumped);
            _dpb.RemoveUnused();
        }

        while (_dpb.TryBumpAfterInsert(out Picture bumped, out _))
        {
            Emit(bumped);
        }

        _dpb.RemoveUnused();
    }

    private void Emit(Picture picture)
    {
        if (picture.NonExisting)
        {
            return;
        }

        _untimed.Add(picture);
        _emitted.Enqueue(picture);
    }

    /// <summary>The decoded reference picture marking process (8.2.5) for a frame. Returns whether an MMCO 5 came.</summary>
    private bool MarkReferences(H264SliceHeader h, Picture pic)
    {
        H264Sps sps = h.Sps;
        if (h.Idr)
        {
            foreach (Picture p in _dpb.Pictures())
            {
                p.IsShortTermReference = p.IsLongTermReference = false;
            }

            if (h.LongTermReference)
            {
                pic.IsLongTermReference = true;
                pic.LongTermFrameIdx = 0;
                _maxLongTermFrameIdx = 0;
            }
            else
            {
                pic.IsShortTermReference = true;
                _maxLongTermFrameIdx = -1;
            }

            return false;
        }

        bool mmco5 = false, currentLong = false;
        if (h.AdaptiveRefPicMarking)
        {
            foreach (H264Mmco op in h.Mmcos)
            {
                switch (op.Op)
                {
                    case 1:
                        if (FindShortTerm(h, h.CurrPicNum - op.DifferenceOfPicNums) is { } unmarked)
                        {
                            unmarked.IsShortTermReference = false;
                        }

                        break;
                    case 2:
                        if (FindLongTerm(op.LongTermPicNum) is { } lt)
                        {
                            lt.IsLongTermReference = false;
                        }

                        break;
                    case 3:
                        if (FindShortTerm(h, h.CurrPicNum - op.DifferenceOfPicNums) is { } made)
                        {
                            UnmarkLongTermIdx(op.LongTermFrameIdx, made);
                            made.IsShortTermReference = false;
                            made.IsLongTermReference = true;
                            made.LongTermFrameIdx = op.LongTermFrameIdx;
                        }

                        break;
                    case 4:
                        _maxLongTermFrameIdx = op.MaxLongTermFrameIdxPlus1 - 1;
                        foreach (Picture p in _dpb.Pictures())
                        {
                            if (p.IsLongTermReference && p.LongTermFrameIdx > _maxLongTermFrameIdx)
                            {
                                p.IsLongTermReference = false;
                            }
                        }

                        break;
                    case 5:
                        foreach (Picture p in _dpb.Pictures())
                        {
                            p.IsShortTermReference = p.IsLongTermReference = false;
                        }

                        _maxLongTermFrameIdx = -1;
                        mmco5 = true;
                        break;
                    case 6:
                        UnmarkLongTermIdx(op.LongTermFrameIdx, pic);
                        pic.IsShortTermReference = false;
                        pic.IsLongTermReference = true;
                        pic.LongTermFrameIdx = op.LongTermFrameIdx;
                        currentLong = true;
                        break;
                }
            }
        }
        else
        {
            SlidingWindow(sps, h.FrameNum);
        }

        if (!currentLong)
        {
            pic.IsShortTermReference = true;
        }

        // A stream that keeps more references than it may (8.2.5.4 overflow) loses its oldest short-term frames.
        while (CountReferences() > Math.Max(sps.MaxNumRefFrames, 1) && DropOldestShortTerm(h.FrameNum, sps))
        {
        }

        return mmco5;
    }

    private void UnmarkLongTermIdx(int idx, Picture keep)
    {
        foreach (Picture p in _dpb.Pictures())
        {
            if (p.IsLongTermReference && p.LongTermFrameIdx == idx && p != keep)
            {
                p.IsLongTermReference = false;
            }
        }
    }

    /// <summary>Counts the reference frames, the current one included.</summary>
    private int CountReferences()
    {
        int n = 1;
        foreach (Picture p in _dpb.Pictures())
        {
            n += p.IsReference ? 1 : 0;
        }

        return n;
    }

    /// <summary>The sliding window (8.2.5.3): when the references fill max_num_ref_frames, the oldest short-term frame goes.</summary>
    private void SlidingWindow(H264Sps sps, int frameNum)
    {
        int shortCount = 0, longCount = 0;
        foreach (Picture p in _dpb.Pictures())
        {
            shortCount += p.IsShortTermReference ? 1 : 0;
            longCount += p.IsLongTermReference ? 1 : 0;
        }

        if (shortCount > 0 && shortCount + longCount >= Math.Max(sps.MaxNumRefFrames, 1))
        {
            DropOldestShortTerm(frameNum, sps);
        }
    }

    private bool DropOldestShortTerm(int frameNum, H264Sps sps)
    {
        Picture? oldest = null;
        int oldestWrap = int.MaxValue;
        foreach (Picture p in _dpb.Pictures())
        {
            if (p.IsShortTermReference)
            {
                int wrap = p.FrameNum > frameNum ? p.FrameNum - sps.MaxFrameNum : p.FrameNum;
                if (wrap < oldestWrap)
                {
                    oldestWrap = wrap;
                    oldest = p;
                }
            }
        }

        if (oldest is null)
        {
            return false;
        }

        oldest.IsShortTermReference = false;
        return true;
    }

    private Picture? FindShortTerm(H264SliceHeader h, int picNum)
    {
        int maxFrameNum = h.Sps.MaxFrameNum;
        foreach (Picture p in _dpb.Pictures())
        {
            int wrap = p.FrameNum > h.FrameNum ? p.FrameNum - maxFrameNum : p.FrameNum;
            if (p.IsShortTermReference && wrap == picNum)
            {
                return p;
            }
        }

        return null;
    }

    private Picture? FindLongTerm(int longTermPicNum)
    {
        foreach (Picture p in _dpb.Pictures())
        {
            if (p.IsLongTermReference && p.LongTermFrameIdx == longTermPicNum)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>The slots the stream needs: one per reference frame, and one for the picture being decoded.</summary>
    private static int SlotsFor(H264Sps sps) => Math.Max(sps.MaxNumRefFrames, 1) + 1;

    /// <summary>A DPB slot no reference frame holds.</summary>
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
        return free >= 0 ? free : throw H264Errors.Corrupt("The stream keeps more reference frames than its max_num_ref_frames.");
    }

    /// <summary>Makes the video session and its pictures for a sequence's size, after the GPU has finished with the old ones.</summary>
    private void CreateSession(H264Sps sps)
    {
        WaitAll();
        DisposeSession();
        VkVideoCapabilitiesKHR caps = Caps.Video;
        uint width = (uint)sps.Width, height = (uint)sps.Height;
        if (width < caps.minCodedExtent.width || height < caps.minCodedExtent.height || width > caps.maxCodedExtent.width || height > caps.maxCodedExtent.height)
        {
            throw H264Errors.Beyond(
                $"{_device.Physical.Name} decodes H.264 from {caps.minCodedExtent.width}x{caps.minCodedExtent.height} to {caps.maxCodedExtent.width}x{caps.maxCodedExtent.height}; {width}x{height} is outside that.");
        }

        if (LevelIndex(sps.LevelIdc) > (int)Caps.H264.maxLevelIdc)
        {
            throw H264Errors.Beyond($"{_device.Physical.Name} decodes H.264 up to level index {(int)Caps.H264.maxLevelIdc}; this stream has level_idc {sps.LevelIdc}.");
        }

        int slots = SlotsFor(sps);
        if (slots > caps.maxDpbSlots || slots - 1 > caps.maxActiveReferencePictures)
        {
            throw H264Errors.Beyond($"{_device.Physical.Name} keeps {caps.maxDpbSlots} pictures; the stream needs {slots}.");
        }

        _codedWidth = AlignUp(width, Math.Max(1u, caps.pictureAccessGranularity.width));
        _codedHeight = AlignUp(height, Math.Max(1u, caps.pictureAccessGranularity.height));
        VkExtensionProperties header = caps.stdHeaderVersion;
        VkVideoSessionCreateInfoKHR info = new()
        {
            queueFamilyIndex = (uint)_queue.Family,
            pVideoProfile = &_profile->Profile,
            pictureFormat = VkFormat.G8_B8R8_2PLANE_420_UNORM,
            maxCodedExtent = new VkExtent2D { width = _codedWidth, height = _codedHeight },
            referencePictureFormat = VkFormat.G8_B8R8_2PLANE_420_UNORM,
            maxDpbSlots = (uint)slots,
            maxActiveReferencePictures = (uint)(slots - 1),
            pStdHeaderVersion = &header,
        };
        _session = _device.CreateVideoSession(info);
        _slots = new VulkanImage[slots];
        for (int i = 0; i < slots; i++)
        {
            _slots[i] = CreatePicture();
        }

        ulong frameBytes = (ulong)_codedWidth * _codedHeight * 3 / 2;
        foreach (Job job in _jobs)
        {
            job.Readback = _device.CreateBuffer(frameBytes, VkBufferUsageFlagBits.TRANSFER_DST_BIT, VkMemoryPropertyFlagBits.HOST_VISIBLE_BIT);
        }

        _alignment = Math.Max(Math.Max(caps.minBitstreamBufferOffsetAlignment, caps.minBitstreamBufferSizeAlignment), 1);
        CreateBitstream(AlignUp(frameBytes / 2, _alignment));
        _needsReset = true;
        _slotsReady = false;
    }

    private void CreateBitstream(ulong regionSize)
    {
        _bitstream?.Dispose();
        _bitstream = null;
        _regionSize = regionSize;
        _bitstream = _device.CreateBuffer(_regionSize * InFlight, VkBufferUsageFlagBits.VIDEO_DECODE_SRC_BIT_KHR, VkMemoryPropertyFlagBits.HOST_VISIBLE_BIT, &_profile->List);
    }

    private VulkanImage CreatePicture()
    {
        VkImageCreateInfo info = new()
        {
            pNext = &_profile->List,
            imageType = VkImageType._2D,
            format = VkFormat.G8_B8R8_2PLANE_420_UNORM,
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

    /// <summary>The session parameters holding a picture's SPS and PPS, made the first time the PPS is used.</summary>
    private ulong Parameters(H264Pps pps)
    {
        if (_parameters.TryGetValue(pps, out VulkanObjectHandle? made))
        {
            return made.Value;
        }

        if (_parameters.Count >= 16)
        {
            // Parameter sets that were replaced: drop them once the GPU is done with them.
            WaitAll();
            DisposeParameters();
        }

        H264Sps sps = pps.Sps;
        StdVideoH264SequenceParameterSet stdSps = new()
        {
            profile_idc = (StdVideoH264ProfileIdc)sps.ProfileIdc,
            level_idc = (StdVideoH264LevelIdc)LevelIndex(sps.LevelIdc),
            chroma_format_idc = (StdVideoH264ChromaFormatIdc)sps.ChromaFormatIdc,
            seq_parameter_set_id = (byte)sps.Id,
            bit_depth_luma_minus8 = (byte)(sps.BitDepthLuma - 8),
            bit_depth_chroma_minus8 = (byte)(sps.BitDepthChroma - 8),
            log2_max_frame_num_minus4 = (byte)(sps.Log2MaxFrameNum - 4),
            pic_order_cnt_type = (StdVideoH264PocType)sps.PocType,
            offset_for_non_ref_pic = sps.OffsetForNonRefPic,
            offset_for_top_to_bottom_field = sps.OffsetForTopToBottomField,
            log2_max_pic_order_cnt_lsb_minus4 = (byte)(sps.PocType == 0 ? sps.Log2MaxPocLsb - 4 : 0),
            num_ref_frames_in_pic_order_cnt_cycle = (byte)sps.OffsetForRefFrame.Length,
            max_num_ref_frames = (byte)sps.MaxNumRefFrames,
            pic_width_in_mbs_minus1 = (uint)sps.WidthInMbs - 1,
            pic_height_in_map_units_minus1 = (uint)sps.HeightInMbs - 1,
            frame_crop_left_offset = (uint)sps.CropLeft,
            frame_crop_right_offset = (uint)sps.CropRight,
            frame_crop_top_offset = (uint)sps.CropTop,
            frame_crop_bottom_offset = (uint)sps.CropBottom,
        };
        int c = sps.ConstraintFlags;
        stdSps.flags.constraint_set0_flag = (c & 0x80) != 0;
        stdSps.flags.constraint_set1_flag = (c & 0x40) != 0;
        stdSps.flags.constraint_set2_flag = (c & 0x20) != 0;
        stdSps.flags.constraint_set3_flag = (c & 0x10) != 0;
        stdSps.flags.constraint_set4_flag = (c & 0x08) != 0;
        stdSps.flags.constraint_set5_flag = (c & 0x04) != 0;
        stdSps.flags.direct_8x8_inference_flag = sps.Direct8x8Inference;
        stdSps.flags.frame_mbs_only_flag = sps.FrameMbsOnly;
        stdSps.flags.delta_pic_order_always_zero_flag = sps.DeltaPicOrderAlwaysZero;
        stdSps.flags.gaps_in_frame_num_value_allowed_flag = sps.GapsInFrameNumAllowed;
        stdSps.flags.frame_cropping_flag = (sps.CropLeft | sps.CropRight | sps.CropTop | sps.CropBottom) != 0;

        // The scaling lists the PPS resolves to (its own, the SPS's or the defaults) go with the PPS, all present.
        StdVideoH264ScalingLists lists = default;
        StdVideoH264PictureParameterSet stdPps = new()
        {
            seq_parameter_set_id = (byte)sps.Id,
            pic_parameter_set_id = (byte)pps.Id,
            num_ref_idx_l0_default_active_minus1 = (byte)(pps.NumRefIdxL0Default - 1),
            num_ref_idx_l1_default_active_minus1 = (byte)(pps.NumRefIdxL1Default - 1),
            weighted_bipred_idc = (StdVideoH264WeightedBipredIdc)pps.WeightedBipredIdc,
            pic_init_qp_minus26 = (sbyte)(pps.PicInitQp - 26),
            chroma_qp_index_offset = (sbyte)pps.ChromaQpIndexOffset,
            second_chroma_qp_index_offset = (sbyte)pps.SecondChromaQpIndexOffset,
        };
        stdPps.flags.transform_8x8_mode_flag = pps.Transform8x8Mode;
        stdPps.flags.redundant_pic_cnt_present_flag = pps.RedundantPicCntPresent;
        stdPps.flags.constrained_intra_pred_flag = pps.ConstrainedIntraPred;
        stdPps.flags.deblocking_filter_control_present_flag = pps.DeblockingFilterControlPresent;
        stdPps.flags.weighted_pred_flag = pps.WeightedPred;
        stdPps.flags.bottom_field_pic_order_in_frame_present_flag = pps.BottomFieldPicOrderInFramePresent;
        stdPps.flags.entropy_coding_mode_flag = pps.Cabac;
        if (!ReferenceEquals(pps.ScalingLists, H264ScalingLists.Flat))
        {
            FillScalingLists(pps.ScalingLists, &lists, pps.Transform8x8Mode ? 2 : 0);
            stdPps.flags.pic_scaling_matrix_present_flag = true;
            stdPps.pScalingLists = &lists;
        }

        int[] offsets = sps.OffsetForRefFrame.Length > 0 ? sps.OffsetForRefFrame : [0];
        ulong raw;
        fixed (int* pOffsets = offsets)
        {
            stdSps.pOffsetForRefFrame = sps.OffsetForRefFrame.Length > 0 ? pOffsets : null;
            VkVideoDecodeH264SessionParametersAddInfoKHR add = new() { stdSPSCount = 1, pStdSPSs = &stdSps, stdPPSCount = 1, pStdPPSs = &stdPps };
            VkVideoDecodeH264SessionParametersCreateInfoKHR h264Info = new() { maxStdSPSCount = 1, maxStdPPSCount = 1, pParametersAddInfo = &add };
            VkVideoSessionParametersCreateInfoKHR info = new() { pNext = &h264Info, videoSession = _session!.Handle };
            VkCheck.ThrowIfFailed(_device.Commands.vkCreateVideoSessionParametersKHR(_device.Raw, &info, null, &raw), "vkCreateVideoSessionParametersKHR");
        }

        _parameters.Add(pps, new VulkanObjectHandle(_device.Handle, raw, _device.Commands.vkDestroyVideoSessionParametersKHR));
        return raw;
    }

    /// <summary>
    /// Fills the Std scaling lists from Egami's, which are in raster order: the Std lists are in the order of the syntax, zigzag
    /// (the high-cqm clip decodes bit-exact only so).
    /// </summary>
    private static void FillScalingLists(H264ScalingLists source, StdVideoH264ScalingLists* target, int lists8x8)
    {
        int mask = 0;
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                target->ScalingList4x4[(i * 16) + j] = source.List4x4[i][H264ScalingLists.Zigzag4x4[j]];
            }

            mask |= 1 << i;
        }

        for (int i = 0; i < lists8x8; i++)
        {
            for (int j = 0; j < 64; j++)
            {
                target->ScalingList8x8[(i * 64) + j] = source.List8x8[i][H264ScalingLists.Zigzag8x8[j]];
            }

            mask |= 1 << (6 + i);
        }

        target->scaling_list_present_mask = (ushort)mask;
        target->use_default_scaling_matrix_mask = 0;
    }

    /// <summary>
    /// The Std level enumerator of a level_idc (Table A-1); level 1b (9) is taken as 1.1, and a value that names no level as the
    /// next level up.
    /// </summary>
    private static int LevelIndex(int levelIdc)
    {
        if (levelIdc == 9)
        {
            return 1;
        }

        ReadOnlySpan<byte> levels = [10, 11, 12, 13, 20, 21, 22, 30, 31, 32, 40, 41, 42, 50, 51, 52, 60, 61, 62];
        for (int i = 0; i < levels.Length; i++)
        {
            if (levels[i] >= levelIdc)
            {
                return i;
            }
        }

        return levels.Length - 1;
    }

    /// <summary>Records and submits the decoding of the current picture into its slot, and the copy of the slot to host memory.</summary>
    private void Submit(Picture pic, H264SliceHeader h)
    {
        int jobIndex = _nextJob;
        _nextJob = (_nextJob + 1) % InFlight;
        Job job = _jobs[jobIndex];
        if (job.Picture is not null)
        {
            // The oldest job: everything submitted before it is done first.
            while (job.Picture is not null)
            {
                Collect(_submitted.Dequeue());
            }
        }

        ulong parameters = Parameters(h.Pps);

        // The slices with start codes, at offsets from the picture's start.
        int size = 0;
        foreach (ReadOnlyMemory<byte> nal in _slices)
        {
            size += 3 + nal.Length;
        }

        if ((ulong)size + _alignment > _regionSize)
        {
            WaitAll();
            CreateBitstream(AlignUp((ulong)size * 2, _alignment));
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

        // The reference frames in the buffer, each in its slot.
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
        vk.vkCmdResetQueryPool(cb, _queryPool.Value, (uint)jobIndex, 1);
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
        StdVideoDecodeH264ReferenceInfo* refInfos = stackalloc StdVideoDecodeH264ReferenceInfo[n + 1];
        VkVideoDecodeH264DpbSlotInfoKHR* slotInfos = stackalloc VkVideoDecodeH264DpbSlotInfoKHR[n + 1];
        VkVideoReferenceSlotInfoKHR* slots = stackalloc VkVideoReferenceSlotInfoKHR[n + 1];
        VkVideoReferenceSlotInfoKHR* beginSlots = stackalloc VkVideoReferenceSlotInfoKHR[n + 1];
        for (int i = 0; i <= n; i++)
        {
            bool setup = i == n;
            Picture p = setup ? pic : refs[i];
            resources[i] = new VkVideoPictureResourceInfoKHR { codedExtent = extent, imageViewBinding = _slots[p.Slot].View };
            refInfos[i] = new StdVideoDecodeH264ReferenceInfo
            {
                FrameNum = (ushort)(p.IsLongTermReference ? p.LongTermFrameIdx : p.FrameNum),
            };
            refInfos[i].PicOrderCnt[0] = p.TopPoc;
            refInfos[i].PicOrderCnt[1] = p.BottomPoc;
            refInfos[i].flags.used_for_long_term_reference = setup ? h.Idr && h.LongTermReference : p.IsLongTermReference;
            refInfos[i].flags.is_non_existing = p.NonExisting;
            slotInfos[i] = new VkVideoDecodeH264DpbSlotInfoKHR { pStdReferenceInfo = &refInfos[i] };
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

        StdVideoDecodeH264PictureInfo std = new()
        {
            seq_parameter_set_id = (byte)h.Sps.Id,
            pic_parameter_set_id = (byte)h.Pps.Id,
            frame_num = (ushort)h.FrameNum,
            idr_pic_id = (ushort)h.IdrPicId,
        };
        std.PicOrderCnt[0] = pic.TopPoc;
        std.PicOrderCnt[1] = pic.BottomPoc;
        std.flags.is_intra = _curIntra;
        std.flags.IdrPicFlag = h.Idr;
        std.flags.is_reference = h.NalRefIdc != 0;
        VkVideoDecodeH264PictureInfoKHR h264Picture = new() { pStdPictureInfo = &std, sliceCount = (uint)_slices.Count, pSliceOffsets = offsets };
        VkVideoDecodeInfoKHR decode = new()
        {
            pNext = &h264Picture,
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
        VkBufferImageCopy* regions = stackalloc VkBufferImageCopy[2];
        regions[0] = new VkBufferImageCopy
        {
            imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlagBits.PLANE_0_BIT, layerCount = 1 },
            imageExtent = new VkExtent3D { width = _codedWidth, height = _codedHeight, depth = 1 },
        };
        regions[1] = new VkBufferImageCopy
        {
            bufferOffset = (ulong)_codedWidth * _codedHeight,
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
        VkCheck.ThrowIfFailed(
            _device.Commands.vkGetQueryPoolResults(_device.Raw, _queryPool.Value, (uint)jobIndex, 1, 8, &status, 8, VkQueryResultFlagBits.WAIT_BIT | VkQueryResultFlagBits.WITH_STATUS_BIT_KHR | VkQueryResultFlagBits._64_BIT),
            "vkGetQueryPoolResults");
        if (status <= 0)
        {
            string message = $"The GPU failed to decode frame_num {pic.FrameNum} (status {status}).";
            Report("H264.CorruptFrame", message);
            if (_errors == DecodeErrorMode.Throw)
            {
                throw new CorruptDataException(H264Errors.FormatName, -1, H264Errors.Prefix + message);
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

    /// <summary>Copies the cropped picture out of a readback buffer's NV12 into planes.</summary>
    private YuvRaster<byte> ReadPicture(VulkanBuffer readback, H264Sps sps)
    {
        ColorCodePoints color = ColorOf(sps, _track.Color);
        ChromaSiting siting = _track.ChromaSiting ?? (sps.Vui is { } vui ? SitingOf(vui.ChromaSampleLocation) : null) ?? ChromaSiting.Left;
        YuvColor yuvColor = color.TryGetYuvColor(siting, out YuvColor c) ? c
            : new YuvColor(sps.CroppedHeight <= 576 ? YuvMatrix.Bt601 : YuvMatrix.Bt709, color.FullRange ? YuvRange.Full : YuvRange.Limited, siting);
        int width = sps.CroppedWidth, height = sps.CroppedHeight;
        YuvRaster<byte> raster = YuvRaster.Create(width, height, ChromaFormat.Yuv420, yuvColor, false, _egami);
        raster.Metadata.CodePoints = color;
        ReadOnlySpan<byte> nv12 = readback.Span;
        int stride = (int)_codedWidth;
        int left = sps.CropLeft * 2, top = sps.CropTop * 2;
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

    private YuvVideoFrame MakeFrame(Picture picture, YuvRaster<byte> raster)
    {
        if (picture.Sei?.Hdr is { } hdr)
        {
            _hdr = hdr;
        }

        raster.Metadata.Hdr = _hdr;
        ColorCodePoints color = raster.Metadata.CodePoints ?? new ColorCodePoints(2, 2, 2, false);
        int ps = picture.Sei?.PicStruct ?? -1;
        bool interlaced = ps switch
        {
            1 or 2 => true,
            >= 3 and <= 6 => _prevInterlaced,
            _ => false,
        };
        _prevInterlaced = interlaced;
        int repeat = ps switch { 5 or 6 => 1, 7 => 2, 8 => 4, _ => 0 };
        FieldOrder order = !interlaced ? FieldOrder.Progressive
            : (picture.TopPoc != picture.BottomPoc ? picture.TopPoc < picture.BottomPoc : ps is 3 or 5) ? FieldOrder.TopFieldFirst : FieldOrder.BottomFieldFirst;
        return new YuvVideoFrame(raster, picture.Time, picture.Duration, color) { Hdr = _hdr, Hdr10Plus = picture.Sei?.Hdr10Plus, FieldOrder = order, RepeatFields = repeat };
    }

    /// <summary>Gets the chroma siting of an H.264 chroma_sample_loc_type (E.2.1), or null when absent.</summary>
    private static ChromaSiting? SitingOf(int chromaSampleLocation) => chromaSampleLocation switch
    {
        0 => ChromaSiting.Left,
        1 => ChromaSiting.Center,
        2 => ChromaSiting.TopLeft,
        _ => null,
    };

    /// <summary>The frame's color, as Egami's software decoder gives it: the VUI's, the container's, or by picture size.</summary>
    private static ColorCodePoints ColorOf(H264Sps sps, ColorCodePoints? container)
    {
        H264Vui? vui = sps.Vui;
        if (vui is not null && (vui.ColourPrimaries != 2 || vui.TransferCharacteristics != 2 || vui.MatrixCoefficients != 2))
        {
            return new ColorCodePoints((byte)vui.ColourPrimaries, (byte)vui.TransferCharacteristics, (byte)vui.MatrixCoefficients, vui.FullRange);
        }

        if (container is { } c)
        {
            return vui is not null && vui.FullRange ? c with { FullRange = true } : c;
        }

        return new ColorCodePoints(2, 2, (byte)(sps.CroppedHeight <= 576 ? 6 : 1), vui?.FullRange ?? false);
    }

    private void Fail(string id, string message, CorruptDataException e)
    {
        Report(id, message);
        if (_errors == DecodeErrorMode.Throw)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
        }
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
        foreach (VulkanObjectHandle handle in _parameters.Values)
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

        _queryPool?.Dispose();
        _pool?.Dispose();
        if (_profile != null)
        {
            NativeMemory.Free(_profile);
        }
    }

    private static uint AlignUp(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>A frame of the stream: its numbers, reference marking, slot and, once read back, its planes.</summary>
    private sealed class Picture : INalDpbPicture
    {
        public int FrameNum { get; set; }

        public int TopPoc { get; set; }

        public int BottomPoc { get; set; }

        public int Poc => Math.Min(TopPoc, BottomPoc);

        public bool IsShortTermReference { get; set; }

        public bool IsLongTermReference { get; set; }

        public int LongTermFrameIdx { get; set; }

        public bool IsReference => IsShortTermReference || IsLongTermReference;

        public bool NonExisting { get; set; }

        public bool Output { get; set; }

        public int Slot { get; set; } = -1;

        public int Job { get; set; } = -1;

        public H264Sps? Sps { get; set; }

        public H264Sei? Sei { get; set; }

        public YuvRaster<byte>? Raster { get; set; }

        public MediaTime Time { get; set; }

        public MediaTime? Duration { get; set; }
    }

    /// <summary>One picture's GPU resources: its command buffer, fence and readback buffer; the query and bitstream share go by its index.</summary>
    private sealed class Job(nint commands, VulkanObjectHandle fence)
    {
        public nint Commands { get; } = commands;

        public VulkanObjectHandle Fence { get; } = fence;

        public VulkanBuffer? Readback { get; set; }

        public Picture? Picture { get; set; }
    }

    /// <summary>The decode profile and its list, in native memory: images, buffers and the session are made against them.</summary>
    private struct ProfileBlock
    {
        public VkVideoProfileInfoKHR Profile;
        public VkVideoDecodeH264ProfileInfoKHR H264;
        public VkVideoDecodeUsageInfoKHR Usage;
        public VkVideoProfileListInfoKHR List;

        public static void Fill(ProfileBlock* self)
        {
            self->Usage = new VkVideoDecodeUsageInfoKHR { videoUsageHints = VkVideoDecodeUsageFlagBitsKHR.TRANSCODING_BIT_KHR };
            self->H264 = new VkVideoDecodeH264ProfileInfoKHR
            {
                pNext = &self->Usage,
                stdProfileIdc = StdVideoH264ProfileIdc.HIGH,
                pictureLayout = VkVideoDecodeH264PictureLayoutFlagBitsKHR.PROGRESSIVE_KHR,
            };
            self->Profile = new VkVideoProfileInfoKHR
            {
                pNext = &self->H264,
                videoCodecOperation = VkVideoCodecOperationFlagBitsKHR.DECODE_H264_BIT_KHR,
                chromaSubsampling = VkVideoChromaSubsamplingFlagBitsKHR._420_BIT_KHR,
                lumaBitDepth = VkVideoComponentBitDepthFlagBitsKHR._8_BIT_KHR,
                chromaBitDepth = VkVideoComponentBitDepthFlagBitsKHR._8_BIT_KHR,
            };
            self->List = new VkVideoProfileListInfoKHR { profileCount = 1, pProfiles = &self->Profile };
        }
    }

    /// <summary>The capabilities of the decode profile on the device.</summary>
    private struct Capabilities
    {
        public VkVideoCapabilitiesKHR Video;
        public VkVideoDecodeCapabilitiesKHR Decode;
        public VkVideoDecodeH264CapabilitiesKHR H264;

        public static Capabilities Query(VulkanPhysicalDevice physical, ProfileBlock* profile)
        {
            VkVideoDecodeH264CapabilitiesKHR h264 = new();
            VkVideoDecodeCapabilitiesKHR decode = new() { pNext = &h264 };
            VkVideoCapabilitiesKHR video = new() { pNext = &decode };
            VkResult result = physical.Instance.Commands.vkGetPhysicalDeviceVideoCapabilitiesKHR(physical.Handle, &profile->Profile, &video);
            if (result < 0)
            {
                // Some drivers refuse usage hints; ask again without them.
                profile->H264.pNext = null;
                result = physical.Instance.Commands.vkGetPhysicalDeviceVideoCapabilitiesKHR(physical.Handle, &profile->Profile, &video);
            }

            VkCheck.ThrowIfFailed(result, "vkGetPhysicalDeviceVideoCapabilitiesKHR");
            video.pNext = null;
            decode.pNext = null;
            return new Capabilities { Video = video, Decode = decode, H264 = h264 };
        }
    }
}
