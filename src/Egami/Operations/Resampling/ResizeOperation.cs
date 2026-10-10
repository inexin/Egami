using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Resampling;

/// <summary>How a resize turns the requested box into an output size and a source area.</summary>
internal enum ResizeKind
{
    /// <summary>Exactly the requested size; a 0 dimension keeps the aspect ratio.</summary>
    Stretch,

    /// <summary>Fits inside the box with the aspect ratio kept.</summary>
    Fit,

    /// <summary>Covers the box with the aspect ratio kept, keeping the part at the anchor.</summary>
    Cover,
}

/// <summary>
/// Resizes with a separable filter: a horizontal pass into a small ring of float rows, then a vertical pass out of the ring, so no
/// full-size intermediate exists. The source can be a sub-rectangle, which is how Fill and a Crop followed by a resize run.
/// </summary>
internal sealed class ResizeOperation(
    ResizeKind mode, int width, int height, ResampleFilter filter, ColorSpace colorSpace, Anchor anchor = Anchor.Center, bool enlarge = true, Rect? crop = null)
    : IRasterOperation, INeedsNewPlane, IHoldsBuffers, Geometry.IUpdatesMetadata
{
    private Plan? _plan;

    public void UpdateMetadata(Metadata.RasterMetadata metadata, Size input, Size output)
    {
        Geometry.ExifUpkeep.Dimensions(metadata, output);
        Geometry.ExifUpkeep.DropThumbnail(metadata);
    }

    /// <summary>
    /// Returns the resize to run on an image stored with <paramref name="orientation"/> before it is turned upright, so that
    /// turning the result equals resizing the upright image: the target swaps for the orientations that swap the size, and
    /// <see cref="Anchor"/> moves to where it lands in the stored image.
    /// </summary>
    internal ResizeOperation BeforeOrienting(Metadata.ImageOrientation orientation)
    {
        bool swaps = orientation >= Metadata.ImageOrientation.Transpose;
        return new ResizeOperation(mode, swaps ? height : width, swaps ? width : height, filter, colorSpace, StoredAnchor(anchor, orientation), enlarge, crop);
    }

    /// <summary>Returns this resize reading only <paramref name="rect"/> of its input, as a Crop before it would.</summary>
    internal ResizeOperation WithCrop(Rect rect)
    {
        return new ResizeOperation(mode, width, height, filter, colorSpace, anchor, enlarge, rect);
    }

    /// <summary>Maps an anchor in the upright image to the same point of the stored image.</summary>
    private static Anchor StoredAnchor(Anchor anchor, Metadata.ImageOrientation orientation)
    {
        // Anchor as a point in [0, 1]², then through the inverse of the turn the orientation asks for.
        int u = anchor switch { Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft => 0, Anchor.TopRight or Anchor.Right or Anchor.BottomRight => 2, _ => 1 };
        int v = anchor switch { Anchor.TopLeft or Anchor.Top or Anchor.TopRight => 0, Anchor.BottomLeft or Anchor.Bottom or Anchor.BottomRight => 2, _ => 1 };
        (int su, int sv) = orientation switch
        {
            Metadata.ImageOrientation.FlipHorizontal => (2 - u, v),
            Metadata.ImageOrientation.Rotate180 => (2 - u, 2 - v),
            Metadata.ImageOrientation.FlipVertical => (u, 2 - v),
            Metadata.ImageOrientation.Transpose => (v, u),
            Metadata.ImageOrientation.Rotate90 => (v, 2 - u),
            Metadata.ImageOrientation.Transverse => (2 - v, 2 - u),
            Metadata.ImageOrientation.Rotate270 => (2 - v, u),
            _ => (u, v),
        };
        return (su, sv) switch
        {
            (0, 0) => Anchor.TopLeft,
            (1, 0) => Anchor.Top,
            (2, 0) => Anchor.TopRight,
            (0, 1) => Anchor.Left,
            (2, 1) => Anchor.Right,
            (0, 2) => Anchor.BottomLeft,
            (1, 2) => Anchor.Bottom,
            (2, 2) => Anchor.BottomRight,
            _ => Anchor.Center,
        };
    }

    /// <summary>Gets whether this resize already reads a sub-rectangle chosen by a merged Crop.</summary>
    internal bool HasCrop => crop is not null;

    public bool NeedsNewPlane(Size input)
    {
        return !PlanFor(input).IsIdentity;
    }

    public Size OutputSize(Size input)
    {
        return PlanFor(input).Output;
    }

    public void ReleaseBuffers()
    {
        _plan?.ReleaseWeights();
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Plan plan = PlanFor(new Size(input.Source.Width, input.Source.Height));
        if (plan.IsIdentity)
        {
            OperationRows.CopyIfSeparate(input);
            return;
        }

        (ResizeWeights horizontal, ResizeWeights vertical) = plan.Weights(filter);
        if (filter == ResampleFilter.Nearest)
        {
            RunNearest(input, horizontal, vertical);
            return;
        }

        RowConverter<TPixel> converter = new(colorSpace == ColorSpace.Linear);
        int outWidth = plan.Output.Width;
        int xLow = horizontal.First[0];
        int xCount = horizontal.First[outWidth - 1] + horizontal.Count[outWidth - 1] - xLow;
        int ringRows = vertical.Stride;
        float[] pairs = horizontal.Pairs;
        // Opaque 8-bit rows take a fixed-point horizontal pass, which measured 1.5 times faster than float for a 5× reduction.
        bool fixedPoint = RowConverter<TPixel>.FourByte && colorSpace == ColorSpace.Encoded;
        short[] fixedWeights = fixedPoint ? horizontal.Fixed : [];
        int[] groupCount = fixedPoint ? horizontal.GroupCount : [];
        int[] pairCount = horizontal.PairCount;
        input.ForEachBand((start, end) =>
        {
            // The source line holds one extra zeroed pixel, which the last odd tap pair reads with a zero weight.
            int lineLength = (xCount + 1) * 4;
            float[] ringBuffer = ArrayPool<float>.Shared.Rent(ringRows * outWidth * 4);
            float[] lineBuffer = ArrayPool<float>.Shared.Rent(lineLength);
            float[] outBuffer = ArrayPool<float>.Shared.Rent(outWidth * 4);
            bool[] opaqueRows = ArrayPool<bool>.Shared.Rent(ringRows);

            // Opaque rows take the fixed-point pass straight from bytes; it reads up to three zeroed pixels past the last tap.
            byte[] byteBuffer = ArrayPool<byte>.Shared.Rent(fixedPoint ? (xCount + 3) * 4 : 0);
            try
            {
                Span<float> ring = ringBuffer.AsSpan(0, ringRows * outWidth * 4);
                Span<float> line = lineBuffer.AsSpan(0, lineLength);
                Span<float> output = outBuffer.AsSpan(0, outWidth * 4);
                line[(xCount * 4)..].Clear();
                int next = vertical.First[start];
                for (int y = start; y < end; y++)
                {
                    int first = vertical.First[y];
                    int count = vertical.Count[y];
                    for (; next < first + count; next++)
                    {
                        int slot = next % ringRows;
                        Span<float> target = ring.Slice(slot * outWidth * 4, outWidth * 4);
                        ReadOnlySpan<TPixel> sourceRow = input.Source.Row(next).Slice(xLow, xCount);
                        if (fixedPoint && ResizeKernels.IsOpaque8(MemoryMarshal.AsBytes(sourceRow)))
                        {
                            Span<byte> bytes = byteBuffer.AsSpan(0, (xCount + 3) * 4);
                            MemoryMarshal.AsBytes(sourceRow).CopyTo(bytes);
                            bytes[(xCount * 4)..].Clear();
                            ResizeKernels.HorizontalFixed8(bytes, horizontal.First, groupCount, fixedWeights, horizontal.GroupStride, xLow, target);
                            opaqueRows[slot] = true;
                            continue;
                        }

                        opaqueRows[slot] = converter.Load(sourceRow, line);
                        ResizeKernels.Horizontal(line, horizontal.First, pairCount, pairs, horizontal.PairStride, xLow, target);
                    }

                    bool opaque = true;
                    for (int k = 0; k < count; k++)
                    {
                        opaque &= opaqueRows[(first + k) % ringRows];
                    }

                    ResizeKernels.Vertical(ring, outWidth * 4, ringRows, first, vertical.Row(y), output);
                    converter.Store(output, input.Destination.Row(y), opaque);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(ringBuffer);
                ArrayPool<float>.Shared.Return(lineBuffer);
                ArrayPool<float>.Shared.Return(outBuffer);
                ArrayPool<bool>.Shared.Return(opaqueRows);
                ArrayPool<byte>.Shared.Return(byteBuffer);
            }
        });
    }

    private static void RunNearest<TPixel>(OperationInput<TPixel> input, ResizeWeights horizontal, ResizeWeights vertical)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int[] columns = horizontal.First;
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                ReadOnlySpan<TPixel> source = input.Source.Row(vertical.First[y]);
                Span<TPixel> destination = input.Destination.Row(y);
                for (int x = 0; x < destination.Length; x++)
                {
                    destination[x] = source[columns[x]];
                }
            }
        });
    }

    private Plan PlanFor(Size input)
    {
        if (_plan is Plan cached && cached.Input == input)
        {
            return cached;
        }

        Rect area = crop ?? new Rect(0, 0, input.Width, input.Height);
        if (!new Rect(0, 0, input.Width, input.Height).Contains(area))
        {
            throw new ArgumentOutOfRangeException($"The crop rectangle {area} leaves the {input.Width}×{input.Height} image.", innerException: null);
        }

        double aw = area.Width;
        double ah = area.Height;
        Size output;
        double sx = area.X;
        double sy = area.Y;
        double sw = aw;
        double sh = ah;
        switch (mode)
        {
            case ResizeKind.Stretch:
                output = new Size(
                    width > 0 ? width : Math.Max(1, (int)Math.Round(aw * height / ah)),
                    height > 0 ? height : Math.Max(1, (int)Math.Round(ah * width / aw)));
                break;
            case ResizeKind.Fit:
                double fit = Math.Min(width / aw, height / ah);
                output = !enlarge && fit >= 1
                    ? area.Size
                    : new Size(Math.Clamp((int)Math.Round(aw * fit), 1, width), Math.Clamp((int)Math.Round(ah * fit), 1, height));
                break;
            default:
                double fill = Math.Max(width / aw, height / ah);
                output = new Size(width, height);
                sw = Math.Min(aw, width / fill);
                sh = Math.Min(ah, height / fill);
                (double ox, double oy) = AnchoredOffset(anchor, aw - sw, ah - sh);
                sx += ox;
                sy += oy;
                break;
        }

        bool identity = output == new Size(input.Width, input.Height) && area.Size == output;
        _plan = new Plan(input, output, sx, sy, sw, sh, area, identity);
        return _plan;
    }

    private static (double X, double Y) AnchoredOffset(Anchor anchor, double spareX, double spareY)
    {
        double x = anchor switch
        {
            Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft => 0,
            Anchor.TopRight or Anchor.Right or Anchor.BottomRight => spareX,
            _ => spareX / 2,
        };
        double y = anchor switch
        {
            Anchor.TopLeft or Anchor.Top or Anchor.TopRight => 0,
            Anchor.BottomLeft or Anchor.Bottom or Anchor.BottomRight => spareY,
            _ => spareY / 2,
        };
        return (x, y);
    }

    /// <summary>The output size and source area for one input size, with the weights built on first use.</summary>
    private sealed record Plan(Size Input, Size Output, double SourceX, double SourceY, double SourceWidth, double SourceHeight, Rect Area, bool IsIdentity)
    {
        private (ResizeWeights, ResizeWeights)? _weights;

        internal void ReleaseWeights()
        {
            if (_weights is (ResizeWeights horizontal, ResizeWeights vertical))
            {
                horizontal.Dispose();
                vertical.Dispose();
                _weights = null;
            }
        }

        internal (ResizeWeights Horizontal, ResizeWeights Vertical) Weights(ResampleFilter filter)
        {
            return _weights ??= (
                ResizeWeights.Build(filter, Output.Width, SourceX, SourceWidth, Area.X, Area.Right - 1),
                ResizeWeights.Build(filter, Output.Height, SourceY, SourceHeight, Area.Y, Area.Bottom - 1));
        }
    }
}

/// <summary>
/// Moves rows between a pixel type and the float working form: four floats per pixel, RGBA in 0..1, encoded or linear,
/// premultiplied unless the row is opaque. Rgba8, Bgra8 and RgbaF32 have vector paths; other types go through RgbaF32.
/// </summary>
internal sealed class RowConverter<TPixel>(bool linear)
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private static readonly bool HasAlpha = TPixel.Layout.Alpha != AlphaKind.None;
    private static readonly bool EightBit = TPixel.Layout.ChannelType == PixelChannelType.Unorm8;
    internal static readonly bool FourByte = typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Bgra8);

    /// <summary>CMYK types filter their four inks as numbers, so black stays black instead of being rebuilt from RGB.</summary>
    internal static readonly bool Ink = CmykInk.Is<TPixel>();

    /// <summary>Converts <paramref name="source"/> to working pixels at the start of <paramref name="destination"/>.</summary>
    /// <returns><see langword="true"/> if every pixel is opaque, so nothing was premultiplied.</returns>
    internal bool Load(ReadOnlySpan<TPixel> source, Span<float> destination)
    {
        Span<float> pixels = destination[..(source.Length * 4)];
        if (Ink)
        {
            CmykInk.Load(source, pixels);
            return true;
        }

        if (FourByte && !linear)
        {
            return ResizeKernels.Load8(MemoryMarshal.AsBytes(source), pixels);
        }

        if (FourByte)
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(source);
            float[] table = Srgb.LinearFrom8;
            for (int i = 0; i < bytes.Length; i += 4)
            {
                pixels[i] = table[bytes[i]];
                pixels[i + 1] = table[bytes[i + 1]];
                pixels[i + 2] = table[bytes[i + 2]];
                pixels[i + 3] = bytes[i + 3] * (1f / 255f);
            }
        }
        else
        {
            PixelConvert.Convert(source, MemoryMarshal.Cast<float, RgbaF32>(pixels));
            if (linear)
            {
                ToLinear(pixels);
            }
        }

        return !HasAlpha || ResizeKernels.PremultiplyRow(pixels);
    }

    /// <summary>
    /// Converts working pixels back to <typeparamref name="TPixel"/>, dividing out alpha and clamping the filter's overshoot.
    /// <paramref name="opaque"/> says every source row that contributed was opaque. The source is used as scratch.
    /// </summary>
    internal void Store(Span<float> source, Span<TPixel> destination, bool opaque)
    {
        Span<float> pixels = source[..(destination.Length * 4)];
        if (Ink)
        {
            CmykInk.Store<TPixel>(pixels, destination);
            return;
        }

        opaque |= !HasAlpha;
        if (FourByte && !linear)
        {
            ResizeKernels.Store8(pixels, MemoryMarshal.AsBytes(destination), opaque);
            return;
        }

        ResizeKernels.Unpremultiply(pixels, opaque);
        if (FourByte)
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(destination);
            for (int i = 0; i < bytes.Length; i += 4)
            {
                bytes[i] = Srgb.FromLinearTo8(pixels[i]);
                bytes[i + 1] = Srgb.FromLinearTo8(pixels[i + 1]);
                bytes[i + 2] = Srgb.FromLinearTo8(pixels[i + 2]);
                bytes[i + 3] = (byte)(int)((pixels[i + 3] * 255f) + 0.5f);
            }

            return;
        }

        if (linear)
        {
            FromLinear(pixels);
        }

        PixelConvert.Convert<RgbaF32, TPixel>(MemoryMarshal.Cast<float, RgbaF32>(pixels), destination);
    }

    private static void ToLinear(Span<float> pixels)
    {
        float[] table = Srgb.LinearFrom8;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                // 8-bit values arrive as exact multiples of 1/255, so the table gives the same result as the formula.
                pixels[i + c] = EightBit ? table[(int)((pixels[i + c] * 255f) + 0.5f)] : (float)Srgb.ToLinear(pixels[i + c]);
            }
        }
    }

    private static void FromLinear(Span<float> pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            for (int c = 0; c < 3; c++)
            {
                pixels[i + c] = EightBit ? Srgb.FromLinearTo8(pixels[i + c]) * (1f / 255f) : (float)Srgb.FromLinear(pixels[i + c]);
            }
        }
    }
}
