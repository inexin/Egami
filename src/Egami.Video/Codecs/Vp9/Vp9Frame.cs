using System.Buffers;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// One decoded VP9 frame as the decoder keeps it for prediction: three planes of <typeparamref name="T"/> samples (byte for 8 bit,
/// ushort above), each sized to whole 64x64 superblocks so blocks that overhang the frame are written without checks. Pixels
/// past the coded size are never read by later frames: references are read with their edges repeated, as libvpx's border
/// extension does. Reference slots share a frame by counting references; the planes come from the stream's memory source.
/// </summary>
internal sealed class Vp9Frame<T> : IDisposable
    where T : unmanaged
{
    private readonly IMemoryOwner<byte>?[] _owners = new IMemoryOwner<byte>?[3];
    private readonly Memory<byte>[] _planes = new Memory<byte>[3];

    public Vp9Frame(int width, int height, int subsamplingX, int subsamplingY, int bitDepth, IPixelMemorySource memory)
    {
        Width = width;
        Height = height;
        SubsamplingX = subsamplingX;
        SubsamplingY = subsamplingY;
        BitDepth = bitDepth;
        int alignedWidth = (width + 63) & ~63, alignedHeight = (height + 63) & ~63;
        Strides = [alignedWidth, alignedWidth >> subsamplingX, alignedWidth >> subsamplingX];
        int[] rows = [alignedHeight, alignedHeight >> subsamplingY, alignedHeight >> subsamplingY];
        for (int p = 0; p < 3; p++)
        {
            int bytes = Strides[p] * rows[p] * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
            if (bytes <= memory.MaxBlockBytes)
            {
                _owners[p] = memory.Allocate(bytes, ClearMode.Zero);
                _planes[p] = _owners[p]!.Memory;
            }
            else
            {
                _planes[p] = new byte[bytes];
            }
        }
    }

    /// <summary>Gets the coded width.</summary>
    public int Width { get; }

    /// <summary>Gets the coded height.</summary>
    public int Height { get; }

    public int SubsamplingX { get; }

    public int SubsamplingY { get; }

    public int BitDepth { get; }

    /// <summary>Gets the row stride of each plane, in samples.</summary>
    public int[] Strides { get; }

    /// <summary>Gets or sets the stream's color space (VP9 color_space, 0 to 7) the frame was coded with.</summary>
    public int ColorSpace { get; set; }

    public int ColorRange { get; set; }

    public int RenderWidth { get; set; }

    public int RenderHeight { get; set; }

    /// <summary>Gets or sets how many reference slots and in-flight decodes hold the frame.</summary>
    public int RefCount { get; set; }

    /// <summary>Gets the width a plane's prediction sees (the coded width).</summary>
    public int CropWidth(int plane) => plane == 0 ? Width : (Width + SubsamplingX) >> SubsamplingX;

    public int CropHeight(int plane) => plane == 0 ? Height : (Height + SubsamplingY) >> SubsamplingY;

    /// <summary>Gets the width intra prediction sees: the coded width rounded up to 8, as libvpx's y_width and uv_width.</summary>
    public int AlignedWidth(int plane) => ((Width + 7) & ~7) >> (plane == 0 ? 0 : SubsamplingX);

    public int AlignedHeight(int plane) => ((Height + 7) & ~7) >> (plane == 0 ? 0 : SubsamplingY);

    public Span<T> Plane(int plane) => MemoryMarshal.Cast<byte, T>(_planes[plane].Span);

    /// <summary>Gets whether the frame can be reused for a new frame of these dimensions and format.</summary>
    public bool Matches(int width, int height, int subsamplingX, int subsamplingY, int bitDepth) =>
        ((Width + 63) & ~63) == ((width + 63) & ~63) && ((Height + 63) & ~63) == ((height + 63) & ~63) &&
        SubsamplingX == subsamplingX && SubsamplingY == subsamplingY && BitDepth == bitDepth;

    public void Dispose()
    {
        for (int p = 0; p < 3; p++)
        {
            _owners[p]?.Dispose();
            _owners[p] = null;
            _planes[p] = default;
        }
    }
}
