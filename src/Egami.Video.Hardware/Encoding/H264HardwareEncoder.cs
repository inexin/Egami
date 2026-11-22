using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Hardware.Vulkan;
using Egami.Video.Hardware.Vulkan.Native;

namespace Egami.Video.Hardware.Encoding;

/// <summary>
/// Encodes H.264 on the GPU's video engine through Vulkan Video (<c>VK_KHR_video_encode_h264</c>). Egami plans the pictures
/// (IDR, P and non-reference B pictures in closed runs), keeps the reference lists and fills the parameter sets and picture
/// headers; the driver codes the slices and writes the SPS and PPS from Egami's values. Frames are uploaded as NV12 and up to
/// <see cref="InFlight"/> pictures run on the GPU while the next ones are prepared.
/// </summary>
internal sealed unsafe class H264HardwareEncoder : IVideoEncoder
{
    /// <summary>The pictures that may be on the GPU at once.</summary>
    internal const int InFlight = 4;

    /// <summary>The quality when the options leave it open, as Egami's software H.264 encoder's.</summary>
    internal const int DefaultQuality = 60;

    /// <summary>The effort when the options leave it open: the driver's best quality level on drivers with five or fewer.</summary>
    internal const int DefaultEffort = 4;

    private const int QueryStatusInsufficientBuffer = -1000299000;

    private readonly VideoEncoderOptions _options;
    private readonly H264EncoderOptions _h264;
    private readonly MediaContext _context;
    private readonly VulkanDevice _device;
    private readonly VulkanQueue _queue;
    private readonly ProfileBlock* _profile;
    private readonly VulkanCommandPool _pool;
    private readonly VulkanVideoSession _session;
    private readonly VulkanObjectHandle _queryPool;
    private readonly VulkanBuffer _bitstream;
    private readonly VulkanImage[] _dpb;
    private readonly Job[] _jobs = new Job[InFlight];
    private readonly Queue<int> _submitted = new();
    private readonly Queue<EncodedPacket> _ready = new();
    private readonly Stack<VulkanBuffer> _freeStaging = new();
    private readonly List<Input> _pending = [];
    private readonly List<Reference> _references = [];
    private readonly Queue<MediaTime> _shownTimes = new();
    private readonly uint _codedWidth;
    private readonly uint _codedHeight;
    private readonly ulong _regionSize;
    private readonly int _keyInterval;
    private readonly int _bFrames;
    private readonly int _pReferences;
    private readonly int _maxRefFrames;
    private readonly int _levelIndex;
    private readonly int _qp;
    private readonly int _qpOffsetI;
    private readonly int _qpOffsetB;
    private readonly uint _qualityLevel;
    private readonly int _slices;
    private readonly bool _cabac;
    private readonly VkVideoEncodeRateControlModeFlagBitsKHR _rateMode;
    private readonly Rational _timeBase;
    private VulkanObjectHandle? _parameters;
    private byte[] _sps = [];
    private byte[] _pps = [];
    private int _index;
    private int _lastKey;
    private int _idrIndex;
    private int _idrPicId = -1;
    private int _frameNum;
    private int _nextJob;
    private long _packets;
    private MediaTime _firstTime;
    private bool _started;
    private bool _disposed;

    public H264HardwareEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Width, 2, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Height, 2, nameof(options));
        if ((options.Width & 1) != 0 || (options.Height & 1) != 0)
        {
            throw new ArgumentException($"H.264 4:2:0 needs an even width and height; {options.Width}x{options.Height} is not.", nameof(options));
        }

        if (options.FrameRate.Numerator <= 0 || options.FrameRate.Denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FrameRate, "The frame rate must be positive.");
        }

        int quality = Check(options.Quality ?? DefaultQuality, 0, 100, "Quality");
        int effort = Check(options.Effort ?? DefaultEffort, 0, 10, "Effort");
        H264EncoderOptions h264 = options.H264 ?? H264EncoderOptions.Default;
        if (h264.MinQuantizer > h264.MaxQuantizer)
        {
            throw new ArgumentException($"The smallest QP {h264.MinQuantizer} is above the largest {h264.MaxQuantizer}.", nameof(options));
        }

        if (h264.Profile == H264Profile.ConstrainedBaseline && h264.Entropy == H264EntropyCoder.Cabac)
        {
            throw new ArgumentException("Constrained Baseline codes with CAVLC only.", nameof(options));
        }

        if (h264.Profile == H264Profile.ConstrainedBaseline && h264.BFrames > 0)
        {
            throw new ArgumentException("Constrained Baseline has no B-frames.", nameof(options));
        }

        if (options.FirstPassStats is not null)
        {
            throw new ArgumentException("The hardware encoder codes in one pass; it takes no first-pass statistics.", nameof(options));
        }

        long bitrate = 0;
        _rateMode = options.RateControl switch
        {
            RateControlMode.Quality or RateControlMode.Quantizer => VkVideoEncodeRateControlModeFlagBitsKHR.DISABLED_BIT_KHR,
            RateControlMode.Bitrate => VkVideoEncodeRateControlModeFlagBitsKHR.VBR_BIT_KHR,
            RateControlMode.ConstantBitrate => VkVideoEncodeRateControlModeFlagBitsKHR.CBR_BIT_KHR,
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.RateControl, "Unknown rate control mode."),
        };
        if (options.RateControl is RateControlMode.Bitrate or RateControlMode.ConstantBitrate)
        {
            bitrate = options.Bitrate is > 0 and var b ? b : throw new ArgumentException($"{options.RateControl} needs a positive Bitrate.", nameof(options));
        }

        _options = options;
        _h264 = h264;
        _context = context;
        _timeBase = options.FrameRate.Reciprocal();
        _qp = Math.Clamp(options.Quantizer is int q ? Check(q, 0, 51, "Quantizer") : QualityToQp(quality), h264.MinQuantizer, h264.MaxQuantizer);
        double fps = (double)options.FrameRate.Numerator / options.FrameRate.Denominator;
        _keyInterval = options.KeyframeInterval is int k ? Check(k, 1, int.MaxValue, "KeyframeInterval") : Math.Max(1, (int)Math.Ceiling(fps * 10));

        (StdVideoH264ProfileIdc profileIdc, string profileName) = h264.Profile switch
        {
            H264Profile.ConstrainedBaseline => (StdVideoH264ProfileIdc.BASELINE, "Baseline"),
            H264Profile.Main => (StdVideoH264ProfileIdc.MAIN, "Main"),
            _ => (StdVideoH264ProfileIdc.HIGH, "High"),
        };
        _device = VulkanVideoContext.GetDevice(VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR, profileName);
        _queue = _device.EncodeQueue ?? throw new HardwareVideoException($"{_device.Physical.Name} has no video encode queue.");
        if ((_device.Physical.QueueFamilies[_queue.Family].Flags & VkQueueFlagBits.TRANSFER_BIT) == 0)
        {
            throw new HardwareVideoException($"{_device.Physical.Name}'s encode queue cannot copy frames to the GPU (no transfer support).");
        }

        _profile = (ProfileBlock*)NativeMemory.AllocZeroed((nuint)sizeof(ProfileBlock));
        try
        {
            bool realtime = options.LatencyMode == LatencyMode.Realtime;
            ProfileBlock.Fill(_profile, profileIdc, realtime);
            Capabilities caps = Capabilities.Query(_device.Physical, _profile);
            CheckSize(caps.Video, options.Width, options.Height);
            if ((caps.Encode.rateControlModes & _rateMode) == 0)
            {
                throw new NotSupportedException($"{_device.Physical.Name} has no {options.RateControl} rate control for H.264 (it has {caps.Encode.rateControlModes}).");
            }

            _codedWidth = AlignUp((uint)options.Width, Math.Max(16u, caps.Video.pictureAccessGranularity.width));
            _codedHeight = AlignUp((uint)options.Height, Math.Max(16u, caps.Video.pictureAccessGranularity.height));
            _qualityLevel = QualityLevel(effort, caps.Encode.maxQualityLevels);

            // The QP spacing of the picture types follows the driver's preferred constant QPs at this quality level.
            VkVideoEncodeH264QualityLevelPropertiesKHR preferred = Capabilities.Preferred(_device.Physical, _profile, _qualityLevel);
            (_qpOffsetI, _qpOffsetB) = QpOffsets(preferred.preferredConstantQp);

            // B pictures only when asked for: drivers may prefer them and still not predict from the later reference
            // (NVIDIA Pascal codes every B macroblock from the earlier one), which makes them cost more than P pictures.
            bool canB = profileIdc != StdVideoH264ProfileIdc.BASELINE && caps.H264.maxBPictureL0ReferenceCount > 0 && caps.H264.maxL1ReferenceCount > 0;
            int bFrames = options.LatencyMode != LatencyMode.Quality || !canB ? 0 : h264.BFrames ?? 0;
            _bFrames = Math.Clamp(bFrames, 0, 16);
            _pReferences = (int)Math.Clamp(Math.Min(caps.H264.maxPPictureL0ReferenceCount, (uint)RefFrames(effort)), 1, 16);
            int widthMbs = (int)(_codedWidth / 16), heightMbs = (int)(_codedHeight / 16);
            int maxRef = Math.Max(_pReferences, _bFrames > 0 ? 2 : 1);
            long peak = _rateMode switch
            {
                VkVideoEncodeRateControlModeFlagBitsKHR.CBR_BIT_KHR => bitrate,
                VkVideoEncodeRateControlModeFlagBitsKHR.VBR_BIT_KHR => options.MaxBitrate is > 0 and var m ? Math.Max(m, bitrate) : bitrate * 2,
                _ => 0,
            };
            bool high = profileIdc == StdVideoH264ProfileIdc.HIGH;
            H264Levels.Level level;
            if (h264.Level is int fixedLevel)
            {
                level = H264Levels.All[H264Levels.IndexOf(fixedLevel)];
                if (H264Levels.Misfit(level, widthMbs, heightMbs, fps, 1, peak, high) is { } why)
                {
                    throw new ArgumentException($"The stream does not fit its level: {why}.", nameof(options));
                }
            }
            else
            {
                level = H264Levels.Choose(widthMbs, heightMbs, fps, maxRef, peak, high);
            }

            // Fewer references where the level's buffer does not hold them.
            while (maxRef > 1 && H264Levels.Misfit(level, widthMbs, heightMbs, fps, maxRef, 0, high) is not null)
            {
                maxRef--;
            }

            if (maxRef < 2)
            {
                _bFrames = 0;
            }

            _pReferences = Math.Min(_pReferences, maxRef);
            _maxRefFrames = maxRef;
            _levelIndex = H264Levels.IndexOf(level.Idc);
            if (_levelIndex > (int)caps.H264.maxLevelIdc)
            {
                throw new NotSupportedException($"{_device.Physical.Name} encodes H.264 up to level {H264Levels.All[(int)caps.H264.maxLevelIdc].Idc / 10.0:0.0}; this stream needs {level.Idc / 10.0:0.0}.");
            }

            _cabac = profileIdc != StdVideoH264ProfileIdc.BASELINE && h264.Entropy != H264EntropyCoder.Cavlc;
            _slices = (int)Math.Clamp((uint)(h264.Slices ?? 1), 1u, Math.Max(1u, caps.H264.maxSliceCount));

            uint slots = (uint)_maxRefFrames + 1;
            if (slots > caps.Video.maxDpbSlots || _maxRefFrames > caps.Video.maxActiveReferencePictures)
            {
                throw new NotSupportedException($"{_device.Physical.Name} keeps {caps.Video.maxDpbSlots} pictures; the stream needs {slots}.");
            }

            VkExtensionProperties header = caps.Video.stdHeaderVersion;
            VkVideoSessionCreateInfoKHR sessionInfo = new()
            {
                queueFamilyIndex = (uint)_queue.Family,
                pVideoProfile = &_profile->Profile,
                pictureFormat = VkFormat.G8_B8R8_2PLANE_420_UNORM,
                maxCodedExtent = new VkExtent2D { width = _codedWidth, height = _codedHeight },
                referencePictureFormat = VkFormat.G8_B8R8_2PLANE_420_UNORM,
                maxDpbSlots = slots,
                maxActiveReferencePictures = (uint)_maxRefFrames,
                pStdHeaderVersion = &header,
            };
            _session = _device.CreateVideoSession(sessionInfo);
            _pool = _device.CreateCommandPool(_queue);
            _dpb = new VulkanImage[slots];
            for (int i = 0; i < slots; i++)
            {
                _dpb[i] = CreatePicture(VkImageUsageFlagBits.VIDEO_ENCODE_DPB_BIT_KHR);
            }

            // Each picture in flight has its own share of the bitstream buffer: room for a raw picture and a margin, which no
            // coded picture reaches.
            ulong alignment = Math.Max(caps.Video.minBitstreamBufferOffsetAlignment, caps.Video.minBitstreamBufferSizeAlignment);
            _regionSize = AlignUp((ulong)Nv12Writer.Size((int)_codedWidth, (int)_codedHeight) + (1 << 20), Math.Max(alignment, 1));
            _bitstream = _device.CreateBuffer(
                _regionSize * InFlight, VkBufferUsageFlagBits.VIDEO_ENCODE_DST_BIT_KHR, VkMemoryPropertyFlagBits.HOST_VISIBLE_BIT, &_profile->List);
            VkQueryPoolVideoEncodeFeedbackCreateInfoKHR feedback = new()
            {
                pNext = &_profile->Profile,
                encodeFeedbackFlags = VkVideoEncodeFeedbackFlagBitsKHR.BITSTREAM_BUFFER_OFFSET_BIT_KHR | VkVideoEncodeFeedbackFlagBitsKHR.BITSTREAM_BYTES_WRITTEN_BIT_KHR,
            };
            VkQueryPoolCreateInfo queryInfo = new() { pNext = &feedback, queryType = VkQueryType.VIDEO_ENCODE_FEEDBACK_KHR, queryCount = InFlight };
            ulong queryPool;
            VkCheck.ThrowIfFailed(_device.Commands.vkCreateQueryPool(_device.Raw, &queryInfo, null, &queryPool), "vkCreateQueryPool");
            _queryPool = new VulkanObjectHandle(_device.Handle, queryPool, _device.Commands.vkDestroyQueryPool);
            for (int i = 0; i < InFlight; i++)
            {
                _jobs[i] = new Job(CreatePicture(VkImageUsageFlagBits.VIDEO_ENCODE_SRC_BIT_KHR | VkImageUsageFlagBits.TRANSFER_DST_BIT), _pool.Allocate(), _device.CreateFence());
            }
        }
        catch
        {
            DisposeResources();
            throw;
        }

        Track = MakeTrack(DefaultColor, null);
    }

    /// <summary>The color of frames converted to the coded format: BT.601 limited range, as Egami's software H.264 encoder's.</summary>
    internal static readonly ColorCodePoints DefaultColor = new(2, 2, 6, false);

    public VideoTrackInfo Track { get; private set; }

    /// <summary>Gets the driver's quality level in use, for tests.</summary>
    internal uint QualityLevelInUse => _qualityLevel;

    /// <summary>Gets the B-frames between reference frames, for tests.</summary>
    internal int BFrames => _bFrames;

    /// <summary>Gets the reference frames the stream keeps, for tests.</summary>
    internal int MaxRefFrames => _maxRefFrames;

    /// <summary>The QP for a quality of 0 to 100, on the scale of Egami's software H.264 encoder: 51 at 0, 23 at 50.</summary>
    /// <summary>
    /// The QP of I and B pictures relative to P pictures: the spacing of the driver's preferred constant QPs, or -2 and +2
    /// where it states none that make sense.
    /// </summary>
    internal static (int I, int B) QpOffsets(VkVideoEncodeH264QpKHR preferred)
    {
        int i = preferred.qpI - preferred.qpP, b = preferred.qpB - preferred.qpP;
        return preferred.qpP > 0 && i is >= -6 and <= 0 && b is >= 0 and <= 6 ? (i, b) : (-2, 2);
    }

    internal static int QualityToQp(int quality) => Math.Clamp((int)Math.Round(51 - (0.56 * quality)), 0, 51);

    /// <summary>
    /// The driver quality level for an effort: effort 4 and up the driver's best (its highest level), lower efforts its faster
    /// levels in step.
    /// </summary>
    internal static uint QualityLevel(int effort, uint levels) =>
        levels <= 1 ? 0 : (uint)Math.Min(levels - 1, (long)Math.Round(effort * (levels - 1) / 4.0));

    /// <summary>The reference frames P pictures may use at an effort, as far as the GPU allows: one, two from effort 2, three from 4.</summary>
    internal static int RefFrames(int effort) => effort >= 4 ? 3 : effort >= 2 ? 2 : 1;

    public void Send(VideoFrame frame) => Send(frame, EncodeFrameOptions.Default);

    public void Send(VideoFrame frame, EncodeFrameOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Width != _options.Width || frame.Height != _options.Height)
        {
            throw new ArgumentException($"The frame is {frame.Width}x{frame.Height}; the encoder makes {_options.Width}x{_options.Height}.", nameof(frame));
        }

        if (frame.Planes is null && frame.Pixels is null)
        {
            throw new ArgumentException("The frame has neither planes nor pixels.", nameof(frame));
        }

        if (!_started)
        {
            ColorCodePoints color = ColorOf(frame);
            CreateParameters(color);
            Track = MakeTrack(color, frame.Hdr);
            _started = true;
        }

        VulkanBuffer staging = RentStaging();
        try
        {
            Upload(frame, staging);
        }
        catch
        {
            _freeStaging.Push(staging);
            throw;
        }

        MediaTime timestamp = frame.Timestamp.Rescale(_timeBase);
        MediaTime duration = frame.Duration is { } d ? d.Rescale(_timeBase) : new MediaTime(1, _timeBase);
        int index = _index++;
        if (_bFrames > 0)
        {
            if (index == 0)
            {
                _firstTime = timestamp;
            }

            _shownTimes.Enqueue(timestamp);
        }

        bool key = index == 0 || options.ForceKeyframe || index - _lastKey >= _keyInterval;
        Input input = new(index, timestamp, duration, staging);
        if (key)
        {
            _lastKey = index;
            CodeRun();
            Code(input, StdVideoH264PictureType.IDR);
        }
        else
        {
            _pending.Add(input);
            if (_pending.Count > _bFrames)
            {
                CodeRun();
            }
        }
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CodeRun();
        while (_submitted.Count > 0)
        {
            Collect(_submitted.Dequeue(), wait: true);
        }
    }

    public bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_submitted.Count > 0 && _device.Commands.vkGetFenceStatus(_device.Raw, _jobs[_submitted.Peek()].Fence.Value) == VkResult.SUCCESS)
        {
            Collect(_submitted.Dequeue(), wait: true);
        }

        return _ready.TryDequeue(out packet);
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
            // Let the GPU finish with the pictures before they are freed.
            _ = _device.Commands.vkQueueWaitIdle(_queue.Handle);
        }

        while (_ready.TryDequeue(out EncodedPacket? p))
        {
            p.Dispose();
        }

        DisposeResources();
    }

    /// <summary>Codes the frames held for a run: the last as a P picture, then the ones before it as B pictures.</summary>
    private void CodeRun()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        Code(_pending[^1], StdVideoH264PictureType.P);
        for (int i = 0; i < _pending.Count - 1; i++)
        {
            Code(_pending[i], StdVideoH264PictureType.B);
        }

        _pending.Clear();
    }

    /// <summary>Records and submits one picture, collecting the oldest one first when every job is busy.</summary>
    private void Code(Input input, StdVideoH264PictureType type)
    {
        int jobIndex = _nextJob;
        _nextJob = (_nextJob + 1) % InFlight;
        Job job = _jobs[jobIndex];
        if (job.Busy)
        {
            Collect(_submitted.Dequeue(), wait: true);
        }

        bool idr = type == StdVideoH264PictureType.IDR;
        bool reference = type != StdVideoH264PictureType.B;
        if (idr)
        {
            _idrPicId = (_idrPicId + 1) & 0xFFFF;
            _frameNum = 0;
            _idrIndex = input.Index;
            _references.Clear();
        }

        int poc = 2 * (input.Index - _idrIndex);
        int setupSlot = FreeSlot();
        Record(job, jobIndex, input, type, poc, setupSlot);
        _device.Submit(_queue, job.Commands, job.Fence);
        job.Busy = true;
        job.Input = input;
        job.Key = idr;
        job.Type = type;
        _submitted.Enqueue(jobIndex);

        if (reference)
        {
            _references.Insert(0, new Reference(setupSlot, _frameNum, poc, idr));
            if (_references.Count > _maxRefFrames)
            {
                _references.RemoveAt(_references.Count - 1);
            }

            _frameNum = (_frameNum + 1) & 0xFFFF;
        }
    }

    /// <summary>A DPB slot no reference holds.</summary>
    private int FreeSlot()
    {
        for (int slot = 0; slot < _dpb.Length; slot++)
        {
            if (!_references.Exists(r => r.Slot == slot))
            {
                return slot;
            }
        }

        throw new InvalidOperationException("Every DPB slot holds a reference.");
    }

    private void Record(Job job, int jobIndex, Input input, StdVideoH264PictureType type, int poc, int setupSlot)
    {
        VkDeviceCommands vk = _device.Commands;
        nint cb = job.Commands;
        _pool.Begin(cb);
        vk.vkCmdResetQueryPool(cb, _queryPool.Value, (uint)jobIndex, 1);

        // Upload: staging buffer to the source picture.
        VkImageMemoryBarrier2* barriers = stackalloc VkImageMemoryBarrier2[1 + 17];
        barriers[0] = Barrier(job.Source.Handle, VkImageLayout.UNDEFINED, VkImageLayout.TRANSFER_DST_OPTIMAL, VkPipelineStageFlagBits2.VIDEO_ENCODE_BIT_KHR, 0, VkPipelineStageFlagBits2.ALL_TRANSFER_BIT, VkAccessFlagBits2.TRANSFER_WRITE_BIT);
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
        vk.vkCmdCopyBufferToImage(cb, input.Staging.Handle, job.Source.Handle, VkImageLayout.TRANSFER_DST_OPTIMAL, 2, regions);

        int count = 0;
        barriers[count++] = Barrier(job.Source.Handle, VkImageLayout.TRANSFER_DST_OPTIMAL, VkImageLayout.VIDEO_ENCODE_SRC_KHR, VkPipelineStageFlagBits2.ALL_TRANSFER_BIT, VkAccessFlagBits2.TRANSFER_WRITE_BIT, VkPipelineStageFlagBits2.VIDEO_ENCODE_BIT_KHR, VkAccessFlagBits2.VIDEO_ENCODE_READ_BIT_KHR);
        if (!_dpbReady)
        {
            _dpbReady = true;
            for (int i = 0; i < _dpb.Length; i++)
            {
                barriers[count++] = Barrier(_dpb[i].Handle, VkImageLayout.UNDEFINED, VkImageLayout.VIDEO_ENCODE_DPB_KHR, VkPipelineStageFlagBits2.NONE, 0, VkPipelineStageFlagBits2.VIDEO_ENCODE_BIT_KHR, VkAccessFlagBits2.VIDEO_ENCODE_READ_BIT_KHR | VkAccessFlagBits2.VIDEO_ENCODE_WRITE_BIT_KHR);
            }
        }

        // The pictures coded before this one wrote the references it reads.
        VkMemoryBarrier2 dpbHazard = new()
        {
            srcStageMask = VkPipelineStageFlagBits2.VIDEO_ENCODE_BIT_KHR,
            srcAccessMask = VkAccessFlagBits2.VIDEO_ENCODE_WRITE_BIT_KHR,
            dstStageMask = VkPipelineStageFlagBits2.VIDEO_ENCODE_BIT_KHR,
            dstAccessMask = VkAccessFlagBits2.VIDEO_ENCODE_READ_BIT_KHR | VkAccessFlagBits2.VIDEO_ENCODE_WRITE_BIT_KHR,
        };
        PipelineBarrier(cb, barriers, count, &dpbHazard);

        // The references this picture uses: P pictures the most recent ones, B pictures the reference shown before and after.
        bool idr = type == StdVideoH264PictureType.IDR;
        Span<int> l0 = stackalloc int[16];
        Span<int> l1 = stackalloc int[1];
        int l0Count = 0, l1Count = 0;
        if (type == StdVideoH264PictureType.P)
        {
            l0Count = Math.Min(_pReferences, _references.Count);
            for (int i = 0; i < l0Count; i++)
            {
                l0[i] = i;
            }
        }
        else if (type == StdVideoH264PictureType.B)
        {
            int before = _references.FindIndex(r => r.Poc < poc);
            int after = _references.FindIndex(r => r.Poc > poc);
            if (before >= 0)
            {
                l0[l0Count++] = before;
            }

            if (after >= 0)
            {
                l1[l1Count++] = after;
            }
        }

        int refCount = _references.Count;
        StdVideoEncodeH264ReferenceInfo* refInfos = stackalloc StdVideoEncodeH264ReferenceInfo[Math.Max(refCount, 1)];
        VkVideoEncodeH264DpbSlotInfoKHR* refSlotInfos = stackalloc VkVideoEncodeH264DpbSlotInfoKHR[Math.Max(refCount, 1)];
        VkVideoPictureResourceInfoKHR* resources = stackalloc VkVideoPictureResourceInfoKHR[refCount + 2];
        VkVideoReferenceSlotInfoKHR* beginSlots = stackalloc VkVideoReferenceSlotInfoKHR[refCount + 1];
        VkVideoReferenceSlotInfoKHR* refSlots = stackalloc VkVideoReferenceSlotInfoKHR[Math.Max(refCount, 1)];
        VkExtent2D extent = new() { width = _codedWidth, height = _codedHeight };
        resources[0] = new VkVideoPictureResourceInfoKHR { codedExtent = extent, imageViewBinding = _dpb[setupSlot].View };
        resources[1] = new VkVideoPictureResourceInfoKHR { codedExtent = extent, imageViewBinding = job.Source.View };
        beginSlots[0] = new VkVideoReferenceSlotInfoKHR { slotIndex = -1, pPictureResource = &resources[0] };
        int used = 0;
        for (int i = 0; i < refCount; i++)
        {
            Reference r = _references[i];
            resources[i + 2] = new VkVideoPictureResourceInfoKHR { codedExtent = extent, imageViewBinding = _dpb[r.Slot].View };
            beginSlots[i + 1] = new VkVideoReferenceSlotInfoKHR { slotIndex = r.Slot, pPictureResource = &resources[i + 2] };
            refInfos[i] = new StdVideoEncodeH264ReferenceInfo
            {
                primary_pic_type = r.Idr ? StdVideoH264PictureType.IDR : StdVideoH264PictureType.P,
                FrameNum = (uint)r.FrameNum,
                PicOrderCnt = r.Poc,
            };
            refSlotInfos[i] = new VkVideoEncodeH264DpbSlotInfoKHR { pStdReferenceInfo = &refInfos[i] };
            if (l0[..l0Count].Contains(i) || l1[..l1Count].Contains(i))
            {
                refSlots[used++] = new VkVideoReferenceSlotInfoKHR { pNext = &refSlotInfos[i], slotIndex = r.Slot, pPictureResource = &resources[i + 2] };
            }
        }

        // Rate control: set once with a reset before the first picture, then stated with every picture.
        VkVideoEncodeRateControlLayerInfoKHR layer = default;
        VkVideoEncodeH264RateControlLayerInfoKHR h264Layer = default;
        VkVideoEncodeH264RateControlInfoKHR h264Rate = default;
        VkVideoEncodeRateControlInfoKHR rate = default;
        RateControl(&rate, &h264Rate, &layer, &h264Layer);
        VkVideoBeginCodingInfoKHR begin = new()
        {
            pNext = _rateSet ? &rate : null,
            videoSession = _session.Handle,
            videoSessionParameters = _parameters!.Value,
            referenceSlotCount = (uint)refCount + 1,
            pReferenceSlots = beginSlots,
        };
        vk.vkCmdBeginVideoCodingKHR(cb, &begin);
        if (!_rateSet)
        {
            VkVideoEncodeQualityLevelInfoKHR quality = new() { pNext = &rate, qualityLevel = _qualityLevel };
            VkVideoCodingControlInfoKHR control = new()
            {
                pNext = &quality,
                flags = VkVideoCodingControlFlagBitsKHR.RESET_BIT_KHR | VkVideoCodingControlFlagBitsKHR.ENCODE_RATE_CONTROL_BIT_KHR | VkVideoCodingControlFlagBitsKHR.ENCODE_QUALITY_LEVEL_BIT_KHR,
            };
            vk.vkCmdControlVideoCodingKHR(cb, &control);
            _rateSet = true;
        }

        // The picture's headers.
        StdVideoEncodeH264ReferenceListsInfo lists = default;
        for (int i = 0; i < 32; i++)
        {
            lists.RefPicList0[i] = 0xFF;
            lists.RefPicList1[i] = 0xFF;
        }

        for (int i = 0; i < l0Count; i++)
        {
            lists.RefPicList0[i] = (byte)_references[l0[i]].Slot;
        }

        for (int i = 0; i < l1Count; i++)
        {
            lists.RefPicList1[i] = (byte)_references[l1[i]].Slot;
        }

        lists.num_ref_idx_l0_active_minus1 = (byte)Math.Max(0, l0Count - 1);
        lists.num_ref_idx_l1_active_minus1 = (byte)Math.Max(0, l1Count - 1);
        StdVideoEncodeH264PictureInfo picture = new()
        {
            idr_pic_id = (ushort)_idrPicId,
            primary_pic_type = type,
            frame_num = (uint)_frameNum,
            PicOrderCnt = poc,
            pRefLists = &lists,
        };
        picture.flags.IdrPicFlag = idr;
        picture.flags.is_reference = type != StdVideoH264PictureType.B;
        StdVideoH264SliceType sliceType = type switch
        {
            StdVideoH264PictureType.B => StdVideoH264SliceType.B,
            StdVideoH264PictureType.P => StdVideoH264SliceType.P,
            _ => StdVideoH264SliceType.I,
        };
        StdVideoEncodeH264SliceHeader slice = new() { slice_type = sliceType };
        slice.flags.direct_spatial_mv_pred_flag = sliceType == StdVideoH264SliceType.B;
        slice.flags.num_ref_idx_active_override_flag = (sliceType == StdVideoH264SliceType.P && l0Count != 1) || (sliceType == StdVideoH264SliceType.B && (l0Count != 1 || l1Count != 1));
        int qp = Math.Clamp(sliceType switch { StdVideoH264SliceType.I => _qp + _qpOffsetI, StdVideoH264SliceType.B => _qp + _qpOffsetB, _ => _qp }, _h264.MinQuantizer, _h264.MaxQuantizer);
        VkVideoEncodeH264NaluSliceInfoKHR* sliceInfos = stackalloc VkVideoEncodeH264NaluSliceInfoKHR[_slices];
        for (int i = 0; i < _slices; i++)
        {
            sliceInfos[i] = new VkVideoEncodeH264NaluSliceInfoKHR
            {
                constantQp = _rateMode == VkVideoEncodeRateControlModeFlagBitsKHR.DISABLED_BIT_KHR ? qp : 0,
                pStdSliceHeader = &slice,
            };
        }

        VkVideoEncodeH264PictureInfoKHR h264Picture = new() { naluSliceEntryCount = (uint)_slices, pNaluSliceEntries = sliceInfos, pStdPictureInfo = &picture };
        StdVideoEncodeH264ReferenceInfo setupInfo = new()
        {
            primary_pic_type = type,
            FrameNum = (uint)_frameNum,
            PicOrderCnt = poc,
        };
        VkVideoEncodeH264DpbSlotInfoKHR setupSlotInfo = new() { pStdReferenceInfo = &setupInfo };
        VkVideoReferenceSlotInfoKHR setup = new() { pNext = &setupSlotInfo, slotIndex = setupSlot, pPictureResource = &resources[0] };
        VkVideoEncodeInfoKHR encode = new()
        {
            pNext = &h264Picture,
            dstBuffer = _bitstream.Handle,
            dstBufferOffset = (ulong)jobIndex * _regionSize,
            dstBufferRange = _regionSize,
            srcPictureResource = resources[1],
            pSetupReferenceSlot = &setup,
            referenceSlotCount = (uint)used,
            pReferenceSlots = used > 0 ? refSlots : null,
        };
        vk.vkCmdBeginQuery(cb, _queryPool.Value, (uint)jobIndex, 0);
        vk.vkCmdEncodeVideoKHR(cb, &encode);
        vk.vkCmdEndQuery(cb, _queryPool.Value, (uint)jobIndex);
        VkVideoEndCodingInfoKHR end = new();
        vk.vkCmdEndVideoCodingKHR(cb, &end);

        VkMemoryBarrier2 toHost = new()
        {
            srcStageMask = VkPipelineStageFlagBits2.VIDEO_ENCODE_BIT_KHR,
            srcAccessMask = VkAccessFlagBits2.VIDEO_ENCODE_WRITE_BIT_KHR,
            dstStageMask = VkPipelineStageFlagBits2.HOST_BIT,
            dstAccessMask = VkAccessFlagBits2.HOST_READ_BIT,
        };
        PipelineBarrier(cb, null, 0, &toHost);
        _pool.End(cb);
    }

    private bool _rateSet;
    private bool _dpbReady;

    /// <summary>Fills the rate control state: the QP per slice (disabled), or the driver's constant or variable bitrate.</summary>
    private void RateControl(VkVideoEncodeRateControlInfoKHR* rate, VkVideoEncodeH264RateControlInfoKHR* h264Rate, VkVideoEncodeRateControlLayerInfoKHR* layer, VkVideoEncodeH264RateControlLayerInfoKHR* h264Layer)
    {
        *rate = new VkVideoEncodeRateControlInfoKHR { rateControlMode = _rateMode };
        if (_rateMode == VkVideoEncodeRateControlModeFlagBitsKHR.DISABLED_BIT_KHR)
        {
            return;
        }

        long bitrate = _options.Bitrate!.Value;
        long peak = _rateMode == VkVideoEncodeRateControlModeFlagBitsKHR.CBR_BIT_KHR
            ? bitrate
            : _options.MaxBitrate is > 0 and var m ? Math.Max(m, bitrate) : bitrate * 2;
        long buffer = _options.BufferSize is > 0 and var size ? size : bitrate;
        *h264Layer = new VkVideoEncodeH264RateControlLayerInfoKHR();
        if (_h264.MinQuantizer > 0)
        {
            h264Layer->useMinQp = 1;
            h264Layer->minQp = new VkVideoEncodeH264QpKHR { qpI = _h264.MinQuantizer, qpP = _h264.MinQuantizer, qpB = _h264.MinQuantizer };
        }

        if (_h264.MaxQuantizer < 51)
        {
            h264Layer->useMaxQp = 1;
            h264Layer->maxQp = new VkVideoEncodeH264QpKHR { qpI = _h264.MaxQuantizer, qpP = _h264.MaxQuantizer, qpB = _h264.MaxQuantizer };
        }

        *layer = new VkVideoEncodeRateControlLayerInfoKHR
        {
            pNext = h264Layer,
            averageBitrate = (ulong)bitrate,
            maxBitrate = (ulong)peak,
            frameRateNumerator = (uint)_options.FrameRate.Numerator,
            frameRateDenominator = (uint)_options.FrameRate.Denominator,
        };
        *h264Rate = new VkVideoEncodeH264RateControlInfoKHR
        {
            flags = VkVideoEncodeH264RateControlFlagBitsKHR.REGULAR_GOP_BIT_KHR | VkVideoEncodeH264RateControlFlagBitsKHR.REFERENCE_PATTERN_FLAT_BIT_KHR,
            gopFrameCount = (uint)_keyInterval,
            idrPeriod = (uint)_keyInterval,
            consecutiveBFrameCount = (uint)_bFrames,
            temporalLayerCount = 1,
        };
        uint bufferMs = (uint)Math.Clamp(buffer * 1000 / bitrate, 1, uint.MaxValue);
        rate->pNext = h264Rate;
        rate->layerCount = 1;
        rate->pLayers = layer;
        rate->virtualBufferSizeInMs = bufferMs;
        rate->initialVirtualBufferSizeInMs = bufferMs / 2;
    }

    /// <summary>Waits for a job, takes its packet out of the bitstream buffer and frees the job.</summary>
    private void Collect(int jobIndex, bool wait)
    {
        Job job = _jobs[jobIndex];
        if (wait)
        {
            _device.WaitAndReset(job.Fence);
        }

        uint* result = stackalloc uint[3];
        VkCheck.ThrowIfFailed(
            _device.Commands.vkGetQueryPoolResults(_device.Raw, _queryPool.Value, (uint)jobIndex, 1, 12, result, 12, VkQueryResultFlagBits.WAIT_BIT | VkQueryResultFlagBits.WITH_STATUS_BIT_KHR),
            "vkGetQueryPoolResults");
        Input input = job.Input!;
        job.Busy = false;
        job.Input = null;
        _freeStaging.Push(input.Staging);
        int status = (int)result[2];
        if (status <= 0)
        {
            throw new HardwareVideoException(
                status == QueryStatusInsufficientBuffer ? "The coded picture did not fit its bitstream buffer." : $"The GPU failed to encode frame {input.Index} (status {status}).",
                status);
        }

        ReadOnlySpan<byte> coded = _bitstream.Span.Slice((int)((ulong)jobIndex * _regionSize) + (int)result[0], (int)result[1]);
        EncodedPacket packet = MakePacket(coded, job.Key, input);
        packet.IsDiscardable = job.Type == StdVideoH264PictureType.B;
        if (_bFrames > 0)
        {
            long k = _packets;
            packet.DecodeTimestamp = k < 1 ? new MediaTime(_firstTime.Ticks - 1, _timeBase) : _shownTimes.Dequeue();
        }

        _packets++;
        _ready.Enqueue(packet);
    }

    private EncodedPacket MakePacket(ReadOnlySpan<byte> coded, bool key, Input input)
    {
        EncodedPacket packet;
        if (_h264.AnnexB)
        {
            int extra = key ? 8 + _sps.Length + _pps.Length : 0;
            packet = EncodedPacket.Rent(1, extra + coded.Length, input.Timestamp);
            Span<byte> data = packet.GetWritableData().Span;
            if (key)
            {
                WriteStartCoded(_sps, data);
                WriteStartCoded(_pps, data[(4 + _sps.Length)..]);
            }

            coded.CopyTo(data[extra..]);
        }
        else
        {
            byte[] prefixed = NalUnits.ToLengthPrefixed(coded);
            packet = EncodedPacket.Rent(1, prefixed.Length, input.Timestamp);
            prefixed.CopyTo(packet.GetWritableData().Span);
        }

        packet.IsKeyframe = key;
        packet.Duration = input.Duration;
        return packet;
    }

    private static void WriteStartCoded(ReadOnlySpan<byte> nal, Span<byte> target)
    {
        target[0] = 0;
        target[1] = 0;
        target[2] = 0;
        target[3] = 1;
        nal.CopyTo(target[4..]);
    }

    /// <summary>
    /// Makes the session parameters from Egami's SPS and PPS values and has the driver write them, with whatever it changed.
    /// </summary>
    private void CreateParameters(ColorCodePoints color)
    {
        StdVideoH264SequenceParameterSetVui vui = new()
        {
            aspect_ratio_idc = StdVideoH264AspectRatioIdc.SQUARE,
            sar_width = 1,
            sar_height = 1,
            video_format = 5,
            colour_primaries = color.Primaries,
            transfer_characteristics = color.TransferCharacteristics,
            matrix_coefficients = color.MatrixCoefficients,
            num_units_in_tick = (uint)_options.FrameRate.Denominator,
            time_scale = (uint)Math.Min(uint.MaxValue, 2L * _options.FrameRate.Numerator),
            max_num_reorder_frames = (byte)(_bFrames > 0 ? 1 : 0),
            max_dec_frame_buffering = (byte)_maxRefFrames,
        };
        vui.flags.aspect_ratio_info_present_flag = true;
        vui.flags.video_signal_type_present_flag = true;
        vui.flags.video_full_range_flag = color.FullRange;
        vui.flags.color_description_present_flag = true;
        vui.flags.timing_info_present_flag = true;
        vui.flags.fixed_frame_rate_flag = true;
        vui.flags.bitstream_restriction_flag = true;

        StdVideoH264ProfileIdc profileIdc = _profile->H264.stdProfileIdc;
        StdVideoH264SequenceParameterSet sps = new()
        {
            profile_idc = profileIdc,
            level_idc = (StdVideoH264LevelIdc)_levelIndex,
            chroma_format_idc = StdVideoH264ChromaFormatIdc._420,
            log2_max_frame_num_minus4 = 12,
            pic_order_cnt_type = StdVideoH264PocType._0,
            log2_max_pic_order_cnt_lsb_minus4 = 12,
            max_num_ref_frames = (byte)_maxRefFrames,
            pic_width_in_mbs_minus1 = (_codedWidth / 16) - 1,
            pic_height_in_map_units_minus1 = (_codedHeight / 16) - 1,
            frame_crop_right_offset = (_codedWidth - (uint)_options.Width) / 2,
            frame_crop_bottom_offset = (_codedHeight - (uint)_options.Height) / 2,
            pSequenceParameterSetVui = &vui,
        };
        sps.flags.constraint_set1_flag = profileIdc == StdVideoH264ProfileIdc.BASELINE; // Constrained Baseline
        sps.flags.frame_mbs_only_flag = true;
        sps.flags.direct_8x8_inference_flag = true;
        sps.flags.frame_cropping_flag = _codedWidth != (uint)_options.Width || _codedHeight != (uint)_options.Height;
        sps.flags.vui_parameters_present_flag = true;

        StdVideoH264PictureParameterSet pps = new();
        pps.flags.entropy_coding_mode_flag = _cabac;
        pps.flags.transform_8x8_mode_flag = profileIdc == StdVideoH264ProfileIdc.HIGH;
        pps.flags.deblocking_filter_control_present_flag = true;

        VkVideoEncodeH264SessionParametersAddInfoKHR add = new() { stdSPSCount = 1, pStdSPSs = &sps, stdPPSCount = 1, pStdPPSs = &pps };
        VkVideoEncodeQualityLevelInfoKHR quality = new() { qualityLevel = _qualityLevel };
        VkVideoEncodeH264SessionParametersCreateInfoKHR h264Info = new() { pNext = &quality, maxStdSPSCount = 1, maxStdPPSCount = 1, pParametersAddInfo = &add };
        VkVideoSessionParametersCreateInfoKHR info = new() { pNext = &h264Info, videoSession = _session.Handle };
        ulong raw;
        VkCheck.ThrowIfFailed(_device.Commands.vkCreateVideoSessionParametersKHR(_device.Raw, &info, null, &raw), "vkCreateVideoSessionParametersKHR");
        _parameters = new VulkanObjectHandle(_device.Handle, raw, _device.Commands.vkDestroyVideoSessionParametersKHR);

        VkVideoEncodeH264SessionParametersGetInfoKHR h264Get = new() { writeStdSPS = 1, writeStdPPS = 1 };
        VkVideoEncodeSessionParametersGetInfoKHR get = new() { pNext = &h264Get, videoSessionParameters = raw };
        VkVideoEncodeH264SessionParametersFeedbackInfoKHR h264Feedback = new();
        VkVideoEncodeSessionParametersFeedbackInfoKHR feedback = new() { pNext = &h264Feedback };
        nuint size = 0;
        VkCheck.ThrowIfFailed(_device.Commands.vkGetEncodedVideoSessionParametersKHR(_device.Raw, &get, &feedback, &size, null), "vkGetEncodedVideoSessionParametersKHR");
        byte[] written = new byte[size];
        fixed (byte* p = written)
        {
            VkCheck.ThrowIfFailed(_device.Commands.vkGetEncodedVideoSessionParametersKHR(_device.Raw, &get, &feedback, &size, p), "vkGetEncodedVideoSessionParametersKHR");
        }

        foreach (Range unit in NalUnits.SplitAnnexB(written.AsSpan(0, (int)size)))
        {
            byte[] nal = written[unit];
            switch (nal[0] & 0x1F)
            {
                case 7:
                    _sps = nal;
                    break;
                case 8:
                    _pps = nal;
                    break;
            }
        }

        if (_sps.Length == 0 || _pps.Length == 0)
        {
            throw new HardwareVideoException("The driver wrote no SPS or PPS.");
        }

        ParametersOverridden = h264Feedback.hasStdSPSOverrides != 0 || h264Feedback.hasStdPPSOverrides != 0;
    }

    /// <summary>Gets whether the driver changed Egami's SPS or PPS values, for tests.</summary>
    internal bool ParametersOverridden { get; private set; }

    /// <summary>Gets the SPS the driver wrote, NAL header included, for tests.</summary>
    internal byte[] Sps => _sps;

    private VideoTrackInfo MakeTrack(ColorCodePoints color, HdrMetadata? hdr) => new()
    {
        Number = 1,
        Kind = TrackKind.Video,
        CodecId = CodecIds.Avc,
        CodecPrivate = _h264.AnnexB || _sps.Length == 0 ? ReadOnlyMemory<byte>.Empty : NalUnits.AvcC(_sps, _pps),
        TimeBase = _timeBase,
        Width = _options.Width,
        Height = _options.Height,
        FrameRate = _options.FrameRate,
        BitDepth = 8,
        ChromaFormat = ChromaFormat.Yuv420,
        Color = color,
        Hdr = hdr,
    };

    /// <summary>
    /// The color of a track's frames: 4:2:0 8-bit planes are coded as they are, so their color (the frame's, else the planes')
    /// is the track's; everything else is converted to BT.601 limited range.
    /// </summary>
    private static ColorCodePoints ColorOf(VideoFrame frame)
    {
        if (frame.Planes is not YuvRaster<byte> { ChromaFormat: ChromaFormat.Yuv420 } planes)
        {
            return DefaultColor;
        }

        if (frame.Color is { } color)
        {
            return color;
        }

        int matrix = planes.Color.Matrix switch
        {
            YuvMatrix.Bt709 => 1,
            YuvMatrix.Bt2020Ncl => 9,
            _ => 6,
        };
        return new ColorCodePoints(2, 2, (byte)matrix, planes.Color.Range == YuvRange.Full);
    }

    /// <summary>Writes a frame into a staging buffer as NV12, converting it to 8-bit 4:2:0 first when it is anything else.</summary>
    private void Upload(VideoFrame frame, VulkanBuffer staging)
    {
        Span<byte> target = staging.Span;
        if (frame.Planes is YuvRaster<byte> { ChromaFormat: ChromaFormat.Yuv420 } planes)
        {
            Nv12Writer.Write(planes, target, (int)_codedWidth, (int)_codedHeight);
            return;
        }

        Raster? converted = null;
        Raster raster = frame.Pixels ?? (converted = frame.Planes!.ToRaster());
        try
        {
            YuvColor color = new(YuvMatrix.Bt601, YuvRange.Limited, ChromaSiting.Left);
            using YuvRaster<byte> yuv = YuvRaster.FromRaster(raster, ChromaFormat.Yuv420, color, false);
            Nv12Writer.Write(yuv, target, (int)_codedWidth, (int)_codedHeight);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private VulkanBuffer RentStaging() =>
        _freeStaging.TryPop(out VulkanBuffer? buffer)
            ? buffer
            : _device.CreateBuffer((ulong)Nv12Writer.Size((int)_codedWidth, (int)_codedHeight), VkBufferUsageFlagBits.TRANSFER_SRC_BIT, VkMemoryPropertyFlagBits.HOST_VISIBLE_BIT);

    private VulkanImage CreatePicture(VkImageUsageFlagBits usage)
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
            usage = usage,
            sharingMode = VkSharingMode.EXCLUSIVE,
            initialLayout = VkImageLayout.UNDEFINED,
        };
        return _device.CreateImage(info);
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

    private void CheckSize(in VkVideoCapabilitiesKHR caps, int width, int height)
    {
        if (width < caps.minCodedExtent.width || height < caps.minCodedExtent.height || width > caps.maxCodedExtent.width || height > caps.maxCodedExtent.height)
        {
            throw new NotSupportedException(
                $"{_device.Physical.Name} encodes H.264 from {caps.minCodedExtent.width}x{caps.minCodedExtent.height} to {caps.maxCodedExtent.width}x{caps.maxCodedExtent.height}; {width}x{height} is outside that.");
        }
    }

    private void DisposeResources()
    {
        foreach (Job? job in _jobs)
        {
            job?.Source.Dispose();
            job?.Fence.Dispose();
        }

        while (_freeStaging.TryPop(out VulkanBuffer? b))
        {
            b.Dispose();
        }

        foreach (Input input in _pending)
        {
            input.Staging.Dispose();
        }

        _pending.Clear();
        foreach (VulkanImage? image in _dpb ?? [])
        {
            image?.Dispose();
        }

        _parameters?.Dispose();
        _queryPool?.Dispose();
        _bitstream?.Dispose();
        _session?.Dispose();
        _pool?.Dispose();
        if (_profile != null)
        {
            NativeMemory.Free(_profile);
        }
    }

    private static uint AlignUp(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    private static int Check(int value, int min, int max, string name, VideoEncoderOptions? options = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(nameof(options), value, $"{name} must be {min} to {max}.") : value;

    /// <summary>A frame waiting to be coded or on the GPU, with its NV12 copy.</summary>
    private sealed record Input(int Index, MediaTime Timestamp, MediaTime Duration, VulkanBuffer Staging);

    /// <summary>A reference picture: its DPB slot and the numbers the slice headers name it by.</summary>
    private readonly record struct Reference(int Slot, int FrameNum, int Poc, bool Idr);

    /// <summary>One picture's GPU resources: its source picture, command buffer and fence; the query and bitstream share go by its index.</summary>
    private sealed class Job(VulkanImage source, nint commands, VulkanObjectHandle fence)
    {
        public VulkanImage Source { get; } = source;

        public nint Commands { get; } = commands;

        public VulkanObjectHandle Fence { get; } = fence;

        public bool Busy { get; set; }

        public bool Key { get; set; }

        public StdVideoH264PictureType Type { get; set; }

        public Input? Input { get; set; }
    }

    /// <summary>
    /// The video profile and the structs chained to it, in native memory: images, buffers and the session are made against
    /// pointers to them.
    /// </summary>
    private struct ProfileBlock
    {
        public VkVideoProfileInfoKHR Profile;
        public VkVideoEncodeH264ProfileInfoKHR H264;
        public VkVideoEncodeUsageInfoKHR Usage;
        public VkVideoProfileListInfoKHR List;

        /// <summary>
        /// Fills the block. The usage info is always chained: without it drivers may ignore the quality level and use their
        /// slowest preset (found in the G0 spike).
        /// </summary>
        public static void Fill(ProfileBlock* self, StdVideoH264ProfileIdc profileIdc, bool realtime)
        {
            self->Usage = new VkVideoEncodeUsageInfoKHR
            {
                videoUsageHints = realtime ? VkVideoEncodeUsageFlagBitsKHR.STREAMING_BIT_KHR : VkVideoEncodeUsageFlagBitsKHR.TRANSCODING_BIT_KHR,
                videoContentHints = VkVideoEncodeContentFlagBitsKHR.DEFAULT_KHR,
                tuningMode = realtime ? VkVideoEncodeTuningModeKHR.LOW_LATENCY_KHR : VkVideoEncodeTuningModeKHR.HIGH_QUALITY_KHR,
            };
            self->H264 = new VkVideoEncodeH264ProfileInfoKHR { pNext = &self->Usage, stdProfileIdc = profileIdc };
            self->Profile = new VkVideoProfileInfoKHR
            {
                pNext = &self->H264,
                videoCodecOperation = VkVideoCodecOperationFlagBitsKHR.ENCODE_H264_BIT_KHR,
                chromaSubsampling = VkVideoChromaSubsamplingFlagBitsKHR._420_BIT_KHR,
                lumaBitDepth = VkVideoComponentBitDepthFlagBitsKHR._8_BIT_KHR,
                chromaBitDepth = VkVideoComponentBitDepthFlagBitsKHR._8_BIT_KHR,
            };
            self->List = new VkVideoProfileListInfoKHR { profileCount = 1, pProfiles = &self->Profile };
        }
    }

    /// <summary>The capabilities of the profile on the device.</summary>
    private struct Capabilities
    {
        public VkVideoCapabilitiesKHR Video;
        public VkVideoEncodeCapabilitiesKHR Encode;
        public VkVideoEncodeH264CapabilitiesKHR H264;

        public static Capabilities Query(VulkanPhysicalDevice physical, ProfileBlock* profile)
        {
            VkVideoEncodeH264CapabilitiesKHR h264 = new();
            VkVideoEncodeCapabilitiesKHR encode = new() { pNext = &h264 };
            VkVideoCapabilitiesKHR video = new() { pNext = &encode };
            VkResult result = physical.Instance.Commands.vkGetPhysicalDeviceVideoCapabilitiesKHR(physical.Handle, &profile->Profile, &video);
            if (result < 0)
            {
                // Some drivers refuse a tuning mode; ask again with the default one.
                profile->Usage.tuningMode = VkVideoEncodeTuningModeKHR.DEFAULT_KHR;
                result = physical.Instance.Commands.vkGetPhysicalDeviceVideoCapabilitiesKHR(physical.Handle, &profile->Profile, &video);
            }

            VkCheck.ThrowIfFailed(result, "vkGetPhysicalDeviceVideoCapabilitiesKHR");
            video.pNext = null;
            encode.pNext = null;
            return new Capabilities { Video = video, Encode = encode, H264 = h264 };
        }

        /// <summary>The settings the driver prefers at a quality level; zeros where it can't say.</summary>
        public static VkVideoEncodeH264QualityLevelPropertiesKHR Preferred(VulkanPhysicalDevice physical, ProfileBlock* profile, uint qualityLevel)
        {
            VkVideoEncodeH264QualityLevelPropertiesKHR h264 = new();
            VkVideoEncodeQualityLevelPropertiesKHR properties = new() { pNext = &h264 };
            VkPhysicalDeviceVideoEncodeQualityLevelInfoKHR info = new() { pVideoProfile = &profile->Profile, qualityLevel = qualityLevel };
            VkResult result = physical.Instance.Commands.vkGetPhysicalDeviceVideoEncodeQualityLevelPropertiesKHR(physical.Handle, &info, &properties);
            h264.pNext = null;
            return result < 0 ? default : h264;
        }
    }
}
