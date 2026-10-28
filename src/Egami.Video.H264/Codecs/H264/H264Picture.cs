using System.Diagnostics;
using System.Runtime.CompilerServices;

using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>How a frame was coded: as a frame picture, an MBAFF frame or two field pictures.</summary>
internal enum H264Coding : byte
{
    Frame,
    Mbaff,
    Fields,
}

/// <summary>
/// A decoded frame in the decoded picture buffer: its samples, kept with a border of replicated edge samples for motion
/// compensation, and the motion the frames after it read for direct prediction and deblocking.
/// </summary>
internal sealed class H264Picture : INalDpbPicture
{
    /// <summary>The border around each plane, in samples: motion vectors reaching further read through a clamped copy.</summary>
    internal const int Pad = 32;

    /// <summary>Extra samples after each plane, so vector loads of a block near the last row may read a little past it.</summary>
    internal const int Slack = 32;

    private static int s_nextId;

    private readonly Array _y;
    private readonly H264Picture? _frame;
    private H264Picture? _top;
    private H264Picture? _bottom;
    private int _id;
    private int _frameNum;
    private int _frameNumWrap;
    private int _longTermFrameIdx;
    private bool _nonExisting;

    // Which fields (bit 0 top, bit 1 bottom) are marked for short- and long-term reference.
    private int _shortTerm;
    private int _longTerm;
    private readonly Array _cb;
    private readonly Array _cr;

    internal H264Picture(int widthMbs, int heightMbs, int chromaFormatIdc, bool narrow)
    {
        Narrow = narrow;
        WidthMbs = widthMbs;
        HeightMbs = heightMbs;
        ChromaFormatIdc = chromaFormatIdc;
        Width = widthMbs * 16;
        Height = heightMbs * 16;
        Stride = Width + (2 * Pad);
        _y = narrow ? new byte[(Stride * (Height + (2 * Pad))) + Slack] : new ushort[(Stride * (Height + (2 * Pad))) + Slack];
        ChromaWidth = chromaFormatIdc == 0 ? 0 : Width / 2;
        ChromaHeight = chromaFormatIdc switch { 0 => 0, 1 => Height / 2, _ => Height };
        ChromaStride = ChromaWidth + (2 * Pad);
        int chromaSize = chromaFormatIdc == 0 ? 0 : (ChromaStride * (ChromaHeight + (2 * Pad))) + Slack;
        _cb = narrow ? new byte[chromaSize] : new ushort[chromaSize];
        _cr = narrow ? new byte[chromaSize] : new ushort[chromaSize];
        Origin = (Pad * Stride) + Pad;
        ChromaOrigin = (Pad * ChromaStride) + Pad;
        int blocks = widthMbs * 4 * heightMbs * 4;
        Mv = [new int[blocks], new int[blocks]];
        RefIdx = [new sbyte[blocks], new sbyte[blocks]];
        RefPicId = [new int[blocks], new int[blocks]];
        MbIntra = new bool[widthMbs * heightMbs];
        ColZero = new bool[blocks];
        PadY = Pad;
        SpanY = Height + (2 * Pad);
        ChromaSpanY = ChromaHeight + (2 * Pad);
        PrefetchTop = -Pad;
        PrefetchBottom = Height + Pad - 21;
        PrefetchChromaBottom = ChromaHeight + Pad - 9;
        Parity = -1;
    }

    /// <summary>Makes a view of one field of a frame: every other row of its planes, with motion data of its own.</summary>
    private H264Picture(H264Picture frame, int parity)
    {
        _frame = frame;
        Parity = parity;
        Narrow = frame.Narrow;
        WidthMbs = frame.WidthMbs;
        HeightMbs = frame.HeightMbs / 2;
        ChromaFormatIdc = frame.ChromaFormatIdc;
        Width = frame.Width;
        Height = frame.Height / 2;
        Stride = frame.Stride * 2;
        ChromaWidth = frame.ChromaWidth;
        ChromaHeight = frame.ChromaHeight / 2;
        ChromaStride = frame.ChromaStride * 2;
        _y = frame._y;
        _cb = frame._cb;
        _cr = frame._cr;
        Origin = frame.Origin + (parity * frame.Stride);
        ChromaOrigin = frame.ChromaOrigin + (parity * frame.ChromaStride);

        // The border rows hold the frame's edge rows, not this field's: reads that reach past the field's edge are clamped.
        PadY = 0;
        SpanY = Height;
        ChromaSpanY = ChromaHeight;

        // Prefetches stay in the memory around the field: half the frame's border rows.
        PrefetchTop = -Pad / 2;
        PrefetchBottom = Math.Max(PrefetchTop, Height + (Pad / 2) - 21);
        PrefetchChromaBottom = Math.Max(PrefetchTop, ChromaHeight + (Pad / 2) - 9);
        int blocks = WidthMbs * 4 * HeightMbs * 4;
        Mv = [new int[blocks], new int[blocks]];
        RefIdx = [new sbyte[blocks], new sbyte[blocks]];
        RefPicId = [new int[blocks], new int[blocks]];
        MbIntra = new bool[WidthMbs * HeightMbs];
        ColZero = new bool[blocks];
    }

    /// <summary>
    /// Gets a number no other picture of the process has, which motion data uses to name reference pictures: a frame's is a
    /// multiple of 4, and its top and bottom fields' are 1 and 2 more.
    /// </summary>
    internal int Id => _id;

    /// <summary>Gets which field this picture is a view of: 0 top, 1 bottom, -1 for a frame.</summary>
    internal int Parity { get; }

    /// <summary>Gets the frame a field view belongs to, or the frame itself.</summary>
    internal H264Picture Frame => _frame ?? this;

    /// <summary>
    /// Gets how many rows of valid samples the border above and below the picture holds: a field's border rows hold the frame's
    /// edge rows, so its motion compensation clamps instead.
    /// </summary>
    internal int PadY { get; }

    /// <summary>Gets the rows of luma and chroma samples motion compensation may read without a clamped copy: Height + 2 PadY.</summary>
    internal int SpanY { get; }

    internal int ChromaSpanY { get; }

    /// <summary>Gets the rows a prefetch of luma (from <see cref="PrefetchTop"/>) or chroma may start at, inside the planes' memory.</summary>
    internal int PrefetchTop { get; }

    internal int PrefetchBottom { get; }

    internal int PrefetchChromaBottom { get; }

    /// <summary>Gets or sets which fields of the frame are decoded (bit 0 top, bit 1 bottom); a frame picture decodes both.</summary>
    internal int DecodedFields { get; set; } = 3;

    internal int WidthMbs { get; }

    internal int HeightMbs { get; }

    internal int ChromaFormatIdc { get; }

    internal int Width { get; }

    internal int Height { get; }

    internal int Stride { get; }

    internal int ChromaWidth { get; }

    internal int ChromaHeight { get; }

    internal int ChromaStride { get; }

    /// <summary>Gets whether the planes hold bytes (both bit depths 8) rather than 16-bit samples.</summary>
    internal bool Narrow { get; }

    /// <summary>Gets the planes as arrays of either sample type, for copies that do not look at the samples.</summary>
    internal Array YPlane => _y;

    internal Array CbPlane => _cb;

    internal Array CrPlane => _cr;

    /// <summary>
    /// Gets the luma plane, Stride by Height + 2 * Pad samples; sample (0, 0) is at <see cref="Origin"/>. T is byte when
    /// <see cref="Narrow"/>, else ushort.
    /// </summary>
    internal T[] Y<T>()
        where T : unmanaged
    {
        Debug.Assert(H264Samples.Narrow<T>() == Narrow, "The sample type is the picture's.");
        return Unsafe.As<T[]>(_y);
    }

    internal T[] Cb<T>()
        where T : unmanaged
    {
        Debug.Assert(H264Samples.Narrow<T>() == Narrow, "The sample type is the picture's.");
        return Unsafe.As<T[]>(_cb);
    }

    internal T[] Cr<T>()
        where T : unmanaged
    {
        Debug.Assert(H264Samples.Narrow<T>() == Narrow, "The sample type is the picture's.");
        return Unsafe.As<T[]>(_cr);
    }

    internal int Origin { get; }

    internal int ChromaOrigin { get; }

    /// <summary>Gets the motion vectors per 4x4 block in raster order, x in the low 16 bits and y in the high 16 bits, per list.</summary>
    internal int[][] Mv { get; }

    /// <summary>Gets the reference indices per 4x4 block, -1 where the list is not used, per list.</summary>
    internal sbyte[][] RefIdx { get; }

    /// <summary>Gets the <see cref="Id"/> of the reference picture per 4x4 block, -1 where the list is not used, per list.</summary>
    internal int[][] RefPicId { get; }

    /// <summary>Gets whether each macroblock is intra coded.</summary>
    internal bool[] MbIntra { get; }

    /// <summary>
    /// Gets, per 4x4 block of a reference picture's inter macroblocks, whether the block refers to reference index 0 with a
    /// vector of at most 1 in each component: colZeroFlag's test (8.4.1.2.2) when the picture is the co-located one. Worked out
    /// as each macroblock is decoded, for the corner blocks with direct_8x8_inference and every block without; the idea is
    /// libavc's.
    /// </summary>
    internal bool[] ColZero { get; }

    /// <summary>Gets or sets frame_num; a field's is its frame's.</summary>
    internal int FrameNum
    {
        get => Frame._frameNum;
        set => Frame._frameNum = value;
    }

    /// <summary>Gets or sets FrameNumWrap (8.2.4.1), set while building reference lists; a field's is its frame's.</summary>
    internal int FrameNumWrap
    {
        get => Frame._frameNumWrap;
        set => Frame._frameNumWrap = value;
    }

    /// <summary>Gets or sets LongTermFrameIdx; a field's is its frame's.</summary>
    internal int LongTermFrameIdx
    {
        get => Frame._longTermFrameIdx;
        set => Frame._longTermFrameIdx = value;
    }

    /// <summary>
    /// Gets or sets whether the picture is marked for short-term reference: for a frame, both of its fields (setting marks or
    /// unmarks both); for a field view, that field.
    /// </summary>
    internal bool IsShortTermReference
    {
        get => _frame is null ? _shortTerm == 3 : (_frame._shortTerm & (1 << Parity)) != 0;
        set => Mark(ref Frame._shortTerm, value);
    }

    /// <summary>Gets or sets whether the picture is marked for long-term reference, as <see cref="IsShortTermReference"/>.</summary>
    internal bool IsLongTermReference
    {
        get => _frame is null ? _longTerm == 3 : (_frame._longTerm & (1 << Parity)) != 0;
        set => Mark(ref Frame._longTerm, value);
    }

    /// <summary>Gets whether the frame has a field marked for short-term reference, as the sliding window counts frames.</summary>
    internal bool HasShortTermField => Frame._shortTerm != 0;

    /// <summary>Gets whether the frame has a field marked for long-term reference.</summary>
    internal bool HasLongTermField => Frame._longTerm != 0;

    /// <inheritdoc/>
    public bool IsReference => _frame is null ? (_shortTerm | _longTerm) != 0 : ((_frame._shortTerm | _frame._longTerm) & (1 << Parity)) != 0;

    /// <summary>Gets or sets whether this frame stands in for a frame_num gap and has no samples of its own (8.2.5.2).</summary>
    internal bool NonExisting
    {
        get => Frame._nonExisting;
        set => Frame._nonExisting = value;
    }

    internal int TopPoc { get; set; }

    internal int BottomPoc { get; set; }

    /// <summary>Gets the picture order count (8.2.1): a frame's is the smaller of its decoded fields', a field's its own.</summary>
    internal int Poc => _frame is not null ? (Parity == 0 ? _frame.TopPoc : _frame.BottomPoc)
        : DecodedFields == 3 ? Math.Min(TopPoc, BottomPoc) : DecodedFields == 1 ? TopPoc : BottomPoc;

    /// <summary>Gets or sets how the frame was coded, which tells where its motion is kept for direct prediction (8.4.1.2.1).</summary>
    internal H264Coding CodedAs { get; set; }

    /// <summary>Gets or sets, for an MBAFF frame, whether each macroblock (raster order) is in a field pair, else null.</summary>
    internal bool[]? FieldPairs { get; set; }

    /// <summary>Gets or sets whether the frame is to be output (it was decoded at or after the point decoding started from).</summary>
    internal bool Output { get; set; } = true;

    /// <summary>Gets or sets whether the frame was decoded with errors (concealed or partly missing).</summary>
    internal bool Damaged { get; set; }

    /// <summary>Gets or sets what the adapter attaches to the frame (its SEI and decode order).</summary>
    internal object? Tag { get; set; }

    /// <summary>Gets or sets whether a memory_management_control_operation 5 came with the frame (8.2.1).</summary>
    internal bool HadMmco5 { get; set; }

    /// <summary>Gets or sets the SPS the frame was decoded with, which gives its cropping, bit depth and colour.</summary>
    internal H264Sps? Sps { get; set; }

    /// <summary>Gets or sets the SEI messages of the frame's access unit.</summary>
    internal H264Sei? Sei { get; set; }

    /// <summary>Gets a view of one of the frame's fields: 0 top, 1 bottom.</summary>
    internal H264Picture Field(int parity)
    {
        Debug.Assert(_frame is null, "Fields are views of frames.");
        return parity == 0 ? _top ??= new H264Picture(this, 0) { _id = _id + 1 } : _bottom ??= new H264Picture(this, 1) { _id = _id + 2 };
    }

    private void Mark(ref int marks, bool value)
    {
        int bits = _frame is null ? 3 : 1 << Parity;
        marks = value ? marks | bits : marks & ~bits;
    }

    /// <summary>Makes the picture fresh for a new frame.</summary>
    internal void Reset()
    {
        _id = Interlocked.Add(ref s_nextId, 4);
        if (_top is not null)
        {
            _top._id = _id + 1;
        }

        if (_bottom is not null)
        {
            _bottom._id = _id + 2;
        }

        _shortTerm = _longTerm = 0;
        DecodedFields = 3;
        NonExisting = false;
        Damaged = false;
        HadMmco5 = false;
        Tag = null;
        Sei = null;
        LongTermFrameIdx = 0;
    }

    /// <summary>Copies the edge samples into the border, so motion compensation can read past the edges.</summary>
    internal void ExtendEdges()
    {
        if (Narrow)
        {
            ExtendEdges<byte>();
        }
        else
        {
            ExtendEdges<ushort>();
        }
    }

    private void ExtendEdges<T>()
        where T : unmanaged
    {
        ExtendPlane(Y<T>(), Stride, Width, Height);
        if (ChromaFormatIdc != 0)
        {
            ExtendPlane(Cb<T>(), ChromaStride, ChromaWidth, ChromaHeight);
            ExtendPlane(Cr<T>(), ChromaStride, ChromaWidth, ChromaHeight);
        }
    }

    private static void ExtendPlane<T>(T[] plane, int stride, int width, int height)
        where T : unmanaged
    {
        Span<T> p = plane;
        for (int y = 0; y < height; y++)
        {
            int row = (y + Pad) * stride;
            p.Slice(row, Pad).Fill(p[row + Pad]);
            p.Slice(row + Pad + width, Pad).Fill(p[row + Pad + width - 1]);
        }

        Span<T> top = p.Slice(Pad * stride, stride);
        for (int y = 0; y < Pad; y++)
        {
            top.CopyTo(p.Slice(y * stride, stride));
        }

        Span<T> bottom = p.Slice((Pad + height - 1) * stride, stride);
        for (int y = Pad + height; y < height + (2 * Pad); y++)
        {
            bottom.CopyTo(p.Slice(y * stride, stride));
        }
    }
}
