using System.Diagnostics;
using System.Runtime.CompilerServices;

using Egami.Nal;

namespace Egami.Video.Codecs.H264;

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
    }

    /// <summary>Gets a number no other picture of the process has, which motion data uses to name reference pictures.</summary>
    internal int Id { get; private set; }

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

    internal int FrameNum { get; set; }

    /// <summary>Gets or sets FrameNumWrap (8.2.4.1), set while building reference lists.</summary>
    internal int FrameNumWrap { get; set; }

    internal int LongTermFrameIdx { get; set; }

    internal bool IsShortTermReference { get; set; }

    internal bool IsLongTermReference { get; set; }

    /// <inheritdoc/>
    public bool IsReference => IsShortTermReference || IsLongTermReference;

    /// <summary>Gets or sets whether this frame stands in for a frame_num gap and has no samples of its own (8.2.5.2).</summary>
    internal bool NonExisting { get; set; }

    internal int TopPoc { get; set; }

    internal int BottomPoc { get; set; }

    internal int Poc => Math.Min(TopPoc, BottomPoc);

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

    /// <summary>Makes the picture fresh for a new frame.</summary>
    internal void Reset()
    {
        Id = Interlocked.Increment(ref s_nextId);
        IsShortTermReference = false;
        IsLongTermReference = false;
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
