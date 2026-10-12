using System.Buffers;

namespace Egami.Formats.Webp;

/// <summary>
/// One decoded VP8 frame as the decoder keeps it for prediction: the macroblock-aligned planes (the pixels past the display size
/// are decoded too, and later frames predict from them) with a border of copied edge pixels around each, so most motion vectors
/// read memory directly. A stream keeps four of these and points its last, golden and alternate references at them. The planes
/// come from the stream's <see cref="IPixelMemorySource"/>, one block each, pinned for the buffer's life; a plane larger than the
/// source's largest block is a pinned array instead.
/// </summary>
internal sealed unsafe class Vp8FrameBuffer : IDisposable
{
    /// <summary>The luma border: 32 pixels, as libvpx keeps, plus room for the farthest a clamped vector and its filter taps read.</summary>
    internal const int Border = 48;

    /// <summary>The chroma border.</summary>
    internal const int ChromaBorder = Border / 2;

    private readonly Plane _y;
    private readonly Plane _u;
    private readonly Plane _v;

    internal Vp8FrameBuffer(int width, int height, IPixelMemorySource memory)
    {
        Width = width;
        Height = height;
        AlignedWidth = (width + 15) & ~15;
        AlignedHeight = (height + 15) & ~15;
        StrideY = AlignedWidth + (2 * Border);
        StrideC = (AlignedWidth / 2) + (2 * ChromaBorder);
        OriginY = (Border * StrideY) + Border;
        OriginC = (ChromaBorder * StrideC) + ChromaBorder;
        _y = new Plane((StrideY * (AlignedHeight + (2 * Border))) + Vp8InterPredict.Slack, memory);
        int chroma = (StrideC * ((AlignedHeight / 2) + (2 * ChromaBorder))) + Vp8InterPredict.Slack;
        _u = new Plane(chroma, memory);
        _v = new Plane(chroma, memory);
    }

    /// <summary>Gets the display width.</summary>
    internal int Width { get; }

    /// <summary>Gets the display height.</summary>
    internal int Height { get; }

    /// <summary>Gets the decoded width, a multiple of 16.</summary>
    internal int AlignedWidth { get; }

    /// <summary>Gets the decoded height, a multiple of 16.</summary>
    internal int AlignedHeight { get; }

    internal int StrideY { get; }

    internal int StrideC { get; }

    /// <summary>Gets where luma pixel (0, 0) sits in <see cref="Y"/>.</summary>
    internal int OriginY { get; }

    /// <summary>Gets where chroma pixel (0, 0) sits in <see cref="U"/> and <see cref="V"/>.</summary>
    internal int OriginC { get; }

    internal Span<byte> Y => new(_y.Pointer, _y.Length);

    internal Span<byte> U => new(_u.Pointer, _u.Length);

    internal Span<byte> V => new(_v.Pointer, _v.Length);

    /// <summary>Gets the buffer's bytes.</summary>
    internal static long BytesFor(int width, int height)
    {
        long w = (width + 15) & ~15, h = (height + 15) & ~15;
        return ((w + (2 * Border)) * (h + (2 * Border))) + (2 * (((w / 2) + Border) * ((h / 2) + Border))) + (3 * Vp8InterPredict.Slack);
    }

    /// <summary>Copies the decoded edge pixels out into the borders, as the references for later frames need.</summary>
    internal void ExtendBorders()
    {
        Extend(Y, OriginY, StrideY, AlignedWidth, AlignedHeight, Border);
        Extend(U, OriginC, StrideC, AlignedWidth / 2, AlignedHeight / 2, ChromaBorder);
        Extend(V, OriginC, StrideC, AlignedWidth / 2, AlignedHeight / 2, ChromaBorder);
    }

    /// <summary>Sets every pixel, borders included, to <paramref name="value"/>: the mid-gray reference concealment starts from.</summary>
    internal void Fill(byte value)
    {
        Y.Fill(value);
        U.Fill(value);
        V.Fill(value);
    }

    public void Dispose()
    {
        _y.Dispose();
        _u.Dispose();
        _v.Dispose();
    }

    private static void Extend(Span<byte> p, int origin, int stride, int width, int height, int border)
    {
        for (int y = 0; y < height; y++)
        {
            int row = origin + (y * stride);
            p.Slice(row - border, border).Fill(p[row]);
            p.Slice(row + width, border).Fill(p[row + width - 1]);
        }

        ReadOnlySpan<byte> top = p.Slice(origin - border, stride);
        ReadOnlySpan<byte> bottom = p.Slice(origin - border + ((height - 1) * stride), stride);
        for (int y = 1; y <= border; y++)
        {
            top.CopyTo(p.Slice(origin - border - (y * stride), stride));
            bottom.CopyTo(p.Slice(origin - border + ((height - 1 + y) * stride), stride));
        }
    }

    /// <summary>One plane's memory, zeroed and pinned: a block of the memory source, or a pinned array when it is too large for one.</summary>
    private sealed class Plane : IDisposable
    {
        private IMemoryOwner<byte>? _owner;
        private MemoryHandle _pin;

        internal Plane(int length, IPixelMemorySource memory)
        {
            Length = length;
            Memory<byte> block;
            if (length <= memory.MaxBlockBytes)
            {
                _owner = memory.Allocate(length, ClearMode.Zero);
                block = _owner.Memory;
            }
            else
            {
                block = GC.AllocateArray<byte>(length, pinned: true);
            }

            _pin = block.Pin();
            Pointer = (byte*)_pin.Pointer;
        }

        internal int Length { get; private set; }

        internal byte* Pointer { get; private set; }

        public void Dispose()
        {
            if (Pointer is not null)
            {
                Pointer = null;
                Length = 0;
                _pin.Dispose();
                _owner?.Dispose();
                _owner = null;
            }
        }
    }
}
