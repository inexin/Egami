using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Operations.Filters;

/// <summary>
/// The fast Gaussian blur: three extended box passes (Gwosdek, Grewenig, Bruhn and Weickert, 2011) per axis. A box of radius r
/// gets a fractional weight α on the two taps just outside it, and r and α are chosen so the three passes' variance is exactly σ²;
/// each pass is a running sum, so the cost per pixel does not grow with σ.
/// </summary>
internal sealed class BoxCascade
{
    /// <summary>The passes per axis.</summary>
    internal const int Passes = 3;

    private readonly int[] _radii;
    private readonly float[] _alphas;
    private readonly float[] _norms;

    private BoxCascade(int[] radii, float[] alphas, float[] norms)
    {
        _radii = radii;
        _alphas = alphas;
        _norms = norms;
        Reach = radii.Sum() + radii.Length;
    }

    /// <summary>Gets how far the passes together read past a pixel: each reads r + 1 on either side.</summary>
    internal int Reach { get; }

    /// <summary>Gets the box radius of each pass.</summary>
    internal ReadOnlySpan<int> Radii => _radii;

    /// <summary>Gets the weight of each pass's two outer taps, relative to the inner ones.</summary>
    internal ReadOnlySpan<float> Alphas => _alphas;

    /// <summary>Returns the passes for a Gaussian of <paramref name="sigma"/>, which must be above 0.</summary>
    internal static BoxCascade For(double sigma)
    {
        // One pass of radius r with outer weight α has variance (r(r+1)(2r+1)/3 + 2α(r+1)²) / (2r+1+2α); r is the largest box
        // whose plain variance r(r+1)/3 fits under σ² / passes, and α makes up the rest exactly.
        double v = sigma * sigma / Passes;
        int r = (int)Math.Floor(Math.Sqrt((3 * v) + 0.25) - 0.5);
        double alpha = (2 * r + 1) * ((r * (r + 1.0)) - (3 * v)) / (6 * (v - ((r + 1.0) * (r + 1.0))));
        int[] radii = new int[Passes];
        float[] alphas = new float[Passes];
        float[] norms = new float[Passes];
        for (int i = 0; i < Passes; i++)
        {
            radii[i] = r;
            alphas[i] = (float)alpha;
            norms[i] = (float)(1 / ((2 * r) + 1 + (2 * alpha)));
        }

        return new BoxCascade(radii, alphas, norms);
    }

    /// <summary>The variance of the passes together, from the radii and weights as stored.</summary>
    internal double Variance()
    {
        double total = 0;
        for (int i = 0; i < Passes; i++)
        {
            double r = _radii[i], a = _alphas[i];
            total += ((r * (r + 1) * ((2 * r) + 1) / 3) + (2 * a * (r + 1) * (r + 1))) / ((2 * r) + 1 + (2 * a));
        }

        return total;
    }

    /// <summary>
    /// Runs the horizontal passes in place on a row of <paramref name="width"/> pixels of <paramref name="channels"/> floats
    /// (1 or 4), padded by <see cref="Reach"/> pixels on each side; the result is the first <paramref name="width"/> pixels.
    /// </summary>
    internal void Horizontal(Span<float> row, int width, int channels)
    {
        int length = width + (2 * Reach);
        for (int p = 0; p < Passes; p++)
        {
            int r = _radii[p];
            length -= 2 * (r + 1);
            if (channels == 4)
            {
                Pass4(MemoryMarshal.Cast<float, Vector4>(row), length, r, _alphas[p], _norms[p]);
            }
            else
            {
                Pass1(row, length, r, _alphas[p], _norms[p]);
            }
        }
    }

    /// <summary>
    /// <see cref="Horizontal(Span{float}, int, int)"/> on two rows of four-float pixels at once: each running sum waits on the add
    /// before it, so two independent sums keep the core busy.
    /// </summary>
    internal void Horizontal(Span<float> a, Span<float> b, int width)
    {
        int length = width + (2 * Reach);
        for (int p = 0; p < Passes; p++)
        {
            int r = _radii[p];
            length -= 2 * (r + 1);
            Pass4(MemoryMarshal.Cast<float, Vector4>(a), MemoryMarshal.Cast<float, Vector4>(b), length, r, _alphas[p], _norms[p]);
        }
    }

    private static void Pass4(Span<Vector4> a, Span<Vector4> b, int count, int r, float alpha, float norm)
    {
        int last = (2 * r) + 2;
        if (a.Length < count + last || b.Length < count + last)
        {
            throw new ArgumentException("A row is too short.");
        }

        Vector4 innerA = Vector4.Zero, innerB = Vector4.Zero;
        for (int j = 1; j < last; j++)
        {
            innerA += a[j];
            innerB += b[j];
        }

        ref Vector4 ra = ref MemoryMarshal.GetReference(a);
        ref Vector4 rb = ref MemoryMarshal.GetReference(b);
        for (int i = 0; i < count; i++)
        {
            ref Vector4 pa = ref Unsafe.Add(ref ra, i);
            ref Vector4 pb = ref Unsafe.Add(ref rb, i);
            Vector4 farA = Unsafe.Add(ref pa, last), farB = Unsafe.Add(ref pb, last);
            Vector4 nextA = Unsafe.Add(ref pa, 1), nextB = Unsafe.Add(ref pb, 1);
            pa = (innerA + ((pa + farA) * alpha)) * norm;
            pb = (innerB + ((pb + farB) * alpha)) * norm;
            innerA += farA - nextA;
            innerB += farB - nextB;
        }
    }

    // out[i] = norm × (Σ in[i + 1 .. i + 2r + 1] + α (in[i] + in[i + 2r + 2])), written over in[i], which is no longer needed.
    private static void Pass4(Span<Vector4> row, int count, int r, float alpha, float norm)
    {
        int last = (2 * r) + 2;
        Vector4 inner = Vector4.Zero;
        for (int j = 1; j < last; j++)
        {
            inner += row[j];
        }

        for (int i = 0; i < count; i++)
        {
            Vector4 outer = row[i] + row[i + last];
            Vector4 next = row[i + 1];
            row[i] = (inner + (outer * alpha)) * norm;
            inner += row[i + last] - next;
        }
    }

    private static void Pass1(Span<float> row, int count, int r, float alpha, float norm)
    {
        int last = (2 * r) + 2;
        float inner = 0;
        for (int j = 1; j < last; j++)
        {
            inner += row[j];
        }

        for (int i = 0; i < count; i++)
        {
            float outer = row[i] + row[i + last];
            float next = row[i + 1];
            row[i] = (inner + (outer * alpha)) * norm;
            inner += row[i + last] - next;
        }
    }

    /// <summary>
    /// The vertical passes over a run of rows: rows come from <see cref="Load"/>, already filtered horizontally, by virtual index
    /// (rows past the edges as the subclass's edge rule gives them), and each output row goes to <see cref="Store"/>. The passes
    /// are chained through rings that hold just the rows each one reads, and pull rows from the one before as they need them.
    /// One instance serves one thread; <see cref="Run"/> may be called again for the next strip of columns.
    /// </summary>
    internal abstract class Vertical : IDisposable
    {
        private readonly BoxCascade _cascade;
        private readonly int _capacity;
        private readonly float[][] _rings;
        private readonly int[] _ringRows;
        private readonly int[] _next;
        private readonly float[][] _sums;
        private readonly float[] _output;
        private readonly int[] _made = new int[Passes];
        private readonly float[] _zeros;
        private int _length;

        /// <summary>Rents the rings for rows of up to <paramref name="capacity"/> floats.</summary>
        /// <param name="cascade">The vertical passes.</param>
        /// <param name="capacity">The most floats in a row.</param>
        protected Vertical(BoxCascade cascade, int capacity)
        {
            _cascade = cascade;
            _capacity = capacity;
            _rings = new float[Passes][];
            _ringRows = new int[Passes];
            _next = new int[Passes];
            _sums = new float[Passes][];
            for (int p = 0; p < Passes; p++)
            {
                // A power of two, so a row's slot is a mask; pass p reads 2r + 3 rows at a time.
                _ringRows[p] = (int)BitOperations.RoundUpToPowerOf2((uint)((2 * cascade._radii[p]) + 3));
                _rings[p] = ArrayPool<float>.Shared.Rent(_ringRows[p] * capacity);
                _sums[p] = ArrayPool<float>.Shared.Rent(capacity);
            }

            _output = ArrayPool<float>.Shared.Rent(capacity);
            _zeros = ArrayPool<float>.Shared.Rent(capacity);
            _zeros.AsSpan(0, capacity).Clear();
        }

        /// <summary>Produces output rows <paramref name="start"/> to <paramref name="end"/> (exclusive) of <paramref name="length"/> floats.</summary>
        internal void Run(int start, int end, int length)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _capacity);

            _length = length;

            // Level p holds the rows pass p reads; it must start r + 1 rows above the first row pass p makes.
            int first = start;
            for (int p = Passes - 1; p >= 0; p--)
            {
                first -= _cascade._radii[p] + 1;
                _next[p] = first;
            }

            // The first row each pass makes, where its running sum starts.
            for (int p = 0; p < Passes; p++)
            {
                _made[p] = _next[p] + _cascade._radii[p] + 1;
            }

            Span<float> output = _output.AsSpan(0, length);
            for (int y = start; y < end; y++)
            {
                Make(Passes - 1, y, output);
                Store(y, output);
            }
        }

        /// <summary>Fills <paramref name="target"/> with the horizontally filtered row at virtual index <paramref name="v"/>.</summary>
        protected abstract void Load(int v, Span<float> target);

        /// <summary>Takes output row <paramref name="y"/>.</summary>
        protected abstract void Store(int y, Span<float> row);

        /// <summary>Makes row <paramref name="y"/> of pass <paramref name="p"/>'s output into <paramref name="target"/>.</summary>
        private void Make(int p, int y, Span<float> target)
        {
            int r = _cascade._radii[p];

            // Bring level p up to row y + r + 1: from the source for the first pass, else from the pass before.
            while (_next[p] <= y + r + 1)
            {
                Span<float> slot = Row(p, _next[p]);
                if (p == 0)
                {
                    Load(_next[p], slot);
                }
                else
                {
                    Make(p - 1, _next[p], slot);
                }

                _next[p]++;
            }

            Span<float> sum = _sums[p].AsSpan(0, _length);
            if (y == _made[p])
            {
                sum.Clear();
                for (int v = y - r; v < y + r; v++)
                {
                    Add(Row(p, v), sum);
                }

                // The last row of the window is added by the step, with nothing taken away.
                _made[p] = int.MinValue;
                Step(sum, Row(p, y + r), _zeros.AsSpan(0, _length), Row(p, y - r - 1), Row(p, y + r + 1), _cascade._alphas[p], _cascade._norms[p], target);
            }
            else
            {
                Step(sum, Row(p, y + r), Row(p, y - r - 1), Row(p, y - r - 1), Row(p, y + r + 1), _cascade._alphas[p], _cascade._norms[p], target);
            }
        }

        private Span<float> Row(int p, int v)
        {
            return _rings[p].AsSpan((v & (_ringRows[p] - 1)) * _length, _length);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>Returns the rented rows.</summary>
        protected virtual void Dispose(bool disposing)
        {
            for (int p = 0; p < Passes; p++)
            {
                ArrayPool<float>.Shared.Return(_rings[p]);
                ArrayPool<float>.Shared.Return(_sums[p]);
            }

            ArrayPool<float>.Shared.Return(_output);
            ArrayPool<float>.Shared.Return(_zeros);
        }

        private static void Add(ReadOnlySpan<float> row, Span<float> sum)
        {
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                for (; i <= sum.Length - Vector256<float>.Count; i += Vector256<float>.Count)
                {
                    (Vector256.Create(sum[i..]) + Vector256.Create(row[i..])).CopyTo(sum[i..]);
                }
            }

            for (; i < sum.Length; i++)
            {
                sum[i] += row[i];
            }
        }

        /// <summary>
        /// Slides the window sum down a row (adds <paramref name="added"/>, takes away <paramref name="removed"/>) and writes
        /// norm × (sum + α (above + below)) to <paramref name="target"/>, in one pass over the row.
        /// </summary>
        private static void Step(Span<float> sum, ReadOnlySpan<float> added, ReadOnlySpan<float> removed, ReadOnlySpan<float> above, ReadOnlySpan<float> below, float alpha, float norm, Span<float> target)
        {
            int length = sum.Length;
            if (added.Length < length || removed.Length < length || above.Length < length || below.Length < length || target.Length < length)
            {
                throw new ArgumentException("A row is too short.");
            }

            ref float s = ref MemoryMarshal.GetReference(sum);
            ref float a = ref MemoryMarshal.GetReference(added);
            ref float d = ref MemoryMarshal.GetReference(removed);
            ref float u = ref MemoryMarshal.GetReference(above);
            ref float b = ref MemoryMarshal.GetReference(below);
            ref float t = ref MemoryMarshal.GetReference(target);
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<float> va = Vector256.Create(alpha), vn = Vector256.Create(norm);
                for (; i <= length - Vector256<float>.Count; i += Vector256<float>.Count)
                {
                    nuint at = (nuint)i;
                    Vector256<float> next = Vector256.LoadUnsafe(ref s, at) + (Vector256.LoadUnsafe(ref a, at) - Vector256.LoadUnsafe(ref d, at));
                    next.StoreUnsafe(ref s, at);
                    ((next + ((Vector256.LoadUnsafe(ref u, at) + Vector256.LoadUnsafe(ref b, at)) * va)) * vn).StoreUnsafe(ref t, at);
                }
            }

            for (; i < length; i++)
            {
                float next = sum[i] + (added[i] - removed[i]);
                sum[i] = next;
                target[i] = (next + ((above[i] + below[i]) * alpha)) * norm;
            }
        }
    }
}
