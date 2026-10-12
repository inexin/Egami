namespace Egami.Formats.Webp;

/// <summary>
/// Decodes a VP8 stream frame by frame (RFC 6386), the engine behind the public stream decoder: it keeps the state that lasts
/// between frames and four frame buffers, which the last, golden and alternate references and the frame being decoded point at.
/// Slots that hold the same frame share a buffer, and buffers are picked and counted as libvpx picks and counts them, so even
/// libvpx's quirk of leaving a macroblock's chroma unpredicted (vectors far outside a version 3 frame) reads the same old pixels.
/// </summary>
internal sealed class Vp8VideoDecoder : IDisposable
{
    /// <summary>The format name diagnostics and exceptions carry.</summary>
    internal const string FormatName = "VP8";

    private const int Last = 1, Golden = 2, Alternate = 3;

    private readonly Vp8FrameBuffer?[] _buffers = new Vp8FrameBuffer?[4];
    private readonly int[] _counts = new int[4];
    private int _last, _golden, _alternate, _new;
    private bool _haveKeyFrame;
    private readonly IPixelMemorySource _memory;
    private readonly SafetyLimits _limits;
    private readonly List<(Thread Thread, SemaphoreSlim Start)> _workers = [];
    private readonly CountdownEvent _done = new(0);
    private Action<int>? _job;
    private Exception? _failure;
    private bool _abort;
    private volatile bool _stopping;
    private Vp8FrameHeader _lastKey;

    /// <summary>
    /// Starts a stream that decodes with up to <paramref name="threads"/> threads, macroblock rows in a wavefront, its frame
    /// buffers from <paramref name="memory"/> (Egami's pool by default) and its frame size within <paramref name="limits"/>.
    /// </summary>
    internal Vp8VideoDecoder(int threads = 1, IPixelMemorySource? memory = null, SafetyLimits? limits = null)
    {
        Threads = Math.Max(1, threads);
        _memory = memory ?? EgamiContext.Default.Memory;
        _limits = limits ?? SafetyLimits.Default;
    }

    /// <summary>Gets the most threads a frame decodes with.</summary>
    internal int Threads { get; }

    /// <summary>Gets the state that lasts from frame to frame.</summary>
    internal Vp8StreamState State { get; } = new();

    /// <summary>Gets the modes of the frame decoded last; empty until the first key frame.</summary>
    internal Vp8ModeInfo Modes { get; private set; } = new(0, 0);

    /// <summary>Gets the buffer the frame being decoded goes into.</summary>
    internal Vp8FrameBuffer? Target => _buffers[_new];

    /// <summary>Gets the frame size of the last key frame.</summary>
    internal int Width { get; private set; }

    internal int Height { get; private set; }

    /// <summary>Gets the decoder of the frame decoded last, for its header fields.</summary>
    internal Vp8Decoder? LastFrame { get; private set; }

    /// <summary>Gets the frame to show after the last <see cref="Decode"/>, or null when it was a hidden frame.</summary>
    internal Vp8FrameBuffer? Shown { get; private set; }

    /// <summary>Gets the bytes the frame buffers hold.</summary>
    internal long BufferBytes => _buffers[0] is null ? 0 : 4 * Vp8FrameBuffer.BytesFor(Width, Height);

    /// <summary>Gets the buffer a reference slot (1 last, 2 golden, 3 alternate) points at.</summary>
    internal Vp8FrameBuffer Reference(int slot) => _buffers[slot == Last ? _last : slot == Golden ? _golden : _alternate]!;

    /// <summary>
    /// Gets or sets whether broken frames are concealed rather than thrown: a frame whose data runs out has its damaged
    /// macroblocks copied from the previous frame (mid-gray when there is none), a frame whose header cannot be read repeats the
    /// previous frame, and an inter frame with no key frame before it predicts from mid-gray references.
    /// </summary>
    internal bool Conceal { get; set; }

    /// <summary>Gets whether the last <see cref="Decode"/> concealed anything.</summary>
    internal bool Concealed { get; private set; }

    /// <summary>
    /// Decodes the frame in <paramref name="data"/>[<paramref name="offset"/>..+<paramref name="length"/>], updates the references
    /// and returns its header. <see cref="Shown"/> is then the frame to show. Broken data throws
    /// <see cref="CorruptDataException"/> unless <see cref="Conceal"/> is set.
    /// </summary>
    internal Vp8FrameHeader Decode(byte[] data, int offset, int length)
    {
        Concealed = false;
        Shown = null;
        Vp8FrameHeader header;
        try
        {
            header = Vp8Decoder.ReadVideoHeader(data.AsSpan(offset, length), _lastKey);
        }
        catch (CorruptDataException) when (Conceal)
        {
            return RepeatLast();
        }

        if (!header.KeyFrame && !_haveKeyFrame)
        {
            if (!Conceal)
            {
                throw new CorruptDataException(FormatName, 0, "The VP8 frame is an inter frame with no key frame before it; decoding starts at a key frame.");
            }

            Concealed = true;
            if (_buffers[0] is null)
            {
                // No key frame has ever given the frame size: there is nothing to show.
                return header with { Show = false };
            }

            Reference(Last).Fill(128);
            Reference(Golden).Fill(128);
            Reference(Alternate).Fill(128);
            State.ResetForKeyFrame();
            _haveKeyFrame = true;
        }

        if (header.KeyFrame)
        {
            if (header.Width != Width || header.Height != Height || _buffers[0] is null)
            {
                Allocate(header.Width, header.Height);
            }

            State.ResetForKeyFrame();
            _lastKey = header;
        }

        _new = FreeBuffer();
        Vp8Decoder decoder;
        try
        {
            decoder = Vp8Decoder.CreateVideo(data, offset, length, header, this);
            decoder.DecodeFrame();
        }
        catch (CorruptDataException) when (Conceal)
        {
            _counts[_new]--;
            return RepeatLast();
        }
        catch
        {
            _counts[_new]--;
            throw;
        }

        int damaged = decoder.Damaged!.Count(d => d);
        if (damaged > 0)
        {
            if (!Conceal)
            {
                _counts[_new]--;
                if (!decoder.RefreshEntropy)
                {
                    State.RestoreEntropy();
                }

                throw new CorruptDataException(FormatName, 0, $"The VP8 frame's data runs out before {damaged} of its {decoder.Damaged!.Length} macroblocks are read.");
            }

            ConcealMacroblocks(decoder);
            Concealed = true;
        }

        _buffers[_new]!.ExtendBorders();
        if (!decoder.RefreshEntropy)
        {
            State.RestoreEntropy();
        }

        _haveKeyFrame |= header.KeyFrame;
        LastFrame = decoder;
        UpdateReferences(decoder, header.KeyFrame);
        return header;
    }

    /// <summary>Concealment of a frame that could not be decoded: the last frame is shown again, the references are kept.</summary>
    private Vp8FrameHeader RepeatLast()
    {
        Concealed = true;
        if (!_haveKeyFrame || _buffers[0] is null)
        {
            return new Vp8FrameHeader(Width, Height, 0, KeyFrame: false, Show: false);
        }

        Shown = Reference(Last);
        return new Vp8FrameHeader(Width, Height, 0, KeyFrame: false, Version: _lastKey.Version);
    }

    /// <summary>Replaces the damaged macroblocks of the frame just decoded with the previous frame's, or mid-gray when there is none.</summary>
    private void ConcealMacroblocks(Vp8Decoder frame)
    {
        Vp8FrameBuffer target = _buffers[_new]!;
        Vp8FrameBuffer? previous = _haveKeyFrame ? Reference(Last) : null;
        bool[] damaged = frame.Damaged!;
        for (int i = 0; i < damaged.Length; i++)
        {
            if (!damaged[i])
            {
                continue;
            }

            int x = i % frame.MbWidth, y = i / frame.MbWidth;
            Copy(target.Y, target.OriginY + (y * 16 * target.StrideY) + (x * 16), target.StrideY, 16, previous is null ? default : previous.Y);
            Copy(target.U, target.OriginC + (y * 8 * target.StrideC) + (x * 8), target.StrideC, 8, previous is null ? default : previous.U);
            Copy(target.V, target.OriginC + (y * 8 * target.StrideC) + (x * 8), target.StrideC, 8, previous is null ? default : previous.V);
        }

        static void Copy(Span<byte> to, int at, int stride, int size, Span<byte> from)
        {
            for (int r = 0; r < size; r++)
            {
                Span<byte> row = to.Slice(at + (r * stride), size);
                if (from.IsEmpty)
                {
                    row.Fill(128);
                }
                else
                {
                    from.Slice(at + (r * stride), size).CopyTo(row);
                }
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> for workers 0 to <paramref name="workers"/> − 1 at once: worker 0 on the calling thread, the
    /// others on threads the decoder keeps for its life. A worker that throws stops the others' waits, and the first exception is
    /// thrown here once all have returned.
    /// </summary>
    internal void RunWorkers(int workers, Action<int> work)
    {
        while (_workers.Count < workers - 1)
        {
            int index = _workers.Count + 1;
            SemaphoreSlim start = new(0);
            Thread thread = new(() => WorkerLoop(index, start)) { IsBackground = true, Name = "Egami VP8 rows" };
            _workers.Add((thread, start));
            thread.Start();
        }

        _job = work;
        _failure = null;
        _abort = false;
        _done.Reset(workers - 1);
        for (int i = 0; i < workers - 1; i++)
        {
            _workers[i].Start.Release();
        }

        try
        {
            work(0);
        }
        catch (Exception e)
        {
            Fail(e);
        }

        _done.Wait();
        _job = null;
        if (_failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(_failure);
        }
    }

    /// <summary>Waits until <paramref name="progress"/>[<paramref name="row"/>] reaches <paramref name="count"/> macroblocks.</summary>
    internal void WaitFor(int[] progress, int row, int count)
    {
        SpinWait spin = default;
        while (Volatile.Read(ref progress[row]) < count)
        {
            if (Volatile.Read(ref _abort))
            {
                throw new OperationCanceledException();
            }

            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        foreach ((Thread _, SemaphoreSlim start) in _workers)
        {
            start.Release();
        }

        _workers.Clear();
        FreeBuffers();
        Shown = null;
    }

    private void FreeBuffers()
    {
        for (int i = 0; i < 4; i++)
        {
            _buffers[i]?.Dispose();
            _buffers[i] = null;
        }
    }

    private void WorkerLoop(int index, SemaphoreSlim start)
    {
        while (true)
        {
            start.Wait();
            if (_stopping)
            {
                return;
            }

            try
            {
                _job!(index);
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                _done.Signal();
            }
        }
    }

    private void Fail(Exception e)
    {
        if (e is not OperationCanceledException)
        {
            Interlocked.CompareExchange(ref _failure, e, null);
        }

        Volatile.Write(ref _abort, true);
    }

    /// <summary>Drops the references; the next frame must be a key frame.</summary>
    internal void Reset()
    {
        _haveKeyFrame = false;
        Shown = null;
        LastFrame = null;
    }

    private void Allocate(int width, int height)
    {
        _limits.CheckFrameSize(width, height);
        long bytes = 4 * Vp8FrameBuffer.BytesFor(width, height);
        if (bytes > _limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), _limits.MaxTotalBytes, bytes);
        }

        FreeBuffers();
        Width = width;
        Height = height;
        for (int i = 0; i < 4; i++)
        {
            _buffers[i] = new Vp8FrameBuffer(width, height, _memory);
        }

        // As libvpx sets them up: the new frame takes buffer 0, the references the other three.
        _new = 0;
        _last = 1;
        _golden = 2;
        _alternate = 3;
        _counts.AsSpan().Fill(1);
        _counts[0] = 0;
        int mbw = (width + 15) >> 4;
        int mbh = (height + 15) >> 4;
        Modes = new Vp8ModeInfo(mbw, mbh);
        State.SegmentMap = new byte[mbw * mbh];
    }

    private int FreeBuffer()
    {
        int i = Array.IndexOf(_counts, 0);
        _counts[i] = 1;
        return i;
    }

    private void Point(ref int slot, int buffer)
    {
        if (_counts[slot] > 0)
        {
            _counts[slot]--;
        }

        slot = buffer;
        _counts[buffer]++;
    }

    /// <summary>Copies and refreshes the references as the frame header says, in libvpx's order: the alternate copy first, then the golden copy, then the refreshes.</summary>
    private void UpdateReferences(Vp8Decoder frame, bool key)
    {
        if (!key)
        {
            if (frame.CopyToAlternate == 3 || frame.CopyToGolden == 3)
            {
                _counts[_new]--;
                throw new CorruptDataException(FormatName, 0, "The VP8 frame copies a reference from an unknown buffer.");
            }

            if (frame.CopyToAlternate != 0)
            {
                Point(ref _alternate, frame.CopyToAlternate == 1 ? _last : _golden);
            }

            if (frame.CopyToGolden != 0)
            {
                Point(ref _golden, frame.CopyToGolden == 1 ? _last : _alternate);
            }
        }

        if (key || frame.RefreshGolden)
        {
            Point(ref _golden, _new);
        }

        if (key || frame.RefreshAlternate)
        {
            Point(ref _alternate, _new);
        }

        Vp8FrameBuffer shown;
        if (frame.RefreshLast)
        {
            Point(ref _last, _new);
            shown = _buffers[_last]!;
        }
        else
        {
            shown = _buffers[_new]!;
        }

        _counts[_new]--;
        Shown = frame.Header.Show ? shown : null;
    }
}
