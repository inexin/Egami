using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Emboss: a Gaussian blur of σ, then the derivative along the light's direction from a 3 × 3 Sobel stencil, scaled by the depth
/// and added to mid gray: 0.5 − depth · (∂h/∂x · cos θ + ∂h/∂y · sin θ), with y up and θ counterclockwise from the x axis (as
/// ImageMagick's -shade azimuth), so slopes that face the light are bright and flat areas are 0.5. The height is the Rec. 709
/// luminance of the stored color, or each color channel on its own; alpha is kept. Both passes repeat the edge pixel. Rows are
/// blurred band by band through a ring, so memory does not grow with the image; every value is computed by the same float
/// operations wherever it falls, so bands and scopes give the same result.
/// </summary>
internal sealed class EmbossOperation : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    private readonly float[] _weights;
    private readonly int _r;
    private readonly float _cos;
    private readonly float _sin;
    private readonly float _depth;
    private readonly bool _gray;

    internal EmbossOperation(float angle, float depth, float sigma, bool grayscale)
    {
        _weights = sigma > 0 ? ConvolutionOperation.GaussianWeights(sigma) : [1f];
        _r = _weights.Length / 2;
        double a = angle * Math.PI / 180;
        _cos = (float)Math.Cos(a);
        _sin = (float)Math.Sin(a);
        _depth = depth;
        _gray = grayscale;
    }

    public (int X, int Y)? Reach => (_r + 1, _r + 1);

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int height = input.Source.Height;
        long bytes = (long)input.Source.Width * Unsafe.SizeOf<TPixel>() * height;
        Chunks.Run(height, bytes, input.Context.MaxParallelism, (start, end) => Band(input, start, end), input.CancellationToken);
    }

    private void Band<TPixel>(OperationInput<TPixel> input, int start, int end)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        int width = source.Width, height = source.Height, r = _r, taps = _weights.Length;

        // Rgba8 and Bgra8 read their channels and write the relief as bytes, with the float conversion's arithmetic (b / 255 in,
        // v × 255 + 0.5 out), so the pixels are the same; blue is first in Bgra8.
        bool bytes = RowConverter<TPixel>.FourByte;
        int red = typeof(TPixel) == typeof(Bgra8) ? 2 : 0;
        int channels = _gray ? 1 : 3, ringRows = taps, line = width + (2 * r);

        // Per channel: a ring of horizontally blurred rows, three fully blurred rows, and one padded source row.
        int ringSize = ringRows * width, blurredSize = 3 * width;
        int perChannel = ringSize + blurredSize + line;
        float[] buffer = ArrayPool<float>.Shared.Rent((channels * perChannel) + (2 * width * 4) + (3 * width));
        try
        {
            Span<float> all = buffer.AsSpan();
            Span<float> straight = all.Slice(channels * perChannel, width * 4);
            Span<float> output = all.Slice((channels * perChannel) + (width * 4), width * 4);
            Span<float> scratch = all.Slice((channels * perChannel) + (2 * width * 4), 3 * width);

            // Blurred rows t from max(start − 1, 0) to min(end, height − 1) are made in order; each needs ring rows t − r to t + r.
            int first = Math.Max(start - 1, 0), last = Math.Min(end, height - 1);
            int nextSource = first - r, nextBlurred = first;
            for (int y = start; y < end; y++)
            {
                if ((y & 15) == 0)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                }

                for (int target = Math.Min(y + 1, last); nextBlurred <= target; nextBlurred++)
                {
                    for (; nextSource <= nextBlurred + r; nextSource++)
                    {
                        ReadOnlySpan<TPixel> sourceRow = source.Row(Math.Clamp(nextSource, 0, height - 1));
                        if (!bytes)
                        {
                            ReliefRows.LoadStraight(sourceRow, straight);
                        }

                        for (int c = 0; c < channels; c++)
                        {
                            Span<float> block = all.Slice(c * perChannel, perChannel);
                            Span<float> padded = block.Slice(ringSize + blurredSize, line);
                            if (bytes)
                            {
                                ExtractBytes(MemoryMarshal.Cast<TPixel, uint>(sourceRow), padded.Slice(r, width), c == 0 ? red : c == 2 ? 2 - red : 1);
                            }
                            else
                            {
                                Extract(straight, padded.Slice(r, width), c);
                            }

                            padded[..r].Fill(padded[r]);
                            padded[(r + width)..].Fill(padded[r + width - 1]);
                            Span<float> horizontal = block.Slice(Slot(nextSource, ringRows) * width, width);
                            for (int k = 0; k < taps; k++)
                            {
                                ConvolutionKernels.Accumulate(padded.Slice(k, width), _weights[k], horizontal, k == 0);
                            }
                        }
                    }

                    for (int c = 0; c < channels; c++)
                    {
                        Span<float> block = all.Slice(c * perChannel, perChannel);
                        Span<float> blurred = block.Slice(ringSize + (nextBlurred % 3 * width), width);
                        for (int k = 0; k < taps; k++)
                        {
                            ConvolutionKernels.Accumulate(block.Slice(Slot(nextBlurred - r + k, ringRows) * width, width), _weights[k], blurred, k == 0);
                        }
                    }
                }

                if (bytes)
                {
                    // Each channel's relief in a row of its own, then packed with the source's alpha byte.
                    for (int c = 0; c < channels; c++)
                    {
                        Span<float> blurredRows = all.Slice((c * perChannel) + ringSize, blurredSize);
                        ReadOnlySpan<float> up = blurredRows.Slice(Math.Max(y - 1, 0) % 3 * width, width);
                        ReadOnlySpan<float> center = blurredRows.Slice(y % 3 * width, width);
                        ReadOnlySpan<float> down = blurredRows.Slice(Math.Min(y + 1, height - 1) % 3 * width, width);
                        Relief(up, center, down, scratch, output.Slice(c * width, width), 0, 1);
                    }

                    StoreBytes(output, width, MemoryMarshal.Cast<TPixel, uint>(source.Row(y)), MemoryMarshal.Cast<TPixel, uint>(destination.Row(y)), red);
                    continue;
                }

                ReliefRows.LoadStraight(source.Row(y), straight);
                for (int c = 0; c < channels; c++)
                {
                    Span<float> blurredRows = all.Slice((c * perChannel) + ringSize, blurredSize);
                    ReadOnlySpan<float> up = blurredRows.Slice(Math.Max(y - 1, 0) % 3 * width, width);
                    ReadOnlySpan<float> center = blurredRows.Slice(y % 3 * width, width);
                    ReadOnlySpan<float> down = blurredRows.Slice(Math.Min(y + 1, height - 1) % 3 * width, width);
                    Relief(up, center, down, scratch, output, c, 4);
                }

                for (int x = 0; x < width; x++)
                {
                    int i = x * 4;
                    if (_gray)
                    {
                        output[i + 1] = output[i];
                        output[i + 2] = output[i];
                    }

                    output[i + 3] = straight[i + 3];
                }

                ReliefRows.StoreStraight(output, destination.Row(y));
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    private static int Slot(int v, int rows) => ((v % rows) + rows) % rows;

    /// <summary>Writes channel <paramref name="c"/> of a straight row (or its luminance, when gray) to <paramref name="target"/>.</summary>
    private void Extract(ReadOnlySpan<float> straight, Span<float> target, int c)
    {
        if (_gray)
        {
            ReliefRows.Heights(straight, target, luminance: true);
            return;
        }

        for (int x = 0; x < target.Length; x++)
        {
            target[x] = straight[(x * 4) + c];
        }
    }

    /// <summary>
    /// Writes each pixel's height from four-byte pixels: the Rec. 709 luminance when gray, else the channel at byte
    /// <paramref name="at"/>, as <see cref="Extract"/> computes them from the float row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ExtractBytes(ReadOnlySpan<uint> pixels, Span<float> target, int at)
    {
        int x = 0, red = at, blue = 2 - at;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<uint> low = Vector256.Create(0xFFu);
            Vector256<float> scale = Vector256.Create(255f);
            if (_gray)
            {
                Vector256<float> wr = Vector256.Create((float)ReliefRows.Red), wg = Vector256.Create((float)ReliefRows.Green), wb = Vector256.Create((float)ReliefRows.Blue);
                for (; x <= target.Length - Vector256<uint>.Count; x += Vector256<uint>.Count)
                {
                    Vector256<uint> p = Vector256.Create(pixels[x..]);
                    Vector256<float> r = Vector256.ConvertToSingle(((p >>> (red * 8)) & low).AsInt32()) / scale;
                    Vector256<float> g = Vector256.ConvertToSingle(((p >>> 8) & low).AsInt32()) / scale;
                    Vector256<float> b = Vector256.ConvertToSingle(((p >>> (blue * 8)) & low).AsInt32()) / scale;
                    Vector256.Min(Vector256.Max(((wr * r) + (wg * g)) + (wb * b), Vector256<float>.Zero), Vector256<float>.One).CopyTo(target[x..]);
                }
            }
            else
            {
                for (; x <= target.Length - Vector256<uint>.Count; x += Vector256<uint>.Count)
                {
                    (Vector256.ConvertToSingle(((Vector256.Create(pixels[x..]) >>> (at * 8)) & low).AsInt32()) / scale).CopyTo(target[x..]);
                }
            }
        }

        for (; x < target.Length; x++)
        {
            uint p = pixels[x];
            target[x] = _gray
                ? Math.Clamp(((float)ReliefRows.Red * (((p >> (red * 8)) & 0xFF) / 255f)) + ((float)ReliefRows.Green * (((p >> 8) & 0xFF) / 255f)) + ((float)ReliefRows.Blue * (((p >> (blue * 8)) & 0xFF) / 255f)), 0f, 1f)
                : ((p >> (at * 8)) & 0xFF) / 255f;
        }
    }

    /// <summary>
    /// Packs the relief rows (one per channel, <paramref name="width"/> apart, one row for gray) into four-byte pixels with the
    /// source's alpha byte, each value as (int)(v × 255 + 0.5), red at byte <paramref name="red"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void StoreBytes(ReadOnlySpan<float> values, int width, ReadOnlySpan<uint> source, Span<uint> row, int red)
    {
        ReadOnlySpan<float> first = values[..width], green = _gray ? first : values.Slice(width, width), third = _gray ? first : values.Slice(2 * width, width);
        int x = 0, blue = 2 - red;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> scale = Vector256.Create(255f), half = Vector256.Create(0.5f);
            Vector256<uint> alpha = Vector256.Create(0xFF000000u);
            for (; x <= width - Vector256<uint>.Count; x += Vector256<uint>.Count)
            {
                Vector256<uint> r = Vector256.ConvertToInt32((Vector256.Create(first[x..]) * scale) + half).AsUInt32();
                Vector256<uint> g = Vector256.ConvertToInt32((Vector256.Create(green[x..]) * scale) + half).AsUInt32();
                Vector256<uint> b = Vector256.ConvertToInt32((Vector256.Create(third[x..]) * scale) + half).AsUInt32();
                ((r << (red * 8)) | (g << 8) | (b << (blue * 8)) | (Vector256.Create(source[x..]) & alpha)).CopyTo(row[x..]);
            }
        }

        for (; x < width; x++)
        {
            uint r = (uint)(int)((first[x] * 255f) + 0.5f), g = (uint)(int)((green[x] * 255f) + 0.5f), b = (uint)(int)((third[x] * 255f) + 0.5f);
            row[x] = (r << (red * 8)) | (g << 8) | (b << (blue * 8)) | (source[x] & 0xFF000000u);
        }
    }

    /// <summary>The directional Sobel derivative of the blurred rows, into channel <paramref name="c"/> of <paramref name="output"/>, <paramref name="stride"/> floats a pixel.</summary>
    private void Relief(ReadOnlySpan<float> up, ReadOnlySpan<float> center, ReadOnlySpan<float> down, Span<float> scratch, Span<float> output, int c, int stride)
    {
        int width = center.Length;
        Span<float> columns = scratch[..width], rows = scratch.Slice(width, width);
        for (int x = 0; x < width; x++)
        {
            columns[x] = up[x] + (2 * center[x]) + down[x];
            rows[x] = down[x] - up[x];
        }

        // Sobel over 8 gives the slope per pixel; y is flipped so the angle turns counterclockwise on screen.
        float kx = -_depth * _cos / 8, ky = _depth * _sin / 8;
        for (int x = 0; x < width; x++)
        {
            int left = Math.Max(x - 1, 0), right = Math.Min(x + 1, width - 1);
            float gx = columns[right] - columns[left];
            float gy = rows[left] + (2 * rows[x]) + rows[right];
            output[(x * stride) + c] = Math.Clamp(0.5f + (kx * gx) + (ky * gy), 0f, 1f);
        }
    }
}
