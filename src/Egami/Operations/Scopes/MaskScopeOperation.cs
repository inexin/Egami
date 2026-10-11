using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations.Compose;
using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Scopes;

/// <summary>
/// Within(mask): runs a sub-chain, then mixes each changed pixel with the original by the mask's coverage on premultiplied values:
/// out = original + m·(result − original), so 0 keeps the original and full keeps the result exactly. Only the bounding box of
/// non-zero coverage is touched. A sub-chain of one per-pixel step mixes band by band as it runs, with no area-sized copy;
/// others keep the original area in a scratch plane counted against MaxTotalBytes. 8-bit targets mix with 8-bit coverage, the
/// others with float coverage.
/// </summary>
internal sealed class MaskScopeOperation : IRasterOperation, IAreaStep, IScope, Tone.IMeasuredScope
{
    private readonly Mask _mask;
    private readonly IReadOnlyList<IRasterOperation> _steps;
    private readonly MaskCoverage? _coverage;

    internal MaskScopeOperation(Mask mask, IReadOnlyList<IRasterOperation> steps, MaskCoverage? coverage = null)
    {
        _mask = mask;
        _steps = steps;
        _coverage = coverage;
    }

    public Size OutputSize(Size input)
    {
        return input;
    }

    public IRasterOperation Bind(Func<Colors.LinearCurve?> curve)
    {
        MaskScopeOperation bound = new(_mask, ScopeRunner.Bind(_steps, curve), new MaskCoverage(_mask));
        return Tone.MeasuredScopeOperation.Holds(bound._steps) ? new Tone.MeasuredScopeOperation(bound) : bound;
    }

    public IRasterOperation Resolve<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, Tone.MeasureScope outer, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        MaskCoverage coverage = _coverage ?? new MaskCoverage(_mask);
        Tone.MeasureScope inner = outer.WithMask(coverage);
        return new MaskScopeOperation(_mask, Tone.MeasuredScopeOperation.ResolveSteps(_steps, frames, inner.Area, inner, context, cancellationToken), coverage);
    }

    public void Validate(Size size)
    {
        Rect bounds = (_coverage ?? new MaskCoverage(_mask)).Bounds;
        ScopeRunner.Validate(_steps, size, bounds.Intersect(new Rect(0, 0, size.Width, size.Height)));
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
        MaskCoverage coverage = _coverage ?? new MaskCoverage(_mask);
        area = coverage.Bounds.Intersect(area);
        if (area.IsEmpty || _steps.Count == 0)
        {
            return;
        }

        // Masking a raster with itself reads coverage from a copy, so the changes being mixed do not change the mask.
        Raster? snapshot = coverage.Source.HasPlane(input.Destination) ? coverage.Source.Derive(_ => { }) : null;
        try
        {
            MaskCoverage reading = snapshot is null ? coverage : coverage.From(snapshot);
            if (_steps.Count == 1 && _steps[0] is IRunsOnViews and not FlipOperation)
            {
                Fused(input, area, reading);
            }
            else
            {
                Scratch(input, area, reading);
            }
        }
        finally
        {
            snapshot?.Dispose();
        }
    }

    /// <summary>One per-pixel step: each band keeps its original rows in pooled memory, runs the step on its view, and mixes at once.</summary>
    private void Fused<TPixel>(OperationInput<TPixel> input, Rect area, MaskCoverage coverage)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        IRasterOperation step = _steps[0];
        EgamiContext single = input.Context with { MaxParallelism = 1 };
        RowRunner.Run(area.Height, (long)area.Width * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>(), input.Context.MaxParallelism, (start, end) =>
        {
            int rows = end - start;
            TPixel[] original = ArrayPool<TPixel>.Shared.Rent(rows * area.Width);
            try
            {
                for (int r = 0; r < rows; r++)
                {
                    input.Destination.Row(area.Y + start + r).Slice(area.X, area.Width).CopyTo(original.AsSpan(r * area.Width, area.Width));
                }

                PixelPlane<TPixel> view = new(input.Destination, new Rect(area.X, area.Y + start, area.Width, rows));
                step.Run(new OperationInput<TPixel>(view, view, single, input.FrameIndex, input.CancellationToken) { Memory = input.Memory });
                Mixer<TPixel> mixer = new(coverage, area.Width);
                try
                {
                    for (int r = 0; r < rows; r++)
                    {
                        int y = area.Y + start + r;
                        mixer.Mix(y, area.X, original.AsSpan(r * area.Width, area.Width), input.Destination.Row(y).Slice(area.X, area.Width));
                    }
                }
                finally
                {
                    mixer.Dispose();
                }
            }
            finally
            {
                ArrayPool<TPixel>.Shared.Return(original);
            }
        }, input.CancellationToken);
    }

    /// <summary>Any sub-chain: the original area is kept in a scratch plane, the chain runs on the image, and the result is mixed back.</summary>
    private void Scratch<TPixel>(OperationInput<TPixel> input, Rect area, MaskCoverage coverage)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> original = ScopeRunner.Scratch(input, area.Width, area.Height);
        try
        {
            ScopeRunner.Copy(input.Destination, area, original, new Point(0, 0));
            ScopeRunner.Run(_steps, input, area);
            RowRunner.Run(area.Height, (long)area.Width * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>(), input.Context.MaxParallelism, (start, end) =>
            {
                Mixer<TPixel> mixer = new(coverage, area.Width);
                try
                {
                    for (int r = start; r < end; r++)
                    {
                        mixer.Mix(area.Y + r, area.X, original.Row(r), input.Destination.Row(area.Y + r).Slice(area.X, area.Width));
                    }
                }
                finally
                {
                    mixer.Dispose();
                }
            }, input.CancellationToken);
        }
        finally
        {
            original.Dispose();
        }
    }

    /// <summary>
    /// Mixes a result row into its original by coverage, writing the result row. Pixels with coverage 0 or full are copied exactly;
    /// rows that are all 0 or all full are copied whole.
    /// </summary>
    private ref struct Mixer<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private static readonly bool Bytes = RowConverter<TPixel>.Premul8 || RowConverter<TPixel>.FourByte;
        private static readonly RowConverter<TPixel> Converter = new(null, inksAsRgb: true);

        private readonly MaskCoverage _coverage;
        private readonly byte[]? _bytes;
        private readonly float[]? _floats;

        internal Mixer(MaskCoverage coverage, int width)
        {
            _coverage = coverage;
            _bytes = Bytes ? ArrayPool<byte>.Shared.Rent(width) : null;
            _floats = Bytes ? null : ArrayPool<float>.Shared.Rent(9 * width);
        }

        internal readonly void Mix(int y, int x, Span<TPixel> original, Span<TPixel> result)
        {
            int n = result.Length;
            if (_bytes is not null)
            {
                Span<byte> m = _bytes.AsSpan(0, n);
                _coverage.Row8(y, x, m);
                int partial = m.IndexOfAnyExcept((byte)0, (byte)255);
                if (partial < 0)
                {
                    CopyWhere(m, original, result);
                    return;
                }

                if (Opaque(original) && Opaque(result))
                {
                    // Opaque pixels are their own premultiplied form, and the mix of two opaque pixels is opaque: one exact pass in
                    // the 8-bit kernel into the original's scratch, where coverage 0 and full give the original and the result exactly.
                    BlendKernels.Blend8(BlendMode.Source, MemoryMarshal.Cast<TPixel, uint>(result), MemoryMarshal.Cast<TPixel, uint>(original), m, 255, bgr: false);
                    original.CopyTo(result);
                    return;
                }

                // Source with coverage gives round((o·m' + r·(255 − m')) / 255) per premultiplied channel; with m' = 255 − m that is
                // the original mixed toward the result by m. It runs on a copy, so pixels at 0 or full keep their exact values.
                byte[] flipped = ArrayPool<byte>.Shared.Rent(n);
                TPixel[] copy = ArrayPool<TPixel>.Shared.Rent(n);
                try
                {
                    Span<byte> keep = flipped.AsSpan(0, n);
                    for (int i = 0; i < n; i++)
                    {
                        keep[i] = (byte)(255 - m[i]);
                    }

                    Span<TPixel> mixed = copy.AsSpan(0, n);
                    result.CopyTo(mixed);
                    new BlendRows<TPixel>(BlendMode.Source, 1, null).Blend(original, mixed, keep);
                    for (int i = 0; i < n; i++)
                    {
                        if (m[i] == 0)
                        {
                            result[i] = original[i];
                        }
                        else if (m[i] != 255)
                        {
                            result[i] = mixed[i];
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(flipped);
                    ArrayPool<TPixel>.Shared.Return(copy);
                }

                return;
            }

            Span<float> coverage = _floats.AsSpan(0, n);
            _coverage.RowFloat(y, x, coverage);
            if (coverage.IndexOfAnyExcept(0f) < 0)
            {
                original.CopyTo(result);
                return;
            }

            if (coverage.IndexOfAnyExcept(1f) < 0)
            {
                return;
            }

            Span<float> o = _floats.AsSpan(n, 4 * n), r = _floats.AsSpan(5 * n, 4 * n);
            Converter.Load(original, o);
            Converter.Load(result, r);
            for (int i = 0; i < n; i++)
            {
                float k = coverage[i];
                int p = i * 4;
                r[p] = o[p] + (k * (r[p] - o[p]));
                r[p + 1] = o[p + 1] + (k * (r[p + 1] - o[p + 1]));
                r[p + 2] = o[p + 2] + (k * (r[p + 2] - o[p + 2]));
                r[p + 3] = o[p + 3] + (k * (r[p + 3] - o[p + 3]));
            }

            TPixel[] stored = ArrayPool<TPixel>.Shared.Rent(n);
            try
            {
                Span<TPixel> row = stored.AsSpan(0, n);
                Converter.Store(r, row, opaque: false);
                for (int i = 0; i < n; i++)
                {
                    if (coverage[i] <= 0f)
                    {
                        result[i] = original[i];
                    }
                    else if (coverage[i] < 1f)
                    {
                        result[i] = row[i];
                    }
                }
            }
            finally
            {
                ArrayPool<TPixel>.Shared.Return(stored);
            }
        }

        public readonly void Dispose()
        {
            if (_bytes is not null)
            {
                ArrayPool<byte>.Shared.Return(_bytes);
            }

            if (_floats is not null)
            {
                ArrayPool<float>.Shared.Return(_floats);
            }
        }

        private static bool Opaque(ReadOnlySpan<TPixel> row)
        {
            ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<TPixel, uint>(row);
            int i = 0;
            if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
            {
                System.Runtime.Intrinsics.Vector256<uint> all = System.Runtime.Intrinsics.Vector256.Create(uint.MaxValue);
                for (; i + 8 <= pixels.Length; i += 8)
                {
                    all &= System.Runtime.Intrinsics.Vector256.Create(pixels.Slice(i, 8));
                }

                if (!System.Runtime.Intrinsics.Vector256.GreaterThanOrEqualAll(all, System.Runtime.Intrinsics.Vector256.Create(0xFF000000u)))
                {
                    return false;
                }
            }

            for (; i < pixels.Length; i++)
            {
                if (pixels[i] < 0xFF000000u)
                {
                    return false;
                }
            }

            return true;
        }

        private static void CopyWhere(ReadOnlySpan<byte> m, ReadOnlySpan<TPixel> original, Span<TPixel> result)
        {
            if (m.IndexOfAnyExcept((byte)0) < 0)
            {
                original.CopyTo(result);
                return;
            }

            for (int i = 0; i < m.Length; i++)
            {
                if (m[i] == 0)
                {
                    result[i] = original[i];
                }
            }
        }
    }
}
