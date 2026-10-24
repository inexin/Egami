using Egami.Colors;
using Egami.Formats.Avif;
using Egami.Pixels;

namespace Egami.Formats.Av1;

/// <summary>
/// Encodes an AV1 video stream (AV1 Bitstream and Decoding Process 1.0.0 with errata 1) a frame at a time: key, intra-only and
/// inter frames, inter blocks predicted from up to seven references with every single and compound mode, inter-intra, overlapped
/// and warped motion, and hidden frames shown later with show_existing_frame. Each frame's <see cref="Av1FramePlan"/> says what it
/// is (its type, whether it is shown), its quality or quantizer, and which reference slots it reads and refreshes, so rate control
/// and group-of-frames planning stay with the caller: Egami.Video drives this encoder for its containers, and any other source of
/// frames can too. The frames decode in libaom and dav1d exactly as in <see cref="Av1StreamDecoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Encode"/> returns low-overhead OBUs (section 5), each with its size field and without temporal delimiters, as MP4,
/// Matroska and WebM store them: a key frame starts with the sequence header. A hidden frame's OBUs are held and lead the temporal
/// unit of the next shown frame, so every <see cref="Av1EncodedFrame.Data"/> that is not empty is one whole temporal unit.
/// </para>
/// <para>
/// The encoder keeps the eight reference slots as a decoder does, decides each frame on up to
/// <see cref="EgamiContext.MaxParallelism"/> threads (one per tile) and checks the frame size against the context's
/// <see cref="EgamiContext.Limits"/>. The output does not depend on the thread count. An instance is not thread-safe.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-encode"/>
/// </example>
public sealed class Av1StreamEncoder : IDisposable
{
    private readonly Av1SequenceHeader _sequence;
    private readonly byte[] _sequenceObu;
    private readonly int _parallelism;
    private readonly Av1Frame?[] _slots = new Av1Frame?[8];
    private readonly Dictionary<Av1Frame, Av1ReferencePlane> _planes = new(ReferenceEqualityComparer.Instance);
    private readonly List<byte> _pending = [];
    private int _shown;
    private bool _started;
    private bool _disposed;

    /// <summary>Starts a stream with the sequence <paramref name="sequence"/> describes.</summary>
    /// <param name="sequence">The frame size, bit depth, chroma format, color and level, or a sequence header to reproduce.</param>
    /// <param name="context">The threads and limits to use; <see cref="EgamiContext.Default"/> when null.</param>
    /// <exception cref="LimitExceededException">The frame size breaks the context's safety limits.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-encode"/>
    /// </example>
    public Av1StreamEncoder(Av1SequenceOptions sequence, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        EgamiContext c = context ?? EgamiContext.Default;
        (_sequence, byte[] payload) = sequence.Build();
        c.Limits.CheckFrameSize(_sequence.MaxFrameWidth, _sequence.MaxFrameHeight);
        _parallelism = c.MaxParallelism;
        List<byte> obu = [];
        Av1ObuWriter.Obu(obu, Av1ObuType.SequenceHeader, payload);
        _sequenceObu = [.. obu];
    }

    /// <summary>Gets the sequence header OBU, with its size field, as an av1C box's configOBUs and a key frame's temporal unit hold it.</summary>
    public ReadOnlyMemory<byte> SequenceHeader => _sequenceObu;

    /// <summary>Gets the frame width.</summary>
    public int Width => _sequence.MaxFrameWidth;

    /// <summary>Gets the last coded frame as a decoder reconstructs it, for tests.</summary>
    internal Av1Frame? Reconstruction { get; private set; }

    /// <summary>Gets the frame height.</summary>
    public int Height => _sequence.MaxFrameHeight;

    /// <summary>
    /// Encodes one frame as <paramref name="plan"/> says and updates the reference slots. The first frame is always a key frame.
    /// </summary>
    /// <param name="frame">
    /// The picture at the stream's size, chroma format and bit depth: 8-bit samples for an 8-bit stream, else 16-bit. Its color is
    /// taken as the sequence's.
    /// </param>
    /// <param name="plan">How to code the frame.</param>
    /// <returns>The frame's OBUs (a whole temporal unit once a frame is shown) and what coding it cost.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="frame"/> does not match the stream, or the plan names an empty slot or a switch frame.
    /// </exception>
    public Av1EncodedFrame Encode(YuvRaster frame, Av1FramePlan plan)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(plan);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Width != Width || frame.Height != Height || frame.ChromaFormat != _sequence.Chroma || frame.BitDepth != _sequence.BitDepth
            || (_sequence.BitDepth == 8) != frame is YuvRaster<byte>)
        {
            throw new ArgumentException($"The frame must be {_sequence.BitDepth}-bit {_sequence.Chroma} at {Width}×{Height}; it is "
                + $"{frame.BitDepth}-bit {frame.ChromaFormat} at {frame.Width}×{frame.Height}.", nameof(frame));
        }

        return EncodePlanes(Input(frame), plan);
    }

    /// <summary>Gets the sequence header the stream codes.</summary>
    internal Av1SequenceHeader Sequence => _sequence;

    /// <summary>Encodes one frame from planes at the stream's size and depth, as <see cref="Encode(YuvRaster, Av1FramePlan)"/> does.</summary>
    internal Av1EncodedFrame EncodePlanes(Av1EncoderInput input, Av1FramePlan plan)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Av1FrameKind kind = _started ? plan.Kind : Av1FrameKind.Key;
        if (kind == Av1FrameKind.Switch)
        {
            throw new ArgumentException("The encoder writes key, intra-only and inter frames; switch frames are not supported.", nameof(plan));
        }

        bool intra = kind != Av1FrameKind.Inter;
        int orderHint = (plan.OrderHint ?? _shown) & ((1 << _sequence.OrderHintBits) - 1);
        if (!_sequence.EnableOrderHint)
        {
            orderHint = 0;
        }

        Av1EncoderSettings settings = new()
        {
            QIndex = plan.QIndex ?? AvifWriter.QIndexFor(plan.Quality),
            Lossless = plan.QIndex == 0,
            Effort = plan.Effort,
            ScreenContent = plan.ScreenContent,
            IntraBlockCopy = intra ? null : false,
            Parallelism = _parallelism,
            Stream = true,
            Reference = plan.Referenced ?? (kind == Av1FrameKind.Key || plan.RefreshSlots != 0),
            Video = plan.Tune == Av1Tune.Video,
            Analysis = plan.Tune == Av1Tune.Video && plan.Analysis is { } a && a.Width == Width && a.Height == Height ? a : null,
            DeltaQuantizers = plan.DeltaQuantizers,
            VarianceBoost = plan.VarianceBoost ?? plan.Tune == Av1Tune.Image,
            Layer = intra ? 0 : plan.Layer,
            Intra = intra,
        };
        Av1SearchProfile profile = Av1SearchProfile.For(settings.Effort, settings.Lossless, settings.Reference);
        Av1FrameParameters parameters = Av1Encoder.ParametersFor(input, _sequence, settings);
        parameters.FrameType = kind == Av1FrameKind.Key ? Av1FrameType.Key : kind == Av1FrameKind.IntraOnly ? Av1FrameType.IntraOnly : Av1FrameType.Inter;
        parameters.ShowFrame = plan.Show;
        parameters.ShowableFrame = !plan.Show && plan.Showable;
        parameters.ErrorResilientMode = plan.ErrorResilient && !(kind == Av1FrameKind.Key && plan.Show);
        parameters.OrderHint = orderHint;
        parameters.DisableFrameEndUpdateCdf = false;
        parameters.RefreshFrameFlags = kind == Av1FrameKind.Key && plan.Show ? 0xFF : plan.RefreshSlots;
        if (kind == Av1FrameKind.IntraOnly && parameters.RefreshFrameFlags == 0xFF)
        {
            throw new ArgumentException("An intra-only frame cannot refresh every slot.", nameof(plan));
        }

        if (!plan.DeltaQuantizers)
        {
            parameters.DeltaQPresent = false;
        }

        for (int i = 0; i < 8; i++)
        {
            parameters.RefOrderHints[i] = _slots[i]?.Header.OrderHint ?? 0;
        }

        Av1ReferencePlane?[] planes = new Av1ReferencePlane?[8];
        int[] search = [];
        if (!intra)
        {
            parameters.AllowIntrabc = false;
            search = References(plan, parameters, orderHint, profile, planes);
            bool errorResilient = parameters.ErrorResilientMode;
            parameters.PrimaryRefFrame = errorResilient ? 7 : plan.PrimaryReference ?? 0;
            parameters.AllowHighPrecisionMv = settings.Effort >= 3 && settings.QIndex < 128;
            parameters.InterpolationFilter = profile.Filters > 0 ? Interp.Switchable : Interp.Regular;
            parameters.IsMotionModeSwitchable = profile.MotionModes;
            parameters.UseRefFrameMvs = _sequence.EnableRefFrameMvs && !errorResilient;
            parameters.ReferenceSelect = profile.Compound > 0 && search.Length >= 2;
            Span<int> hints = stackalloc int[8], skipFrames = stackalloc int[2];
            for (int i = 0; i < 7; i++)
            {
                hints[Ref.Last + i] = _slots[parameters.RefFrameIdx[i]]!.Header.OrderHint;
            }

            parameters.SkipModeAllowed = Av1FrameHeader.SkipModeAllowed(_sequence, false, parameters.ReferenceSelect, orderHint, hints, skipFrames);
            parameters.SkipModePresent = parameters.SkipModeAllowed;
            parameters.AllowWarpedMotion = profile.MotionModes && _sequence.EnableWarpedMotion && !errorResilient;
            if (parameters.PrimaryRefFrame != 7)
            {
                Av1FrameHeader primary = _slots[parameters.RefFrameIdx[parameters.PrimaryRefFrame]]!.Header;
                parameters.PreviousGmParams = primary.GmParams;
                parameters.PreviousLoopFilterRefDeltas = primary.LoopFilterRefDeltas;
                parameters.PreviousLoopFilterModeDeltas = primary.LoopFilterModeDeltas;
            }

            if (profile.GlobalMotion)
            {
                Av1GlobalMotion.Estimate(input, parameters, planes, search, _sequence.BitDepth);
            }
        }

        parameters.Slots = (Av1Frame?[])_slots.Clone();
        (byte[] payload, Av1Frame reconstruction, _, _) = Av1Encoder.EncodeFrame(input, settings, _sequence, parameters, planes, search);
        Av1MotionField.Save(reconstruction);
        Reconstruction = reconstruction;
        for (int i = 0; i < 8; i++)
        {
            if (((reconstruction.Header.RefreshFrameFlags >> i) & 1) != 0)
            {
                _slots[i] = reconstruction;
            }
        }

        DropPlanes();
        _started = true;
        List<byte> data = _pending;
        if (kind == Av1FrameKind.Key)
        {
            data.AddRange(_sequenceObu);
        }

        Av1ObuWriter.Obu(data, Av1ObuType.Frame, payload);
        (long luma, long chroma) = SquaredErrors(input, reconstruction);

        byte[] unit = [];
        if (plan.Show)
        {
            unit = [.. data];
            data.Clear();
            _shown++;
        }

        return new Av1EncodedFrame(unit, payload.Length * 8L, kind, plan.Show, false, reconstruction.Header.BaseQIdx, orderHint, luma, chroma,
            (long)Width * Height, _sequence.BitDepth);
    }

    /// <summary>
    /// Runs the temporal model over frames ahead, in the order they will be coded (<see cref="Av1AnalysisFrame.References"/> says
    /// which earlier ones each predicts from): how much of each frame's content, block by block, the frames after it reuse. A
    /// frame's <see cref="Av1FrameAnalysis"/> goes in its plan (<see cref="Av1FramePlan.Analysis"/>), and its
    /// <see cref="Av1FrameAnalysis.Importance"/> can set a group's base quantizer. The frames are not coded and the stream does
    /// not change; the result depends only on the pictures and references, not on the thread count.
    /// </summary>
    /// <param name="frames">The frames, in coding order, at the stream's size.</param>
    /// <returns>Each frame's analysis, in the same order.</returns>
    /// <exception cref="ArgumentException">A picture is not at the stream's size, or a frame names a reference that is not before it.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-analyze"/>
    /// </example>
    public IReadOnlyList<Av1FrameAnalysis> Analyze(IReadOnlyList<Av1AnalysisFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < frames.Count; i++)
        {
            Av1AnalysisFrame frame = frames[i] ?? throw new ArgumentException($"Frame {i} is null.", nameof(frames));
            if (frame.Picture.Width != Width || frame.Picture.Height != Height)
            {
                throw new ArgumentException($"Frame {i} is {frame.Picture.Width}×{frame.Picture.Height}; the stream is {Width}×{Height}.", nameof(frames));
            }

            if (frame.References.Any(r => r < 0 || r >= i))
            {
                throw new ArgumentException($"Frame {i} names a reference that is not before it.", nameof(frames));
            }
        }

        return Av1TemporalModel.Analyze(frames, Width, Height, _parallelism);
    }

    /// <summary>
    /// Shows the frame in a slot again (show_existing_frame), as a hidden alternate reference is shown when its time comes: a
    /// temporal unit of one frame header OBU, after any hidden frames held for it. Showing a key frame this way also refreshes
    /// every slot with it.
    /// </summary>
    /// <param name="slot">The slot, 0 to 7.</param>
    /// <returns>The temporal unit.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside 0 to 7.</exception>
    /// <exception cref="ArgumentException">The slot is empty or its frame cannot be shown (it was shown already, or was not showable).</exception>
    public Av1EncodedFrame ShowExisting(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 7);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Av1Frame existing = _slots[slot] ?? throw new ArgumentException($"Slot {slot} holds no frame.", nameof(slot));
        if (!existing.Showable)
        {
            throw new ArgumentException($"The frame in slot {slot} cannot be shown: it was shown already or is not showable.", nameof(slot));
        }

        List<byte> data = _pending;
        Av1ObuWriter.Obu(data, Av1ObuType.FrameHeader, Av1ObuWriter.ShowExistingFrame(slot));
        Av1FrameKind kind = (Av1FrameKind)existing.Header.FrameType;
        if (existing.Header.FrameType == Av1FrameType.Key)
        {
            existing.Showable = false;
            _slots.AsSpan().Fill(existing);
        }

        byte[] unit = [.. data];
        data.Clear();
        _shown++;
        return new Av1EncodedFrame(unit, 0, kind, true, true, existing.Header.BaseQIdx, existing.Header.OrderHint, -1, -1, (long)Width * Height, _sequence.BitDepth);
    }

    /// <summary>Drops the reference slots and the frames held for the next temporal unit.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Array.Clear(_slots);
            _planes.Clear();
            _pending.Clear();
        }
    }

    /// <summary>
    /// Points LAST to ALTREF at the plan's slots and lists the references an inter block searches: one of each frame the slots hold
    /// that the plan allows, nearest in order first, as many as the effort searches; builds the bordered luma of each.
    /// </summary>
    private int[] References(Av1FramePlan plan, Av1FrameParameters parameters, int orderHint, Av1SearchProfile profile, Av1ReferencePlane?[] planes)
    {
        IReadOnlyList<int> slots = plan.ReferenceSlots;
        List<int> refs = [];
        for (int i = 0; i < 7; i++)
        {
            int slot = slots[i];
            Av1Frame frame = _slots[slot] ?? throw new ArgumentException($"The plan's {(Av1References)(1 << i)} reference reads slot {slot}, which holds no frame.", nameof(plan));
            if (frame.Header.UpscaledWidth != Width || frame.Header.FrameHeight != Height)
            {
                throw new ArgumentException("The encoder predicts from frames of the stream's own size only.", nameof(plan));
            }

            parameters.RefFrameIdx[i] = slot;
            if (((int)plan.References & (1 << i)) != 0 && !refs.Exists(r => ReferenceEquals(_slots[slots[r - Ref.Last]], frame)))
            {
                refs.Add(Ref.Last + i);
            }
        }

        refs.Sort((a, b) =>
        {
            int da = Math.Abs(Av1FrameHeader.RelativeDist(_sequence, _slots[slots[a - Ref.Last]]!.Header.OrderHint, orderHint));
            int db = Math.Abs(Av1FrameHeader.RelativeDist(_sequence, _slots[slots[b - Ref.Last]]!.Header.OrderHint, orderHint));
            return da != db ? da.CompareTo(db) : a.CompareTo(b);
        });
        if (refs.Count > profile.InterReferences)
        {
            refs.RemoveRange(profile.InterReferences, refs.Count - profile.InterReferences);
        }

        foreach (int rf in refs)
        {
            Av1Frame frame = _slots[slots[rf - Ref.Last]]!;
            if (!_planes.TryGetValue(frame, out Av1ReferencePlane? plane))
            {
                _planes[frame] = plane = new Av1ReferencePlane(frame);
            }

            planes[rf] = plane;
        }

        return [.. refs];
    }

    /// <summary>Forgets the bordered luma of frames no slot holds.</summary>
    private void DropPlanes()
    {
        foreach (Av1Frame frame in _planes.Keys.ToArray())
        {
            if (Array.IndexOf(_slots, frame) < 0)
            {
                _planes.Remove(frame);
            }
        }
    }

    private Av1EncoderInput Input(YuvRaster frame)
    {
        int planes = _sequence.Monochrome ? 1 : 3;
        ushort[][] data = new ushort[planes][];
        int ssx = _sequence.SubsamplingX, ssy = _sequence.SubsamplingY;
        for (int plane = 0; plane < planes; plane++)
        {
            int w = plane == 0 ? Width : (Width + ssx) >> ssx, h = plane == 0 ? Height : (Height + ssy) >> ssy;
            ushort[] samples = new ushort[w * h];
            for (int y = 0; y < h; y++)
            {
                Span<ushort> row = samples.AsSpan(y * w, w);
                if (frame is YuvRaster<byte> bytes)
                {
                    ReadOnlySpan<byte> source = (plane == 0 ? bytes.Y : plane == 1 ? bytes.U! : bytes.V!).Row(y)[..w];
                    for (int x = 0; x < w; x++)
                    {
                        row[x] = source[x];
                    }
                }
                else
                {
                    YuvRaster<ushort> words = (YuvRaster<ushort>)frame;
                    (plane == 0 ? words.Y : plane == 1 ? words.U! : words.V!).Row(y)[..w].CopyTo(row);
                }
            }

            data[plane] = samples;
        }

        return new Av1EncoderInput
        {
            Width = Width,
            Height = Height,
            BitDepth = _sequence.BitDepth,
            Chroma = _sequence.Chroma,
            Planes = data,
            ColorPrimaries = _sequence.ColorPrimaries,
            TransferCharacteristics = _sequence.TransferCharacteristics,
            MatrixCoefficients = _sequence.MatrixCoefficients,
            FullRange = _sequence.FullRange,
            ChromaSamplePosition = _sequence.ChromaSamplePosition,
        };
    }

    private static (long Luma, long Chroma) SquaredErrors(Av1EncoderInput input, Av1Frame frame)
    {
        long luma = 0, chroma = 0;
        int ssx = frame.Sequence.SubsamplingX, ssy = frame.Sequence.SubsamplingY;
        for (int plane = 0; plane < input.Planes.Length; plane++)
        {
            int w = plane == 0 ? input.Width : (input.Width + ssx) >> ssx, h = plane == 0 ? input.Height : (input.Height + ssy) >> ssy;
            ushort[] a = input.Planes[plane], b = frame.Plane<ushort>(plane);
            int stride = frame.Strides[plane];
            long sum = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int d = a[(y * w) + x] - b[(y * stride) + x];
                    sum += d * d;
                }
            }

            if (plane == 0)
            {
                luma = sum;
            }
            else
            {
                chroma += sum;
            }
        }

        return (luma, chroma);
    }
}

/// <summary>
/// The sequence an <see cref="Av1StreamEncoder"/> writes: frame size, bit depth, chroma format, color and level, every coding tool
/// on; or, from <see cref="FromSequenceHeader"/>, a given sequence header reproduced byte for byte, as a cut that joins an existing
/// stream needs.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-encode"/>
/// </example>
public sealed record Av1SequenceOptions
{
    private readonly int _width = 1, _height = 1, _bitDepth = 8;
    private readonly int? _level;
    // The payload of a sequence header to reproduce, from FromSequenceHeader.
    private byte[]? SourceHeader { get; init; }

    /// <summary>Gets the frame width, 1 to 65536.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 65536.</exception>
    public required int Width
    {
        get => _width;
        init => _width = InRange(value, 1, 65536);
    }

    /// <summary>Gets the frame height, 1 to 65536.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 65536.</exception>
    public required int Height
    {
        get => _height;
        init => _height = InRange(value, 1, 65536);
    }

    /// <summary>Gets the bit depth: 8 (the default), 10 or 12. 12 bits needs the Professional profile, which the encoder chooses.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 8, 10 or 12.</exception>
    public int BitDepth
    {
        get => _bitDepth;
        init => _bitDepth = value is 8 or 10 or 12 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The bit depth must be 8, 10 or 12.");
    }

    /// <summary>Gets the chroma format: 4:2:0 (the default, Main profile), 4:4:4 (High), 4:2:2 (Professional) or 4:0:0.</summary>
    public ChromaFormat Chroma { get; init; } = ChromaFormat.Yuv420;

    /// <summary>Gets the color the sequence header names (CICP and range). Defaults to unspecified, limited range.</summary>
    public ColorCodePoints CodePoints { get; init; } = new(2, 2, 2, false);

    /// <summary>Gets where 4:2:0 chroma sits, which the sequence header names; null (the default) for unknown.</summary>
    public ChromaSiting? ChromaSiting { get; init; }

    /// <summary>
    /// Gets seq_level_idx, 0 to 23 or 31; null (the default) for the smallest level whose picture size holds the frames. The level
    /// also bounds the sample rate, which the caller knows and the encoder does not.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 23 and not 31.</exception>
    public int? Level
    {
        get => _level;
        init => _level = value is null or (>= 0 and <= 23) or 31 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The level must be 0 to 23, or 31.");
    }

    /// <summary>Gets the tier of levels 8 and up: false for Main (the default), true for High.</summary>
    public bool HighTier { get; init; }

    /// <summary>
    /// Gets options that reproduce a sequence header exactly: the stream's size, bit depth, chroma, color and tools are the
    /// header's, and <see cref="Av1StreamEncoder.SequenceHeader"/> is the same OBU.
    /// </summary>
    /// <param name="obus">OBUs holding the sequence header, each with its size field, as an av1C box's configOBUs or a key frame's temporal unit holds them.</param>
    /// <exception cref="CorruptDataException">The data holds no valid sequence header.</exception>
    /// <exception cref="UnsupportedFormatException">The header uses what the encoder does not write: frame ids, a decoder model, scalable layers or a reduced still picture header.</exception>
    public static Av1SequenceOptions FromSequenceHeader(ReadOnlySpan<byte> obus)
    {
        foreach (Av1Obu obu in Av1Obu.Split(obus, "AV1"))
        {
            if (obu.Type == Av1ObuType.SequenceHeader)
            {
                byte[] payload = obus.Slice(obu.PayloadStart, obu.PayloadLength).ToArray();
                Av1SequenceHeader s = Av1SequenceHeader.Parse(payload, "AV1");
                if (s.ReducedStillPictureHeader || s.FrameIdNumbersPresent || s.DecoderModelInfoPresent || s.OperatingPoints.Any(p => p.Idc != 0))
                {
                    throw new UnsupportedFormatException("The AV1 encoder cannot reproduce a sequence header with frame ids, a decoder model, scalable layers or a reduced still picture header.");
                }

                return new Av1SequenceOptions
                {
                    Width = s.MaxFrameWidth,
                    Height = s.MaxFrameHeight,
                    BitDepth = s.BitDepth,
                    Chroma = s.Chroma,
                    CodePoints = new ColorCodePoints((byte)s.ColorPrimaries, (byte)s.TransferCharacteristics, (byte)s.MatrixCoefficients, s.FullRange),
                    Level = s.OperatingPoints[0].Level,
                    HighTier = s.OperatingPoints[0].Tier != 0,
                    SourceHeader = payload,
                };
            }
        }

        throw new CorruptDataException("AV1", 0, "The data holds no AV1 sequence header.");
    }

    /// <summary>Gets the sequence header the options make, and its payload.</summary>
    internal (Av1SequenceHeader Header, byte[] Payload) Build()
    {
        if (SourceHeader is byte[] source)
        {
            return (Av1SequenceHeader.Parse(source, "AV1"), source);
        }

        bool mono = Chroma == ChromaFormat.Yuv400;
        (int ssx, int ssy) = Chroma switch
        {
            ChromaFormat.Yuv444 => (0, 0),
            ChromaFormat.Yuv422 => (1, 0),
            _ => (1, 1),
        };
        int profile = BitDepth == 12 || Chroma == ChromaFormat.Yuv422 ? 2 : Chroma == ChromaFormat.Yuv444 ? 1 : 0;
        int level = Level ?? Av1Encoder.Level(Width, Height);
        Av1SequenceHeader written = new()
        {
            Profile = profile,
            OperatingPoints = [new Av1OperatingPoint(0, level, level > 7 && HighTier ? 1 : 0)],
            FrameWidthBits = Math.Max(1, 32 - int.LeadingZeroCount(Width - 1)),
            FrameHeightBits = Math.Max(1, 32 - int.LeadingZeroCount(Height - 1)),
            MaxFrameWidth = Width,
            MaxFrameHeight = Height,
            EnableFilterIntra = true,
            EnableIntraEdgeFilter = true,
            EnableInterintraCompound = true,
            EnableMaskedCompound = true,
            EnableWarpedMotion = true,
            EnableDualFilter = true,
            EnableOrderHint = true,
            EnableJntComp = true,
            EnableRefFrameMvs = true,
            ForceScreenContentTools = 2,
            ForceIntegerMv = 2,
            OrderHintBits = 7,
            EnableCdef = true,
            EnableRestoration = true,
            BitDepth = BitDepth,
            Monochrome = mono,
            ColorDescriptionPresent = true,
            ColorPrimaries = CodePoints.Primaries,
            TransferCharacteristics = CodePoints.TransferCharacteristics,
            MatrixCoefficients = CodePoints.MatrixCoefficients,
            FullRange = CodePoints.FullRange,
            SubsamplingX = ssx,
            SubsamplingY = ssy,
            ChromaSamplePosition = ssx == 1 && ssy == 1 ? ChromaSiting switch { Pixels.ChromaSiting.Left => 1, Pixels.ChromaSiting.TopLeft => 2, _ => 0 } : 0,
        };
        byte[] payload = Av1ObuWriter.SequenceHeader(written);
        return (Av1SequenceHeader.Parse(payload, "AV1"), payload);
    }

    private static int InRange(int value, int min, int max, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"The value must be {min} to {max}.") : value;
}

/// <summary>How <see cref="Av1StreamEncoder.Encode"/> codes one frame: its type, quality, and the reference slots it reads and refreshes.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-encode"/>
/// </example>
public sealed record Av1FramePlan
{
    private readonly int _quality = 75;
    private readonly int? _qIndex;
    private readonly int _effort = 4;
    private readonly int? _orderHint;
    private readonly int? _primary;
    private readonly int _refresh = 1;
    private readonly int _layer;
    private readonly IReadOnlyList<int> _slots = [0, 1, 2, 3, 4, 5, 6];

    /// <summary>Gets the plan of a shown inter frame that reads slots 0 to 6 as LAST to ALTREF and refreshes slot 0, at the default quality and effort.</summary>
    public static Av1FramePlan Default { get; } = new();

    /// <summary>
    /// Gets the frame's type: <see cref="Av1FrameKind.Inter"/> (the default), <see cref="Av1FrameKind.Key"/> (decodes on its own
    /// and, shown, refreshes every slot) or <see cref="Av1FrameKind.IntraOnly"/> (intra, keeping the other slots).
    /// </summary>
    public Av1FrameKind Kind { get; init; } = Av1FrameKind.Inter;

    /// <summary>Gets the quality, 0 (smallest) to 100 (best), on AVIF's scale (<see cref="AvifWriterOptions.Quality"/>). Defaults to 75. Ignored when <see cref="QIndex"/> is set.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 100.</exception>
    public int Quality
    {
        get => _quality;
        init => _quality = InRange(value, 0, 100);
    }

    /// <summary>Gets base_q_idx, 0 (lossless) to 255, which rate control sets to replace <see cref="Quality"/>; null (the default) to use the quality.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 255.</exception>
    public int? QIndex
    {
        get => _qIndex;
        init => _qIndex = value is int q ? InRange(q, 0, 255) : null;
    }

    /// <summary>
    /// Gets how hard the encoder works, 0 (fastest) to 10 (smallest frames), on AVIF's scale: how far it searches for motion, how
    /// many references and modes it tries, and from Effort 5, 6 and 7 the compound masks, overlapped and warped motion, dual filters
    /// and global motion. Defaults to 4.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 10.</exception>
    public int Effort
    {
        get => _effort;
        init => _effort = InRange(value, 0, 10);
    }

    /// <summary>Gets whether the frame is shown. A hidden frame, such as an alternate reference shown later with <see cref="Av1StreamEncoder.ShowExisting"/>, only fills slots. Defaults to true.</summary>
    public bool Show { get; init; } = true;

    /// <summary>Gets whether a hidden frame may be shown later with show_existing_frame. Defaults to true.</summary>
    public bool Showable { get; init; } = true;

    /// <summary>
    /// Gets the frame's order hint, its place in display order, 0 to 127 (higher values wrap); null (the default) for the number
    /// of frames shown so far. A hidden frame shown later sets the place it will show at.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? OrderHint
    {
        get => _orderHint;
        init => _orderHint = value is int h ? InRange(h, 0, int.MaxValue) : null;
    }

    /// <summary>Gets the slots, 0 to 7, that LAST, LAST2, LAST3, GOLDEN, BWDREF, ALTREF2 and ALTREF read, in that order. Defaults to 0 to 6.</summary>
    /// <exception cref="ArgumentException">The list does not have seven slots of 0 to 7.</exception>
    public IReadOnlyList<int> ReferenceSlots
    {
        get => _slots;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _slots = value.Count == 7 && value.All(s => s is >= 0 and <= 7) ? [.. value] : throw new ArgumentException("The plan needs seven reference slots of 0 to 7.", nameof(value));
        }
    }

    /// <summary>Gets which references inter blocks may predict from. Defaults to all; references that read the same frame are searched once.</summary>
    public Av1References References { get; init; } = Av1References.All;

    /// <summary>Gets the slots the frame refreshes, one bit per slot. Defaults to 1, slot 0; a shown key frame refreshes all.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 255.</exception>
    public int RefreshSlots
    {
        get => _refresh;
        init => _refresh = InRange(value, 0, 255);
    }

    /// <summary>
    /// Gets the reference, 0 (LAST) to 6 (ALTREF), whose probabilities and state an inter frame starts from; null (the default) for
    /// LAST. Ignored in error resilient frames, which start from the defaults.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 6.</exception>
    public int? PrimaryReference
    {
        get => _primary;
        init => _primary = value is int p ? InRange(p, 0, 6) : null;
    }

    /// <summary>
    /// Gets whether later frames predict from this one; null (the default) when it refreshes a slot. A frame nothing predicts from,
    /// such as the top layer of a pyramid, is searched without the tools whose misses would otherwise carry on into the frames that
    /// predict from it: transform size search, chroma from luma, inter-intra, overlapped and warped motion, filter choice, global
    /// motion, and the deblocking and CDEF searches of higher efforts. The caller knows; the encoder cannot tell from the slots alone.
    /// </summary>
    public bool? Referenced { get; init; }

    /// <summary>Gets whether the frame decodes without the probabilities and vectors of earlier frames, as real-time streams want. Defaults to false.</summary>
    public bool ErrorResilient { get; init; }

    /// <summary>
    /// Gets whether each superblock may have its own quantizer: from the frame's <see cref="Analysis"/> on a referenced layer-0
    /// video frame, else by <see cref="VarianceBoost"/>. Defaults to true.
    /// </summary>
    public bool DeltaQuantizers { get; init; } = true;

    /// <summary>
    /// Gets whether superblocks the temporal analysis sets no quantizer for get finer quantizers where the picture is flat or finely
    /// graded, where banding and blocking show (Variance Boost, from libaom's tune IQ): SSIM and the eye gain more than PSNR loses.
    /// Null (the default) for on under <see cref="Av1Tune.Image"/> and off under <see cref="Av1Tune.Video"/>. Needs
    /// <see cref="DeltaQuantizers"/>.
    /// </summary>
    public bool? VarianceBoost { get; init; }

    /// <summary>
    /// Gets what the frame's coding tools are tuned for: <see cref="Av1Tune.Video"/> (the default) or <see cref="Av1Tune.Image"/>,
    /// the perceptual tools of still pictures.
    /// </summary>
    public Av1Tune Tune { get; init; }

    /// <summary>
    /// Gets the frame's layer in its group's pyramid, 0 (the default: key, golden and the group's base frames) to 7 (the deepest):
    /// a video frame's rate-distortion multiplier rises with it, so the frames fewer others predict from spend fewer bits on
    /// what they alone show. Intra frames are layer 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 7.</exception>
    public int Layer
    {
        get => _layer;
        init => _layer = InRange(value, 0, 7);
    }

    /// <summary>Gets whether screen content tools (palettes, and intra block copy in intra frames) are on; null (the default) detects screen content.</summary>
    public bool? ScreenContent { get; init; }

    /// <summary>
    /// Gets the frame's temporal analysis from <see cref="Av1StreamEncoder.Analyze"/>, or null (the default) for none. A video frame
    /// at layer 0 that others predict from codes finer quantizers in the superblocks the frames after it reuse most, when
    /// <see cref="DeltaQuantizers"/> is on; other frames lower the rate-distortion multiplier of those blocks instead.
    /// </summary>
    public Av1FrameAnalysis? Analysis { get; init; }

    private static int InRange(int value, int min, int max, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"The value must be {min} to {max}.") : value;
}

/// <summary>What an <see cref="Av1FramePlan"/>'s coding tools are tuned for.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-encode"/>
/// </example>
public enum Av1Tune
{
    /// <summary>
    /// Video, measured by PSNR and SSIM as libaom's and SVT-AV1's defaults are: one quantizer for luma and chroma, no quantization
    /// matrices, no loop filter sharpness, and rate-distortion multipliers by frame type and pyramid layer (<see cref="Av1FramePlan.Layer"/>).
    /// A frame's quality matters most through the frames that predict from it.
    /// </summary>
    Video,

    /// <summary>
    /// Still pictures, as <see cref="Avif.AvifWriter"/> codes them (libaom's tune IQ): finer chroma, quantization matrices,
    /// adaptive loop filter sharpness, Variance Boost (<see cref="Av1FramePlan.DeltaQuantizers"/>) and multipliers weighted by
    /// local variance.
    /// </summary>
    Image,
}

/// <summary>The references an AV1 inter frame may predict from.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-encode"/>
/// </example>
[Flags]
public enum Av1References
{
    /// <summary>None: every block is intra.</summary>
    None = 0,

    /// <summary>LAST.</summary>
    Last = 1,

    /// <summary>LAST2.</summary>
    Last2 = 2,

    /// <summary>LAST3.</summary>
    Last3 = 4,

    /// <summary>GOLDEN.</summary>
    Golden = 8,

    /// <summary>BWDREF.</summary>
    Backward = 16,

    /// <summary>ALTREF2.</summary>
    Alternate2 = 32,

    /// <summary>ALTREF.</summary>
    Alternate = 64,

    /// <summary>All seven.</summary>
    All = 127,
}

/// <summary>One frame <see cref="Av1StreamEncoder"/> wrote, and what it cost.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-encode"/>
/// </example>
public sealed class Av1EncodedFrame
{
    internal Av1EncodedFrame(byte[] data, long bits, Av1FrameKind kind, bool shown, bool existing, int qindex, int orderHint, long lumaError,
        long chromaError, long lumaSamples, int bitDepth)
    {
        Data = data;
        Bits = bits;
        Kind = kind;
        IsShown = shown;
        IsShownExisting = existing;
        QIndex = qindex;
        OrderHint = orderHint;
        LumaSquaredError = lumaError;
        ChromaSquaredError = chromaError;
        LumaSamples = lumaSamples;
        BitDepth = bitDepth;
    }

    /// <summary>
    /// Gets the temporal unit's OBUs, as an MP4 sample or a Matroska block holds them: empty for a hidden frame, whose OBUs lead the
    /// next shown frame's unit.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Gets the size of this frame's own frame OBU payload in bits; 0 for a frame shown again.</summary>
    public long Bits { get; }

    /// <summary>Gets the frame's type.</summary>
    public Av1FrameKind Kind { get; }

    /// <summary>Gets whether the frame is shown.</summary>
    public bool IsShown { get; }

    /// <summary>Gets whether this is an earlier frame shown again with show_existing_frame.</summary>
    public bool IsShownExisting { get; }

    /// <summary>Gets the frame's base_q_idx, 0 to 255.</summary>
    public int QIndex { get; }

    /// <summary>Gets the frame's order hint.</summary>
    public int OrderHint { get; }

    /// <summary>Gets the sum of squared differences between the source's luma and the decoded frame's; −1 for a frame shown again.</summary>
    public long LumaSquaredError { get; }

    /// <summary>Gets the sum of squared differences between the source's chroma (both planes) and the decoded frame's; −1 for a frame shown again.</summary>
    public long ChromaSquaredError { get; }

    /// <summary>Gets the luma PSNR of the decoded frame against the source, in dB; infinity when they are the same, NaN for a frame shown again.</summary>
    public double LumaPsnr
    {
        get
        {
            if (LumaSquaredError < 0)
            {
                return double.NaN;
            }

            double peak = (1 << BitDepth) - 1;
            return LumaSquaredError == 0 ? double.PositiveInfinity : 10 * Math.Log10(peak * peak * LumaSamples / LumaSquaredError);
        }
    }

    private long LumaSamples { get; }

    private int BitDepth { get; }
}
