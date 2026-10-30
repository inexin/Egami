using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Webp;

/// <summary>
/// Decodes one VP8 frame (RFC 6386): the frame header, segments, intra modes, coefficient tokens over 1 to 8 partitions, the
/// inverse transforms and the loop filter. For a WebP still (a key frame, the payload of a VP8 chunk) it works a macroblock row at
/// a time into 4:2:0 planes: a row is final once the row below it has been filtered, so it holds about two macroblock rows. For a
/// video frame (<see cref="Vp8VideoDecoder"/>) it decodes into a whole bordered frame buffer and adds inter frames: motion vectors,
/// prediction from the last, golden and alternate frames, and the per-reference filter levels; there it follows libvpx where the
/// two references differ (segment levels clamped before the deltas, inner edges kept by the token count), and a still follows
/// libwebp. The state a stream of frames needs lives in <see cref="Vp8StreamState"/>.
/// </summary>
internal sealed partial class Vp8Decoder : IDisposable
{
    /// <summary>The rows above a macroblock row that its loop filter may change: kept back until that row is filtered.</summary>
    private const int Tail = 8;

    private const int ChromaTail = 4;

    internal static ReadOnlySpan<byte> Bands => [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];
    // Each position's band times 33 (3 contexts × 11 probabilities): where its probabilities start in a type's 264.
    private static ReadOnlySpan<byte> BandOffsets => [0, 33, 66, 99, 198, 132, 165, 198, 198, 198, 198, 198, 198, 198, 198, 231, 0];

    internal static ReadOnlySpan<byte> Zigzag => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];
    internal static ReadOnlySpan<byte> Cat3 => [173, 148, 140];
    internal static ReadOnlySpan<byte> Cat4 => [176, 155, 140, 135];
    internal static ReadOnlySpan<byte> Cat5 => [180, 157, 141, 134, 130];
    internal static ReadOnlySpan<byte> Cat6 => [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];

    // The 4 × 4 mode tree of RFC 6386 section 11.2, as pairs of children: positive is the next pair, otherwise minus the mode.
    internal static readonly sbyte[] BModeTree = [-Vp8Dsp.BDc, 2, -Vp8Dsp.BTm, 4, -Vp8Dsp.BVe, 6, 8, 12, -Vp8Dsp.BHe, 10, -Vp8Dsp.BRd, -Vp8Dsp.BVr, -Vp8Dsp.BLd, 14, -Vp8Dsp.BVl, 16, -Vp8Dsp.BHd, -Vp8Dsp.BHu];

    private byte[] _data;
    private readonly Vp8StreamState _state;
    private Vp8BoolDecoder _modes;
    private readonly Vp8BoolDecoder[] _partitions;
    private int _partitionMask;

    // Frame header.
    private bool _segmentation;
    private bool _updateMap;
    private bool _useSkip;
    private int _skipProbability;
    private int _filterType;
    private readonly short[] _quant = new short[4 * 6];
    private readonly FilterInfo[] _filterInfo;

    // Contexts: per column of the row above and per row of the macroblock to the left.
    private readonly byte[] _modesAbove;
    private readonly byte[] _modesLeft = new byte[4];
    private readonly byte[] _nzAbove;

    // Reconstruction: the unfiltered bottom rows of the macroblock row above, and the 4 × 4 modes of the macroblock being read.
    private readonly byte[] _topY;
    private readonly byte[] _topU;
    private readonly byte[] _topV;
    private readonly byte[] _subModes = new byte[16];

    // What a macroblock row being decoded works in: the main one, and for a video frame decoded on several threads one per thread.
    private readonly Row _main;

    // A video frame's macroblock modes, read before its rows.
    private Macroblock[]? _macroblocks;

    /// <summary>Gets, for a video frame, the macroblocks whose modes or tokens ran past the end of the data.</summary>
    internal bool[]? Damaged { get; private set; }

    /// <summary>Gets how many of <see cref="Damaged"/> are set, counted as they are found.</summary>
    internal int DamagedCount
    {
        get => _damagedCount;
        private set => _damagedCount = value;
    }

    private int _damagedCount;

    // A video frame decoded on several threads: each row's progress, the rows' work state and the workers' job, kept from frame
    // to frame.
    private int[]? _progress;
    private Row[]? _rows;
    private Action<int>? _rowJob;
    private int _workerCount;

    // The planes being written: for a still the tail of the row above then the current macroblock row; for a video frame the whole
    // frame buffer.
    private readonly byte[]? _rentY;
    private readonly byte[]? _rentU;
    private readonly byte[]? _rentV;
    private readonly GCHandle[]? _pins;

    // The planes being written, pinned: the rented rows of a still or the target frame buffer's planes.
    private unsafe byte* _planeY;
    private unsafe byte* _planeU;
    private unsafe byte* _planeV;
    private int _lengthY;
    private int _lengthC;
    private int _strideY;
    private int _strideC;
    private Snapshot? _snapshot;
    private bool _disposed;

    private Vp8Decoder(byte[] data, int offset, int length, Vp8StreamState state, Vp8FrameHeader header, Vp8VideoDecoder? video)
    {
        _data = data;
        _state = state;
        _video = video;
        Width = header.Width;
        Height = header.Height;
        MbWidth = (Width + 15) >> 4;
        MbHeight = (Height + 15) >> 4;

        _partitions = new Vp8BoolDecoder[8];
        _filterInfo = new FilterInfo[video is null ? 8 : 4 * 4 * 4];
        _modesAbove = new byte[MbWidth * 4];
        _nzAbove = new byte[MbWidth * 9];
        _topY = new byte[MbWidth * 16];
        _topU = new byte[MbWidth * 8];
        _topV = new byte[MbWidth * 8];
        _main = new Row(MbWidth);
        if (video is null)
        {
            _strideY = MbWidth * 16;
            _strideC = MbWidth * 8;
            _rentY = ArrayPool<byte>.Shared.Rent(_strideY * (Tail + 16));
            _rentU = ArrayPool<byte>.Shared.Rent(_strideC * (ChromaTail + 8));
            _rentV = ArrayPool<byte>.Shared.Rent(_strideC * (ChromaTail + 8));
            _pins = [GCHandle.Alloc(_rentY, GCHandleType.Pinned), GCHandle.Alloc(_rentU, GCHandleType.Pinned), GCHandle.Alloc(_rentV, GCHandleType.Pinned)];
            unsafe
            {
                _planeY = (byte*)_pins[0].AddrOfPinnedObject();
                _planeU = (byte*)_pins[1].AddrOfPinnedObject();
                _planeV = (byte*)_pins[2].AddrOfPinnedObject();
            }

            _lengthY = _rentY.Length;
            _lengthC = Math.Min(_rentU.Length, _rentV.Length);
            _main.RowY = Tail * _strideY;
            _main.RowC = ChromaTail * _strideC;
        }

        Begin(data, offset, length, header);
    }

    /// <summary>
    /// Starts the next video frame of the same size in this decoder, which keeps its arrays from frame to frame: a video decodes
    /// with no allocation after its first key frame.
    /// </summary>
    internal void Restart(byte[] data, int offset, int length, Vp8FrameHeader header)
    {
        Array.Clear(_modesAbove);
        Array.Clear(_nzAbove);
        Begin(data, offset, length, header);
    }

    /// <summary>Reads the frame header and the first partition's frame fields, and points the decoder at the frame's planes.</summary>
    private void Begin(byte[] data, int offset, int length, Vp8FrameHeader header)
    {
        Vp8StreamState state = _state;
        Vp8VideoDecoder? video = _video;
        _data = data;
        Header = header;
        _updateMap = false;
        UpdateSegmentData = false;
        UpdateFilterDeltas = false;
        RefreshGolden = RefreshAlternate = false;
        CopyToGolden = CopyToAlternate = 0;
        ProbIntra = ProbLast = ProbGolden = 0;

        int firstStart = offset + (header.KeyFrame ? 10 : 3);
        int end = offset + length;

        // libvpx reads the first partition up to the end of the frame; libwebp stops at its stated size.
        _modes = new Vp8BoolDecoder(data, firstStart, video is null ? header.FirstPartitionSize : end - firstStart);
        ref Vp8BoolDecoder br = ref _modes;
        if (header.KeyFrame)
        {
            br.GetValue(1); // color space
            state.ClampingType = br.GetValue(1); // Egami clamps either way
        }

        _segmentation = br.GetValue(1) != 0;
        if (_segmentation)
        {
            _updateMap = br.GetValue(1) != 0;
            UpdateSegmentData = br.GetValue(1) != 0;
            if (UpdateSegmentData)
            {
                state.AbsoluteSegmentValues = br.GetValue(1) != 0;
                for (int s = 0; s < 4; s++)
                {
                    state.SegmentQuantizer[s] = br.GetValue(1) != 0 ? br.GetSignedValue(7) : 0;
                }

                for (int s = 0; s < 4; s++)
                {
                    state.SegmentFilterLevel[s] = br.GetValue(1) != 0 ? br.GetSignedValue(6) : 0;
                }
            }

            if (_updateMap)
            {
                for (int s = 0; s < 3; s++)
                {
                    state.SegmentProbabilities[s] = (byte)(br.GetValue(1) != 0 ? br.GetValue(8) : 255);
                }
            }
        }

        bool simple = br.GetValue(1) != 0;
        int level = br.GetValue(6);
        int sharpness = br.GetValue(3);
        bool useDeltas = br.GetValue(1) != 0;
        FilterSimple = simple;
        FilterLevel = level;
        Sharpness = sharpness;
        FilterDeltas = useDeltas;
        if (useDeltas && br.GetValue(1) != 0)
        {
            UpdateFilterDeltas = true;
            for (int i = 0; i < 4; i++)
            {
                if (br.GetValue(1) != 0)
                {
                    state.ReferenceFilterDeltas[i] = br.GetSignedValue(6);
                }
            }

            for (int i = 0; i < 4; i++)
            {
                if (br.GetValue(1) != 0)
                {
                    state.ModeFilterDeltas[i] = br.GetSignedValue(6);
                }
            }
        }

        // libwebp and libvpx both turn the filter off when the frame level is 0.
        _filterType = level == 0 ? 0 : simple ? 1 : 2;

        int partitions = 1 << br.GetValue(2);
        _partitionMask = partitions - 1;
        int tokensStart = firstStart + header.FirstPartitionSize;
        int sizes = tokensStart;
        int partStart = tokensStart + (3 * (partitions - 1));
        if (partStart > end)
        {
            throw new CorruptDataException(FormatName, -1, "The VP8 partition sizes run past the end of the frame.");
        }

        for (int p = 0; p < partitions - 1; p++)
        {
            int size = data[sizes] | (data[sizes + 1] << 8) | (data[sizes + 2] << 16);
            if (video is not null && size > end - partStart)
            {
                throw new CorruptDataException(FormatName, -1, $"VP8 token partition {p} runs past the end of the frame.");
            }

            size = Math.Min(size, end - partStart);
            _partitions[p] = new Vp8BoolDecoder(data, partStart, size);
            partStart += size;
            sizes += 3;
        }

        _partitions[partitions - 1] = new Vp8BoolDecoder(data, partStart, end - partStart);

        ParseQuantizers(ref br);
        if (!header.KeyFrame)
        {
            ParseReferenceUpdates(ref br);
        }

        RefreshEntropy = br.GetValue(1) != 0;
        if (video is not null && !RefreshEntropy)
        {
            state.SaveEntropy();
        }

        RefreshLast = header.KeyFrame || br.GetValue(1) != 0;
        ParseProbabilities(ref br);
        _useSkip = br.GetValue(1) != 0;
        _skipProbability = _useSkip ? br.GetValue(8) : 0;
        if (!header.KeyFrame)
        {
            ParseInterProbabilities(ref br);
        }

        if (_filterType > 0)
        {
            if (video is null)
            {
                ComputeFilterStrengths(level, sharpness, useDeltas);
            }
            else
            {
                ComputeVideoFilterStrengths(level, sharpness, useDeltas);
            }
        }

        if (video is not null)
        {
            Vp8FrameBuffer target = video.Target!;
            _strideY = target.StrideY;
            _strideC = target.StrideC;
            unsafe
            {
                fixed (byte* y = target.Y, u = target.U, v = target.V)
                {
                    // The frame buffer's planes are pinned for its life.
                    _planeY = y;
                    _planeU = u;
                    _planeV = v;
                }
            }

            _lengthY = target.Y.Length;
            _lengthC = target.U.Length;
        }
    }

    /// <summary>Gets the frame width.</summary>
    internal int Width { get; }

    /// <summary>Gets the frame height.</summary>
    internal int Height { get; }

    internal int MbWidth { get; }

    internal int MbHeight { get; }

    /// <summary>Gets the next macroblock row <see cref="DecodeRow"/> decodes.</summary>
    internal int NextMbRow { get; private set; }

    /// <summary>Gets the luma rows final so far: rows before this one have been handed out by <see cref="DecodeRow"/>.</summary>
    internal int RowsDone { get; private set; }

    /// <summary>
    /// Gets or sets how many bytes of the payload have arrived, for an incremental decode; <see cref="int.MaxValue"/> (the default)
    /// once all of it has. A row that reads past it is undone and <see cref="DecodeRow"/> throws
    /// <see cref="Streaming.NeedsInputException"/>, to be called again when more has arrived.
    /// </summary>
    internal int Arrived { get; set; } = int.MaxValue;

    /// <summary>Gets the payload bytes that must have arrived before the first row can be decoded: the frame header and first partition, and the partition sizes.</summary>
    internal static int HeaderBytes(ReadOnlySpan<byte> payload)
    {
        int tag = payload[0] | (payload[1] << 8) | (payload[2] << 16);
        return 10 + (tag >> 5) + 21;
    }

    /// <summary>Gets whether any partition read past its end.</summary>
    internal bool PastEnd
    {
        get
        {
            if (_modes.PastEnd)
            {
                return true;
            }

            for (int p = 0; p <= _partitionMask; p++)
            {
                if (_partitions[p].PastEnd)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Returns the bytes the decoder holds for a frame of this width: two macroblock rows of planes and its contexts.</summary>
    internal static long WorkingBytes(int width)
    {
        int mbw = (width + 15) >> 4;
        return ((long)mbw * 16 * (Tail + 16)) + (2L * mbw * 8 * (ChromaTail + 8)) + ((long)mbw * 64);
    }

    private int StrideY => _strideY;

    /// <summary>Gets the luma plane being written: the row buffer of a still, the frame buffer of a video frame.</summary>
    private unsafe Span<byte> RowsY => new(_planeY, _lengthY);

    private unsafe Span<byte> RowsU => new(_planeU, _lengthC);

    private unsafe Span<byte> RowsV => new(_planeV, _lengthC);

    private int StrideC => _strideC;

    /// <summary>Reads a VP8 frame header (RFC 6386 section 9.1) from the first 10 bytes of the payload.</summary>
    internal static Vp8FrameHeader ReadHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, -1, "The VP8 frame header is cut short.");
        }

        int tag = payload[0] | (payload[1] << 8) | (payload[2] << 16);
        if ((tag & 1) != 0)
        {
            throw new NotSupportedException("The VP8 data is an inter frame. WebP holds key frames only; VP8 video belongs to Egami.Video.");
        }

        int version = (tag >> 1) & 7;
        int firstPartition = tag >> 5;
        if (version > 3)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, 0, $"The VP8 version {version} is unknown.");
        }

        if (payload[3] != 0x9D || payload[4] != 0x01 || payload[5] != 0x2A)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, 3, "The VP8 start code is wrong.");
        }

        int width = (payload[6] | (payload[7] << 8)) & 0x3FFF;
        int height = (payload[8] | (payload[9] << 8)) & 0x3FFF;
        if (width == 0 || height == 0)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, 6, $"The VP8 image size {width}×{height} is invalid.");
        }

        if (firstPartition > payload.Length - 10)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, 0, "The first VP8 partition runs past the end of the frame.");
        }

        return new Vp8FrameHeader(width, height, firstPartition);
    }

    /// <summary>
    /// Reads the frame tag of a video frame and, for a key frame, its start code, size and scaling; an inter frame keeps those of
    /// <paramref name="lastKey"/>, the last key frame. Versions 4 to 7 decode as version 0, as libvpx decodes them.
    /// </summary>
    internal static Vp8FrameHeader ReadVideoHeader(ReadOnlySpan<byte> payload, Vp8FrameHeader lastKey)
    {
        int width = lastKey.Width, height = lastKey.Height;
        if (payload.Length < 3)
        {
            throw new CorruptDataException(Vp8VideoDecoder.FormatName, 0, "The VP8 frame is cut short.");
        }

        int tag = payload[0] | (payload[1] << 8) | (payload[2] << 16);
        bool key = (tag & 1) == 0;
        int version = (tag >> 1) & 7;
        bool show = ((tag >> 4) & 1) != 0;
        int firstPartition = tag >> 5;
        int horizontal = lastKey.HorizontalScale, vertical = lastKey.VerticalScale;
        if (key)
        {
            if (payload.Length < 10)
            {
                throw new CorruptDataException(Vp8VideoDecoder.FormatName, 3, "The VP8 key frame header is cut short.");
            }

            if (payload[3] != 0x9D || payload[4] != 0x01 || payload[5] != 0x2A)
            {
                throw new CorruptDataException(Vp8VideoDecoder.FormatName, 3, "The VP8 start code is wrong.");
            }

            width = (payload[6] | (payload[7] << 8)) & 0x3FFF;
            horizontal = payload[7] >> 6;
            height = (payload[8] | (payload[9] << 8)) & 0x3FFF;
            vertical = payload[9] >> 6;
            if (width == 0 || height == 0)
            {
                throw new CorruptDataException(Vp8VideoDecoder.FormatName, 6, $"The VP8 frame size {width}×{height} is invalid.");
            }
        }

        if (firstPartition > payload.Length - (key ? 10 : 3))
        {
            throw new CorruptDataException(Vp8VideoDecoder.FormatName, 0, "The first VP8 partition runs past the end of the frame.");
        }

        return new Vp8FrameHeader(width, height, firstPartition, key, version > 3 ? 0 : version, show, horizontal, vertical);
    }

    /// <summary>Starts decoding the frame in <paramref name="data"/>[<paramref name="offset"/>..+<paramref name="length"/>].</summary>
    internal static Vp8Decoder Create(byte[] data, int offset, int length, Vp8StreamState? state = null)
    {
        Vp8FrameHeader header = ReadHeader(data.AsSpan(offset, length));
        return new Vp8Decoder(data, offset, length, state ?? new Vp8StreamState(), header, null);
    }

    /// <summary>Starts decoding a video frame into <paramref name="video"/>'s target buffer, predicting from its references.</summary>
    internal static Vp8Decoder CreateVideo(byte[] data, int offset, int length, Vp8FrameHeader header, Vp8VideoDecoder video) =>
        new(data, offset, length, video.State, header, video);

    /// <summary>
    /// Decodes a video frame into the target buffer: every macroblock's modes first (they share one partition), then the rows,
    /// on as many threads as the token partitions and <see cref="Vp8VideoDecoder.Threads"/> allow, in a wavefront. With
    /// <paramref name="reconstruction"/> (the encoder's, of the frame it just wrote) the tokens are not read: the frame's
    /// unfiltered pixels are taken from it and <paramref name="zero"/> says which macroblocks have no coefficients.
    /// </summary>
    internal void DecodeFrame(Vp8Planes? reconstruction = null, bool[]? zero = null)
    {
        Vp8VideoDecoder video = _video!;
        _macroblocks ??= new Macroblock[MbWidth * MbHeight];
        Damaged ??= new bool[MbWidth * MbHeight];
        DamagedCount = 0;
        Vp8StageTimes.Lap(Vp8Stage.Setup);
        for (int mby = 0; mby < MbHeight; mby++)
        {
            Array.Clear(_modesLeft);
            for (int mbx = 0; mbx < MbWidth; mbx++)
            {
                _macroblocks[(mby * MbWidth) + mbx] = ParseModes(mbx, mby);

                // Modes read past the end of the frame: this macroblock's and every later one's are made up.
                bool past = _modes.PastEnd;
                Damaged[(mby * MbWidth) + mbx] = past;
                DamagedCount += past ? 1 : 0;
            }
        }

        Vp8StageTimes.Lap(Vp8Stage.Modes);
        if (reconstruction is not null)
        {
            FilterReconstruction(reconstruction, zero!);
            NextMbRow = MbHeight;
            RowsDone = Height;
            return;
        }

        // Rows that share a partition must go in order, so each thread takes every n-th row, n a power of two no more than the
        // partitions.
        // Set every frame: the decoder lives from frame to frame, and the token partitions, so the workers, may change.
        _main.DeferFilter = false;
        int workers = 1;
        while (workers * 2 <= Math.Min(Math.Min(video.Threads, Partitions), MbHeight))
        {
            workers *= 2;
        }

        if (workers == 1)
        {
            // One thread: each row is reconstructed whole, then filtered, as libvpx does, so an intra macroblock finds the
            // unfiltered pixels to its left in the frame and inter macroblocks need not keep copies of theirs.
            _main.DeferFilter = true;
            for (int mby = 0; mby < MbHeight; mby++)
            {
                DecodeVideoRow(mby, _main, null);
            }
        }
        else
        {
            // The progress, the rows and the job are made on the first frame decoded on several threads and kept.
            _progress ??= new int[MbHeight];
            Array.Clear(_progress);
            if (_rows is null || _rows.Length < workers)
            {
                Row[] rows = new Row[workers];
                rows[0] = _main;
                for (int w = 1; w < workers; w++)
                {
                    rows[w] = _rows is not null && w < _rows.Length ? _rows[w] : new Row(MbWidth);
                }

                _rows = rows;
            }

            _workerCount = workers;
            _rowJob ??= w =>
            {
                for (int mby = w; mby < MbHeight; mby += _workerCount)
                {
                    DecodeVideoRow(mby, _rows![w], _progress);
                }
            };
            video.RunWorkers(workers, _rowJob);
        }

        NextMbRow = MbHeight;
        RowsDone = Height;
    }

    private string FormatName => _video is null ? WebpFormat.Info.Name : Vp8VideoDecoder.FormatName;

    /// <summary>
    /// Decodes the next macroblock row and filters it, then calls <paramref name="emit"/> for the rows that became final: luma rows
    /// from <see cref="RowsDone"/> on, with the chroma rows that go with them. Returns false once every row is out.
    /// </summary>
    internal bool DecodeRow(RowSink emit)
    {
        if (RowsDone >= Height)
        {
            return false;
        }

        int mby = NextMbRow;
        if (mby < MbHeight)
        {
            bool incremental = Arrived != int.MaxValue;
            if (incremental)
            {
                _snapshot ??= new Snapshot(this);
                _snapshot.Save(this, mby);
            }

            DecodeMacroblockRow(mby, _main);
            if (incremental && (_modes.Position > Arrived || _partitions[mby & _partitionMask].Position > Arrived))
            {
                // The row read bytes that have not arrived: undo it and wait.
                _snapshot!.Restore(this, mby);
                throw new Streaming.NeedsInputException();
            }

            NextMbRow++;
        }

        // Final now: the tail of the row above, and the current row but its last rows (which the row below may still filter).
        bool last = mby >= MbHeight - 1;
        int lumaEnd = last ? Height : Math.Min(Height, (mby * 16) + 16 - Tail);
        int chromaHeight = (Height + 1) >> 1;
        int chromaEnd = last ? chromaHeight : Math.Min(chromaHeight, (mby * 8) + 8 - ChromaTail);
        int chromaStart = (RowsDone + 1) >> 1;
        emit(this, RowsDone, lumaEnd, chromaStart, chromaEnd);
        RowsDone = lumaEnd;
        if (!last)
        {
            // The current row's last rows become the tail.
            Array.Copy(_rentY!, StrideY * 16, _rentY!, 0, StrideY * Tail);
            Array.Copy(_rentU!, StrideC * 8, _rentU!, 0, StrideC * ChromaTail);
            Array.Copy(_rentV!, StrideC * 8, _rentV!, 0, StrideC * ChromaTail);
        }
        else
        {
            RowsDone = Height;
        }

        Vp8StageTimes.Lap(Vp8Stage.Output);

        return true;
    }

    /// <summary>Gets luma row <paramref name="y"/>, one of the rows the current call to <see cref="RowSink"/> hands out.</summary>
    internal ReadOnlySpan<byte> RowY(int y) => RowsY.Slice(RowIndex(y, 16, Tail) * StrideY, Width);

    /// <summary>Gets chroma row <paramref name="y"/> of U.</summary>
    internal ReadOnlySpan<byte> RowU(int y) => RowsU.Slice(RowIndex(y, 8, ChromaTail) * StrideC, (Width + 1) >> 1);

    /// <summary>Gets chroma row <paramref name="y"/> of V.</summary>
    internal ReadOnlySpan<byte> RowV(int y) => RowsV.Slice(RowIndex(y, 8, ChromaTail) * StrideC, (Width + 1) >> 1);

    /// <summary>Returns where row <paramref name="y"/> sits in the row buffer: the tail of the row above, then the current macroblock row.</summary>
    private int RowIndex(int y, int mbSize, int tail)
    {
        int current = (NextMbRow - 1) * mbSize;
        return y >= current ? tail + (y - current) : tail - (current - y);
    }

    public void Dispose()
    {
        if (!_disposed && _video is null)
        {
            _disposed = true;
            foreach (GCHandle pin in _pins!)
            {
                pin.Free();
            }

            ArrayPool<byte>.Shared.Return(_rentY!);
            ArrayPool<byte>.Shared.Return(_rentU!);
            ArrayPool<byte>.Shared.Return(_rentV!);
        }
    }

    private void ParseQuantizers(ref Vp8BoolDecoder br)
    {
        int baseQ = br.GetValue(7);
        int dqy1Dc = br.GetOptionalSigned(4);
        int dqy2Dc = br.GetOptionalSigned(4);
        int dqy2Ac = br.GetOptionalSigned(4);
        int dquvDc = br.GetOptionalSigned(4);
        int dquvAc = br.GetOptionalSigned(4);
        int[] quantizers = Quantizers;
        quantizers[0] = baseQ;
        quantizers[1] = dqy1Dc;
        quantizers[2] = dqy2Dc;
        quantizers[3] = dqy2Ac;
        quantizers[4] = dquvDc;
        quantizers[5] = dquvAc;
        for (int s = 0; s < 4; s++)
        {
            int q = baseQ;
            if (_segmentation)
            {
                q = _state.SegmentQuantizer[s] + (_state.AbsoluteSegmentValues ? 0 : baseQ);
                if (_video is not null)
                {
                    // libvpx clamps the segment's index before adding the deltas; libwebp clamps only the sum.
                    q = Math.Clamp(q, 0, 127);
                }
            }

            Span<short> m = _quant.AsSpan(s * 6, 6);
            m[0] = Vp8Tables.DcTable[Math.Clamp(q + dqy1Dc, 0, 127)];
            m[1] = Vp8Tables.AcTable[Math.Clamp(q, 0, 127)];
            m[2] = (short)(Vp8Tables.DcTable[Math.Clamp(q + dqy2Dc, 0, 127)] * 2);

            // x × 155 / 100 for every step in the table, at least 8.
            m[3] = (short)Math.Max(8, (Vp8Tables.AcTable[Math.Clamp(q + dqy2Ac, 0, 127)] * 101581) >> 16);
            m[4] = Vp8Tables.DcTable[Math.Clamp(q + dquvDc, 0, 117)];
            m[5] = Vp8Tables.AcTable[Math.Clamp(q + dquvAc, 0, 127)];
        }
    }

    private void ParseProbabilities(ref Vp8BoolDecoder br)
    {
        Span<byte> probabilities = _state.CoefficientProbabilities;
        ReadOnlySpan<byte> update = Vp8Tables.CoeffUpdateProbs;
        for (int i = 0; i < probabilities.Length; i++)
        {
            if (br.GetBit(update[i]) != 0)
            {
                probabilities[i] = (byte)br.GetValue(8);
            }
        }
    }

    private void ComputeFilterStrengths(int frameLevel, int sharpness, bool useDeltas)
    {
        for (int s = 0; s < 4; s++)
        {
            int baseLevel = frameLevel;
            if (_segmentation)
            {
                baseLevel = _state.SegmentFilterLevel[s] + (_state.AbsoluteSegmentValues ? 0 : frameLevel);
            }

            for (int i4x4 = 0; i4x4 <= 1; i4x4++)
            {
                int level = baseLevel;
                if (useDeltas)
                {
                    level += _state.ReferenceFilterDeltas[0];
                    if (i4x4 != 0)
                    {
                        level += _state.ModeFilterDeltas[0];
                    }
                }

                level = Math.Clamp(level, 0, 63);
                ref FilterInfo info = ref _filterInfo[(s * 2) + i4x4];
                if (level == 0)
                {
                    info = default;
                    continue;
                }

                int interior = level;
                if (sharpness > 0)
                {
                    interior >>= sharpness > 4 ? 2 : 1;
                    interior = Math.Min(interior, 9 - sharpness);
                }

                interior = Math.Max(interior, 1);
                info = new FilterInfo((2 * level) + interior, interior, level >= 40 ? 2 : level >= 15 ? 1 : 0);
            }
        }
    }

    private void DecodeMacroblockRow(int mby, Row c)
    {
        Vp8StageTimes.Lap(Vp8Stage.Setup);
        ref Vp8BoolDecoder tokens = ref _partitions[mby & _partitionMask];
        Array.Clear(_modesLeft);
        StartRow(mby, c);
        for (int mbx = 0; mbx < MbWidth; mbx++)
        {
            Macroblock mb = ParseModes(mbx, mby);
            Vp8StageTimes.Lap(Vp8Stage.Modes);
            DecodeMacroblock(mbx, mby, mb, c, ref tokens);
            Vp8StageTimes.Lap(Vp8Stage.Reconstruct);
        }

        if (_filterType > 0)
        {
            for (int mbx = 0; mbx < MbWidth; mbx++)
            {
                FilterMacroblock(mbx, mby, c);
            }

            Vp8StageTimes.Lap(Vp8Stage.Filter);
        }
    }

    /// <summary>
    /// Decodes and filters one macroblock row of a video frame whose modes are read, a macroblock at a time. With
    /// <paramref name="progress"/>, other threads decode the rows around it: each macroblock waits until the row above has
    /// finished the macroblock above-right of it (its intra prediction and the loop filter's top edge need that), and says how
    /// far this row has come.
    /// </summary>
    private void DecodeVideoRow(int mby, Row c, int[]? progress)
    {
        ref Vp8BoolDecoder tokens = ref _partitions[mby & _partitionMask];
        c.RowY = _video!.Target!.OriginY + (mby * 16 * _strideY);
        c.RowC = _video.Target.OriginC + (mby * 8 * _strideC);
        StartRow(mby, c);
        int first = mby * MbWidth;
        for (int mbx = 0; mbx < MbWidth; mbx++)
        {
            if (progress is not null && mby > 0)
            {
                _video.WaitFor(progress, mby - 1, Math.Min(mbx + 2, MbWidth));
            }

            DecodeMacroblock(mbx, mby, _macroblocks![first + mbx], c, ref tokens);
            Vp8StageTimes.Lap(Vp8Stage.Reconstruct);
            if (_filterType > 0 && !c.DeferFilter)
            {
                FilterMacroblock(mbx, mby, c);
                Vp8StageTimes.Lap(Vp8Stage.Filter);
            }

            if (progress is not null)
            {
                Volatile.Write(ref progress[mby], mbx + 1);
            }
        }

        if (_filterType > 0 && c.DeferFilter)
        {
            // In raster order as before; no reconstruction in the row reads a pixel the filter changes.
            for (int mbx = 0; mbx < MbWidth; mbx++)
            {
                FilterMacroblock(mbx, mby, c);
            }

            Vp8StageTimes.Lap(Vp8Stage.Filter);
        }
    }

    /// <summary>
    /// Takes the encoder's unfiltered reconstruction of the frame as its pixels, notes which macroblocks are skipped as reading
    /// their tokens would, and runs the loop filter: one row after another, or in a wavefront on the decoder's threads (a
    /// macroblock's edges wait for the row above to have filtered the one above-right of it).
    /// </summary>
    private void FilterReconstruction(Vp8Planes recon, bool[] zero)
    {
        Vp8VideoDecoder video = _video!;
        Vp8FrameBuffer target = video.Target!;
        int width = MbWidth * 16, rows = MbHeight * 16;
        for (int y = 0; y < rows; y++)
        {
            recon.Y.AsSpan(y * recon.Stride, width).CopyTo(target.Y.Slice(target.OriginY + (y * target.StrideY), width));
        }

        for (int y = 0; y < rows / 2; y++)
        {
            recon.U.AsSpan(y * recon.ChromaStride, width / 2).CopyTo(target.U.Slice(target.OriginC + (y * target.StrideC), width / 2));
            recon.V.AsSpan(y * recon.ChromaStride, width / 2).CopyTo(target.V.Slice(target.OriginC + (y * target.StrideC), width / 2));
        }

        Vp8ModeInfo modes = video.Modes;
        for (int mby = 0; mby < MbHeight; mby++)
        {
            for (int mbx = 0; mbx < MbWidth; mbx++)
            {
                Macroblock mb = _macroblocks![(mby * MbWidth) + mbx];
                modes.Skip[modes.Index(mbx, mby)] = (byte)(mb.Skip || zero[(mby * MbWidth) + mbx] ? 1 : 0);
            }
        }

        Vp8StageTimes.Lap(Vp8Stage.Reconstruct);
        if (_filterType == 0)
        {
            return;
        }

        int workers = Math.Min(video.Threads, MbHeight);
        if (workers == 1)
        {
            for (int mby = 0; mby < MbHeight; mby++)
            {
                FilterRow(mby, _main, null);
            }
        }
        else
        {
            _progress ??= new int[MbHeight];
            Array.Clear(_progress);
            if (_rows is null || _rows.Length < workers)
            {
                Row[] all = new Row[workers];
                all[0] = _main;
                for (int w = 1; w < workers; w++)
                {
                    all[w] = _rows is not null && w < _rows.Length ? _rows[w] : new Row(MbWidth);
                }

                _rows = all;
            }

            video.RunWorkers(workers, w =>
            {
                for (int mby = w; mby < MbHeight; mby += workers)
                {
                    FilterRow(mby, _rows![w], _progress);
                }
            });
        }

        Vp8StageTimes.Lap(Vp8Stage.Filter);
    }

    /// <summary>Filters one macroblock row of the target buffer, as <see cref="DecodeMacroblock"/> notes each macroblock's filter.</summary>
    private void FilterRow(int mby, Row c, int[]? progress)
    {
        Vp8ModeInfo modes = _video!.Modes;
        c.RowY = _video.Target!.OriginY + (mby * 16 * _strideY);
        c.RowC = _video.Target.OriginC + (mby * 8 * _strideC);
        for (int mbx = 0; mbx < MbWidth; mbx++)
        {
            if (progress is not null && mby > 0)
            {
                _video.WaitFor(progress, mby - 1, Math.Min(mbx + 2, MbWidth));
            }

            Macroblock mb = _macroblocks![(mby * MbWidth) + mbx];
            bool skipped = modes.Skip[modes.Index(mbx, mby)] != 0;
            int modeIndex = mb.Reference == 0 ? (mb.Intra4x4 ? 0 : 1) : mb.Mode == Vp8ModeInfo.Zero ? 1 : mb.Mode == Vp8ModeInfo.Split ? 3 : 2;
            c.Filter[mbx] = new MacroblockFilter(_filterInfo[(((mb.Segment * 4) + mb.Reference) * 4) + modeIndex], !mb.HasY2 || !skipped);
            FilterMacroblock(mbx, mby, c);
            if (progress is not null)
            {
                Volatile.Write(ref progress[mby], mbx + 1);
            }
        }
    }

    /// <summary>Sets up a row's left contexts and the left and top edges of its work buffers: 129 down the left, 127 above the first row.</summary>
    private static void StartRow(int mby, Row c)
    {
        Array.Clear(c.NzLeft);
        c.LeftInter = false;
        Span<byte> wy = c.WorkY;
        Span<byte> wu = c.WorkU;
        Span<byte> wv = c.WorkV;
        const int Y0 = Vp8Dsp.Stride + 8;
        const int C0 = Vp8Dsp.Stride + 8;
        for (int j = 0; j < 16; j++)
        {
            wy[Y0 + (j * Vp8Dsp.Stride) - 1] = 129;
        }

        for (int j = 0; j < 8; j++)
        {
            wu[C0 + (j * Vp8Dsp.Stride) - 1] = 129;
            wv[C0 + (j * Vp8Dsp.Stride) - 1] = 129;
        }

        if (mby > 0)
        {
            wy[Y0 - Vp8Dsp.Stride - 1] = wu[C0 - Vp8Dsp.Stride - 1] = wv[C0 - Vp8Dsp.Stride - 1] = 129;
        }
        else
        {
            wy.Slice(Y0 - Vp8Dsp.Stride - 1, 16 + 4 + 1).Fill(127);
            wu.Slice(C0 - Vp8Dsp.Stride - 1, 8 + 1).Fill(127);
            wv.Slice(C0 - Vp8Dsp.Stride - 1, 8 + 1).Fill(127);
        }
    }

    /// <summary>Reads a macroblock's coefficients, notes how the loop filter treats it, and reconstructs it.</summary>
    private void DecodeMacroblock(int mbx, int mby, Macroblock mb, Row c, ref Vp8BoolDecoder tokens)
    {
        bool anyNonZero = false;
        bool anyTokens = false;
        if (!mb.Skip)
        {
            anyNonZero = ParseResiduals(ref tokens, mbx, mb, c, out anyTokens);
            if (tokens.PastEnd && Damaged is not null && !Damaged[(mby * MbWidth) + mbx])
            {
                Damaged[(mby * MbWidth) + mbx] = true;
                Interlocked.Increment(ref _damagedCount);
            }
        }
        else
        {
            Array.Clear(c.Codes);
            c.NzLeft.AsSpan(0, 8).Clear();
            _nzAbove.AsSpan(mbx * 9, 8).Clear();
            if (mb.HasY2)
            {
                c.NzLeft[8] = 0;
                _nzAbove[(mbx * 9) + 8] = 0;
            }
        }

        Vp8StageTimes.Lap(Vp8Stage.Tokens);
        c.AnyCoefficients = anyNonZero;

        if (_video is not null)
        {
            // libvpx marks a macroblock whose blocks are all empty as skipped; its loop filter then leaves the inner edges of
            // 16 × 16 macroblocks alone.
            bool skipped = mb.Skip || !anyTokens;
            _video.Modes.Skip[_video.Modes.Index(mbx, mby)] = (byte)(skipped ? 1 : 0);
            if (_filterType > 0)
            {
                int modeIndex = mb.Reference == 0 ? (mb.Intra4x4 ? 0 : 1) : mb.Mode == Vp8ModeInfo.Zero ? 1 : mb.Mode == Vp8ModeInfo.Split ? 3 : 2;
                FilterInfo info = _filterInfo[(((mb.Segment * 4) + mb.Reference) * 4) + modeIndex];
                c.Filter[mbx] = new MacroblockFilter(info, !mb.HasY2 || !skipped);
            }
        }
        else if (_filterType > 0)
        {
            FilterInfo info = _filterInfo[(mb.Segment * 2) + (mb.Intra4x4 ? 1 : 0)];
            c.Filter[mbx] = new MacroblockFilter(info, mb.Intra4x4 || anyNonZero);
        }

        Reconstruct(mbx, mby, mb, c);
    }

    private Macroblock ParseModes(int mbx, int mby)
    {
        ref Vp8BoolDecoder br = ref _modes;
        int segment = 0;
        if (_updateMap)
        {
            ReadOnlySpan<byte> p = _state.SegmentProbabilities;
            segment = br.GetBit(p[0]) == 0 ? br.GetBit(p[1]) : 2 + br.GetBit(p[2]);
        }

        if (_state.SegmentMap is byte[] map)
        {
            // The map lasts from frame to frame while frames do not update it; a key frame without an update resets it.
            int at = (mby * MbWidth) + mbx;
            if (_updateMap || Header.KeyFrame)
            {
                map[at] = (byte)segment;
            }
            else
            {
                segment = map[at];
            }
        }

        bool skip = _useSkip && br.GetBit(_skipProbability) != 0;
        if (!Header.KeyFrame)
        {
            return ParseInterModes(ref br, mbx, mby, segment, skip);
        }

        Span<byte> above = _modesAbove.AsSpan(mbx * 4, 4);
        bool intra4x4 = br.GetBit(145) == 0;
        int ymode;
        if (!intra4x4)
        {
            ymode = br.GetBit(156) != 0 ? (br.GetBit(128) != 0 ? 3 : 2) : (br.GetBit(163) != 0 ? 1 : 0);

            // The implied 4 × 4 modes of a 16 × 16 macroblock, for its neighbours' contexts.
            byte implied = ymode switch { 0 => Vp8Dsp.BDc, 1 => Vp8Dsp.BVe, 2 => Vp8Dsp.BHe, _ => Vp8Dsp.BTm };
            above.Fill(implied);
            _modesLeft.AsSpan().Fill(implied);
        }
        else
        {
            ymode = -1;
            ReadOnlySpan<byte> probs = Vp8Tables.BModeProbs;
            for (int y = 0; y < 4; y++)
            {
                int left = _modesLeft[y];
                for (int x = 0; x < 4; x++)
                {
                    int at = ((above[x] * 10) + left) * 9;
                    int i = 0;
                    do
                    {
                        i = BModeTree[i + br.GetBit(probs[at + (i >> 1)])];
                    }
                    while (i > 0);
                    left = -i;
                    above[x] = (byte)left;
                    _subModes[(y * 4) + x] = (byte)left;
                }

                _modesLeft[y] = (byte)left;
            }
        }

        int uvmode = br.GetBit(142) == 0 ? 0 : br.GetBit(114) == 0 ? 1 : br.GetBit(183) != 0 ? 3 : 2;
        Macroblock mb = new(segment, skip, intra4x4 ? Vp8ModeInfo.BPred : ymode, uvmode);
        if (_video is not null)
        {
            RecordIntra(mbx, mby, mb);
        }

        return mb;
    }

    /// <summary>
    /// Reads a macroblock's coefficients; returns whether any block is transformed (libwebp's test for the loop filter), and in
    /// <paramref name="anyTokens"/> whether any block has a token before its end of block (libvpx's).
    /// </summary>
    private bool ParseResiduals(ref Vp8BoolDecoder br, int mbx, Macroblock mb, Row row, out bool anyTokens)
    {
        Span<short> coefficients = row.Coefficients;
        coefficients.Clear();
        Span<byte> nzTop = _nzAbove.AsSpan(mbx * 9, 9);
        Span<byte> nzLeft = row.NzLeft;
        ReadOnlySpan<short> q = _quant.AsSpan(mb.Segment * 6, 6);
        bool any = false;
        int tokens = 0;
        int first;
        int type;
        if (mb.HasY2)
        {
            Span<short> dc = stackalloc short[16];
            dc.Clear();
            int ctx = nzTop[8] + nzLeft[8];
            int nz = GetCoefficients(ref br, 1, ctx, q[2], q[3], 0, dc);
            nzTop[8] = nzLeft[8] = (byte)(nz > 0 ? 1 : 0);
            tokens = nz;
            if (nz > 1)
            {
                Vp8Dsp.InverseWalshHadamard(dc, coefficients);
            }
            else
            {
                short dc0 = (short)((dc[0] + 3) >> 3);
                for (int i = 0; i < 256; i += 16)
                {
                    coefficients[i] = dc0;
                }
            }

            first = 1;
            type = 0;
        }
        else
        {
            first = 0;
            type = 3;
        }

        for (int y = 0; y < 4; y++)
        {
            int l = nzLeft[y];
            for (int x = 0; x < 4; x++)
            {
                int block = (y * 4) + x;
                Span<short> c = coefficients.Slice(block * 16, 16);
                int nz = GetCoefficients(ref br, type, l + nzTop[x], q[0], q[1], first, c);
                l = nz > first ? 1 : 0;
                nzTop[x] = (byte)l;
                tokens |= l;
                row.Codes[block] = Code(nz, c[0]);
                any |= row.Codes[block] != 0;
            }

            nzLeft[y] = (byte)l;
        }

        for (int plane = 0; plane < 2; plane++)
        {
            int topAt = 4 + (plane * 2);
            int leftAt = 4 + (plane * 2);
            for (int y = 0; y < 2; y++)
            {
                int l = nzLeft[leftAt + y];
                for (int x = 0; x < 2; x++)
                {
                    int block = 16 + (plane * 4) + (y * 2) + x;
                    Span<short> c = coefficients.Slice(block * 16, 16);
                    int nz = GetCoefficients(ref br, 2, l + nzTop[topAt + x], q[4], q[5], 0, c);
                    l = nz > 0 ? 1 : 0;
                    nzTop[topAt + x] = (byte)l;
                    tokens |= l;
                    row.Codes[block] = Code(nz, c[0]);
                    any |= row.Codes[block] != 0;
                }

                nzLeft[leftAt + y] = (byte)l;
            }
        }

        anyTokens = tokens != 0;
        return any;
    }

    /// <summary>Returns how a block is transformed: 0 not at all, 1 DC only, 2 or 3 in full.</summary>
    private static byte Code(int nz, short dc) => (byte)(nz > 3 ? 3 : nz > 1 ? 2 : dc != 0 ? 1 : 0);

    /// <summary>
    /// Reads one block's tokens (RFC 6386 section 13) into <paramref name="output"/>, dequantized and in raster order, starting at
    /// coefficient <paramref name="n"/>; returns the position after the last token read (16 if the block ran to its end).
    /// </summary>
    private int GetCoefficients(ref Vp8BoolDecoder br, int type, int ctx, short dcQuant, short acQuant, int n, Span<short> output)
    {
        // The bit state lives in locals for the whole block: this loop is where most of a lossy decode's time goes.
        br.Take(out ulong value, out int bits, out uint range);
        ref byte probs = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_state.CoefficientProbabilities), type * 8 * 3 * 11);
        ref byte bands = ref MemoryMarshal.GetReference(BandOffsets);
        ref byte zigzag = ref MemoryMarshal.GetReference(Zigzag);
        _ = output[15];
        ref short outputs = ref MemoryMarshal.GetReference(output);
        int p = Unsafe.Add(ref bands, n) + (ctx * 11);
        int result = 16;
        for (; n < 16; n++)
        {
            if (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref probs, p)) == 0)
            {
                result = n;
                break;
            }

            bool done = false;
            while (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref probs, p + 1)) == 0)
            {
                n++;
                if (n == 16)
                {
                    done = true;
                    break;
                }

                p = Unsafe.Add(ref bands, n);
            }

            if (done)
            {
                break;
            }

            int v;
            int next = Unsafe.Add(ref bands, n + 1);
            if (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref probs, p + 2)) == 0)
            {
                v = 1;
                p = next + 11;
            }
            else
            {
                v = LargeValue(ref br, ref value, ref bits, ref range, ref Unsafe.Add(ref probs, p));
                p = next + 22;
            }

            int coefficient = Vp8BoolDecoder.Signed(ref br, ref value, ref bits, ref range, v) * (n > 0 ? acQuant : dcQuant);

            // Stored as 16 bits, as libwebp stores it, so even absurd values come out the same.
            Unsafe.Add(ref outputs, Unsafe.Add(ref zigzag, n)) = (short)coefficient;
        }

        br.Put(value, bits, range);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LargeValue(ref Vp8BoolDecoder br, ref ulong value, ref int bits, ref uint range, ref byte p)
    {
        if (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 3)) == 0)
        {
            return Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 4)) == 0
                ? 2 : 3 + Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 5));
        }

        if (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 6)) == 0)
        {
            if (Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 7)) == 0)
            {
                return 5 + Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, 159);
            }

            int high = Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, 165);
            return 7 + (2 * high) + Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, 145);
        }

        int bit1 = Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 8));
        int bit0 = Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, Unsafe.Add(ref p, 9 + bit1));
        int cat = (2 * bit1) + bit0;
        ReadOnlySpan<byte> table = cat switch { 0 => Cat3, 1 => Cat4, 2 => Cat5, _ => Cat6 };
        int v = 0;
        foreach (byte prob in table)
        {
            v += v + Vp8BoolDecoder.Bit(ref br, ref value, ref bits, ref range, prob);
        }

        return v + 3 + (8 << cat);
    }

    /// <summary>Predicts the macroblock, adds its residuals in the work buffers and copies it, unfiltered, into the current row.</summary>
    private void Reconstruct(int mbx, int mby, Macroblock mb, Row c)
    {
        if (mb.Reference != 0)
        {
            ReconstructInter(mbx, mby, mb, c);
            return;
        }

        Span<byte> wy = c.WorkY;
        Span<byte> wu = c.WorkU;
        Span<byte> wv = c.WorkV;
        const int S = Vp8Dsp.Stride;
        const int Y0 = S + 8;
        const int C0 = S + 8;

        ref byte y0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(c.WorkY), Y0);
        ref byte u0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(c.WorkU), C0);
        ref byte v0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(c.WorkV), C0);

        // The left samples (and the corner) from the macroblock just reconstructed: from the frame after an inter macroblock
        // when the row is filtered only once it is whole (the frame holds them unfiltered then), else from the work buffers.
        if (mbx > 0 && c.LeftInter && c.DeferFilter)
        {
            ref byte fy = ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsY), c.RowY + (mbx * 16) - 4);
            ref byte fu = ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsU), c.RowC + (mbx * 8) - 4);
            ref byte fv = ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsV), c.RowC + (mbx * 8) - 4);
            _ = RowsY.Slice(c.RowY + (mbx * 16) - 4 + (15 * StrideY), 4);
            _ = RowsU.Slice(c.RowC + (mbx * 8) - 4 + (7 * StrideC), 4);
            _ = RowsV.Slice(c.RowC + (mbx * 8) - 4 + (7 * StrideC), 4);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref y0, -S - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref y0, -S + 12)));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref u0, -S - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref u0, -S + 4)));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref v0, -S - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref v0, -S + 4)));
            for (int j = 0; j < 16; j++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref y0, (j * S) - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref fy, j * StrideY)));
            }

            for (int j = 0; j < 8; j++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref u0, (j * S) - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref fu, j * StrideC)));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref v0, (j * S) - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref fv, j * StrideC)));
            }
        }
        else if (mbx > 0)
        {
            for (int j = -1; j < 16; j++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref y0, (j * S) - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref y0, (j * S) + 12)));
            }

            for (int j = -1; j < 8; j++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref u0, (j * S) - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref u0, (j * S) + 4)));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref v0, (j * S) - 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref v0, (j * S) + 4)));
            }
        }

        c.LeftInter = false;
        if (mby > 0)
        {
            _topY.AsSpan(mbx * 16, 16).CopyTo(wy.Slice(Y0 - S, 16));
            _topU.AsSpan(mbx * 8, 8).CopyTo(wu.Slice(C0 - S, 8));
            _topV.AsSpan(mbx * 8, 8).CopyTo(wv.Slice(C0 - S, 8));
        }

        ReadOnlySpan<short> coefficients = c.Coefficients;
        if (mb.Intra4x4)
        {
            // The four pixels above and right of the macroblock, repeated down the right edge for the blocks below the first row.
            Span<byte> topRight = wy.Slice(Y0 - S + 16, 4);
            if (mby > 0)
            {
                if (mbx >= MbWidth - 1)
                {
                    topRight.Fill(_topY[(mbx * 16) + 15]);
                }
                else
                {
                    _topY.AsSpan((mbx + 1) * 16, 4).CopyTo(topRight);
                }
            }

            for (int k = 1; k <= 3; k++)
            {
                topRight.CopyTo(wy.Slice(Y0 + (((k * 4) - 1) * S) + 16, 4));
            }

            ReadOnlySpan<byte> subModes = _video is null ? _subModes : _video.Modes.SubModes.AsSpan(_video.Modes.Index(mbx, mby) * 16, 16);
            for (int n = 0; n < 16; n++)
            {
                int at = Y0 + ((n >> 2) * 4 * S) + ((n & 3) * 4);
                Vp8Dsp.PredictSubblock(subModes[n], wy, at);
                AddResidual(c.Codes[n], coefficients.Slice(n * 16, 16), wy, at);
            }
        }
        else
        {
            Vp8Dsp.PredictBlock(EdgeMode(mb.YMode, mbx, mby), wy, Y0, 16);
            AddResiduals(c, 0, 16, ref y0, S);
        }

        Vp8Dsp.BlockMode chroma = EdgeMode(mb.UvMode, mbx, mby);
        Vp8Dsp.PredictBlock(chroma, wu, C0, 8);
        Vp8Dsp.PredictBlock(chroma, wv, C0, 8);
        AddResiduals(c, 16, 4, ref u0, S);
        AddResiduals(c, 20, 4, ref v0, S);

        // Unfiltered bottom rows for the macroblock row below, and the pixels into the current row.
        if (mby < MbHeight - 1)
        {
            wy.Slice(Y0 + (15 * S), 16).CopyTo(_topY.AsSpan(mbx * 16, 16));
            wu.Slice(C0 + (7 * S), 8).CopyTo(_topU.AsSpan(mbx * 8, 8));
            wv.Slice(C0 + (7 * S), 8).CopyTo(_topV.AsSpan(mbx * 8, 8));
        }

        // The rows the macroblock covers lie inside the planes: the row buffer or frame buffer was sized for every macroblock.
        _ = RowsY.Slice(c.RowY + (15 * StrideY) + (mbx * 16), 16);
        _ = RowsU.Slice(c.RowC + (7 * StrideC) + (mbx * 8), 8);
        _ = RowsV.Slice(c.RowC + (7 * StrideC) + (mbx * 8), 8);
        ref byte dy = ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsY), c.RowY + (mbx * 16));
        ref byte du = ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsU), c.RowC + (mbx * 8));
        ref byte dv = ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsV), c.RowC + (mbx * 8));
        for (int j = 0; j < 16; j++)
        {
            Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dy, j * StrideY), ref Unsafe.Add(ref y0, j * S), 16);
        }

        for (int j = 0; j < 8; j++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref du, j * StrideC), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref u0, j * S)));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dv, j * StrideC), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref v0, j * S)));
        }
    }

    /// <summary>
    /// Adds the residuals of <paramref name="count"/> blocks from <paramref name="first"/> (16 luma blocks or 4 of one chroma
    /// plane, four or two a row) to the pixels at <paramref name="dst"/>, two side-by-side blocks at a time: one 16-bit
    /// transform for both when either has AC coefficients, one DC add for both when neither does, nothing when both are empty.
    /// The caller has checked that the 16 × 16 or 8 × 8 pixels lie in their plane.
    /// </summary>
    private static void AddResiduals(Row c, int first, int count, ref byte dst, int stride)
    {
        ref byte codes = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(c.Codes), first);
        ref short coefficients = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(c.Coefficients), first * 16);
        int rowShift = count == 16 ? 2 : 1;
        for (int n = 0; n < count; n += 2)
        {
            int left = Unsafe.Add(ref codes, n), right = Unsafe.Add(ref codes, n + 1);
            if ((left | right) == 0)
            {
                continue;
            }

            ref byte at = ref Unsafe.Add(ref dst, ((n >> rowShift) * 4 * stride) + ((n & ((1 << rowShift) - 1)) * 4));
            ref short pair = ref Unsafe.Add(ref coefficients, n * 16);
            if (!Vp8TransformSimd.IsSupported)
            {
                AddResidual((byte)left, MemoryMarshal.CreateReadOnlySpan(ref pair, 16), MemoryMarshal.CreateSpan(ref at, (3 * stride) + 4), 0, stride);
                AddResidual((byte)right, MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref pair, 16), 16), MemoryMarshal.CreateSpan(ref Unsafe.Add(ref at, 4), (3 * stride) + 4), 0, stride);
            }
            else if (left > 1 || right > 1)
            {
                Vp8TransformSimd.InverseTransformAddTwo(ref pair, ref at, stride);
            }
            else
            {
                Vp8TransformSimd.DcAddTwo(pair, Unsafe.Add(ref pair, 16), ref at, stride);
            }
        }
    }

    private static void AddResidual(byte code, ReadOnlySpan<short> coefficients, Span<byte> buffer, int at, int stride = Vp8Dsp.Stride)
    {
        if (code == 1)
        {
            Vp8Dsp.InverseTransformDcAdd(coefficients[0], buffer, at, stride);
        }
        else if (code > 1)
        {
            Vp8Dsp.InverseTransformAdd(coefficients, buffer, at, stride);
        }
    }

    /// <summary>
    /// Reconstructs an inter macroblock straight in the frame buffer: its prediction, then its residuals. The work buffers get
    /// its right columns and the row above's corner, unfiltered, for an intra macroblock to its right.
    /// </summary>
    private void ReconstructInter(int mbx, int mby, Macroblock mb, Row c)
    {
        const int S = Vp8Dsp.Stride;
        const int Y0 = S + 8;
        const int C0 = S + 8;
        int dy = c.RowY + (mbx * 16);
        int dc = c.RowC + (mbx * 8);
        Vp8StageTimes.Lap(Vp8Stage.Reconstruct);
        PredictInter(mbx, mby, c, dy, dc);
        Vp8StageTimes.Lap(Vp8Stage.Inter);
        if (c.AnyCoefficients)
        {
            _ = RowsY.Slice(dy + (15 * StrideY), 16);
            _ = RowsU.Slice(dc + (7 * StrideC), 8);
            _ = RowsV.Slice(dc + (7 * StrideC), 8);
            AddResiduals(c, 0, 16, ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsY), dy), StrideY);
            AddResiduals(c, 16, 4, ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsU), dc), StrideC);
            AddResiduals(c, 20, 4, ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsV), dc), StrideC);
        }

        _ = RowsY.Slice(dy + (15 * StrideY), 16);
        _ = RowsU.Slice(dc + (7 * StrideC), 8);
        _ = RowsV.Slice(dc + (7 * StrideC), 8);
        ref byte fy = ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsY), dy);
        ref byte fu = ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsU), dc);
        ref byte fv = ref Unsafe.Add(ref MemoryMarshal.GetReference(RowsV), dc);
        ref byte y0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(c.WorkY), Y0);
        ref byte u0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(c.WorkU), C0);
        ref byte v0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(c.WorkV), C0);
        c.LeftInter = true;
        if (!c.DeferFilter)
        {
            // Filtered macroblock by macroblock, this one's right columns are changed before the next one is reconstructed:
            // keep them unfiltered for an intra macroblock to its right.
            for (int j = 0; j < 16; j++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref y0, (j * S) + 12), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref fy, (j * StrideY) + 12)));
            }

            for (int j = 0; j < 8; j++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref u0, (j * S) + 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref fu, (j * StrideC) + 4)));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref v0, (j * S) + 4), Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref fv, (j * StrideC) + 4)));
            }
        }

        if (mby > 0)
        {
            // The corner the next macroblock sees: the row above, before this macroblock's bottom row replaces it.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref y0, -S + 12), Unsafe.ReadUnaligned<uint>(ref _topY[(mbx * 16) + 12]));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref u0, -S + 4), Unsafe.ReadUnaligned<uint>(ref _topU[(mbx * 8) + 4]));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref v0, -S + 4), Unsafe.ReadUnaligned<uint>(ref _topV[(mbx * 8) + 4]));
        }

        if (mby < MbHeight - 1)
        {
            RowsY.Slice(dy + (15 * StrideY), 16).CopyTo(_topY.AsSpan(mbx * 16, 16));
            RowsU.Slice(dc + (7 * StrideC), 8).CopyTo(_topU.AsSpan(mbx * 8, 8));
            RowsV.Slice(dc + (7 * StrideC), 8).CopyTo(_topV.AsSpan(mbx * 8, 8));
        }
    }

    /// <summary>Maps a 16 × 16 or chroma mode to its prediction, using the DC variants at the frame's top and left edges.</summary>
    internal static Vp8Dsp.BlockMode EdgeMode(int mode, int mbx, int mby)
    {
        if (mode != 0)
        {
            return (Vp8Dsp.BlockMode)mode;
        }

        return mbx == 0 ? (mby == 0 ? Vp8Dsp.BlockMode.DcNoTopLeft : Vp8Dsp.BlockMode.DcNoLeft) : (mby == 0 ? Vp8Dsp.BlockMode.DcNoTop : Vp8Dsp.BlockMode.Dc);
    }

    /// <summary>Filters one macroblock in the row buffer (RFC 6386 section 15): its left edge, inner vertical edges, top edge, inner horizontal edges.</summary>
    private void FilterMacroblock(int mbx, int mby, Row c)
    {
        MacroblockFilter f = c.Filter[mbx];
        int limit = f.Info.Limit;
        if (limit == 0)
        {
            return;
        }

        int sy = StrideY;
        int at = c.RowY + (mbx * 16);
        int interior = f.Info.Interior;
        int hev = f.Info.HevThreshold;
        Vp8FilterSimd.Kind edge = _filterType == 1 ? Vp8FilterSimd.Kind.Simple : Vp8FilterSimd.Kind.MacroblockEdge;
        Vp8FilterSimd.Kind inner = _filterType == 1 ? Vp8FilterSimd.Kind.Simple : Vp8FilterSimd.Kind.Inner;
        bool chroma = _filterType == 2;
        int sc = StrideC;
        int ac = c.RowC + (mbx * 8);
        if (mbx > 0)
        {
            LumaEdge(vertical: true, at, edge, limit + 4, interior, hev);
            if (chroma)
            {
                ChromaEdge(vertical: true, ac, edge, limit + 4, interior, hev);
            }
        }

        if (f.Inner)
        {
            if (Vp8FilterSimd.Columns)
            {
                Vp8FilterSimd.InnerVerticalEdges16(RowsY, at, sy, inner, limit, interior, hev);
            }
            else
            {
                for (int k = 4; k < 16; k += 4)
                {
                    LumaEdge(vertical: true, at + k, inner, limit, interior, hev);
                }
            }

            if (chroma)
            {
                ChromaEdge(vertical: true, ac + 4, inner, limit, interior, hev);
            }
        }

        if (mby > 0)
        {
            LumaEdge(vertical: false, at, edge, limit + 4, interior, hev);
            if (chroma)
            {
                ChromaEdge(vertical: false, ac, edge, limit + 4, interior, hev);
            }
        }

        if (f.Inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                LumaEdge(vertical: false, at + (k * sy), inner, limit, interior, hev);
            }

            if (chroma)
            {
                ChromaEdge(vertical: false, ac + (4 * sc), inner, limit, interior, hev);
            }
        }
    }

    /// <summary>
    /// Filters the luma of one macroblock of a plane with the normal filter, as <see cref="FilterMacroblock"/> does: its left edge
    /// unless it is in the first column, inner vertical edges, its top edge unless it is in the first row, inner horizontal edges.
    /// The encoder tries loop filter levels with it.
    /// </summary>
    internal static void FilterLuma(Span<byte> y, int at, int stride, FilterInfo info, bool left, bool top, bool inner)
    {
        int limit = info.Limit;
        if (limit == 0)
        {
            return;
        }

        int interior = info.Interior;
        int hev = info.HevThreshold;
        if (left)
        {
            LumaEdge(y, stride, vertical: true, at, Vp8FilterSimd.Kind.MacroblockEdge, limit + 4, interior, hev);
        }

        if (inner)
        {
            if (Vp8FilterSimd.Columns)
            {
                Vp8FilterSimd.InnerVerticalEdges16(y, at, stride, Vp8FilterSimd.Kind.Inner, limit, interior, hev);
            }
            else
            {
                for (int k = 4; k < 16; k += 4)
                {
                    LumaEdge(y, stride, vertical: true, at + k, Vp8FilterSimd.Kind.Inner, limit, interior, hev);
                }
            }
        }

        if (top)
        {
            LumaEdge(y, stride, vertical: false, at, Vp8FilterSimd.Kind.MacroblockEdge, limit + 4, interior, hev);
        }

        if (inner)
        {
            for (int k = 4; k < 16; k += 4)
            {
                LumaEdge(y, stride, vertical: false, at + (k * stride), Vp8FilterSimd.Kind.Inner, limit, interior, hev);
            }
        }
    }

    /// <summary>Filters one luma edge 16 pixels long: a vertical edge (between columns) or a horizontal one (between rows).</summary>
    private void LumaEdge(bool vertical, int at, Vp8FilterSimd.Kind kind, int threshold, int interior, int hev) =>
        LumaEdge(RowsY, StrideY, vertical, at, kind, threshold, interior, hev);

    private static void LumaEdge(Span<byte> y, int stride, bool vertical, int at, Vp8FilterSimd.Kind kind, int threshold, int interior, int hev)
    {
        if (vertical ? Vp8FilterSimd.Columns : Vp8FilterSimd.Rows)
        {
            if (vertical)
            {
                Vp8FilterSimd.VerticalEdge16(y, at, stride, kind, threshold, interior, hev);
            }
            else
            {
                Vp8FilterSimd.HorizontalEdge16(y, at, stride, kind, threshold, interior, hev);
            }

            return;
        }

        int step = vertical ? 1 : stride;
        int along = vertical ? stride : 1;
        switch (kind)
        {
            case Vp8FilterSimd.Kind.Simple:
                Vp8Dsp.SimpleFilter(y, at, step, along, 16, threshold);
                break;
            case Vp8FilterSimd.Kind.MacroblockEdge:
                Vp8Dsp.MacroblockEdgeFilter(y, at, step, along, 16, threshold, interior, hev);
                break;
            default:
                Vp8Dsp.InnerEdgeFilter(y, at, step, along, 16, threshold, interior, hev);
                break;
        }
    }

    /// <summary>Filters one edge of U and of V, 8 pixels long each.</summary>
    private void ChromaEdge(bool vertical, int at, Vp8FilterSimd.Kind kind, int threshold, int interior, int hev)
    {
        Span<byte> u = RowsU;
        Span<byte> v = RowsV;
        int stride = StrideC;
        if (vertical ? Vp8FilterSimd.Columns : Vp8FilterSimd.Rows)
        {
            if (vertical)
            {
                Vp8FilterSimd.VerticalEdge8(u, v, at, stride, kind, threshold, interior, hev);
            }
            else
            {
                Vp8FilterSimd.HorizontalEdge8(u, v, at, stride, kind, threshold, interior, hev);
            }

            return;
        }

        int step = vertical ? 1 : stride;
        int along = vertical ? stride : 1;
        if (kind == Vp8FilterSimd.Kind.MacroblockEdge)
        {
            Vp8Dsp.MacroblockEdgeFilter(u, at, step, along, 8, threshold, interior, hev);
            Vp8Dsp.MacroblockEdgeFilter(v, at, step, along, 8, threshold, interior, hev);
        }
        else
        {
            Vp8Dsp.InnerEdgeFilter(u, at, step, along, 8, threshold, interior, hev);
            Vp8Dsp.InnerEdgeFilter(v, at, step, along, 8, threshold, interior, hev);
        }
    }

    /// <summary>What a macroblock row changes, kept so an incremental decode can undo a row that ran out of bytes.</summary>
    private sealed class Snapshot(Vp8Decoder decoder)
    {
        private readonly byte[] _modesAbove = new byte[decoder._modesAbove.Length];
        private readonly byte[] _nzAbove = new byte[decoder._nzAbove.Length];
        private readonly byte[] _topY = new byte[decoder._topY.Length];
        private readonly byte[] _topU = new byte[decoder._topU.Length];
        private readonly byte[] _topV = new byte[decoder._topV.Length];
        private readonly byte[] _tailY = new byte[decoder.StrideY * Tail];
        private readonly byte[] _tailU = new byte[decoder.StrideC * ChromaTail];
        private readonly byte[] _tailV = new byte[decoder.StrideC * ChromaTail];
        private Vp8BoolDecoder _modes;
        private Vp8BoolDecoder _tokens;

        internal void Save(Vp8Decoder d, int mby)
        {
            d._modesAbove.CopyTo(_modesAbove, 0);
            d._nzAbove.CopyTo(_nzAbove, 0);
            d._topY.CopyTo(_topY, 0);
            d._topU.CopyTo(_topU, 0);
            d._topV.CopyTo(_topV, 0);
            Array.Copy(d._rentY!, _tailY, _tailY.Length);
            Array.Copy(d._rentU!, _tailU, _tailU.Length);
            Array.Copy(d._rentV!, _tailV, _tailV.Length);
            _modes = d._modes;
            _tokens = d._partitions[mby & d._partitionMask];
        }

        internal void Restore(Vp8Decoder d, int mby)
        {
            _modesAbove.CopyTo(d._modesAbove, 0);
            _nzAbove.CopyTo(d._nzAbove, 0);
            _topY.CopyTo(d._topY, 0);
            _topU.CopyTo(d._topU, 0);
            _topV.CopyTo(d._topV, 0);
            Array.Copy(_tailY, d._rentY!, _tailY.Length);
            Array.Copy(_tailU, d._rentU!, _tailU.Length);
            Array.Copy(_tailV, d._rentV!, _tailV.Length);
            d._modes = _modes;
            d._partitions[mby & d._partitionMask] = _tokens;
        }
    }

    /// <summary>The state one macroblock row works in: the work buffers with their borders, the left contexts, the coefficients.</summary>
    private sealed class Row(int mbWidth)
    {
        internal readonly byte[] WorkY = new byte[Vp8Dsp.Stride * 17];
        internal readonly byte[] WorkU = new byte[Vp8Dsp.Stride * 9];
        internal readonly byte[] WorkV = new byte[Vp8Dsp.Stride * 9];
        internal readonly byte[] NzLeft = new byte[9];
        internal readonly short[] Coefficients = new short[384];
        internal readonly byte[] Codes = new byte[24];
        internal readonly MacroblockFilter[] Filter = new MacroblockFilter[mbWidth];

        /// <summary>Where the row's first luma and chroma pixels go in the planes being written.</summary>
        internal int RowY;
        internal int RowC;

        /// <summary>Whether any block of the macroblock being reconstructed has a residual.</summary>
        internal bool AnyCoefficients;

        /// <summary>Whether the row is filtered only once it is whole (one-thread video), not macroblock by macroblock.</summary>
        internal bool DeferFilter;

        /// <summary>Whether the macroblock to the left of the one being reconstructed was an inter macroblock.</summary>
        internal bool LeftInter;
    }

    /// <summary>Receives the rows a call to <see cref="DecodeRow"/> made final: luma rows [from, to) and chroma rows [chromaFrom, chromaTo).</summary>
    internal delegate void RowSink(Vp8Decoder decoder, int from, int to, int chromaFrom, int chromaTo);

    /// <summary>A macroblock's modes: <paramref name="Mode"/> numbered as <see cref="Vp8ModeInfo"/> numbers them, the reference 0 for intra.</summary>
    private readonly record struct Macroblock(int Segment, bool Skip, int Mode, int UvMode, int Reference = 0)
    {
        internal bool Intra4x4 => Mode == Vp8ModeInfo.BPred;

        /// <summary>Gets the 16 × 16 intra mode, DC, V, H or TM.</summary>
        internal int YMode => Mode;

        /// <summary>Gets whether the luma DCs go through the second-order block: all but B_PRED and split macroblocks.</summary>
        internal bool HasY2 => Mode != Vp8ModeInfo.BPred && Mode != Vp8ModeInfo.Split;
    }

    /// <summary>A filter strength: the edge limit, the interior limit and the high-variance threshold; a limit of 0 means no filtering.</summary>
    internal readonly record struct FilterInfo(int Limit, int Interior, int HevThreshold);

    private readonly record struct MacroblockFilter(FilterInfo Info, bool Inner);
}

/// <summary>The frame tag of a VP8 frame (RFC 6386 section 9.1), and for a key frame its size.</summary>
internal readonly record struct Vp8FrameHeader(int Width, int Height, int FirstPartitionSize, bool KeyFrame = true, int Version = 0, bool Show = true, int HorizontalScale = 0, int VerticalScale = 0);
