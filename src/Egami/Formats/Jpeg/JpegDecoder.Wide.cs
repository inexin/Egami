using System.Buffers;
using System.Numerics;

using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <content>
/// 12-bit files: every scan goes into the coefficient buffer, as for a progressive file, and the last pass runs a 32-bit IDCT
/// into 16-bit strips, then upsamples and converts them through 16-bit pixels. The 8-bit path is chosen once per image and never
/// sees any of this.
/// </content>
internal sealed partial class JpegDecoder<TPixel>
{
    private ushort[] _wideRow = [];
    private ushort[] _wideSamples = [];
    private int[] _wideSums = [];

    /// <summary>Dequantizes and transforms MCU row <paramref name="my"/> of the coefficient buffer into the 16-bit strips.</summary>
    private void TransformWideRow(int my)
    {
        Span<short> natural = stackalloc short[64];
        foreach (Component c in _components)
        {
            Span<ushort> strip = c.WideStrip(my);
            int end = Math.Min(_mcusX * c.H, c.EndColumn);
            for (int v = 0; v < c.V; v++)
            {
                int by = (my * c.V) + v;
                Span<short> row = c.BlockRow(by);
                for (int bx = c.FirstColumn; bx < end; bx++)
                {
                    Span<short> block = row.Slice(bx * 64, 64);
                    Span<ushort> target = strip[(((v * c.StripWidth) + bx) * c.BlockSize)..];
                    if (c.Quant is null)
                    {
                        // A preview before the component's first scan: mid-gray.
                        JpegIdct12.FillDc(0, target, c.StripWidth, c.BlockSize);
                        continue;
                    }

                    ulong nonzero = JpegBlock.NonZero(block);
                    if (c.BlockSize > 1 && (nonzero & ~1UL) == 0)
                    {
                        JpegIdct12.FillDc(block[0] * c.Quant![0], target, c.StripWidth, c.BlockSize);
                        continue;
                    }

                    natural.Clear();
                    for (; nonzero != 0; nonzero &= nonzero - 1)
                    {
                        int z = BitOperations.TrailingZeroCount(nonzero);
                        natural[JpegTables.ZigZag[z]] = block[z];
                    }

                    c.WideIdct(natural, target);
                }
            }
        }
    }

    /// <summary>Upsamples, converts and writes the output rows of MCU row <paramref name="my"/> of a 12-bit file.</summary>
    private void EmitWideRows(int my)
    {
        int first = my * _mcuRows;
        int last = Math.Min(_height, first + _mcuRows);
        for (int y = first; y < last; y++)
        {
            ConvertWideRow(0, y, _raster!.Row(y));
        }
    }

    /// <summary>Converts the part of output row <paramref name="y"/> from column <paramref name="x"/> of a 12-bit file into <paramref name="target"/>.</summary>
    private void ConvertWideRow<TOut>(int x, int y, Span<TOut> target)
        where TOut : unmanaged, IPixelType<TOut>
    {
        int count = _components.Length;
        int width = target.Length;
        ReadOnlySpan<ushort> a = WideSampleRow(0, y).Slice(x, width);
        ReadOnlySpan<ushort> b = count > 1 ? WideSampleRow(1, y).Slice(x, width) : default;
        ReadOnlySpan<ushort> c = count > 2 ? WideSampleRow(2, y).Slice(x, width) : default;
        ReadOnlySpan<ushort> k = count > 3 ? WideSampleRow(3, y).Slice(x, width) : default;
        JpegWide.ConvertRow(_header.Details.ColorSpace, _header.Precision, a, b, c, k, target, _wideRow);
    }

    /// <summary>Returns one full-width 16-bit row of component <paramref name="index"/> for output row <paramref name="y"/>, upsampled as <see cref="SampleRow"/> does.</summary>
    private ReadOnlySpan<ushort> WideSampleRow(int index, int y)
    {
        Component c = _components[index];
        int hr = c.HorizontalRatio;
        int vr = c.VerticalRatio;
        int width = _width;
        int row = y / vr;
        ReadOnlySpan<ushort> cur = c.WideRow(row);
        if (hr == 1 && vr == 1)
        {
            return cur[..width];
        }

        Span<ushort> output = _wideSamples.AsSpan(index * width, width);
        if (_fast || _blockSize == 1 || (hr != 1 && hr != 2) || (vr != 1 && vr != 2) || !c.WholeRatios)
        {
            JpegWide.Replicate(cur, hr, output);
            return output;
        }

        int n = c.Width;
        Span<int> sums = _wideSums.AsSpan(0, n + 2);
        if (vr == 2)
        {
            bool upper = (y & 1) == 0;
            ReadOnlySpan<ushort> near = c.WideRow(Math.Clamp(upper ? row - 1 : row + 1, 0, c.Height - 1));
            if (hr == 1)
            {
                JpegWide.Vertical(cur[..width], near[..width], output, upper ? 1 : 2);
                return output;
            }

            JpegWide.ColumnSums(cur, near, n, sums);
            JpegWide.Double(sums, n, output, 8, 7, 4);
            return output;
        }

        JpegWide.Widen(cur, n, sums);
        JpegWide.Double(sums, n, output, 1, 2, 2);
        return output;
    }

    private void DisposeWide()
    {
        if (_wideRow.Length > 0)
        {
            ArrayPool<ushort>.Shared.Return(_wideRow);
            ArrayPool<ushort>.Shared.Return(_wideSamples);
            ArrayPool<int>.Shared.Return(_wideSums);
            _wideRow = [];
            _wideSamples = [];
            _wideSums = [];
        }
    }
}
