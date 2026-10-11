using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Scopes;

/// <summary>
/// OnChannels: runs a same-size sub-chain, then keeps its result only in the chosen working RGBA channels, restoring the others
/// from the original, as ImageMagick's -channel does. A sub-chain of color matrices never gets here: RasterOps turns it into one
/// matrix whose other rows are the identity.
/// </summary>
internal sealed class ChannelScopeOperation(ColorChannels channels, IReadOnlyList<IRasterOperation> steps) : IRasterOperation, IAreaStep, IScope
{
    public Size OutputSize(Size input)
    {
        return input;
    }

    public IRasterOperation Bind(Func<Colors.LinearCurve?> curve)
    {
        return new ChannelScopeOperation(channels, ScopeRunner.Bind(steps, curve));
    }

    public void Validate(Size size)
    {
        ScopeRunner.Validate(steps, size, new Rect(0, 0, size.Width, size.Height));
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        PixelPlane<TPixel> plane = input.Destination;
        RunArea(new OperationInput<TPixel>(plane, plane, input.Context, input.FrameIndex, input.CancellationToken) { Memory = input.Memory }, new Rect(0, 0, plane.Width, plane.Height));
    }

    public void RunArea<TPixel>(OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (area.IsEmpty || steps.Count == 0 || channels == ColorChannels.None)
        {
            return;
        }

        if ((channels & ColorChannels.All) == ColorChannels.All)
        {
            ScopeRunner.Run(steps, input, area);
            return;
        }

        PixelPlane<TPixel> original = ScopeRunner.Scratch(input, area.Width, area.Height);
        try
        {
            ScopeRunner.Copy(input.Destination, area, original, new Point(0, 0));
            ScopeRunner.Run(steps, input, area);
            int[]? bytes = ByteOffsets<TPixel>();
            RowRunner.Run(area.Height, (long)area.Width * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>(), input.Context.MaxParallelism, (start, end) =>
            {
                for (int r = start; r < end; r++)
                {
                    Restore(original.Row(r), input.Destination.Row(area.Y + r).Slice(area.X, area.Width), bytes);
                }
            }, input.CancellationToken);
        }
        finally
        {
            original.Dispose();
        }
    }

    /// <summary>Gets, for straight one-byte-per-channel RGBA types, the byte offsets of red, green, blue and alpha; null for others.</summary>
    private static int[]? ByteOffsets<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return typeof(TPixel) == typeof(Rgba8) ? [0, 1, 2, 3] : typeof(TPixel) == typeof(Bgra8) ? [2, 1, 0, 3] : null;
    }

    /// <summary>Copies the channels not chosen from the original into the result row.</summary>
    private void Restore<TPixel>(ReadOnlySpan<TPixel> original, Span<TPixel> result, int[]? bytes)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (bytes is not null)
        {
            // Straight bytes: a mask with 0xFF in every byte of a kept channel, so result = (result & keep) | (original & ~keep).
            uint keep = 0;
            for (int c = 0; c < 4; c++)
            {
                if (((int)channels & (1 << c)) != 0)
                {
                    keep |= 0xFFu << (bytes[c] * 8);
                }
            }

            ReadOnlySpan<uint> o = MemoryMarshal.Cast<TPixel, uint>(original);
            Span<uint> r = MemoryMarshal.Cast<TPixel, uint>(result);
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = (r[i] & keep) | (o[i] & ~keep);
            }

            return;
        }

        RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(2 * result.Length);
        try
        {
            Span<RgbaF32> before = buffer.AsSpan(0, result.Length), after = buffer.AsSpan(result.Length, result.Length);
            PixelConvert.Convert(original, before);
            PixelConvert.Convert<TPixel, RgbaF32>(result, after);
            for (int i = 0; i < after.Length; i++)
            {
                RgbaF32 b = before[i], a = after[i];
                after[i] = new RgbaF32(
                    (channels & ColorChannels.Red) != 0 ? a.R : b.R,
                    (channels & ColorChannels.Green) != 0 ? a.G : b.G,
                    (channels & ColorChannels.Blue) != 0 ? a.B : b.B,
                    (channels & ColorChannels.Alpha) != 0 ? a.A : b.A);
            }

            PixelConvert.Convert<RgbaF32, TPixel>(after, result);
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(buffer);
        }
    }
}
