using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Resampling;

/// <summary>How a resize turns the requested box into an output size and a source area.</summary>
internal enum ResizeMode
{
    /// <summary>Exactly the requested size; a 0 dimension keeps the aspect ratio.</summary>
    Stretch,

    /// <summary>Fits inside the box with the aspect ratio kept.</summary>
    Fit,

    /// <summary>Covers the box with the aspect ratio kept, keeping the part at the anchor.</summary>
    Fill,
}

/// <summary>
/// Resizes with a separable filter: a horizontal pass into a small ring of float rows, then a vertical pass out of the ring, so no
/// full-size intermediate exists. The source can be a sub-rectangle, which is how Fill and a Crop followed by a resize run.
/// </summary>
internal sealed class ResizeOperation(
    ResizeMode mode, int width, int height, ResampleFilter filter, ColorSpace colorSpace, Anchor anchor = Anchor.Center, bool enlarge = true, Rect? crop = null)
    : IRasterOperation, INeedsNewPlane
{
    private Plan? _plan;

    /// <summary>Returns this resize reading only <paramref name="rect"/> of its input, as a Crop before it would.</summary>
    internal ResizeOperation WithCrop(Rect rect)
    {
        return new ResizeOperation(mode, width, height, filter, colorSpace, anchor, enlarge, rect);
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
        input.ForEachBand((start, end) =>
        {
            float[] ringBuffer = ArrayPool<float>.Shared.Rent(ringRows * outWidth * 4);
            float[] lineBuffer = ArrayPool<float>.Shared.Rent(Math.Max(xCount, outWidth) * 4);
            try
            {
                Span<Vector4> ring = MemoryMarshal.Cast<float, Vector4>(ringBuffer.AsSpan(0, ringRows * outWidth * 4));
                Span<Vector4> line = MemoryMarshal.Cast<float, Vector4>(lineBuffer.AsSpan(0, Math.Max(xCount, outWidth) * 4));
                int next = vertical.First[start];
                for (int y = start; y < end; y++)
                {
                    int first = vertical.First[y];
                    int count = vertical.Count[y];
                    for (; next < first + count; next++)
                    {
                        Span<Vector4> source = line[..xCount];
                        converter.Load(input.Source.Row(next).Slice(xLow, xCount), source);
                        HorizontalPass(source, horizontal, xLow, ring.Slice(next % ringRows * outWidth, outWidth));
                    }

                    Span<Vector4> output = line[..outWidth];
                    VerticalPass(ring, outWidth, ringRows, first, vertical.Row(y), output);
                    converter.Store(output, input.Destination.Row(y));
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(ringBuffer);
                ArrayPool<float>.Shared.Return(lineBuffer);
            }
        });
    }

    private static void HorizontalPass(ReadOnlySpan<Vector4> source, ResizeWeights weights, int xLow, Span<Vector4> destination)
    {
        for (int x = 0; x < destination.Length; x++)
        {
            ReadOnlySpan<float> w = weights.Row(x);
            ReadOnlySpan<Vector4> taps = source.Slice(weights.First[x] - xLow, w.Length);
            Vector4 sum = Vector4.Zero;
            for (int k = 0; k < w.Length; k++)
            {
                sum += taps[k] * w[k];
            }

            destination[x] = sum;
        }
    }

    private static void VerticalPass(ReadOnlySpan<Vector4> ring, int width, int ringRows, int first, ReadOnlySpan<float> weights, Span<Vector4> destination)
    {
        destination.Clear();
        for (int k = 0; k < weights.Length; k++)
        {
            ReadOnlySpan<Vector4> row = ring.Slice((first + k) % ringRows * width, width);
            float w = weights[k];
            for (int x = 0; x < width; x++)
            {
                destination[x] += row[x] * w;
            }
        }
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
            case ResizeMode.Stretch:
                output = new Size(
                    width > 0 ? width : Math.Max(1, (int)Math.Round(aw * height / ah)),
                    height > 0 ? height : Math.Max(1, (int)Math.Round(ah * width / aw)));
                break;
            case ResizeMode.Fit:
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

        internal (ResizeWeights Horizontal, ResizeWeights Vertical) Weights(ResampleFilter filter)
        {
            return _weights ??= (
                ResizeWeights.Build(filter, Output.Width, SourceX, SourceWidth, Area.X, Area.Right - 1),
                ResizeWeights.Build(filter, Output.Height, SourceY, SourceHeight, Area.Y, Area.Bottom - 1));
        }
    }
}

/// <summary>
/// Moves rows between a pixel type and the float working form: straight or premultiplied RGBA in 0..1, encoded or linear.
/// </summary>
internal sealed class RowConverter<TPixel>(bool linear)
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private static readonly bool HasAlpha = TPixel.Layout.Alpha != AlphaKind.None;
    private static readonly bool EightBit = TPixel.Layout.ChannelType == PixelChannelType.Unorm8;

    /// <summary>Converts <paramref name="source"/> to premultiplied working pixels.</summary>
    internal void Load(ReadOnlySpan<TPixel> source, Span<Vector4> destination)
    {
        PixelConvert.Convert(source, MemoryMarshal.Cast<Vector4, RgbaF32>(destination));
        if (linear)
        {
            float[] table = Srgb.LinearFrom8;
            for (int i = 0; i < destination.Length; i++)
            {
                Vector4 p = destination[i];
                destination[i] = EightBit
                    ? new Vector4(table[(int)((p.X * 255f) + 0.5f)], table[(int)((p.Y * 255f) + 0.5f)], table[(int)((p.Z * 255f) + 0.5f)], p.W)
                    : new Vector4((float)Srgb.ToLinear(p.X), (float)Srgb.ToLinear(p.Y), (float)Srgb.ToLinear(p.Z), p.W);
            }
        }

        if (HasAlpha)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                Vector4 p = destination[i];
                if (p.W != 1f)
                {
                    destination[i] = new Vector4(p.X * p.W, p.Y * p.W, p.Z * p.W, p.W);
                }
            }
        }
    }

    /// <summary>Converts premultiplied working pixels back to <typeparamref name="TPixel"/>, clamping the filter's overshoot.</summary>
    internal void Store(Span<Vector4> source, Span<TPixel> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            Vector4 p = source[i];
            float a = Math.Clamp(p.W, 0f, 1f);
            if (!HasAlpha || a == 1f)
            {
                p = Vector4.Clamp(p, Vector4.Zero, Vector4.One);
            }
            else if (a > 0f)
            {
                p = Vector4.Clamp(new Vector4(p.X / a, p.Y / a, p.Z / a, 0f), Vector4.Zero, Vector4.One);
            }
            else
            {
                p = Vector4.Zero;
            }

            if (linear)
            {
                p = EightBit
                    ? new Vector4(Srgb.FromLinearTo8(p.X) / 255f, Srgb.FromLinearTo8(p.Y) / 255f, Srgb.FromLinearTo8(p.Z) / 255f, 0f)
                    : new Vector4((float)Srgb.FromLinear(p.X), (float)Srgb.FromLinear(p.Y), (float)Srgb.FromLinear(p.Z), 0f);
            }

            p.W = HasAlpha ? a : 1f;
            source[i] = p;
        }

        PixelConvert.Convert<RgbaF32, TPixel>(MemoryMarshal.Cast<Vector4, RgbaF32>(source), destination);
    }
}
