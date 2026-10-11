using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// The bilateral filter (Tomasi and Manduchi, 1998): each neighbour in the (2r + 1) square weighs
/// exp(−d² / 2σs²) · exp(−Δ² / 2σr²), d its distance and Δ the root-mean-square difference of its premultiplied red, green and
/// blue from the center's, so on gray images Δ is the intensity difference ImageMagick's -bilateral-blur uses. Rows are kept as
/// planes of each channel so eight pixels are weighed at once. Above radius 8 the taps are taken every s pixels, s = ⌈r / 8⌉, with
/// weights scaled by s², which keeps the cost bounded.
/// </summary>
internal sealed class BilateralBlurOperation : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    /// <summary>The largest radius taken tap by tap.</summary>
    internal const int DirectRadius = 8;

    private readonly int _r;
    private readonly int _stride;
    private readonly float _rangeScale;
    private readonly (int Dx, int Dy, float Weight)[] _taps;

    /// <param name="spatialSigma">The spatial sigma.</param>
    /// <param name="rangeSigma">The range sigma.</param>
    /// <param name="radius">The window radius.</param>
    /// <param name="stride">The tap spacing, or <see langword="null"/> for ⌈r / 8⌉; tests pass 1 for the full window.</param>
    internal BilateralBlurOperation(float spatialSigma, float rangeSigma, int radius, int? stride = null)
    {
        _r = radius;
        _stride = stride ?? Math.Max(1, (radius + DirectRadius - 1) / DirectRadius);
        _rangeScale = -1f / (3 * 2 * rangeSigma * rangeSigma);
        List<(int, int, float)> taps = [];
        int reach = radius / _stride * _stride;
        for (int dy = -reach; dy <= reach; dy += _stride)
        {
            for (int dx = -reach; dx <= reach; dx += _stride)
            {
                if ((dx * dx) + (dy * dy) <= radius * radius)
                {
                    taps.Add((dx, dy, (float)Math.Exp(-((dx * dx) + (dy * dy)) / (2.0 * spatialSigma * spatialSigma))));
                }
            }
        }

        _taps = [.. taps];
    }

    public (int X, int Y)? Reach => (_r, _r);

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        RowConverter<TPixel> converter = new(null);
        PaddedRows<TPixel> rows = new(source, converter, EdgeMode.Clamp, _r);
        int width = source.Width, r = _r, side = (2 * r) + 1, padded = width + (2 * r);
        input.ForEachBand((start, end) =>
        {
            // Ring of 2r + 1 rows, each stored as four planes of padded width: R, G, B, A.
            float[] ring = ArrayPool<float>.Shared.Rent(side * 4 * padded);
            float[] line = ArrayPool<float>.Shared.Rent(rows.Length);
            float[] output = ArrayPool<float>.Shared.Rent(width * 4);
            bool[] opaque = ArrayPool<bool>.Shared.Rent(side);
            try
            {
                for (int v = start - r; v < end + r; v++)
                {
                    int slot = Slot(v, side);
                    opaque[slot] = rows.Load(v, line.AsSpan(0, rows.Length));
                    Span<float> planes = ring.AsSpan(slot * 4 * padded, 4 * padded);
                    for (int x = 0; x < padded; x++)
                    {
                        planes[x] = line[x * 4];
                        planes[padded + x] = line[(x * 4) + 1];
                        planes[(2 * padded) + x] = line[(x * 4) + 2];
                        planes[(3 * padded) + x] = line[(x * 4) + 3];
                    }

                    int y = v - r;
                    if (y < start)
                    {
                        continue;
                    }

                    bool allOpaque = true;
                    for (int k = 0; k < side; k++)
                    {
                        allOpaque &= opaque[k];
                    }

                    Row(ring, padded, side, y, width, output.AsSpan(0, width * 4));
                    converter.Store(output.AsSpan(0, width * 4), destination.Row(y), allOpaque);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(ring);
                ArrayPool<float>.Shared.Return(line);
                ArrayPool<float>.Shared.Return(output);
                ArrayPool<bool>.Shared.Return(opaque);
            }
        });
    }

    private static int Slot(int v, int rows) => ((v % rows) + rows) % rows;

    /// <summary>Filters output row <paramref name="y"/> from the ring, writing interleaved premultiplied floats.</summary>
    private void Row(float[] ring, int padded, int side, int y, int width, Span<float> output)
    {
        int r = _r;
        ref float rings = ref MemoryMarshal.GetArrayDataReference(ring);
        int center = Slot(y, side) * 4 * padded;
        Span<int> rowAt = stackalloc int[side];
        for (int dy = -r; dy <= r; dy++)
        {
            rowAt[dy + r] = (Slot(y + dy, side) * 4 * padded) + r;
        }

        int x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Span<float> lanes = stackalloc float[Vector256<float>.Count * 4];
            Vector256<float> scale = Vector256.Create(_rangeScale);
            for (; x <= width - Vector256<float>.Count; x += Vector256<float>.Count)
            {
                nuint c = (nuint)(center + r + x);
                Vector256<float> cr = Vector256.LoadUnsafe(ref rings, c), cg = Vector256.LoadUnsafe(ref rings, c + (nuint)padded), cb = Vector256.LoadUnsafe(ref rings, c + (nuint)(2 * padded));
                Vector256<float> sr = Vector256<float>.Zero, sg = sr, sb = sr, sa = sr, sw = sr;
                foreach ((int dx, int dy, float weight) in _taps)
                {
                    nuint at = (nuint)(rowAt[dy + r] + x + dx);
                    Vector256<float> nr = Vector256.LoadUnsafe(ref rings, at), ng = Vector256.LoadUnsafe(ref rings, at + (nuint)padded);
                    Vector256<float> nb = Vector256.LoadUnsafe(ref rings, at + (nuint)(2 * padded)), na = Vector256.LoadUnsafe(ref rings, at + (nuint)(3 * padded));
                    Vector256<float> dr = nr - cr, dg = ng - cg, db = nb - cb;
                    Vector256<float> w = FastExp.Exp(((dr * dr) + (dg * dg) + (db * db)) * scale) * weight;
                    sr += nr * w;
                    sg += ng * w;
                    sb += nb * w;
                    sa += na * w;
                    sw += w;
                }

                Vector256<float> inverse = Vector256<float>.One / sw;
                (sr * inverse).CopyTo(lanes);
                (sg * inverse).CopyTo(lanes[8..]);
                (sb * inverse).CopyTo(lanes[16..]);
                (sa * inverse).CopyTo(lanes[24..]);
                for (int i = 0; i < Vector256<float>.Count; i++)
                {
                    int o = (x + i) * 4;
                    output[o] = lanes[i];
                    output[o + 1] = lanes[8 + i];
                    output[o + 2] = lanes[16 + i];
                    output[o + 3] = lanes[24 + i];
                }
            }
        }

        for (; x < width; x++)
        {
            int c = center + r + x;
            float cr = ring[c], cg = ring[c + padded], cb = ring[c + (2 * padded)];
            float sr = 0, sg = 0, sb = 0, sa = 0, sw = 0;
            foreach ((int dx, int dy, float weight) in _taps)
            {
                int at = rowAt[dy + r] + x + dx;
                float nr = ring[at], ng = ring[at + padded], nb = ring[at + (2 * padded)], na = ring[at + (3 * padded)];
                float dr = nr - cr, dg = ng - cg, db = nb - cb;
                float w = FastExp.Exp(((dr * dr) + (dg * dg) + (db * db)) * _rangeScale) * weight;
                sr += nr * w;
                sg += ng * w;
                sb += nb * w;
                sa += na * w;
                sw += w;
            }

            float inverse = 1 / sw;
            output[x * 4] = sr * inverse;
            output[(x * 4) + 1] = sg * inverse;
            output[(x * 4) + 2] = sb * inverse;
            output[(x * 4) + 3] = sa * inverse;
        }
    }
}
