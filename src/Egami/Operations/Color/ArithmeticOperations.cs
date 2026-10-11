using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Operations.Scopes;
using Egami.Pixels;

namespace Egami.Operations.Color;

/// <summary>
/// Per-channel arithmetic with a constant, as ImageMagick's -evaluate defines each operator. Integer RGB(A) pixels are evaluated as
/// 16-bit samples in double precision and rounded to nearest, as ImageMagick's Q16 build evaluates them, so 8-bit and 16-bit
/// results are identical to it: 8-bit types run a 256-entry table, 16-bit ones a 65,536-entry table. Other types compute per value
/// on straight floats.
/// </summary>
internal sealed class ArithmeticOperation(PixelMath op, float value, ColorChannels channels) : IRasterOperation, IRunsOnViews
{
    private const double Quantum = 65535;

    public Size OutputSize(Size input) => input;

    /// <summary>Applies the operator to a 16-bit sample and rounds the result to the nearest sample, clamped.</summary>
    internal ushort Apply16(int q)
    {
        double r = op switch
        {
            PixelMath.Add => q + (value * Quantum),
            PixelMath.Subtract => q - (value * Quantum),
            PixelMath.Multiply => q * (double)value,
            PixelMath.Divide => q / (value == 0 ? 1e-12 : (double)value),
            PixelMath.Pow => Quantum * Math.Pow(q / Quantum, value),
            PixelMath.Log => (value * q / Quantum) + 1 > 1e-12 ? Quantum * Math.Log((value * q / Quantum) + 1) / Math.Log(value + 1.0) : 0,
            PixelMath.Min => Math.Min(q, value * Quantum),
            PixelMath.Max => Math.Max(q, value * Quantum),
            _ => value * Quantum,
        };
        return double.IsNaN(r) || r <= 0 ? (ushort)0 : r >= Quantum ? (ushort)65535 : (ushort)(r + 0.5);
    }

    /// <summary>Applies the operator to a value in 0..1 and clamps the result to 0..1, for float pixels.</summary>
    internal double Apply(double v)
    {
        double r = op switch
        {
            PixelMath.Add => v + value,
            PixelMath.Subtract => v - value,
            PixelMath.Multiply => v * value,
            PixelMath.Divide => v / (value == 0 ? 1e-12 : value),
            PixelMath.Pow => Math.Pow(v, value),
            PixelMath.Log => (value * v) + 1 > 1e-12 ? Math.Log((value * v) + 1) / Math.Log(value + 1.0) : 0,
            PixelMath.Min => Math.Min(v, value),
            PixelMath.Max => Math.Max(v, value),
            _ => value,
        };
        return double.IsNaN(r) ? 0 : Math.Clamp(r, 0, 1);
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        OperationRows.CopyIfSeparate(input);
        bool[]? applies = Applies<TPixel>();
        if (applies is not null && TPixel.Layout.ChannelType == PixelChannelType.Unorm8)
        {
            byte[] table = new byte[256];
            for (int v = 0; v < 256; v++)
            {
                table[v] = (byte)((Apply16(v * 257) + 128) / 257);
            }

            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    Span<byte> samples = MemoryMarshal.AsBytes(input.Destination.Row(y));
                    for (int i = 0, c = 0; i < samples.Length; i++, c = c + 1 == applies.Length ? 0 : c + 1)
                    {
                        if (applies[c])
                        {
                            samples[i] = table[samples[i]];
                        }
                    }
                }
            });
            return;
        }

        if (applies is not null)
        {
            ushort[] table = ArrayPool<ushort>.Shared.Rent(65536);
            try
            {
                for (int q = 0; q < 65536; q++)
                {
                    table[q] = Apply16(q);
                }

                input.ForEachBand((start, end) =>
                {
                    for (int y = start; y < end; y++)
                    {
                        Span<ushort> samples = MemoryMarshal.Cast<TPixel, ushort>(input.Destination.Row(y));
                        for (int i = 0, c = 0; i < samples.Length; i++, c = c + 1 == applies.Length ? 0 : c + 1)
                        {
                            if (applies[c])
                            {
                                samples[i] = table[samples[i]];
                            }
                        }
                    }
                });
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(table);
            }

            return;
        }

        int width = input.Destination.Width;
        input.ForEachBand((start, end) =>
        {
            RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
            try
            {
                Span<RgbaF32> row = buffer.AsSpan(0, width);
                for (int y = start; y < end; y++)
                {
                    Span<TPixel> pixels = input.Destination.Row(y);
                    PixelConvert.Convert<TPixel, RgbaF32>(pixels, row);
                    for (int x = 0; x < width; x++)
                    {
                        RgbaF32 p = row[x];
                        row[x] = new RgbaF32(
                            (channels & ColorChannels.Red) != 0 ? (float)Apply(p.R) : p.R,
                            (channels & ColorChannels.Green) != 0 ? (float)Apply(p.G) : p.G,
                            (channels & ColorChannels.Blue) != 0 ? (float)Apply(p.B) : p.B,
                            (channels & ColorChannels.Alpha) != 0 ? (float)Apply(p.A) : p.A);
                    }

                    PixelConvert.Convert<RgbaF32, TPixel>(row, pixels);
                }
            }
            finally
            {
                ArrayPool<RgbaF32>.Shared.Return(buffer);
            }
        });
    }

    /// <summary>
    /// Gets, for straight RGB(A) types with one byte or one 16-bit sample per channel, whether each sample of a pixel is changed;
    /// null for other types.
    /// </summary>
    private bool[]? Applies<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (TPixel.Layout.ChannelType is not (PixelChannelType.Unorm8 or PixelChannelType.Unorm16) || TPixel.Layout.Alpha == AlphaKind.Premultiplied)
        {
            return null;
        }

        int[]? order = TPixel.Layout.ChannelOrder switch
        {
            PixelChannelOrder.Rgba => [0, 1, 2, 3],
            PixelChannelOrder.Bgra => [2, 1, 0, 3],
            PixelChannelOrder.Rgb => [0, 1, 2],
            PixelChannelOrder.Bgr => [2, 1, 0],
            _ => null,
        };
        return order is null || Unsafe.SizeOf<TPixel>() != order.Length * (TPixel.Layout.ChannelType == PixelChannelType.Unorm8 ? 1 : 2)
            ? null
            : [.. order.Select(c => ((int)channels & (1 << c)) != 0)];
    }
}
