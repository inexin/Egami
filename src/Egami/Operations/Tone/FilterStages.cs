using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Operations.Color;

namespace Egami.Operations.Tone;

/// <summary>
/// The tables one run of a filter builds, keyed by what they tabulate, rented from the pool and returned when the run ends, so a
/// filter step allocates nothing per call.
/// </summary>
internal sealed class FilterContext : IDisposable
{
    private readonly Dictionary<object, float[]> _tables = new(ReferenceEqualityComparer.Instance);

    /// <summary>Returns <paramref name="curve"/>'s 4,096-entry table plus a guard entry, building it the first time.</summary>
    internal float[] Table(ToneCurve curve)
    {
        lock (_tables)
        {
            if (!_tables.TryGetValue(curve, out float[]? table))
            {
                table = ArrayPool<float>.Shared.Rent(CurveStage.TableSize + 1);
                for (int i = 0; i < CurveStage.TableSize; i++)
                {
                    table[i] = (float)curve.Map(i / (double)(CurveStage.TableSize - 1));
                }

                table[CurveStage.TableSize] = table[CurveStage.TableSize - 1];
                _tables.Add(curve, table);
            }

            return table;
        }
    }

    public void Dispose()
    {
        lock (_tables)
        {
            foreach (float[] table in _tables.Values)
            {
                ArrayPool<float>.Shared.Return(table);
            }

            _tables.Clear();
        }
    }
}

/// <summary>
/// One stage of a <see cref="ColorFilter"/>: a function of one pixel's straight RGBA in 0..1, whose result is clamped to 0..1 as
/// running the step alone would clamp it.
/// </summary>
internal abstract class FilterStage
{
    /// <summary>Gets whether each output channel depends only on the same input channel, so the stage is four curves.</summary>
    internal virtual bool IsPerChannel => false;

    /// <summary>Gets whether the stage can change alpha.</summary>
    internal virtual bool ChangesAlpha => false;

    /// <summary>Maps one channel's value in double precision; only for per-channel stages.</summary>
    internal virtual double MapChannel(int channel, double value) => throw new InvalidOperationException("Not a per-channel stage.");

    /// <summary>Runs the stage on straight RGBA pixels in place.</summary>
    internal abstract void Apply(Span<Vector4> pixels, FilterContext context);

    /// <summary>Runs <paramref name="stages"/> one after another on the pixels in place.</summary>
    internal static void Run(ReadOnlySpan<FilterStage> stages, Span<Vector4> pixels, FilterContext context)
    {
        foreach (FilterStage stage in stages)
        {
            stage.Apply(pixels, context);
        }
    }

    /// <summary>Maps one channel through per-channel stages in double precision.</summary>
    internal static double MapChannel(ReadOnlySpan<FilterStage> stages, int channel, double value)
    {
        foreach (FilterStage stage in stages)
        {
            value = stage.MapChannel(channel, value);
        }

        return value;
    }

    /// <summary>Gets whether every stage is per-channel.</summary>
    internal static bool AllPerChannel(ReadOnlySpan<FilterStage> stages)
    {
        foreach (FilterStage stage in stages)
        {
            if (!stage.IsPerChannel)
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Clamp01(float value) => value > 0f ? (value < 1f ? value : 1f) : 0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector4 Clamp01(Vector4 value) => Vector4.Min(Vector4.Max(value, Vector4.Zero), Vector4.One);
}

/// <summary>A 4 × 5 color matrix, as <see cref="ColorMatrixOperation"/> applies it.</summary>
internal sealed class MatrixStage(ColorMatrix matrix) : FilterStage
{
    private readonly Vector4[] _columns = ColorKernels.MemoryOrderColumns(matrix, [0, 1, 2, 3]);

    internal ColorMatrix Matrix => matrix;

    internal override bool IsPerChannel => matrix.IsPerChannel;

    internal override bool ChangesAlpha
    {
        get
        {
            for (int c = 0; c < 5; c++)
            {
                if (matrix[3, c] != (c == 3 ? 1 : 0))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal override double MapChannel(int channel, double value)
    {
        double v = (value * matrix[channel, channel]) + matrix[channel, 4];
        return v < 0 ? 0 : v > 1 ? 1 : v;
    }

    internal override void Apply(Span<Vector4> pixels, FilterContext context)
    {
        Vector4 c0 = _columns[0], c1 = _columns[1], c2 = _columns[2], c3 = _columns[3], c4 = _columns[4];
        foreach (ref Vector4 v in pixels)
        {
            v = Clamp01((c0 * v.X) + (c1 * v.Y) + (c2 * v.Z) + (c3 * v.W) + c4);
        }
    }
}

/// <summary>
/// One curve per channel. Exact in double precision through <see cref="MapChannel"/>; on floats, each curve runs as a 4,096-entry
/// table read with linear interpolation.
/// </summary>
internal sealed class CurveStage : FilterStage
{
    internal const int TableSize = 4096;

    private readonly ToneCurve[] _curves;
    private readonly bool _stepped;

    internal CurveStage(ToneCurve red, ToneCurve green, ToneCurve blue, ToneCurve alpha)
    {
        _curves = [red, green, blue, alpha];
        _stepped = !red.IsSmooth || !green.IsSmooth || !blue.IsSmooth || !alpha.IsSmooth;
    }

    internal ReadOnlySpan<ToneCurve> Curves => _curves;

    internal override bool IsPerChannel => true;

    internal override bool ChangesAlpha => !_curves[3].IsIdentity;

    /// <summary>Gets whether red, green and blue share one curve.</summary>
    internal bool SameColorCurves => ReferenceEquals(_curves[0], _curves[1]) && ReferenceEquals(_curves[1], _curves[2]);

    internal override double MapChannel(int channel, double value) => _curves[channel].Map(value);

    /// <summary>Returns this stage followed by <paramref name="next"/> as one stage.</summary>
    internal CurveStage Then(CurveStage next)
    {
        ToneCurve r = _curves[0].Then(next._curves[0]);
        ToneCurve g = SameColorCurves && next.SameColorCurves ? r : _curves[1].Then(next._curves[1]);
        ToneCurve b = SameColorCurves && next.SameColorCurves ? r : _curves[2].Then(next._curves[2]);
        return new CurveStage(r, g, b, _curves[3].Then(next._curves[3]));
    }

    internal override void Apply(Span<Vector4> pixels, FilterContext context)
    {
        if (_stepped)
        {
            // Stepped curves are read exactly: a table would blur their jumps.
            foreach (ref Vector4 v in pixels)
            {
                v = new Vector4((float)_curves[0].Map(v.X), (float)_curves[1].Map(v.Y), (float)_curves[2].Map(v.Z), (float)_curves[3].Map(v.W));
            }

            return;
        }

        float[]? r = Table(context, 0), g = Table(context, 1), b = Table(context, 2), a = Table(context, 3);
        foreach (ref Vector4 v in pixels)
        {
            v = new Vector4(Read(r, v.X), Read(g, v.Y), Read(b, v.Z), Read(a, v.W));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Read(float[]? table, float x)
    {
        if (table is null)
        {
            return x;
        }

        float position = Clamp01(x) * (TableSize - 1);
        int i = (int)position;
        float f = position - i;
        // The guard entry repeats the last value, so i + 1 is always in range.
        return table[i] + ((table[i + 1] - table[i]) * f);
    }

    /// <summary>The channel's 4,096-entry table plus a guard entry, from the run's pool; null where the channel passes through.</summary>
    private float[]? Table(FilterContext context, int channel)
    {
        ToneCurve curve = _curves[channel];
        return curve.IsIdentity ? null : context.Table(curve);
    }
}

/// <summary>Mixes two filters' results per pixel, as Skia's SkColorFilters::Lerp.</summary>
internal sealed class LerpStage(FilterStage[] from, FilterStage[] to, float amount) : FilterStage
{
    internal override bool IsPerChannel => AllPerChannel(from) && AllPerChannel(to);

    internal override bool ChangesAlpha => from.Any(s => s.ChangesAlpha) || to.Any(s => s.ChangesAlpha);

    internal override double MapChannel(int channel, double value)
    {
        double a = MapChannel(from, channel, value), b = MapChannel(to, channel, value);
        double v = a + ((b - a) * amount);
        return v < 0 ? 0 : v > 1 ? 1 : v;
    }

    internal override void Apply(Span<Vector4> pixels, FilterContext context)
    {
        Vector4[] buffer = ArrayPool<Vector4>.Shared.Rent(pixels.Length);
        try
        {
            Span<Vector4> other = buffer.AsSpan(0, pixels.Length);
            pixels.CopyTo(other);
            Run(from, pixels, context);
            Run(to, other, context);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Clamp01(Vector4.Lerp(pixels[i], other[i], amount));
            }
        }
        finally
        {
            ArrayPool<Vector4>.Shared.Return(buffer);
        }
    }
}

/// <summary>Keeps a filter's result only in some channels: the per-pixel form of OnChannels.</summary>
internal sealed class SelectStage(FilterStage[] inner, ColorChannels channels) : FilterStage
{
    internal override bool IsPerChannel => AllPerChannel(inner);

    internal override bool ChangesAlpha => (channels & ColorChannels.Alpha) != 0 && inner.Any(s => s.ChangesAlpha);

    internal override double MapChannel(int channel, double value)
    {
        return ((int)channels & (1 << channel)) != 0 ? MapChannel(inner, channel, value) : value;
    }

    internal override void Apply(Span<Vector4> pixels, FilterContext context)
    {
        Vector4 keep = new(
            (channels & ColorChannels.Red) != 0 ? 1 : 0,
            (channels & ColorChannels.Green) != 0 ? 1 : 0,
            (channels & ColorChannels.Blue) != 0 ? 1 : 0,
            (channels & ColorChannels.Alpha) != 0 ? 1 : 0);
        Vector4[] buffer = ArrayPool<Vector4>.Shared.Rent(pixels.Length);
        try
        {
            Span<Vector4> original = buffer.AsSpan(0, pixels.Length);
            pixels.CopyTo(original);
            Run(inner, pixels, context);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (pixels[i] * keep) + (original[i] * (Vector4.One - keep));
            }
        }
        finally
        {
            ArrayPool<Vector4>.Shared.Return(buffer);
        }
    }
}
