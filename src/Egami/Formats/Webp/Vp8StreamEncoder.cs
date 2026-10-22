using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <summary>
/// Encodes a VP8 video stream (RFC 6386) a frame at a time: key frames, and inter frames that search the last, golden and
/// alternate references for motion and code every VP8 vector mode. Each frame's <see cref="Vp8FramePlan"/> says what it is (key
/// or inter, shown or hidden), its quality or quantizer, and which references it uses and refreshes, so rate control and
/// group-of-frames planning stay with the caller: Egami.Video drives this encoder for WebM, MKV and IVF, and any other source of
/// frames can too. The frames decode in libvpx exactly as in <see cref="Vp8StreamDecoder"/>.
/// </summary>
/// <remarks>
/// The encoder keeps the stream's references in four frame buffers from the context's <see cref="EgamiContext.Memory"/> and
/// decodes each frame it writes to keep them, on up to <see cref="EgamiContext.MaxParallelism"/> threads; the output does not
/// depend on the thread count. The frame size is checked against the context's <see cref="EgamiContext.Limits"/>. An instance is
/// not thread-safe.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Vp8.cs" region="vp8-encode"/>
/// </example>
public sealed class Vp8StreamEncoder : IDisposable
{
    private readonly Vp8Planes _planes;
    private readonly Vp8Encoder _encoder;
    private readonly Vp8VideoDecoder _decoder;
    private readonly Vp8StreamState _saved = new();
    private bool _started;

    // The loop filter level the last inter frame searched its way to, or −1.
    private int _filterLevel = -1;
    private bool _disposed;

    /// <summary>Starts a stream of <paramref name="width"/> × <paramref name="height"/> frames.</summary>
    /// <param name="width">The frame width, 1 to 16383.</param>
    /// <param name="height">The frame height, 1 to 16383.</param>
    /// <param name="context">The memory, threads and limits to use; <see cref="EgamiContext.Default"/> when null.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is outside 1 to 16383.</exception>
    /// <exception cref="LimitExceededException">The frame size breaks the context's safety limits.</exception>
    public Vp8StreamEncoder(int width, int height, EgamiContext? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 16383);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 16383);
        EgamiContext c = context ?? EgamiContext.Default;
        c.Limits.CheckFrameSize(width, height);
        Width = width;
        Height = height;
        _planes = new Vp8Planes(width, height);
        _encoder = new Vp8Encoder(_planes, new Vp8EncoderOptions());
        _decoder = new Vp8VideoDecoder(c.MaxParallelism, c.Memory, c.Limits);
    }

    /// <summary>Gets the frame width.</summary>
    public int Width { get; }

    /// <summary>Gets the frame height.</summary>
    public int Height { get; }

    /// <summary>Sets planes that receive each frame's unfiltered reconstruction, for tests.</summary>
    internal Vp8Planes? Reconstruction
    {
        set => _encoder.Reconstruction = value;
    }

    /// <summary>
    /// Encodes one frame as <paramref name="plan"/> says and updates the references. The first frame is always a key frame.
    /// </summary>
    /// <param name="frame">The picture: 4:2:0, 8 bits, at the stream's size; its color is taken as VP8's (BT.601, limited range).</param>
    /// <param name="plan">How to code the frame.</param>
    /// <returns>The frame's data, as a WebM block or an IVF frame holds it, and what coding it cost.</returns>
    /// <exception cref="ArgumentException"><paramref name="frame"/> is not 4:2:0 8-bit at the stream's size, or the plan copies a reference onto itself.</exception>
    public Vp8EncodedFrame Encode(YuvRaster<byte> frame, Vp8FramePlan plan)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(plan);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.ChromaFormat != ChromaFormat.Yuv420 || frame.Width != Width || frame.Height != Height)
        {
            throw new ArgumentException($"The frame must be 4:2:0 at {Width}×{Height}; it is {frame.ChromaFormat} at {frame.Width}×{frame.Height}.", nameof(frame));
        }

        if (plan.CopyToGolden is Vp8ReferenceCopy.Golden || plan.CopyToAlternate is Vp8ReferenceCopy.Alternate)
        {
            throw new ArgumentException("A reference cannot be copied onto itself: the golden frame copies from the last or the alternate, the alternate from the last or the golden.", nameof(plan));
        }

        Import(frame);
        bool key = plan.KeyFrame || !_started;
        Vp8EncoderOptions options = new()
        {
            Quality = plan.Quality,
            Effort = Method(plan.Effort),
            Quantizer = plan.Quantizer,
            Video = true,
            RefreshEntropy = plan.RefreshEntropy,
            Show = plan.Show,
            FilterLevel = plan.FilterLevel,
            Partitions = plan.Partitions,
        };
        byte[] data;
        int intra;
        if (key)
        {
            data = _encoder.Encode(options);
            intra = _planes.Stride / 16 * (_planes.PaddedHeight / 16);
        }
        else
        {
            (int range, bool quarter, int candidates, int split, int intraGate, int filterStep) = Vp8InterFrame.Search(plan.Effort);
            Vp8InterFrame inter = new()
            {
                State = _decoder.State,
                Previous = _decoder.Modes,
                RefreshLast = plan.RefreshLast,
                RefreshGolden = plan.RefreshGolden,
                RefreshAlternate = plan.RefreshAlternate,
                CopyToGolden = plan.CopyToGolden switch { Vp8ReferenceCopy.Last => 1, Vp8ReferenceCopy.Alternate => 2, _ => 0 },
                CopyToAlternate = plan.CopyToAlternate switch { Vp8ReferenceCopy.Last => 1, Vp8ReferenceCopy.Golden => 2, _ => 0 },
                GoldenSignBias = plan.GoldenSignBias,
                AlternateSignBias = plan.AlternateSignBias,
                SearchRange = range,
                QuarterPixel = quarter,
                Candidates = candidates,
                Split = split,
                IntraGate = intraGate,
                FilterStep = filterStep,
            };
            ReadOnlySpan<Vp8References> slots = [Vp8References.None, Vp8References.Last, Vp8References.Golden, Vp8References.Alternate];
            for (int slot = 1; slot <= 3; slot++)
            {
                inter.References[slot] = (plan.References & slots[slot]) != 0 ? _decoder.Reference(slot) : null;
            }

            data = _encoder.EncodeInter(options, inter);
            if (plan.FilterLevel is null && filterStep > 0)
            {
                data = SearchFilterLevel(data, filterStep);
            }

            intra = _encoder.IntraMacroblocks;
        }

        _decoder.Decode(data, 0, data.Length);
        _started = true;
        Vp8FrameBuffer decoded = _decoder.Decoded!;
        long y = SquaredError(_planes.Y, _planes.Stride, decoded.Y, decoded.OriginY, decoded.StrideY, Width, Height);
        int cw = (Width + 1) / 2, ch = (Height + 1) / 2;
        long u = SquaredError(_planes.U, _planes.ChromaStride, decoded.U, decoded.OriginC, decoded.StrideC, cw, ch);
        long v = SquaredError(_planes.V, _planes.ChromaStride, decoded.V, decoded.OriginC, decoded.StrideC, cw, ch);
        int macroblocks = _planes.Stride / 16 * (_planes.PaddedHeight / 16);
        return new Vp8EncodedFrame(data, key, plan.Show, _encoder.BaseQuantizer, _encoder.FilterLevel, y, u + v, (long)Width * Height, intra, _encoder.ZeroMacroblocks, macroblocks);
    }

    /// <summary>Returns the frame buffers to the memory source and stops the decoder's threads.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _decoder.Dispose();
        }
    }

    /// <summary>The key-frame encoder's effort (libwebp's method, 0 to 6) for an effort of 0 to 10.</summary>
    private static int Method(int effort) => effort switch { 0 => 1, 1 or 2 => 2, 3 => 3, 4 or 5 => 4, 6 or 7 => 5, _ => 6 };

    private void Import(YuvRaster<byte> frame)
    {
        int cw = (Width + 1) / 2, ch = (Height + 1) / 2;
        for (int y = 0; y < Height; y++)
        {
            frame.Y.Row(y)[..Width].CopyTo(_planes.Y.AsSpan(y * _planes.Stride, Width));
        }

        for (int y = 0; y < ch; y++)
        {
            frame.U!.Row(y)[..cw].CopyTo(_planes.U.AsSpan(y * _planes.ChromaStride, cw));
            frame.V!.Row(y)[..cw].CopyTo(_planes.V.AsSpan(y * _planes.ChromaStride, cw));
        }

        _planes.Pad();
    }

    /// <summary>
    /// Tries loop filter levels around the encoder's own by decoding the frame with each, steps halving, and keeps the level whose
    /// luma is closest to the source.
    /// </summary>
    private byte[] SearchFilterLevel(byte[] data, int firstStep)
    {
        // From the level of the inter frame before, which rarely moves far, else from the quantizer's.
        int level = _filterLevel >= 0 ? _filterLevel : _encoder.FilterLevel;
        if (level != _encoder.FilterLevel)
        {
            data = _encoder.WithFilterLevel(level);
        }

        long error = TrialError(data);
        int written = level;
        for (int step = firstStep; step >= 1; step >>= 1)
        {
            foreach (int candidate in (ReadOnlySpan<int>)[level - step, level + step])
            {
                if (candidate < 0 || candidate > 63)
                {
                    continue;
                }

                byte[] trial = _encoder.WithFilterLevel(candidate);
                written = candidate;
                long e = TrialError(trial);
                if (e < error)
                {
                    (error, data) = (e, trial);
                    level = candidate;
                }
            }
        }

        _filterLevel = level;
        return written == level ? data : _encoder.WithFilterLevel(level);
    }

    private long TrialError(byte[] data)
    {
        Vp8FrameBuffer decoded = _decoder.DecodeTrial(data, 0, data.Length, _saved);
        return SquaredError(_planes.Y, _planes.Stride, decoded.Y, decoded.OriginY, decoded.StrideY, Width, Height);
    }

    private static long SquaredError(byte[] source, int stride, ReadOnlySpan<byte> plane, int origin, int planeStride, int width, int height)
    {
        long sum = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> a = source.AsSpan(y * stride, width);
            ReadOnlySpan<byte> b = plane.Slice(origin + (y * planeStride), width);
            int row = 0;
            for (int x = 0; x < width; x++)
            {
                int d = a[x] - b[x];
                row += d * d;
            }

            sum += row;
        }

        return sum;
    }
}

/// <summary>How <see cref="Vp8StreamEncoder.Encode"/> codes one frame: its type, quality and references.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Vp8.cs" region="vp8-encode"/>
/// </example>
public sealed record Vp8FramePlan
{
    private readonly int _quality = 75;
    private readonly int? _quantizer;
    private readonly int _effort = 4;
    private readonly int _partitions = 1;
    private readonly int? _filterLevel;

    /// <summary>Gets the plan of an inter frame against all references that refreshes the last frame, at the default quality and effort.</summary>
    public static Vp8FramePlan Default { get; } = new();

    /// <summary>Gets whether the frame is a key frame, which decodes on its own and refreshes every reference.</summary>
    public bool KeyFrame { get; init; }

    /// <summary>
    /// Gets the quality, 0 (smallest) to 100 (best), on the scale of lossy WebP's quality. Defaults to 75. Ignored when
    /// <see cref="Quantizer"/> is set.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int Quality
    {
        get => _quality;
        init => _quality = InRange(value, 0, 100);
    }

    /// <summary>
    /// Gets VP8's quantizer index, 0 (finest) to 127, which rate control sets to replace <see cref="Quality"/>; null (the
    /// default) to use the quality. A key frame with a quantizer has one quantizer for the whole frame instead of four segments.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 127.</exception>
    public int? Quantizer
    {
        get => _quantizer;
        init => _quantizer = value is int q ? InRange(q, 0, 127) : null;
    }

    /// <summary>
    /// Gets how hard the encoder works, 0 (fastest) to 10 (smallest frames): how far and how finely it searches for motion,
    /// whether it tries split vectors and every intra mode, and whether it tries loop filter levels. Defaults to 4.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 10.</exception>
    public int Effort
    {
        get => _effort;
        init => _effort = InRange(value, 0, 10);
    }

    /// <summary>Gets whether the frame is shown. A hidden frame, typically an alternate reference, only updates the references. Defaults to true.</summary>
    public bool Show { get; init; } = true;

    /// <summary>Gets which references an inter frame may predict from. Defaults to all; with none, every macroblock is intra.</summary>
    public Vp8References References { get; init; } = Vp8References.All;

    /// <summary>Gets whether an inter frame becomes the last frame. Defaults to true.</summary>
    public bool RefreshLast { get; init; } = true;

    /// <summary>Gets whether an inter frame becomes the golden frame.</summary>
    public bool RefreshGolden { get; init; }

    /// <summary>Gets whether an inter frame becomes the alternate frame.</summary>
    public bool RefreshAlternate { get; init; }

    /// <summary>
    /// Gets what an inter frame that does not refresh the golden frame copies into it first: nothing, the last frame or the
    /// alternate frame, as they were before this frame.
    /// </summary>
    public Vp8ReferenceCopy CopyToGolden { get; init; }

    /// <summary>
    /// Gets what an inter frame that does not refresh the alternate frame copies into it first: nothing, the last frame or the
    /// golden frame, as they were before this frame (the golden copy, if any, comes after).
    /// </summary>
    public Vp8ReferenceCopy CopyToAlternate { get; init; }

    /// <summary>Gets whether the golden frame's vectors point the other way, as for a golden frame in the future.</summary>
    public bool GoldenSignBias { get; init; }

    /// <summary>Gets whether the alternate frame's vectors point the other way, as for a hidden alternate frame in the future.</summary>
    public bool AlternateSignBias { get; init; }

    /// <summary>
    /// Gets whether the frame's probability updates carry over to the frames after it. Defaults to true; false makes the stream
    /// error resilient, as real-time streams want: a lost frame then does not spoil the probabilities of the next.
    /// </summary>
    public bool RefreshEntropy { get; init; } = true;

    /// <summary>Gets the number of token partitions, 1, 2, 4 or 8: one is smallest, more let decoders work in parallel. Defaults to 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 1, 2, 4 or 8.</exception>
    public int Partitions
    {
        get => _partitions;
        init => _partitions = value is 1 or 2 or 4 or 8 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The partition count must be 1, 2, 4 or 8.");
    }

    /// <summary>Gets the loop filter level, 0 (off) to 63; null (the default) for the encoder to choose it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 63.</exception>
    public int? FilterLevel
    {
        get => _filterLevel;
        init => _filterLevel = value is int f ? InRange(f, 0, 63) : null;
    }

    private static int InRange(int value, int min, int max, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"The value must be {min} to {max}.") : value;
}

/// <summary>The references a VP8 inter frame may predict from.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Vp8.cs" region="vp8-encode"/>
/// </example>
[Flags]
public enum Vp8References
{
    /// <summary>None: every macroblock is intra.</summary>
    None = 0,

    /// <summary>The last frame.</summary>
    Last = 1,

    /// <summary>The golden frame.</summary>
    Golden = 2,

    /// <summary>The alternate frame.</summary>
    Alternate = 4,

    /// <summary>All three.</summary>
    All = Last | Golden | Alternate,
}

/// <summary>A reference a VP8 inter frame copies into the golden or alternate frame.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Vp8.cs" region="vp8-encode"/>
/// </example>
public enum Vp8ReferenceCopy
{
    /// <summary>No copy: the reference stays as it is.</summary>
    None,

    /// <summary>The last frame.</summary>
    Last,

    /// <summary>The golden frame (into the alternate frame only).</summary>
    Golden,

    /// <summary>The alternate frame (into the golden frame only).</summary>
    Alternate,
}

/// <summary>One frame <see cref="Vp8StreamEncoder.Encode"/> wrote, and what it cost.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Vp8.cs" region="vp8-encode"/>
/// </example>
public sealed class Vp8EncodedFrame
{
    internal Vp8EncodedFrame(byte[] data, bool key, bool shown, int quantizer, int filterLevel, long lumaError, long chromaError, long lumaSamples, int intra, int skipped, int macroblocks)
    {
        Data = data;
        IsKeyFrame = key;
        IsShown = shown;
        Quantizer = quantizer;
        FilterLevel = filterLevel;
        LumaSquaredError = lumaError;
        ChromaSquaredError = chromaError;
        LumaSamples = lumaSamples;
        IntraMacroblocks = intra;
        SkippedMacroblocks = skipped;
        Macroblocks = macroblocks;
    }

    /// <summary>Gets the frame's data, as a WebM block or an IVF frame holds it.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Gets the frame's size in bits.</summary>
    public long Bits => Data.Length * 8L;

    /// <summary>Gets whether the frame is a key frame.</summary>
    public bool IsKeyFrame { get; }

    /// <summary>Gets whether the frame is shown.</summary>
    public bool IsShown { get; }

    /// <summary>Gets the frame's base quantizer index, 0 to 127.</summary>
    public int Quantizer { get; }

    /// <summary>Gets the frame's loop filter level, 0 to 63.</summary>
    public int FilterLevel { get; }

    /// <summary>Gets the sum of squared differences between the source's luma and the decoded frame's.</summary>
    public long LumaSquaredError { get; }

    /// <summary>Gets the sum of squared differences between the source's chroma (both planes) and the decoded frame's.</summary>
    public long ChromaSquaredError { get; }

    /// <summary>Gets the luma PSNR of the decoded frame against the source, in dB; infinity when they are the same.</summary>
    public double LumaPsnr => LumaSquaredError == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 * LumaSamples / LumaSquaredError);

    /// <summary>Gets the frame's macroblocks.</summary>
    public int Macroblocks { get; }

    /// <summary>Gets the macroblocks coded with intra prediction: all of a key frame's.</summary>
    public int IntraMacroblocks { get; }

    /// <summary>Gets the macroblocks with no coefficients.</summary>
    public int SkippedMacroblocks { get; }

    private long LumaSamples { get; }
}
