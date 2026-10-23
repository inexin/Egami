using System.Numerics;


namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// The result of decoding one VP9 frame (one frame of a superframe, or a packet's only frame).
/// </summary>
/// <param name="Shown">Whether the frame is shown (show_frame or show_existing_frame).</param>
/// <param name="Header">The frame's uncompressed header.</param>
internal readonly record struct Vp9FrameResult(bool Shown, Vp9FrameHeader Header);

/// <summary>
/// The VP9 decoder core for one sample type (byte for 8-bit streams, ushort for 10 and 12 bit), translated from libvpx 1.14.1's
/// vp9/decoder (BSD-3; see THIRD-PARTY-NOTICES): reference slots, probability contexts, segmentation and loop filter state carried
/// from frame to frame, and the frame decode itself. Frames come out bit-exact with libvpx's.
/// </summary>
/// <typeparam name="T">The sample type.</typeparam>
internal sealed partial class Vp9Decoder<T> : IDisposable
    where T : unmanaged, IBinaryInteger<T>
{
    private readonly IPixelMemorySource _memory;
    private readonly List<Vp9Frame<T>> _pool = [];
    private readonly Vp9Frame<T>?[] _slots = new Vp9Frame<T>?[8];
    private readonly Vp9FrameContext[] _contexts = [new(), new(), new(), new()];
    private readonly Vp9FrameContext _fc = new();
    private readonly Vp9Counts _counts = new();

    // State carried between frames (VP9_COMMON).
    private readonly int[] _refDeltas = new int[4];
    private readonly int[] _modeDeltas = new int[2];
    private readonly byte[] _segTreeProbs = new byte[7];
    private readonly byte[] _segPredProbs = new byte[3];
    private readonly int[] _featureMask = new int[8];
    private readonly short[] _featureData = new short[8 * 4];
    private readonly int[] _refSignBias = new int[4];
    private bool _segEnabled;
    private bool _segUpdateMap;
    private bool _segTemporalUpdate;
    private bool _segAbsDelta;
    private bool _lastFrameKey;
    private bool _lastIntraOnly;
    private bool _lastShowFrame;
    private bool _frameKey;
    private bool _intraOnly;
    private int _lastWidth;
    private int _lastHeight;
    private int _width;
    private int _height;
    private int _bitDepth = 8;
    private int _subX = 1;
    private int _subY = 1;
    private int _colorSpace;
    private int _colorRange;
    private bool _needResync = true;
    private readonly bool[] _contextInitialized = new bool[4];

    // Per-frame state used by the tiles.
    private Vp9FrameHeader _header = null!;
    private Vp9Frame<T>? _current;
    private readonly Vp9Frame<T>?[] _activeRefs = new Vp9Frame<T>?[3];
    private readonly Vp9ScaleFactors[] _scales = [new(), new(), new()];
    private int _miRows;
    private int _miCols;
    private int _txMode;
    private int _referenceMode;
    private int _compFixedRef;
    private readonly int[] _compVarRef = new int[2];
    private bool _usePrevFrameMvs;
    private bool _lossless;
    private readonly short[] _yDequant = new short[8 * 2];
    private readonly short[] _uvDequant = new short[8 * 2];

    // Block data for the whole frame.
    private Vp9ModeInfo[] _mi = [];
    private int[] _miGrid = [];
    private byte[] _segMapCurrent = [];
    private byte[] _segMapLast = [];
    private Vp9MvRef[] _mvsCurrent = [];
    private Vp9MvRef[] _mvsPrevious = [];
    private readonly byte[][] _aboveContext = [[], [], []];
    private byte[] _abovePartition = [];
    private readonly Vp9LoopFilter<T> _loopFilter = new();
    private Tile[] _tiles = [];
    private byte[] _data = [];

    /// <summary>Gets the most threads a frame's tile columns and loop filter rows run on (EgamiContext.MaxParallelism).</summary>
    public int MaxParallelism { get; init; } = 1;

    /// <summary>Gets how many frame buffers the decoder holds: its reference slots and the frame being decoded, at most nine.</summary>
    internal int FrameBuffers => _pool.Count;

    /// <summary>Gets the limits a frame's size is checked against before anything is allocated for it.</summary>
    public SafetyLimits Limits { get; init; } = SafetyLimits.Default;

    public Vp9Decoder(IPixelMemorySource memory)
    {
        _memory = memory;
    }

    /// <summary>Gets the frame the last <see cref="DecodeFrame"/> produced or showed, or null when none.</summary>
    public Vp9Frame<T>? FrameToShow { get; private set; }

    /// <summary>Gets or sets a callback that sees every decoded frame, hidden ones too, with the adapted probabilities (for tests).</summary>
    public Action<Vp9Frame<T>, byte[]>? FrameDecoded { get; set; }

    /// <summary>Gets the decoder's bit depth (8 for byte samples).</summary>
    public int BitDepth => _bitDepth;

    /// <summary>Gets the size of the frame in reference slot <paramref name="slot"/>, or 0 by 0 when it is empty.</summary>
    public (int Width, int Height) SlotSize(int slot) => _slots[slot] is { } f ? (f.Width, f.Height) : (0, 0);

    /// <summary>Gets the frame in a reference slot, or null (for the encoder, which predicts from what it decoded).</summary>
    internal Vp9Frame<T>? Slot(int slot) => _slots[slot];

    /// <summary>Gets a saved probability context (for the encoder, which codes its next frame with it).</summary>
    internal Vp9FrameContext Context(int index) => _contexts[index];

    /// <summary>Gets the previous frame's motion vectors, as the next frame's candidates read them.</summary>
    internal Vp9MvRef[] PreviousMvs => _mvsPrevious;

    /// <summary>Gets whether a next frame of this size, not error resilient, reads the previous frame's motion vectors.</summary>
    internal bool UsesPreviousMvs(int width, int height) =>
        width == _lastWidth && height == _lastHeight && !_intraOnly && _lastShowFrame && !_frameKey;

    /// <summary>Gets whether a keyframe or intra-only frame is needed before anything decodes.</summary>
    public bool NeedsResync => _needResync;

    /// <summary>
    /// Decodes one frame (not a superframe: split those first). <paramref name="data"/> is copied. Returns where the frame's data
    /// ended, which matters for packets that hold several frames without a superframe index.
    /// </summary>
    /// <exception cref="CorruptDataException">The frame cannot be decoded.</exception>
    public int DecodeFrame(ReadOnlySpan<byte> data, out Vp9FrameResult result)
    {
        if (_data.Length < data.Length + 8)
        {
            _data = new byte[data.Length + 8];
        }

        data.CopyTo(_data);
        FrameToShow = null;
        Vp9FrameHeader h = Vp9FrameHeader.Read(data, SlotSize);
        _header = h;
        result = new Vp9FrameResult(h.ShowFrame, h);
        _lastFrameKey = _frameKey;
        _lastIntraOnly = _intraOnly;
        if (h.ShowExistingFrame)
        {
            Vp9Frame<T> shown = _slots[h.FrameToShowMapIdx]
                ?? throw new CorruptDataException("VP9", 0, $"Slot {h.FrameToShowMapIdx} holds no frame to show.");
            FrameToShow = shown;
            _lastWidth = _width;
            _lastHeight = _height;
            return h.Profile <= 2 ? 1 : 2;
        }

        CheckLimits(h);
        _frameKey = h.KeyFrame;
        _intraOnly = h.IntraOnly;
        if (h.KeyFrame || h.IntraOnly)
        {
            ApplyColorConfig(h);
            if (_needResync)
            {
                // Resync empties every slot (libvpx sets ref_frame_map to -1).
                for (int i = 0; i < 8; i++)
                {
                    Release(_slots[i]);
                    _slots[i] = null;
                }

                _needResync = false;
            }
        }
        else if (_needResync)
        {
            throw new CorruptDataException("VP9", 0, "An inter frame came before any keyframe or intra-only frame.");
        }

        // Active references and their scale factors.
        if (!h.FrameIsIntraOnly)
        {
            for (int i = 0; i < 3; i++)
            {
                Vp9Frame<T> reference = _slots[h.RefFrameIdx[i]]
                    ?? throw new CorruptDataException("VP9", 0, $"Reference slot {h.RefFrameIdx[i]} holds no frame.");
                _activeRefs[i] = reference;
                _refSignBias[Vp9Tables.LastFrame + i] = h.RefFrameSignBias[Vp9Tables.LastFrame + i];
            }

            bool anyValid = false;
            for (int i = 0; i < 3; i++)
            {
                Vp9Frame<T> r = _activeRefs[i]!;
                anyValid |= Vp9ScaleFactors.ValidSize(r.Width, r.Height, h.Width, h.Height);
            }

            if (!anyValid)
            {
                throw new CorruptDataException("VP9", 0, "No reference frame has a size this frame can predict from.");
            }

            for (int i = 0; i < 3; i++)
            {
                Vp9Frame<T> r = _activeRefs[i]!;
                if (r.BitDepth != _bitDepth || r.SubsamplingX != _subX || r.SubsamplingY != _subY)
                {
                    throw new CorruptDataException("VP9", 0, "A reference frame has another bit depth or subsampling.");
                }

                _scales[i].Setup(r.Width, r.Height, h.Width, h.Height);
            }
        }
        else
        {
            Array.Clear(_activeRefs);
        }

        ResizeContextBuffers(h.Width, h.Height);
        Vp9Frame<T> current = NewFrame(h.Width, h.Height);
        current.ColorSpace = _colorSpace;
        current.ColorRange = _colorRange;
        current.RenderWidth = h.RenderWidth;
        current.RenderHeight = h.RenderHeight;
        _current = current;
        if (h.FrameIsIntraOnly || h.ErrorResilientMode)
        {
            SetupPastIndependence(h);
        }

        int frameContextIdx = h.FrameIsIntraOnly || h.ErrorResilientMode ? 0 : h.FrameContextIdx;
        ApplyLoopFilterDeltas(h);
        ApplySegmentation(h);
        SetupDequant(h);
        _lossless = h.Lossless;

        int compressedStart = h.UncompressedHeaderSize;
        if (compressedStart + h.HeaderSizeInBytes > data.Length)
        {
            throw new CorruptDataException("VP9", compressedStart, "The compressed header runs past the end of the frame.");
        }

        _usePrevFrameMvs = !h.ErrorResilientMode && h.Width == _lastWidth && h.Height == _lastHeight && !_lastIntraOnly &&
            _lastShowFrame && !_lastFrameKey;

        if (!_contextInitialized[frameContextIdx])
        {
            throw new CorruptDataException("VP9", 0, "The frame uses a probability context nothing initialized.");
        }

        _fc.CopyFrom(_contexts[frameContextIdx]);
        if (!h.FrameParallelDecodingMode)
        {
            _counts.Clear();
        }

        ReadCompressedHeader(h, compressedStart);
        int end = DecodeTiles(h, compressedStart + h.HeaderSizeInBytes, data.Length);

        if (!h.ErrorResilientMode && !h.FrameParallelDecodingMode)
        {
            AdaptCoefProbs(h, frameContextIdx);
            if (!h.FrameIsIntraOnly)
            {
                AdaptModeProbs(h, frameContextIdx);
                AdaptMvProbs(h, frameContextIdx);
            }
        }

        if (h.RefreshFrameContext)
        {
            _contexts[frameContextIdx].CopyFrom(_fc);
        }

        // Swap the frame into the slots it refreshes.
        for (int i = 0; i < 8; i++)
        {
            if ((h.RefreshFrameFlags & (1 << i)) != 0)
            {
                Release(_slots[i]);
                _slots[i] = current;
                current.RefCount++;
            }
        }

        FrameToShow = h.ShowFrame ? current : null;
        FrameDecoded?.Invoke(current, _fc.P);
        _lastShowFrame = h.ShowFrame;
        if (_segEnabled)
        {
            (_segMapCurrent, _segMapLast) = (_segMapLast, _segMapCurrent);
        }

        (_mvsCurrent, _mvsPrevious) = (_mvsPrevious, _mvsCurrent);
        _lastWidth = _width;
        _lastHeight = _height;
        Release(current);
        return end;
    }

    public void Reset()
    {
        for (int i = 0; i < 8; i++)
        {
            Release(_slots[i]);
            _slots[i] = null;
        }

        _needResync = true;
        _lastShowFrame = false;
        _frameKey = false;
        _intraOnly = false;
        _lastWidth = _lastHeight = 0;
        FrameToShow = null;
    }

    public void Dispose()
    {
        foreach (Vp9Frame<T> f in _pool)
        {
            f.Dispose();
        }

        _pool.Clear();
        Array.Clear(_slots);
    }

    private void ApplyColorConfig(Vp9FrameHeader h)
    {
        _bitDepth = h.BitDepth;
        _colorSpace = h.ColorSpace;
        _colorRange = h.ColorRange;
        _subX = h.SubsamplingX;
        _subY = h.SubsamplingY;
        if ((_bitDepth == 8) != (typeof(T) == typeof(byte)))
        {
            throw new InvalidOperationException("The stream's bit depth changed; the codec adapter restarts the decoder.");
        }
    }

    /// <summary>
    /// Checks a frame's size against <see cref="Limits"/>: its width, height and pixels, and the memory of the frames a stream of
    /// that size can hold at once (eight reference slots and the frame being decoded).
    /// </summary>
    private void CheckLimits(Vp9FrameHeader h)
    {
        SafetyLimits limits = Limits;
        long pixels = (long)h.Width * h.Height;
        if (h.Width > limits.MaxWidth || h.Height > limits.MaxHeight || pixels > limits.MaxPixels)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), limits.MaxPixels, pixels);
        }

        long frameBytes = (long)((h.Width + 63) & ~63) * ((h.Height + 63) & ~63) * 3 * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        if (9 * frameBytes > limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limits.MaxTotalBytes, 9 * frameBytes);
        }
    }

    private Vp9Frame<T> NewFrame(int width, int height)
    {
        foreach (Vp9Frame<T> f in _pool)
        {
            if (f.RefCount == 0 && f.Width == width && f.Height == height && f.SubsamplingX == _subX && f.SubsamplingY == _subY &&
                f.BitDepth == _bitDepth)
            {
                f.RefCount = 1;
                return f;
            }
        }

        // Drop free frames of another size or format before allocating, so the pool stays at the frames in use.
        for (int i = _pool.Count - 1; i >= 0; i--)
        {
            if (_pool[i].RefCount == 0)
            {
                _pool[i].Dispose();
                _pool.RemoveAt(i);
            }
        }

        Vp9Frame<T> frame = new(width, height, _subX, _subY, _bitDepth, _memory) { RefCount = 1 };
        _pool.Add(frame);
        return frame;
    }

    private static void Release(Vp9Frame<T>? frame)
    {
        if (frame is not null && frame.RefCount > 0)
        {
            frame.RefCount--;
        }
    }

    private void ResizeContextBuffers(int width, int height)
    {
        int miRows = (height + 7) >> 3, miCols = (width + 7) >> 3;
        if (width != _width || height != _height || _mi.Length == 0)
        {
            int size = miRows * miCols;
            if (_mi.Length < size)
            {
                _mi = new Vp9ModeInfo[size];
                _miGrid = new int[size];
                _segMapCurrent = new byte[size];
                _segMapLast = new byte[size];
            }
            else
            {
                Array.Clear(_miGrid);
                Array.Clear(_segMapLast);
            }

            if (_mvsCurrent.Length < size)
            {
                _mvsCurrent = new Vp9MvRef[size];
                _mvsPrevious = new Vp9MvRef[size];
            }

            int alignedCols = (miCols + 7) & ~7;
            if (_abovePartition.Length < alignedCols)
            {
                _abovePartition = new byte[alignedCols];
                for (int p = 0; p < 3; p++)
                {
                    _aboveContext[p] = new byte[(2 * alignedCols) + 16];
                }
            }

            _width = width;
            _height = height;
        }

        _miRows = miRows;
        _miCols = miCols;
    }

    /// <summary>Resets what keyframes, intra-only and error resilient frames start from (vp9_setup_past_independence).</summary>
    private void SetupPastIndependence(Vp9FrameHeader h)
    {
        Array.Clear(_featureMask);
        Array.Clear(_featureData);
        _segAbsDelta = false;
        Array.Clear(_segMapLast);
        Array.Clear(_segMapCurrent);
        _refDeltas[0] = 1;
        _refDeltas[1] = 0;
        _refDeltas[2] = -1;
        _refDeltas[3] = -1;
        _modeDeltas[0] = 0;
        _modeDeltas[1] = 0;
        _fc.SetDefaults();
        if (h.KeyFrame || h.ErrorResilientMode || h.ResetFrameContext == 3)
        {
            for (int i = 0; i < 4; i++)
            {
                _contexts[i].CopyFrom(_fc);
                _contextInitialized[i] = true;
            }
        }
        else if (h.ResetFrameContext == 2)
        {
            _contexts[h.FrameContextIdx].CopyFrom(_fc);
            _contextInitialized[h.FrameContextIdx] = true;
        }

        Array.Clear(_refSignBias);
    }

    private void ApplyLoopFilterDeltas(Vp9FrameHeader h)
    {
        for (int i = 0; i < 4; i++)
        {
            if ((h.LoopFilterRefDeltaUpdates & (1 << i)) != 0)
            {
                _refDeltas[i] = h.LoopFilterRefDeltas[i];
            }
        }

        for (int i = 0; i < 2; i++)
        {
            if ((h.LoopFilterModeDeltaUpdates & (1 << i)) != 0)
            {
                _modeDeltas[i] = h.LoopFilterModeDeltas[i];
            }
        }
    }

    private void ApplySegmentation(Vp9FrameHeader h)
    {
        _segEnabled = h.SegmentationEnabled;
        _segUpdateMap = false;
        if (!_segEnabled)
        {
            return;
        }

        _segUpdateMap = h.SegmentationUpdateMap;
        if (_segUpdateMap)
        {
            h.SegmentationTreeProbs.CopyTo(_segTreeProbs, 0);
            _segTemporalUpdate = h.SegmentationTemporalUpdate;
            h.SegmentationPredProbs.CopyTo(_segPredProbs, 0);
        }

        if (h.SegmentationUpdateData)
        {
            _segAbsDelta = h.SegmentationAbsDelta;
            h.FeatureMask.CopyTo(_featureMask, 0);
            h.FeatureData.CopyTo(_featureData, 0);
        }
    }

    private bool SegFeatureActive(int segment, int feature) => _segEnabled && (_featureMask[segment] & (1 << feature)) != 0;

    private int SegData(int segment, int feature) => _featureData[(segment * 4) + feature];

    private void SetupDequant(Vp9FrameHeader h)
    {
        int segments = _segEnabled ? 8 : 1;
        for (int i = 0; i < segments; i++)
        {
            int q = h.BaseQIdx;
            if (SegFeatureActive(i, Vp9Tables.SegLvlAltQ))
            {
                int data = SegData(i, Vp9Tables.SegLvlAltQ);
                q = Math.Clamp(_segAbsDelta ? data : h.BaseQIdx + data, 0, 255);
            }

            _yDequant[i * 2] = DcQuant(q, h.DeltaQYDc);
            _yDequant[(i * 2) + 1] = AcQuant(q, 0);
            _uvDequant[i * 2] = DcQuant(q, h.DeltaQUVDc);
            _uvDequant[(i * 2) + 1] = AcQuant(q, h.DeltaQUVAc);
        }
    }

    private short DcQuant(int q, int delta)
    {
        int i = Math.Clamp(q + delta, 0, 255);
        return _bitDepth switch
        {
            8 => Vp9Tables.DcQLookup8[i],
            10 => Vp9Tables.DcQLookup10[i],
            _ => Vp9Tables.DcQLookup12[i],
        };
    }

    private short AcQuant(int q, int delta)
    {
        int i = Math.Clamp(q + delta, 0, 255);
        return _bitDepth switch
        {
            8 => Vp9Tables.AcQLookup8[i],
            10 => Vp9Tables.AcQLookup10[i],
            _ => Vp9Tables.AcQLookup12[i],
        };
    }
}
