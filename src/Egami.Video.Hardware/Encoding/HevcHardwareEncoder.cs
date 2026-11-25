using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Hevc.Encoder;
using Egami.Video.Hardware.Vulkan;

using Vortice.Vulkan;

namespace Egami.Video.Hardware.Encoding;

/// <summary>
/// Encodes HEVC on the GPU's video engine through Vulkan Video (<c>VK_KHR_video_encode_h265</c>). Egami plans the pictures
/// (IDR, P and, where the GPU codes them, non-reference B pictures in closed runs), keeps the references with an explicit
/// short-term reference picture set per picture and fills the parameter sets and picture headers; the driver codes the slice
/// segments and writes the VPS, SPS and PPS from Egami's values. Frames are uploaded as NV12 and up to <see cref="InFlight"/>
/// pictures run on the GPU while the next ones are prepared.
/// </summary>
internal sealed unsafe class HevcHardwareEncoder : IVideoEncoder
{
    /// <summary>The pictures that may be on the GPU at once.</summary>
    internal const int InFlight = 4;

    /// <summary>The quality when the options leave it open, as Egami's software HEVC encoder's.</summary>
    internal const int DefaultQuality = 60;

    /// <summary>The effort when the options leave it open: the driver's best quality level on drivers with five or fewer.</summary>
    internal const int DefaultEffort = 4;

    private const int QueryStatusInsufficientBuffer = -1000299000;

    private readonly VideoEncoderOptions _options;
    private readonly HevcEncoderOptions _hevc;
    private readonly VulkanDevice _device;
    private readonly VulkanQueue _queue;
    private ProfileBlock* _profile;
    private VulkanCommandPool _pool = null!;
    private VulkanVideoSession _session = null!;
    private VulkanObjectHandle _queryPool = null!;
    private VulkanBuffer _bitstream = null!;
    private VulkanImage[] _dpb = null!;
    private readonly Job[] _jobs = new Job[InFlight];
    private readonly Queue<int> _submitted = new();
    private readonly Queue<EncodedPacket> _ready = new();
    private readonly Stack<VulkanBuffer> _freeStaging = new();
    private readonly List<Input> _pending = [];
    private readonly List<Reference> _references = [];
    private readonly Queue<MediaTime> _shownTimes = new();
    private uint _codedWidth;
    private uint _codedHeight;
    private ulong _regionSize;
    private readonly int _keyInterval;
    private int _bFrames;
    private int _pReferences;
    private int _maxRefFrames;
    private int _levelIndex;
    private int _ctbLog2;
    private int _minTbLog2;
    private int _maxTbLog2;
    private readonly int _qp;
    private int _qpOffsetI;
    private int _qpOffsetB;
    private uint _qualityLevel;
    private readonly VkVideoEncodeRateControlModeFlagsKHR _rateMode;
    private readonly Rational _timeBase;
    private readonly int _effort;
    private readonly long _bitrate;
    private readonly double _fps;
    private int _depth;
    private VulkanObjectHandle? _parameters;
    private byte[] _vps = [];
    private byte[] _sps = [];
    private byte[] _pps = [];
    private int _index;
    private int _lastKey;
    private int _idrIndex;
    private int _nextJob;
    private long _packets;
    private MediaTime _firstTime;
    private bool _started;
    private bool _rateSet;
    private bool _dpbReady;
    private bool _disposed;

    public HevcHardwareEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Width, 2, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Height, 2, nameof(options));
        if ((options.Width & 1) != 0 || (options.Height & 1) != 0)
        {
            throw new ArgumentException($"HEVC 4:2:0 needs an even width and height; {options.Width}x{options.Height} is not.", nameof(options));
        }

        if (options.FrameRate.Numerator <= 0 || options.FrameRate.Denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FrameRate, "The frame rate must be positive.");
        }

        int quality = Check(options.Quality ?? DefaultQuality, 0, 100, "Quality");
        int effort = Check(options.Effort ?? DefaultEffort, 0, 10, "Effort");
        HevcEncoderOptions hevc = options.Hevc ?? HevcEncoderOptions.Default;
        if (hevc.MinQuantizer > hevc.MaxQuantizer)
        {
            throw new ArgumentException($"The smallest QP {hevc.MinQuantizer} is above the largest {hevc.MaxQuantizer}.", nameof(options));
        }

        if (options.FirstPassStats is not null)
        {
            throw new ArgumentException("The hardware encoder codes in one pass; it takes no first-pass statistics.", nameof(options));
        }

        long bitrate = 0;
        _rateMode = options.RateControl switch
        {
            RateControlMode.Quality or RateControlMode.Quantizer => VkVideoEncodeRateControlModeFlagsKHR.Disabled,
            RateControlMode.Bitrate => VkVideoEncodeRateControlModeFlagsKHR.Vbr,
            RateControlMode.ConstantBitrate => VkVideoEncodeRateControlModeFlagsKHR.Cbr,
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.RateControl, "Unknown rate control mode."),
        };
        if (options.RateControl is RateControlMode.Bitrate or RateControlMode.ConstantBitrate)
        {
            bitrate = options.Bitrate is > 0 and var b ? b : throw new ArgumentException($"{options.RateControl} needs a positive Bitrate.", nameof(options));
        }

        _options = options;
        _hevc = hevc;
        _effort = effort;
        _bitrate = bitrate;
        _depth = hevc.Profile == HevcProfile.Main10 ? 10 : 8;
        _timeBase = options.FrameRate.Reciprocal();
        _qp = Math.Clamp(options.Quantizer is int q ? Check(q, 0, 51, "Quantizer") : QualityToQp(quality), hevc.MinQuantizer, hevc.MaxQuantizer);
        _fps = (double)options.FrameRate.Numerator / options.FrameRate.Denominator;
        _keyInterval = options.KeyframeInterval is int k ? Check(k, 1, int.MaxValue, "KeyframeInterval") : Math.Max(1, (int)Math.Ceiling(_fps * 10));

        _device = VulkanVideoContext.GetDevice(VkVideoCodecOperationFlagsKHR.EncodeH265, "Main");
        _queue = _device.EncodeQueue ?? throw new HardwareVideoException($"{_device.Physical.Name} has no video encode queue.");
        if ((_device.Physical.QueueFamilies[_queue.Family].Flags & VkQueueFlags.Transfer) == 0)
        {
            throw new HardwareVideoException($"{_device.Physical.Name}'s encode queue cannot copy frames to the GPU (no transfer support).");
        }

        Initialize();
        Track = MakeTrack(H264HardwareEncoder.DefaultColor, null);
    }

    /// <summary>Makes the GPU resources for the bit depth coded: the profile, session, DPB, buffers and jobs.</summary>
    private void Initialize()
    {
        VideoEncoderOptions options = _options;
        _profile = (ProfileBlock*)NativeMemory.AllocZeroed((nuint)sizeof(ProfileBlock));
        try
        {
            ProfileBlock.Fill(_profile, options.LatencyMode == LatencyMode.Realtime, _depth);
            Capabilities caps = Capabilities.Query(_device.Physical, _profile);
            CheckSize(caps.Video, options.Width, options.Height);
            if ((caps.Encode.rateControlModes & _rateMode) == 0)
            {
                throw new NotSupportedException($"{_device.Physical.Name} has no {options.RateControl} rate control for HEVC (it has {caps.Encode.rateControlModes}).");
            }

            // The largest coding tree block and transform sizes the GPU codes, coding blocks from 8x8.
            _ctbLog2 = (caps.H265.ctbSizes & VkVideoEncodeH265CtbSizeFlagsKHR.Size64) != 0 ? 6
                : (caps.H265.ctbSizes & VkVideoEncodeH265CtbSizeFlagsKHR.Size32) != 0 ? 5 : 4;
            VkVideoEncodeH265TransformBlockSizeFlagsKHR tbs = caps.H265.transformBlockSizes;
            _minTbLog2 = (tbs & VkVideoEncodeH265TransformBlockSizeFlagsKHR.Size4) != 0 ? 2 : (tbs & VkVideoEncodeH265TransformBlockSizeFlagsKHR.Size8) != 0 ? 3 : 4;
            _maxTbLog2 = Math.Min(_ctbLog2, (tbs & VkVideoEncodeH265TransformBlockSizeFlagsKHR.Size32) != 0 ? 5 : (tbs & VkVideoEncodeH265TransformBlockSizeFlagsKHR.Size16) != 0 ? 4 : 3);
            // Pictures below the GPU's smallest are coded at that size and cropped by the conformance window.
            uint alignX = Math.Max(8u, caps.Video.pictureAccessGranularity.width), alignY = Math.Max(8u, caps.Video.pictureAccessGranularity.height);
            _codedWidth = AlignUp(Math.Max((uint)options.Width, caps.Video.minCodedExtent.width), alignX);
            _codedHeight = AlignUp(Math.Max((uint)options.Height, caps.Video.minCodedExtent.height), alignY);
            _qualityLevel = H264HardwareEncoder.QualityLevel(_effort, caps.Encode.maxQualityLevels);

            // The QP spacing of the picture types follows the driver's preferred constant QPs at this quality level.
            VkVideoEncodeH265QualityLevelPropertiesKHR preferred = Capabilities.Preferred(_device.Physical, _profile, _qualityLevel);
            (_qpOffsetI, _qpOffsetB) = QpOffsets(preferred.preferredConstantQp);

            // B pictures only when asked for and the GPU codes them, as the hardware H.264 encoder.
            bool canB = caps.H265.maxBPictureL0ReferenceCount > 0 && caps.H265.maxL1ReferenceCount > 0;
            int bFrames = options.LatencyMode != LatencyMode.Quality || !canB ? 0 : _hevc.BFrames ?? 0;
            _bFrames = Math.Clamp(bFrames, 0, 16);
            _pReferences = (int)Math.Clamp(Math.Min(caps.H265.maxPPictureL0ReferenceCount, (uint)H264HardwareEncoder.RefFrames(_effort)), 1, 15);
            long peak = _rateMode switch
            {
                VkVideoEncodeRateControlModeFlagsKHR.Cbr => _bitrate,
                VkVideoEncodeRateControlModeFlagsKHR.Vbr => options.MaxBitrate is > 0 and var m ? Math.Max(m, _bitrate) : _bitrate * 2,
                _ => 0,
            };
            long buffer = _rateMode == VkVideoEncodeRateControlModeFlagsKHR.Disabled ? 0 : options.BufferSize is > 0 and var size ? size : _bitrate;
            HevcLevels.Demand demand = new(_fps, peak, buffer);
            int level = _hevc.Level ?? HevcLevels.Choose(options.Width, options.Height, demand);
            if (HevcLevels.Misfit(HevcLevels.Of(level), options.Width, options.Height, demand) is { } why)
            {
                throw new ArgumentException($"The stream does not fit its level: {why}.", nameof(options));
            }

            // References as far as the level's buffer holds them, the current picture included.
            int maxRef = Math.Max(_pReferences, _bFrames > 0 ? 2 : 1);
            maxRef = Math.Min(maxRef, HevcLevels.MaxDpbSize(HevcLevels.Of(level), options.Width, options.Height) - 1);
            if (maxRef < 2)
            {
                _bFrames = 0;
            }

            _pReferences = Math.Min(_pReferences, maxRef);
            _maxRefFrames = maxRef;
            _levelIndex = LevelIndex(level);
            if (_levelIndex > (int)caps.H265.maxLevelIdc)
            {
                throw new NotSupportedException($"{_device.Physical.Name} encodes HEVC up to level index {(int)caps.H265.maxLevelIdc}; this stream needs level {level / 10.0:0.0}.");
            }

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
                pictureFormat = PictureFormat,
                maxCodedExtent = new VkExtent2D { width = _codedWidth, height = _codedHeight },
                referencePictureFormat = PictureFormat,
                maxDpbSlots = slots,
                maxActiveReferencePictures = (uint)_maxRefFrames,
                pStdHeaderVersion = &header,
            };
            _session = _device.CreateVideoSession(sessionInfo);
            _pool = _device.CreateCommandPool(_queue);
            _dpb = new VulkanImage[slots];
            for (int i = 0; i < slots; i++)
            {
                _dpb[i] = CreatePicture(VkImageUsageFlags.VideoEncodeDpbKHR);
            }

            // Each picture in flight has its own share of the bitstream buffer: room for a raw picture and a margin, which no
            // coded picture reaches.
            ulong alignment = Math.Max(caps.Video.minBitstreamBufferOffsetAlignment, caps.Video.minBitstreamBufferSizeAlignment);
            _regionSize = AlignUp((ulong)PictureBytes + (1 << 20), Math.Max(alignment, 1));
            _bitstream = _device.CreateBuffer(
                _regionSize * InFlight, VkBufferUsageFlags.VideoEncodeDstKHR, VkMemoryPropertyFlags.HostVisible, &_profile->List);
            VkQueryPoolVideoEncodeFeedbackCreateInfoKHR feedback = new()
            {
                pNext = &_profile->Profile,
                encodeFeedbackFlags = VkVideoEncodeFeedbackFlagsKHR.BitstreamBufferOffset | VkVideoEncodeFeedbackFlagsKHR.BitstreamBytesWritten,
            };
            VkQueryPoolCreateInfo queryInfo = new() { pNext = &feedback, queryType = VkQueryType.VideoEncodeFeedbackKHR, queryCount = InFlight };
            VkQueryPool queryPool;
            VkCheck.ThrowIfFailed(_device.Commands.vkCreateQueryPool(&queryInfo, null, &queryPool), "vkCreateQueryPool");
            _queryPool = new VulkanObjectHandle(_device.Handle, queryPool, _device.Commands.vkDestroyQueryPool_ptr);
            for (int i = 0; i < InFlight; i++)
            {
                _jobs[i] = new Job(CreatePicture(VkImageUsageFlags.VideoEncodeSrcKHR | VkImageUsageFlags.TransferDst), _pool.Allocate(), _device.CreateFence());
            }
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

    public VideoTrackInfo Track { get; private set; }

    /// <summary>Gets the bit depth coded: 8 for Main, 10 for Main 10.</summary>
    internal int BitDepth => _depth;

    /// <summary>Gets the driver's quality level in use, for tests.</summary>
    internal uint QualityLevelInUse => _qualityLevel;

    /// <summary>Gets the B-frames between reference frames, for tests.</summary>
    internal int BFrames => _bFrames;

    /// <summary>Gets the reference pictures the stream keeps, for tests.</summary>
    internal int MaxRefFrames => _maxRefFrames;

    /// <summary>Gets whether the driver changed Egami's VPS, SPS or PPS values, for tests.</summary>
    internal bool ParametersOverridden { get; private set; }

    /// <summary>Gets the SPS the driver wrote, NAL header included, for tests.</summary>
    internal byte[] Sps => _sps;

    /// <summary>The QP for a quality of 0 to 100, on the scale of Egami's software HEVC encoder: 51 at 0, 17 at 60.</summary>
    internal static int QualityToQp(int quality) => Math.Clamp((int)Math.Round(51 - (0.56 * quality)), 0, 51);

    /// <summary>
    /// The QP of I and B pictures relative to P pictures: the spacing of the driver's preferred constant QPs, or -2 and +2
    /// where it states none that make sense.
    /// </summary>
    internal static (int I, int B) QpOffsets(VkVideoEncodeH265QpKHR preferred)
    {
        int i = preferred.qpI - preferred.qpP, b = preferred.qpB - preferred.qpP;
        return preferred.qpP > 0 && i is >= -6 and <= 0 && b is >= 0 and <= 6 ? (i, b) : (-2, 2);
    }

    /// <summary>The Std level enumerator of a level number (ten times the level).</summary>
    internal static int LevelIndex(int level) => level switch
    {
        <= 10 => 0,
        <= 20 => 1,
        <= 21 => 2,
        <= 30 => 3,
        <= 31 => 4,
        <= 40 => 5,
        <= 41 => 6,
        <= 50 => 7,
        <= 51 => 8,
        <= 52 => 9,
        <= 60 => 10,
        <= 61 => 11,
        _ => 12,
    };

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
            // As the software encoder: with no profile asked for, frames of 16-bit words are coded as Main 10.
            if (_hevc.Profile is null && _depth == 8 && frame.Planes is YuvRaster<ushort>)
            {
                DisposeResources();
                _depth = 10;
                Initialize();
            }

            ColorCodePoints color = ColorOf(frame, _depth);
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
            Code(input, StdVideoH265PictureType.Idr);
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
            Collect(_submitted.Dequeue());
        }
    }

    public bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_submitted.Count > 0 && _device.Commands.vkGetFenceStatus(_jobs[_submitted.Peek()].Fence.Value) == VkResult.Success)
        {
            Collect(_submitted.Dequeue());
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

        Code(_pending[^1], StdVideoH265PictureType.P);
        for (int i = 0; i < _pending.Count - 1; i++)
        {
            Code(_pending[i], StdVideoH265PictureType.B);
        }

        _pending.Clear();
    }

    /// <summary>Records and submits one picture, collecting the oldest one first when every job is busy.</summary>
    private void Code(Input input, StdVideoH265PictureType type)
    {
        int jobIndex = _nextJob;
        _nextJob = (_nextJob + 1) % InFlight;
        Job job = _jobs[jobIndex];
        if (job.Busy)
        {
            Collect(_submitted.Dequeue());
        }

        bool idr = type == StdVideoH265PictureType.Idr;
        if (idr)
        {
            _idrIndex = input.Index;
            _references.Clear();
        }

        int poc = input.Index - _idrIndex;
        int setupSlot = FreeSlot();
        Record(job, jobIndex, input, type, poc, setupSlot);
        _device.Submit(_queue, job.Commands, job.Fence);
        job.Busy = true;
        job.Input = input;
        job.Key = idr;
        job.Type = type;
        _submitted.Enqueue(jobIndex);

        if (type != StdVideoH265PictureType.B)
        {
            _references.Insert(0, new Reference(setupSlot, poc, type));
            if (_references.Count > _maxRefFrames)
            {
                _references.RemoveAt(_references.Count - 1);
            }
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

    private void Record(Job job, int jobIndex, Input input, StdVideoH265PictureType type, int poc, int setupSlot)
    {
        VkDeviceApi vk = _device.Commands;
        VkCommandBuffer cb = job.Commands;
        _pool.Begin(cb);
        vk.vkCmdResetQueryPool(cb, _queryPool.Value, (uint)jobIndex, 1);

        // Upload: staging buffer to the source picture.
        VkImageMemoryBarrier2* barriers = stackalloc VkImageMemoryBarrier2[1 + 17];
        barriers[0] = Barrier(job.Source.Handle, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags2.VideoEncodeKHR, 0, VkPipelineStageFlags2.AllTransfer, VkAccessFlags2.TransferWrite);
        PipelineBarrier(cb, barriers, 1, null);
        VkBufferImageCopy* regions = stackalloc VkBufferImageCopy[2];
        regions[0] = new VkBufferImageCopy
        {
            imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Plane0, layerCount = 1 },
            imageExtent = new VkExtent3D { width = _codedWidth, height = _codedHeight, depth = 1 },
        };
        regions[1] = new VkBufferImageCopy
        {
            bufferOffset = (ulong)_codedWidth * _codedHeight * (ulong)(_depth == 10 ? 2 : 1),
            imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Plane1, layerCount = 1 },
            imageExtent = new VkExtent3D { width = _codedWidth / 2, height = _codedHeight / 2, depth = 1 },
        };
        vk.vkCmdCopyBufferToImage(cb, input.Staging.Handle, job.Source.Handle, VkImageLayout.TransferDstOptimal, 2, regions);

        int count = 0;
        barriers[count++] = Barrier(job.Source.Handle, VkImageLayout.TransferDstOptimal, VkImageLayout.VideoEncodeSrcKHR, VkPipelineStageFlags2.AllTransfer, VkAccessFlags2.TransferWrite, VkPipelineStageFlags2.VideoEncodeKHR, VkAccessFlags2.VideoEncodeReadKHR);
        if (!_dpbReady)
        {
            _dpbReady = true;
            for (int i = 0; i < _dpb.Length; i++)
            {
                barriers[count++] = Barrier(_dpb[i].Handle, VkImageLayout.Undefined, VkImageLayout.VideoEncodeDpbKHR, VkPipelineStageFlags2.None, 0, VkPipelineStageFlags2.VideoEncodeKHR, VkAccessFlags2.VideoEncodeReadKHR | VkAccessFlags2.VideoEncodeWriteKHR);
            }
        }

        // The pictures coded before this one wrote the references it reads.
        VkMemoryBarrier2 dpbHazard = new()
        {
            srcStageMask = VkPipelineStageFlags2.VideoEncodeKHR,
            srcAccessMask = VkAccessFlags2.VideoEncodeWriteKHR,
            dstStageMask = VkPipelineStageFlags2.VideoEncodeKHR,
            dstAccessMask = VkAccessFlags2.VideoEncodeReadKHR | VkAccessFlags2.VideoEncodeWriteKHR,
        };
        PipelineBarrier(cb, barriers, count, &dpbHazard);

        // The references this picture uses: P pictures the most recent ones, B pictures the reference shown before and after.
        bool idr = type == StdVideoH265PictureType.Idr;
        Span<int> l0 = stackalloc int[15];
        Span<int> l1 = stackalloc int[1];
        int l0Count = 0, l1Count = 0;
        if (type == StdVideoH265PictureType.P)
        {
            l0Count = Math.Min(_pReferences, _references.Count);
            for (int i = 0; i < l0Count; i++)
            {
                l0[i] = i;
            }
        }
        else if (type == StdVideoH265PictureType.B)
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

        // The short-term reference picture set: every reference kept, nearest first on each side, marked used where a list
        // names it. References left out of it would leave the decoder's buffer.
        StdVideoH265ShortTermRefPicSet rps = default;
        if (!idr)
        {
            List<int> negative = [], positive = [];
            for (int i = 0; i < _references.Count; i++)
            {
                (_references[i].Poc < poc ? negative : positive).Add(i);
            }

            negative.Sort((a, b) => _references[b].Poc.CompareTo(_references[a].Poc));
            positive.Sort((a, b) => _references[a].Poc.CompareTo(_references[b].Poc));
            rps.num_negative_pics = (byte)negative.Count;
            rps.num_positive_pics = (byte)positive.Count;
            int previous = poc;
            for (int j = 0; j < negative.Count; j++)
            {
                int refPoc = _references[negative[j]].Poc;
                rps.delta_poc_s0_minus1[j] = (ushort)(previous - refPoc - 1);
                previous = refPoc;
                if (l0[..l0Count].Contains(negative[j]) || l1[..l1Count].Contains(negative[j]))
                {
                    rps.used_by_curr_pic_s0_flag |= (ushort)(1 << j);
                }
            }

            previous = poc;
            for (int j = 0; j < positive.Count; j++)
            {
                int refPoc = _references[positive[j]].Poc;
                rps.delta_poc_s1_minus1[j] = (ushort)(refPoc - previous - 1);
                previous = refPoc;
                if (l0[..l0Count].Contains(positive[j]) || l1[..l1Count].Contains(positive[j]))
                {
                    rps.used_by_curr_pic_s1_flag |= (ushort)(1 << j);
                }
            }
        }

        int refCount = _references.Count;
        StdVideoEncodeH265ReferenceInfo* refInfos = stackalloc StdVideoEncodeH265ReferenceInfo[Math.Max(refCount, 1)];
        VkVideoEncodeH265DpbSlotInfoKHR* refSlotInfos = stackalloc VkVideoEncodeH265DpbSlotInfoKHR[Math.Max(refCount, 1)];
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
            refInfos[i] = new StdVideoEncodeH265ReferenceInfo { pic_type = r.Type, PicOrderCntVal = r.Poc };
            refSlotInfos[i] = new VkVideoEncodeH265DpbSlotInfoKHR { pStdReferenceInfo = &refInfos[i] };
            if (l0[..l0Count].Contains(i) || l1[..l1Count].Contains(i))
            {
                refSlots[used++] = new VkVideoReferenceSlotInfoKHR { pNext = &refSlotInfos[i], slotIndex = r.Slot, pPictureResource = &resources[i + 2] };
            }
        }

        // Rate control: set once with a reset before the first picture, then stated with every picture.
        VkVideoEncodeRateControlLayerInfoKHR layer = default;
        VkVideoEncodeH265RateControlLayerInfoKHR h265Layer = default;
        VkVideoEncodeH265RateControlInfoKHR h265Rate = default;
        VkVideoEncodeRateControlInfoKHR rate = default;
        RateControl(&rate, &h265Rate, &layer, &h265Layer);
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
                flags = VkVideoCodingControlFlagsKHR.Reset | VkVideoCodingControlFlagsKHR.EncodeRateControl | VkVideoCodingControlFlagsKHR.EncodeQualityLevel,
            };
            vk.vkCmdControlVideoCodingKHR(cb, &control);
            _rateSet = true;
        }

        // The picture's headers.
        StdVideoEncodeH265ReferenceListsInfo lists = default;
        for (int i = 0; i < 15; i++)
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
        StdVideoEncodeH265PictureInfo picture = new()
        {
            pic_type = type,
            PicOrderCntVal = poc,
            pRefLists = idr ? null : &lists,
            pShortTermRefPicSet = idr ? null : &rps,
        };
        picture.flags.is_reference = VulkanBits.Of(type != StdVideoH265PictureType.B);
        picture.flags.IrapPicFlag = VulkanBits.Of(idr);
        picture.flags.pic_output_flag = VulkanBits.Of(true);
        picture.flags.slice_temporal_mvp_enabled_flag = VulkanBits.Of(!idr);
        StdVideoH265SliceType sliceType = type switch
        {
            StdVideoH265PictureType.B => StdVideoH265SliceType.B,
            StdVideoH265PictureType.P => StdVideoH265SliceType.P,
            _ => StdVideoH265SliceType.I,
        };
        StdVideoEncodeH265SliceSegmentHeader slice = new() { slice_type = sliceType, MaxNumMergeCand = 5 };
        slice.flags.first_slice_segment_in_pic_flag = VulkanBits.Of(true);
        slice.flags.slice_sao_luma_flag = VulkanBits.Of(true);
        slice.flags.slice_sao_chroma_flag = VulkanBits.Of(true);
        slice.flags.collocated_from_l0_flag = VulkanBits.Of(true);
        slice.flags.slice_loop_filter_across_slices_enabled_flag = VulkanBits.Of(true);
        slice.flags.num_ref_idx_active_override_flag = VulkanBits.Of((sliceType == StdVideoH265SliceType.P && l0Count != 1) || (sliceType == StdVideoH265SliceType.B && (l0Count != 1 || l1Count != 1)));
        int qp = Math.Clamp(sliceType switch { StdVideoH265SliceType.I => _qp + _qpOffsetI, StdVideoH265SliceType.B => _qp + _qpOffsetB, _ => _qp }, _hevc.MinQuantizer, _hevc.MaxQuantizer);
        VkVideoEncodeH265NaluSliceSegmentInfoKHR sliceInfo = new()
        {
            constantQp = _rateMode == VkVideoEncodeRateControlModeFlagsKHR.Disabled ? qp : 0,
            pStdSliceSegmentHeader = &slice,
        };
        VkVideoEncodeH265PictureInfoKHR h265Picture = new() { naluSliceSegmentEntryCount = 1, pNaluSliceSegmentEntries = &sliceInfo, pStdPictureInfo = &picture };
        StdVideoEncodeH265ReferenceInfo setupInfo = new() { pic_type = type, PicOrderCntVal = poc };
        VkVideoEncodeH265DpbSlotInfoKHR setupSlotInfo = new() { pStdReferenceInfo = &setupInfo };
        VkVideoReferenceSlotInfoKHR setup = new() { pNext = &setupSlotInfo, slotIndex = setupSlot, pPictureResource = &resources[0] };
        VkVideoEncodeInfoKHR encode = new()
        {
            pNext = &h265Picture,
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
            srcStageMask = VkPipelineStageFlags2.VideoEncodeKHR,
            srcAccessMask = VkAccessFlags2.VideoEncodeWriteKHR,
            dstStageMask = VkPipelineStageFlags2.Host,
            dstAccessMask = VkAccessFlags2.HostRead,
        };
        PipelineBarrier(cb, null, 0, &toHost);
        _pool.End(cb);
    }

    /// <summary>Fills the rate control state: the QP per slice segment (disabled), or the driver's constant or variable bitrate.</summary>
    private void RateControl(VkVideoEncodeRateControlInfoKHR* rate, VkVideoEncodeH265RateControlInfoKHR* h265Rate, VkVideoEncodeRateControlLayerInfoKHR* layer, VkVideoEncodeH265RateControlLayerInfoKHR* h265Layer)
    {
        *rate = new VkVideoEncodeRateControlInfoKHR { rateControlMode = _rateMode };
        if (_rateMode == VkVideoEncodeRateControlModeFlagsKHR.Disabled)
        {
            return;
        }

        long bitrate = _options.Bitrate!.Value;
        long peak = _rateMode == VkVideoEncodeRateControlModeFlagsKHR.Cbr
            ? bitrate
            : _options.MaxBitrate is > 0 and var m ? Math.Max(m, bitrate) : bitrate * 2;
        long buffer = _options.BufferSize is > 0 and var size ? size : bitrate;
        *h265Layer = new VkVideoEncodeH265RateControlLayerInfoKHR();
        if (_hevc.MinQuantizer > 0)
        {
            h265Layer->useMinQp = true;
            h265Layer->minQp = new VkVideoEncodeH265QpKHR { qpI = _hevc.MinQuantizer, qpP = _hevc.MinQuantizer, qpB = _hevc.MinQuantizer };
        }

        if (_hevc.MaxQuantizer < 51)
        {
            h265Layer->useMaxQp = true;
            h265Layer->maxQp = new VkVideoEncodeH265QpKHR { qpI = _hevc.MaxQuantizer, qpP = _hevc.MaxQuantizer, qpB = _hevc.MaxQuantizer };
        }

        *layer = new VkVideoEncodeRateControlLayerInfoKHR
        {
            pNext = h265Layer,
            averageBitrate = (ulong)bitrate,
            maxBitrate = (ulong)peak,
            frameRateNumerator = (uint)_options.FrameRate.Numerator,
            frameRateDenominator = (uint)_options.FrameRate.Denominator,
        };
        *h265Rate = new VkVideoEncodeH265RateControlInfoKHR
        {
            flags = VkVideoEncodeH265RateControlFlagsKHR.RegularGop | VkVideoEncodeH265RateControlFlagsKHR.ReferencePatternFlat,
            gopFrameCount = (uint)_keyInterval,
            idrPeriod = (uint)_keyInterval,
            consecutiveBFrameCount = (uint)_bFrames,
            subLayerCount = 1,
        };
        uint bufferMs = (uint)Math.Clamp(buffer * 1000 / bitrate, 1, uint.MaxValue);
        rate->pNext = h265Rate;
        rate->layerCount = 1;
        rate->pLayers = layer;
        rate->virtualBufferSizeInMs = bufferMs;
        rate->initialVirtualBufferSizeInMs = bufferMs / 2;
    }

    /// <summary>Waits for a job, takes its packet out of the bitstream buffer and frees the job.</summary>
    private void Collect(int jobIndex)
    {
        Job job = _jobs[jobIndex];
        _device.WaitAndReset(job.Fence);
        uint* result = stackalloc uint[3];
        VkCheck.ThrowIfFailed(
            _device.Commands.vkGetQueryPoolResults(_queryPool.Value, (uint)jobIndex, 1, 12, result, 12, VkQueryResultFlags.Wait | VkQueryResultFlags.WithStatusKHR),
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
        packet.IsDiscardable = job.Type == StdVideoH265PictureType.B;
        if (_bFrames > 0)
        {
            packet.DecodeTimestamp = _packets < 1 ? new MediaTime(_firstTime.Ticks - 1, _timeBase) : _shownTimes.Dequeue();
        }

        _packets++;
        _ready.Enqueue(packet);
    }

    private EncodedPacket MakePacket(ReadOnlySpan<byte> coded, bool key, Input input)
    {
        EncodedPacket packet;
        if (_hevc.AnnexB)
        {
            int extra = key ? 12 + _vps.Length + _sps.Length + _pps.Length : 0;
            packet = EncodedPacket.Rent(1, extra + coded.Length, input.Timestamp);
            Span<byte> data = packet.GetWritableData().Span;
            if (key)
            {
                WriteStartCoded(_vps, data);
                WriteStartCoded(_sps, data[(4 + _vps.Length)..]);
                WriteStartCoded(_pps, data[(8 + _vps.Length + _sps.Length)..]);
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
    /// Makes the session parameters from Egami's VPS, SPS and PPS values and has the driver write them, with whatever it changed.
    /// </summary>
    private void CreateParameters(ColorCodePoints color)
    {
        StdVideoH265ProfileTierLevel ptl = new()
        {
            general_profile_idc = _depth == 10 ? StdVideoH265ProfileIdc.Main10 : StdVideoH265ProfileIdc.Main,
            general_level_idc = (StdVideoH265LevelIdc)_levelIndex,
        };
        ptl.flags.general_progressive_source_flag = VulkanBits.Of(true);
        ptl.flags.general_frame_only_constraint_flag = VulkanBits.Of(true);
        StdVideoH265DecPicBufMgr dpb = default;
        dpb.max_dec_pic_buffering_minus1[0] = (byte)_maxRefFrames;
        dpb.max_num_reorder_pics[0] = (byte)(_bFrames > 0 ? 1 : 0);

        StdVideoH265VideoParameterSet vps = new() { pDecPicBufMgr = &dpb, pProfileTierLevel = &ptl };
        vps.flags.vps_temporal_id_nesting_flag = VulkanBits.Of(true);
        vps.flags.vps_sub_layer_ordering_info_present_flag = VulkanBits.Of(true);

        StdVideoH265SequenceParameterSetVui vui = new()
        {
            aspect_ratio_idc = StdVideoH265AspectRatioIdc.Square,
            sar_width = 1,
            sar_height = 1,
            video_format = 5,
            colour_primaries = color.Primaries,
            transfer_characteristics = color.TransferCharacteristics,
            matrix_coeffs = color.MatrixCoefficients,
            vui_num_units_in_tick = (uint)_options.FrameRate.Denominator,
            vui_time_scale = (uint)_options.FrameRate.Numerator,
        };
        vui.flags.aspect_ratio_info_present_flag = VulkanBits.Of(true);
        vui.flags.video_signal_type_present_flag = VulkanBits.Of(true);
        vui.flags.video_full_range_flag = VulkanBits.Of(color.FullRange);
        vui.flags.colour_description_present_flag = VulkanBits.Of(true);
        vui.flags.vui_timing_info_present_flag = VulkanBits.Of(true);

        uint cropRight = (_codedWidth - (uint)_options.Width) / 2, cropBottom = (_codedHeight - (uint)_options.Height) / 2;
        StdVideoH265SequenceParameterSet sps = new()
        {
            chroma_format_idc = StdVideoH265ChromaFormatIdc.Idc420,
            bit_depth_luma_minus8 = (byte)(_depth - 8),
            bit_depth_chroma_minus8 = (byte)(_depth - 8),
            pic_width_in_luma_samples = _codedWidth,
            pic_height_in_luma_samples = _codedHeight,
            log2_max_pic_order_cnt_lsb_minus4 = 12,
            log2_min_luma_coding_block_size_minus3 = 0,
            log2_diff_max_min_luma_coding_block_size = (byte)(_ctbLog2 - 3),
            log2_min_luma_transform_block_size_minus2 = (byte)(_minTbLog2 - 2),
            log2_diff_max_min_luma_transform_block_size = (byte)(_maxTbLog2 - _minTbLog2),
            max_transform_hierarchy_depth_inter = (byte)(_ctbLog2 - _minTbLog2),
            max_transform_hierarchy_depth_intra = (byte)(_ctbLog2 - _minTbLog2),
            conf_win_right_offset = cropRight,
            conf_win_bottom_offset = cropBottom,
            pProfileTierLevel = &ptl,
            pDecPicBufMgr = &dpb,
            pSequenceParameterSetVui = &vui,
        };
        sps.flags.sps_temporal_id_nesting_flag = VulkanBits.Of(true);
        sps.flags.conformance_window_flag = VulkanBits.Of(cropRight != 0 || cropBottom != 0);
        sps.flags.sps_sub_layer_ordering_info_present_flag = VulkanBits.Of(true);
        sps.flags.amp_enabled_flag = VulkanBits.Of(true);
        sps.flags.sample_adaptive_offset_enabled_flag = VulkanBits.Of(true);
        sps.flags.sps_temporal_mvp_enabled_flag = VulkanBits.Of(true);
        sps.flags.strong_intra_smoothing_enabled_flag = VulkanBits.Of(true);
        sps.flags.vui_parameters_present_flag = VulkanBits.Of(true);

        StdVideoH265PictureParameterSet pps = new();
        pps.flags.cu_qp_delta_enabled_flag = VulkanBits.Of(_rateMode != VkVideoEncodeRateControlModeFlagsKHR.Disabled);
        pps.flags.pps_loop_filter_across_slices_enabled_flag = VulkanBits.Of(true);

        VkVideoEncodeH265SessionParametersAddInfoKHR add = new() { stdVPSCount = 1, pStdVPSs = &vps, stdSPSCount = 1, pStdSPSs = &sps, stdPPSCount = 1, pStdPPSs = &pps };
        VkVideoEncodeQualityLevelInfoKHR quality = new() { qualityLevel = _qualityLevel };
        VkVideoEncodeH265SessionParametersCreateInfoKHR h265Info = new() { pNext = &quality, maxStdVPSCount = 1, maxStdSPSCount = 1, maxStdPPSCount = 1, pParametersAddInfo = &add };
        VkVideoSessionParametersCreateInfoKHR info = new() { pNext = &h265Info, videoSession = _session.Handle };
        VkVideoSessionParametersKHR raw;
        VkCheck.ThrowIfFailed(_device.Commands.vkCreateVideoSessionParametersKHR(&info, null, &raw), "vkCreateVideoSessionParametersKHR");
        _parameters = new VulkanObjectHandle(_device.Handle, raw, _device.Commands.vkDestroyVideoSessionParametersKHR_ptr);

        VkVideoEncodeH265SessionParametersGetInfoKHR h265Get = new() { writeStdVPS = true, writeStdSPS = true, writeStdPPS = true };
        VkVideoEncodeSessionParametersGetInfoKHR get = new() { pNext = &h265Get, videoSessionParameters = raw };
        VkVideoEncodeH265SessionParametersFeedbackInfoKHR h265Feedback = new();
        VkVideoEncodeSessionParametersFeedbackInfoKHR feedback = new() { pNext = &h265Feedback };
        nuint size = 0;
        VkCheck.ThrowIfFailed(_device.Commands.vkGetEncodedVideoSessionParametersKHR(&get, &feedback, &size, null), "vkGetEncodedVideoSessionParametersKHR");
        byte[] written = new byte[size];
        fixed (byte* p = written)
        {
            VkCheck.ThrowIfFailed(_device.Commands.vkGetEncodedVideoSessionParametersKHR(&get, &feedback, &size, p), "vkGetEncodedVideoSessionParametersKHR");
        }

        foreach (Range unit in NalUnits.SplitAnnexB(written.AsSpan(0, (int)size)))
        {
            byte[] nal = written[unit];
            switch ((nal[0] >> 1) & 63)
            {
                case 32:
                    _vps = nal;
                    break;
                case 33:
                    _sps = nal;
                    break;
                case 34:
                    _pps = nal;
                    break;
            }
        }

        if (_vps.Length == 0 || _sps.Length == 0 || _pps.Length == 0)
        {
            throw new HardwareVideoException("The driver wrote no VPS, SPS or PPS.");
        }

        ParametersOverridden = h265Feedback.hasStdVPSOverrides || h265Feedback.hasStdSPSOverrides || h265Feedback.hasStdPPSOverrides;
    }

    private VideoTrackInfo MakeTrack(ColorCodePoints color, HdrMetadata? hdr) => new()
    {
        Number = 1,
        Kind = TrackKind.Video,
        CodecId = CodecIds.Hevc,
        CodecPrivate = _hevc.AnnexB || _sps.Length == 0 ? ReadOnlyMemory<byte>.Empty : NalUnits.HvcC(_vps, _sps, _pps, _depth),
        TimeBase = _timeBase,
        Width = _options.Width,
        Height = _options.Height,
        FrameRate = _options.FrameRate,
        BitDepth = _depth,
        ChromaFormat = ChromaFormat.Yuv420,
        Color = color,
        Hdr = hdr,
    };

    /// <summary>
    /// The color of a track's frames: 4:2:0 8-bit planes are coded as they are, so their color (the frame's, else the planes')
    /// is the track's; everything else is converted to BT.601 limited range.
    /// </summary>
    private static ColorCodePoints ColorOf(VideoFrame frame, int depth)
    {
        if (frame.Planes is not { ChromaFormat: ChromaFormat.Yuv420 } planes || (planes is YuvRaster<ushort> && depth == 8))
        {
            return H264HardwareEncoder.DefaultColor;
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
        if (_depth == 10 && frame.Planes is { ChromaFormat: ChromaFormat.Yuv420 } yuv420)
        {
            Nv12Writer.WriteP010(yuv420, target, (int)_codedWidth, (int)_codedHeight);
            return;
        }

        if (_depth == 8 && frame.Planes is YuvRaster<byte> { ChromaFormat: ChromaFormat.Yuv420 } planes)
        {
            Nv12Writer.Write(planes, target, (int)_codedWidth, (int)_codedHeight);
            return;
        }

        Raster? converted = null;
        Raster raster = frame.Pixels ?? (converted = frame.Planes!.ToRaster());
        try
        {
            YuvColor color = new(YuvMatrix.Bt601, YuvRange.Limited, ChromaSiting.Left);
            if (_depth == 10)
            {
                using YuvRaster<ushort> words = YuvRaster.FromRaster(raster, ChromaFormat.Yuv420, 10, color, false);
                Nv12Writer.WriteP010(words, target, (int)_codedWidth, (int)_codedHeight);
            }
            else
            {
                using YuvRaster<byte> yuv = YuvRaster.FromRaster(raster, ChromaFormat.Yuv420, color, false);
                Nv12Writer.Write(yuv, target, (int)_codedWidth, (int)_codedHeight);
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private VulkanBuffer RentStaging() =>
        _freeStaging.TryPop(out VulkanBuffer? buffer)
            ? buffer
            : _device.CreateBuffer((ulong)PictureBytes, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible);

    private VulkanImage CreatePicture(VkImageUsageFlags usage)
    {
        VkImageCreateInfo info = new()
        {
            pNext = &_profile->List,
            imageType = VkImageType.Image2D,
            format = PictureFormat,
            extent = new VkExtent3D { width = _codedWidth, height = _codedHeight, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = usage,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        return _device.CreateImage(info);
    }

    private static VkImageMemoryBarrier2 Barrier(
        ulong image, VkImageLayout from, VkImageLayout to, VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess, VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess) => new()
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
            subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.Color, levelCount = 1, layerCount = 1 },
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
        if (width > caps.maxCodedExtent.width || height > caps.maxCodedExtent.height)
        {
            throw new NotSupportedException(
                $"{_device.Physical.Name} encodes HEVC up to {caps.maxCodedExtent.width}x{caps.maxCodedExtent.height}; {width}x{height} is larger.");
        }
    }

    private void DisposeResources()
    {
        for (int i = 0; i < _jobs.Length; i++)
        {
            _jobs[i]?.Source.Dispose();
            _jobs[i]?.Fence.Dispose();
            _jobs[i] = null!;
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
        _parameters = null;
        _queryPool = null!;
        _bitstream = null!;
        _session = null!;
        _pool = null!;
        _dpb = null!;
        if (_profile != null)
        {
            NativeMemory.Free(_profile);
            _profile = null;
        }
    }

    /// <summary>The format of the source and reference pictures: NV12, or P010 for Main 10.</summary>
    private VkFormat PictureFormat => _depth == 10 ? VkFormat.G10X6B10X6R10X62Plane420Unorm3Pack16 : VkFormat.G8B8R82Plane420Unorm;

    /// <summary>The bytes of an uploaded picture of the coded size.</summary>
    private int PictureBytes => Nv12Writer.Size((int)_codedWidth, (int)_codedHeight) * (_depth == 10 ? 2 : 1);

    private static uint AlignUp(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    private static int Check(int value, int min, int max, string name, VideoEncoderOptions? options = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(nameof(options), value, $"{name} must be {min} to {max}.") : value;

    /// <summary>A frame waiting to be coded or on the GPU, with its NV12 copy.</summary>
    private sealed record Input(int Index, MediaTime Timestamp, MediaTime Duration, VulkanBuffer Staging);

    /// <summary>A reference picture: its DPB slot, order count and picture type.</summary>
    private readonly record struct Reference(int Slot, int Poc, StdVideoH265PictureType Type);

    /// <summary>One picture's GPU resources: its source picture, command buffer and fence; the query and bitstream share go by its index.</summary>
    private sealed class Job(VulkanImage source, nint commands, VulkanObjectHandle fence)
    {
        public VulkanImage Source { get; } = source;

        public nint Commands { get; } = commands;

        public VulkanObjectHandle Fence { get; } = fence;

        public bool Busy { get; set; }

        public bool Key { get; set; }

        public StdVideoH265PictureType Type { get; set; }

        public Input? Input { get; set; }
    }

    /// <summary>
    /// The video profile and the structs chained to it, in native memory: images, buffers and the session are made against
    /// pointers to them.
    /// </summary>
    private struct ProfileBlock
    {
        public VkVideoProfileInfoKHR Profile;
        public VkVideoEncodeH265ProfileInfoKHR H265;
        public VkVideoEncodeUsageInfoKHR Usage;
        public VkVideoProfileListInfoKHR List;

        /// <summary>Fills the block, the usage info always chained as for the H.264 encoder.</summary>
        public static void Fill(ProfileBlock* self, bool realtime, int depth)
        {
            self->Usage = new VkVideoEncodeUsageInfoKHR
            {
                videoUsageHints = realtime ? VkVideoEncodeUsageFlagsKHR.Streaming : VkVideoEncodeUsageFlagsKHR.Transcoding,
                videoContentHints = VkVideoEncodeContentFlagsKHR.Default,
                tuningMode = realtime ? VkVideoEncodeTuningModeKHR.LowLatency : VkVideoEncodeTuningModeKHR.HighQuality,
            };
            self->H265 = new VkVideoEncodeH265ProfileInfoKHR { pNext = &self->Usage, stdProfileIdc = depth == 10 ? StdVideoH265ProfileIdc.Main10 : StdVideoH265ProfileIdc.Main };
            self->Profile = new VkVideoProfileInfoKHR
            {
                pNext = &self->H265,
                videoCodecOperation = VkVideoCodecOperationFlagsKHR.EncodeH265,
                chromaSubsampling = VkVideoChromaSubsamplingFlagsKHR.Subsampling420,
                lumaBitDepth = depth == 10 ? VkVideoComponentBitDepthFlagsKHR.Depth10 : VkVideoComponentBitDepthFlagsKHR.Depth8,
                chromaBitDepth = depth == 10 ? VkVideoComponentBitDepthFlagsKHR.Depth10 : VkVideoComponentBitDepthFlagsKHR.Depth8,
            };
            self->List = new VkVideoProfileListInfoKHR { profileCount = 1, pProfiles = &self->Profile };
        }
    }

    /// <summary>The capabilities of the profile on the device.</summary>
    private struct Capabilities
    {
        public VkVideoCapabilitiesKHR Video;
        public VkVideoEncodeCapabilitiesKHR Encode;
        public VkVideoEncodeH265CapabilitiesKHR H265;

        public static Capabilities Query(VulkanPhysicalDevice physical, ProfileBlock* profile)
        {
            VkVideoEncodeH265CapabilitiesKHR h265 = new();
            VkVideoEncodeCapabilitiesKHR encode = new() { pNext = &h265 };
            VkVideoCapabilitiesKHR video = new() { pNext = &encode };
            VkResult result = physical.Instance.Commands.vkGetPhysicalDeviceVideoCapabilitiesKHR(physical.Handle, &profile->Profile, &video);
            if (result < 0)
            {
                // Some drivers refuse a tuning mode; ask again with the default one.
                profile->Usage.tuningMode = VkVideoEncodeTuningModeKHR.Default;
                result = physical.Instance.Commands.vkGetPhysicalDeviceVideoCapabilitiesKHR(physical.Handle, &profile->Profile, &video);
            }

            if (result < 0 && profile->Profile.lumaBitDepth == VkVideoComponentBitDepthFlagsKHR.Depth10)
            {
                throw new NotSupportedException($"{physical.Name} does not encode HEVC Main 10 ({result}).");
            }

            VkCheck.ThrowIfFailed(result, "vkGetPhysicalDeviceVideoCapabilitiesKHR");
            video.pNext = null;
            encode.pNext = null;
            return new Capabilities { Video = video, Encode = encode, H265 = h265 };
        }

        /// <summary>The settings the driver prefers at a quality level; zeros where it can't say.</summary>
        public static VkVideoEncodeH265QualityLevelPropertiesKHR Preferred(VulkanPhysicalDevice physical, ProfileBlock* profile, uint qualityLevel)
        {
            VkVideoEncodeH265QualityLevelPropertiesKHR h265 = new();
            VkVideoEncodeQualityLevelPropertiesKHR properties = new() { pNext = &h265 };
            VkPhysicalDeviceVideoEncodeQualityLevelInfoKHR info = new() { pVideoProfile = &profile->Profile, qualityLevel = qualityLevel };
            VkResult result = physical.Instance.Commands.vkGetPhysicalDeviceVideoEncodeQualityLevelPropertiesKHR(physical.Handle, &info, &properties);
            h265.pNext = null;
            return result < 0 ? default : h265;
        }
    }
}
