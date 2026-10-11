using System.Buffers;
using System.Runtime.CompilerServices;

using Egami.Operations.Geometry;
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
                        ReliefRows.LoadStraight(source.Row(Math.Clamp(nextSource, 0, height - 1)), straight);
                        for (int c = 0; c < channels; c++)
                        {
                            Span<float> block = all.Slice(c * perChannel, perChannel);
                            Span<float> padded = block.Slice(ringSize + blurredSize, line);
                            Extract(straight, padded.Slice(r, width), c);
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

                ReliefRows.LoadStraight(source.Row(y), straight);
                for (int c = 0; c < channels; c++)
                {
                    Span<float> blurredRows = all.Slice((c * perChannel) + ringSize, blurredSize);
                    ReadOnlySpan<float> up = blurredRows.Slice(Math.Max(y - 1, 0) % 3 * width, width);
                    ReadOnlySpan<float> center = blurredRows.Slice(y % 3 * width, width);
                    ReadOnlySpan<float> down = blurredRows.Slice(Math.Min(y + 1, height - 1) % 3 * width, width);
                    Relief(up, center, down, scratch, output, c);
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

    /// <summary>The directional Sobel derivative of the blurred rows, into channel <paramref name="c"/> of <paramref name="output"/>.</summary>
    private void Relief(ReadOnlySpan<float> up, ReadOnlySpan<float> center, ReadOnlySpan<float> down, Span<float> scratch, Span<float> output, int c)
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
            output[(x * 4) + c] = Math.Clamp(0.5f + (kx * gx) + (ky * gy), 0f, 1f);
        }
    }
}
