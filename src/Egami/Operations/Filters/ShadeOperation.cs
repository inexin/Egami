using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// ImageMagick's -shade (and +shade with <c>colored</c>), from its documented formula: the luminance is a height field, the
/// normal is (left − right, below − above, 2) over the 3 × 3 neighbourhood (a y-up frame), and the shading is N·L / |N| for the
/// light at the azimuth and elevation, or L.z where the surface is flat. Values are computed in ImageMagick's 16-bit quantum
/// scale with its Q16 HDRI build's types (intensities and their sums in single precision, the shading in double) and rounded to
/// 8 bits as it does, so Rgba8 and Bgra8 results are identical to it;
/// other types take the same values at full precision. Edges repeat the edge pixel; alpha is kept.
/// </summary>
internal sealed class ShadeOperation : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    private const double QuantumRange = 65535.0;
    private const double Epsilon = 1.0e-12;

    private readonly double _lx;
    private readonly double _ly;
    private readonly double _lz;
    private readonly bool _colored;

    internal ShadeOperation(float azimuth, float elevation, bool colored)
    {
        double a = Math.PI * azimuth / 180.0, e = Math.PI * elevation / 180.0;
        _lx = QuantumRange * Math.Cos(a) * Math.Cos(e);
        _ly = QuantumRange * Math.Sin(a) * Math.Cos(e);
        _lz = QuantumRange * Math.Sin(e);
        _colored = colored;
    }

    public (int X, int Y)? Reach => (1, 1);

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int height = input.Source.Height;
        long bytes = (long)input.Source.Width * Unsafe.SizeOf<TPixel>() * height;
        Chunks.Run(height, bytes, input.Context.MaxParallelism, (start, end) => Band(input, start, end), input.CancellationToken);
    }

    /// <summary>Rounds a quantum to 8 bits as ImageMagick's Q16 HDRI ScaleQuantumToChar does, in single precision.</summary>
    internal static byte ToByte(float quantum)
    {
        if (!(quantum > 0))
        {
            return 0;
        }

        float scaled = quantum / 257f;
        return scaled >= 255f ? (byte)255 : (byte)(scaled + 0.5f);
    }

    private static bool Bgr<TPixel>() => typeof(TPixel) == typeof(Bgra8);

    private void Band<TPixel>(OperationInput<TPixel> input, int start, int end)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        int width = source.Width, height = source.Height, padded = width + 2;
        bool bytes = Resampling.RowConverter<TPixel>.FourByte;
        float[] ringBuffer = ArrayPool<float>.Shared.Rent(3 * padded);
        float[] rowBuffer = ArrayPool<float>.Shared.Rent(width * 4);
        try
        {
            Span<float> ring = ringBuffer.AsSpan(0, 3 * padded);
            Span<float> straight = rowBuffer.AsSpan(0, width * 4);
            int loaded = Math.Max(start - 1, 0) - 1;
            for (int y = start; y < end; y++)
            {
                if ((y & 15) == 0)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                }

                for (int last = Math.Min(y + 1, height - 1); loaded < last;)
                {
                    loaded++;
                    Intensities(source.Row(loaded), ring.Slice(loaded % 3 * padded, padded), straight, bytes);
                }

                ReadOnlySpan<float> pre = ring.Slice(Math.Max(y - 1, 0) % 3 * padded, padded);
                ReadOnlySpan<float> center = ring.Slice(y % 3 * padded, padded);
                ReadOnlySpan<float> post = ring.Slice(Math.Min(y + 1, height - 1) % 3 * padded, padded);
                if (bytes)
                {
                    ShadeBytes(source.Row(y), destination.Row(y), pre, center, post);
                }
                else
                {
                    ReliefRows.LoadStraight(source.Row(y), straight);
                    for (int x = 0; x < width; x++)
                    {
                        double shade = ShadeAt(pre, center, post, x + 1);
                        int i = x * 4;
                        if (_colored)
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                double quantum = (float)(straight[i + c] * (float)QuantumRange);
                                straight[i + c] = Math.Clamp((float)(1.0 / QuantumRange * shade * quantum) / (float)QuantumRange, 0f, 1f);
                            }
                        }
                        else
                        {
                            float v = Math.Clamp((float)shade / (float)QuantumRange, 0f, 1f);
                            straight[i] = v;
                            straight[i + 1] = v;
                            straight[i + 2] = v;
                        }
                    }

                    ReliefRows.StoreStraight(straight, destination.Row(y));
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(ringBuffer);
            ArrayPool<float>.Shared.Return(rowBuffer);
        }
    }

    /// <summary>Writes a row's intensities in quantum units, padded by one repeated pixel on each side.</summary>
    private static void Intensities<TPixel>(ReadOnlySpan<TPixel> row, Span<float> target, Span<float> straight, bool bytes)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = row.Length;
        if (bytes)
        {
            ReadOnlySpan<byte> p = MemoryMarshal.AsBytes(row);
            int ri = Bgr<TPixel>() ? 2 : 0, bi = 2 - ri;
            for (int x = 0; x < width; x++)
            {
                // A byte is the quantum value × 257, exactly, as ImageMagick reads 8-bit files.
                target[x + 1] = Intensity(p[(x * 4) + ri] * 257.0, p[(x * 4) + 1] * 257.0, p[(x * 4) + bi] * 257.0);
            }
        }
        else
        {
            ReliefRows.LoadStraight(row, straight);
            for (int x = 0; x < width; x++)
            {
                int i = x * 4;
                target[x + 1] = Intensity((float)(straight[i] * QuantumRange), (float)(straight[i + 1] * QuantumRange), (float)(straight[i + 2] * QuantumRange));
            }
        }

        target[0] = target[1];
        target[width + 1] = target[width];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Intensity(double red, double green, double blue)
    {
        // ImageMagick's MagickRealType is a float in this build: the weighted sum is made in double and stored in single precision.
        float intensity = (float)((ReliefRows.Red * red) + (ReliefRows.Green * green) + (ReliefRows.Blue * blue));
        return Math.Clamp(intensity, 0f, (float)QuantumRange);
    }

    /// <summary>The shading in quantum units at padded index <paramref name="i"/>, in ImageMagick's order of operations.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double ShadeAt(ReadOnlySpan<float> pre, ReadOnlySpan<float> center, ReadOnlySpan<float> post, int i)
    {
        // Summed in single precision, as ImageMagick sums its float intensities, so flat areas can leave a rounding residue.
        double nx = pre[i - 1] + center[i - 1] + post[i - 1] - pre[i + 1] - center[i + 1] - post[i + 1];
        double ny = post[i - 1] + post[i] + post[i + 1] - pre[i - 1] - pre[i] - pre[i + 1];
        if (Math.Abs(nx) <= Epsilon && Math.Abs(ny) <= Epsilon)
        {
            return _lz;
        }

        const double nz = 2.0 * QuantumRange;
        double distance = (nx * _lx) + (ny * _ly) + (nz * _lz);
        if (distance > Epsilon)
        {
            double length = (nx * nx) + (ny * ny) + (nz * nz);
            if (length > Epsilon * Epsilon)
            {
                return distance / Math.Sqrt(length);
            }
        }

        return 0;
    }

    private void ShadeBytes<TPixel>(ReadOnlySpan<TPixel> sourceRow, Span<TPixel> destinationRow, ReadOnlySpan<float> pre, ReadOnlySpan<float> center, ReadOnlySpan<float> post)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ReadOnlySpan<byte> s = MemoryMarshal.AsBytes(sourceRow);
        Span<byte> d = MemoryMarshal.AsBytes(destinationRow);
        for (int x = 0; x < sourceRow.Length; x++)
        {
            double shade = ShadeAt(pre, center, post, x + 1);
            int i = x * 4;
            if (_colored)
            {
                double factor = 1.0 / QuantumRange * shade;
                d[i] = ToByte((float)(factor * (s[i] * 257.0)));
                d[i + 1] = ToByte((float)(factor * (s[i + 1] * 257.0)));
                d[i + 2] = ToByte((float)(factor * (s[i + 2] * 257.0)));
            }
            else
            {
                byte v = ToByte((float)shade);
                d[i] = v;
                d[i + 1] = v;
                d[i + 2] = v;
            }

            d[i + 3] = s[i + 3];
        }
    }
}
