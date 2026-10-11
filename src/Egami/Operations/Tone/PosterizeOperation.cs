using System.Buffers;

using Egami.Operations.Geometry;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Operations.Tone;

/// <summary>
/// Dithered posterize: red, green and blue rounded to evenly spaced levels with 1.4's 8 × 8 Bayer pattern added first, or with
/// Floyd–Steinberg error diffusion (7, 3, 5 and 1 sixteenths, left to right on every row). Alpha is kept.
/// </summary>
internal sealed class PosterizeOperation(int levels, DitherMode dither) : IRasterOperation
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width;
        float n = levels - 1;
        if (dither == DitherMode.Ordered)
        {
            input.ForEachBand((start, end) =>
            {
                RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(width);
                try
                {
                    Span<RgbaF32> row = buffer.AsSpan(0, width);
                    for (int y = start; y < end; y++)
                    {
                        PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), row);
                        for (int x = 0; x < width; x++)
                        {
                            // The pattern spans one level step, centred on zero.
                            float offset = ((FrameMapper.Bayer[((y & 7) * 8) + (x & 7)] + 0.5f) / 64f) - 0.5f;
                            ref RgbaF32 p = ref row[x];
                            p.R = Round(p.R, offset);
                            p.G = Round(p.G, offset);
                            p.B = Round(p.B, offset);
                        }

                        PixelConvert.Convert<RgbaF32, TPixel>(row, input.Destination.Row(y));
                    }
                }
                finally
                {
                    ArrayPool<RgbaF32>.Shared.Return(buffer);
                }
            });
            return;
        }

        // Error diffusion runs row after row on one thread.
        RgbaF32[] rows = ArrayPool<RgbaF32>.Shared.Rent(width);
        float[] errors = ArrayPool<float>.Shared.Rent(2 * 3 * (width + 2));
        try
        {
            Span<RgbaF32> row = rows.AsSpan(0, width);
            Span<float> current = errors.AsSpan(0, 3 * (width + 2)), next = errors.AsSpan(3 * (width + 2), 3 * (width + 2));
            current.Clear();
            next.Clear();
            for (int y = 0; y < input.Source.Height; y++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), row);
                for (int x = 0; x < width; x++)
                {
                    ref RgbaF32 p = ref row[x];
                    p.R = Diffuse(p.R, x, 0, current, next);
                    p.G = Diffuse(p.G, x, 1, current, next);
                    p.B = Diffuse(p.B, x, 2, current, next);
                }

                PixelConvert.Convert<RgbaF32, TPixel>(row, input.Destination.Row(y));
                Span<float> swap = current;
                current = next;
                next = swap;
                next.Clear();
            }
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(rows);
            ArrayPool<float>.Shared.Return(errors);
        }

        float Round(float v, float offset)
        {
            return MathF.Floor((Math.Clamp(v, 0f, 1f) * n) + 0.5f + offset) is float q ? Math.Clamp(q, 0, n) / n : 0;
        }

        float Diffuse(float v, int x, int c, Span<float> current, Span<float> next)
        {
            int at = ((x + 1) * 3) + c;
            float wanted = Math.Clamp(v + current[at], 0f, 1f);
            float chosen = MathF.Floor((wanted * n) + 0.5f) / n;
            float error = wanted - chosen;
            current[at + 3] += error * (7f / 16);
            next[at - 3] += error * (3f / 16);
            next[at] += error * (5f / 16);
            next[at + 3] += error * (1f / 16);
            return chosen;
        }
    }
}
