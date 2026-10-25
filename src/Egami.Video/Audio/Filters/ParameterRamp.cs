using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Video.Audio.Filters;

/// <summary>
/// A linear ramp of a gain-like parameter: a change made between frames moves to its new value over 5 ms instead of in one
/// step, which would click ("zipper noise").
/// </summary>
internal struct ParameterRamp
{
    private double _step;
    private int _left;

    public ParameterRamp(double value)
    {
        Value = value;
        Target = value;
    }

    /// <summary>Gets the value the next sample gets.</summary>
    public double Value { get; private set; }

    /// <summary>Gets the value the ramp moves to.</summary>
    public double Target { get; private set; }

    /// <summary>Gets whether the ramp has reached its target.</summary>
    public readonly bool IsSteady => _left == 0;

    /// <summary>Returns the ramp length for a sample rate: 5 ms, at least one sample.</summary>
    public static int Length(int sampleRate) => Math.Max(1, (int)Math.Round(sampleRate * 0.005));

    /// <summary>Moves to a new value over <paramref name="length"/> samples, or at once when <paramref name="length"/> is 0.</summary>
    public void Set(double target, int length)
    {
        Target = target;
        if (length <= 0 || target == Value)
        {
            Value = target;
            _left = 0;
            return;
        }

        _left = length;
        _step = (target - Value) / length;
    }

    /// <summary>Returns the value for one sample and steps on.</summary>
    public double Next()
    {
        if (_left == 0)
        {
            return Value;
        }

        double value = Value;
        _left--;
        Value = _left == 0 ? Target : Value + _step;
        return value;
    }

    /// <summary>Fills gains for the next samples.</summary>
    public void Fill(Span<float> gains)
    {
        for (int i = 0; i < gains.Length; i++)
        {
            gains[i] = (float)Next();
        }
    }
}

/// <summary>The sample loops filters share.</summary>
internal static class SampleMath
{
    /// <summary>Multiplies samples by a constant in place.</summary>
    public static void Scale(Span<float> samples, float gain)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            Span<Vector<float>> vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
            Vector<float> g = new(gain);
            for (int v = 0; v < vectors.Length; v++)
            {
                vectors[v] *= g;
            }

            i = vectors.Length * Vector<float>.Count;
        }

        for (; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }

    /// <summary>Multiplies samples by per-sample gains in place.</summary>
    public static void Multiply(Span<float> samples, ReadOnlySpan<float> gains)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            Span<Vector<float>> vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
            ReadOnlySpan<Vector<float>> g = MemoryMarshal.Cast<float, Vector<float>>(gains[..samples.Length]);
            for (int v = 0; v < vectors.Length; v++)
            {
                vectors[v] *= g[v];
            }

            i = vectors.Length * Vector<float>.Count;
        }

        for (; i < samples.Length; i++)
        {
            samples[i] *= gains[i];
        }
    }

    /// <summary>Adds samples times a constant: <c>destination += source * gain</c>.</summary>
    public static void AddScaled(Span<float> destination, ReadOnlySpan<float> source, float gain)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && destination.Length >= Vector<float>.Count)
        {
            Span<Vector<float>> d = MemoryMarshal.Cast<float, Vector<float>>(destination);
            ReadOnlySpan<Vector<float>> s = MemoryMarshal.Cast<float, Vector<float>>(source[..destination.Length]);
            Vector<float> g = new(gain);
            for (int v = 0; v < d.Length; v++)
            {
                d[v] += s[v] * g;
            }

            i = d.Length * Vector<float>.Count;
        }

        for (; i < destination.Length; i++)
        {
            destination[i] += source[i] * gain;
        }
    }

    /// <summary>Adds samples times per-sample gains: <c>destination += source * gains</c>.</summary>
    public static void AddMultiplied(Span<float> destination, ReadOnlySpan<float> source, ReadOnlySpan<float> gains)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] += source[i] * gains[i];
        }
    }

    /// <summary>Converts decibels to a linear factor; negative infinity gives 0.</summary>
    public static double FromDecibels(double decibels) => double.IsNegativeInfinity(decibels) ? 0 : Math.Pow(10, decibels / 20);

    /// <summary>Converts a linear factor to decibels; 0 gives negative infinity.</summary>
    public static double ToDecibels(double factor) => 20 * Math.Log10(Math.Abs(factor));
}
