using System.Buffers;

using Egami.Operations.Geometry;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Operations.Tone;

/// <summary>
/// Dithered posterize: red, green and blue rounded to evenly spaced levels with an ordered pattern added first (1.4's 8 × 8 Bayer,
/// or another map), Riemersma's Hilbert walk, or error diffusion: Floyd–Steinberg (7, 3, 5 and 1 sixteenths, left to right on
/// every row) or another published kernel. Alpha is kept.
/// </summary>
internal sealed class PosterizeOperation(int levels, DitherMode dither) : IRasterOperation
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width;
        float n = levels - 1;
        if (dither is not (DitherMode.Ordered or DitherMode.FloydSteinberg))
        {
            RunOther(input);
            return;
        }

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

    /// <summary>
    /// The other modes: any ordered map, spanning one level step; Riemersma's Hilbert-curve walk; or a published kernel, left to
    /// right on every row. Error is kept per channel in the 0 to 1 scale.
    /// </summary>
    private void RunOther<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width, height = input.Source.Height;
        float n = levels - 1;
        float Level(float v) => Math.Clamp(MathF.Floor((Math.Clamp(v, 0f, 1f) * n) + 0.5f), 0, n) / n;
        if (DitherKernels.Ordered(dither) is (byte[] map, int size))
        {
            int cells = size * size;
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
                            float offset = (((map[((y % size) * size) + (x % size)] + 0.5f) / cells) - 0.5f) / n;
                            ref RgbaF32 p = ref row[x];
                            p.R = Level(p.R + offset);
                            p.G = Level(p.G + offset);
                            p.B = Level(p.B + offset);
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

        // The whole frame, so the Hilbert walk and kernels that reach rows below can update pixels before they are written.
        RgbaF32[] pixels = ArrayPool<RgbaF32>.Shared.Rent(width * height);
        try
        {
            for (int y = 0; y < height; y++)
            {
                PixelConvert.Convert<TPixel, RgbaF32>(input.Source.Row(y), pixels.AsSpan(y * width, width));
            }

            if (dither == DitherMode.Riemersma)
            {
                const int History = 16;
                float[] weights = new float[History];
                float sum = 0;
                for (int i = 0; i < History; i++)
                {
                    weights[i] = MathF.Pow(History, (float)i / (History - 1));
                    sum += weights[i];
                }

                float[] errors = new float[History * 3];
                int head = 0;
                int side = 1;
                while (side < Math.Max(width, height))
                {
                    side *= 2;
                }

                for (long d = 0; d < (long)side * side; d++)
                {
                    (int x, int y) = Quantization.GeneralMapper.HilbertPoint(side, d);
                    if (x >= width || y >= height)
                    {
                        continue;
                    }

                    ref RgbaF32 p = ref pixels[(y * width) + x];
                    for (int c = 0; c < 3; c++)
                    {
                        float correction = 0;
                        for (int k = 0; k < History; k++)
                        {
                            correction += errors[(((head + k) % History) * 3) + c] * weights[k] / sum;
                        }

                        ref float v = ref c == 0 ? ref p.R : ref c == 1 ? ref p.G : ref p.B;
                        float wanted = Math.Clamp(v + correction, 0f, 1f);
                        v = Level(wanted);
                        errors[(head * 3) + c] = wanted - v;
                    }

                    head = (head + 1) % History;
                }
            }
            else
            {
                (int Dx, int Dy, double Weight)[] taps = DitherKernels.Kernel(dither)!.Taps;
                float[] pending = new float[width * height * 3];
                for (int y = 0; y < height; y++)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    for (int x = 0; x < width; x++)
                    {
                        ref RgbaF32 p = ref pixels[(y * width) + x];
                        for (int c = 0; c < 3; c++)
                        {
                            ref float v = ref c == 0 ? ref p.R : ref c == 1 ? ref p.G : ref p.B;
                            float wanted = Math.Clamp(v + pending[(((y * width) + x) * 3) + c], 0f, 1f);
                            v = Level(wanted);
                            float error = wanted - v;
                            foreach ((int dx, int dy, double weight) in taps)
                            {
                                int tx = x + dx, ty = y + dy;
                                if (tx >= 0 && tx < width && ty < height)
                                {
                                    pending[(((ty * width) + tx) * 3) + c] += error * (float)weight;
                                }
                            }
                        }
                    }
                }
            }

            for (int y = 0; y < height; y++)
            {
                PixelConvert.Convert<RgbaF32, TPixel>(pixels.AsSpan(y * width, width), input.Destination.Row(y));
            }
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(pixels);
        }
    }
}
