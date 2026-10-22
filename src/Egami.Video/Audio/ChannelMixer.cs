using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Video.Audio;

/// <summary>
/// Mixes one channel layout into another with a matrix: each output channel is a weighted sum of the input channels. The
/// default matrix follows ITU-R BS.775: a speaker both layouts have passes at full level; a missing center goes to front left
/// and right at -3 dB (0.7071); missing surrounds go to the nearest speakers left (side to back and back to side, at full level when
/// that only renames the pair and at -3 dB when it folds a second pair in, else to the front at -3 dB); a mono output takes left and right at -3 dB and surrounds at -6 dB; LFE is dropped unless asked for.
/// Upmixing only routes the speakers both layouts share, so no channel is made up. Channels with no position map by index.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-mix"/>
/// </example>
public sealed class ChannelMixer
{
    /// <summary>-3 dB: the BS.775 level for center and surrounds.</summary>
    public const float MinusThreeDecibels = 0.70710678f;

    private readonly float[] _matrix;

    /// <summary>Creates a mixer with the default matrix between two layouts.</summary>
    /// <param name="input">The layout mixed from.</param>
    /// <param name="output">The layout mixed to.</param>
    /// <param name="lfeLevel">The level LFE is mixed into the front channels at when the output has no LFE; 0 (the default) drops it.</param>
    public ChannelMixer(ChannelLayout input, ChannelLayout output, float lfeLevel = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input.ChannelCount, 1, nameof(input));
        ArgumentOutOfRangeException.ThrowIfLessThan(output.ChannelCount, 1, nameof(output));
        Input = input;
        Output = output;
        _matrix = DefaultMatrix(input, output, lfeLevel);
    }

    /// <summary>Creates a mixer with the caller's matrix.</summary>
    /// <param name="input">The layout mixed from.</param>
    /// <param name="output">The layout mixed to.</param>
    /// <param name="matrix">The weights, indexed [output channel, input channel].</param>
    /// <exception cref="ArgumentException">The matrix does not have output rows and input columns.</exception>
    public ChannelMixer(ChannelLayout input, ChannelLayout output, float[,] matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.GetLength(0) != output.ChannelCount || matrix.GetLength(1) != input.ChannelCount || input.ChannelCount == 0)
        {
            throw new ArgumentException($"The matrix must be {output.ChannelCount} by {input.ChannelCount}.", nameof(matrix));
        }

        Input = input;
        Output = output;
        _matrix = new float[output.ChannelCount * input.ChannelCount];
        for (int o = 0; o < output.ChannelCount; o++)
        {
            for (int i = 0; i < input.ChannelCount; i++)
            {
                _matrix[(o * input.ChannelCount) + i] = matrix[o, i];
            }
        }
    }

    /// <summary>Gets the layout mixed from.</summary>
    public ChannelLayout Input { get; }

    /// <summary>Gets the layout mixed to.</summary>
    public ChannelLayout Output { get; }

    /// <summary>Gets the weight of an input channel in an output channel.</summary>
    /// <param name="output">The output channel.</param>
    /// <param name="input">The input channel.</param>
    /// <returns>The weight.</returns>
    public float this[int output, int input]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)output, (uint)Output.ChannelCount, nameof(output));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)input, (uint)Input.ChannelCount, nameof(input));
            return _matrix[(output * Input.ChannelCount) + input];
        }
    }

    /// <summary>Returns a copy of the matrix, indexed [output channel, input channel].</summary>
    /// <returns>The matrix.</returns>
    public float[,] GetMatrix()
    {
        float[,] copy = new float[Output.ChannelCount, Input.ChannelCount];
        for (int o = 0; o < Output.ChannelCount; o++)
        {
            for (int i = 0; i < Input.ChannelCount; i++)
            {
                copy[o, i] = _matrix[(o * Input.ChannelCount) + i];
            }
        }

        return copy;
    }

    /// <summary>Mixes a frame into a new frame of the output layout, in the same sample format.</summary>
    /// <param name="source">A frame of the input layout.</param>
    /// <param name="context">The context to allocate from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The mixed frame. The caller must dispose it.</returns>
    /// <exception cref="ArgumentException">The frame's layout is not the input layout.</exception>
    public AudioFrame Mix(AudioFrame source, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        AudioFrame result = new(source.Format, Output, source.SampleRate, source.SampleCount, source.Timestamp, context) { ValidBits = source.ValidBits };
        try
        {
            Mix(source, result);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Mixes a frame into another frame of the output layout and the same sample count, in any sample formats.</summary>
    /// <param name="source">A frame of the input layout.</param>
    /// <param name="destination">A frame of the output layout.</param>
    /// <exception cref="ArgumentException">A layout or the sample count does not match.</exception>
    public void Mix(AudioFrame source, AudioFrame destination) => Mix(source, destination, dither: false);

    internal void Mix(AudioFrame source, AudioFrame destination, bool dither)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (source.Layout != Input || destination.Layout != Output || source.SampleCount != destination.SampleCount)
        {
            throw new ArgumentException($"The mixer goes from {Input} to {Output}, with as many samples out as in.", nameof(destination));
        }

        int inputs = Input.ChannelCount, outputs = Output.ChannelCount;
        int count = source.SampleCount;
        int chunk = SampleConverter.Chunk;
        float[] buffer = ArrayPool<float>.Shared.Rent((inputs + 1) * chunk);
        SampleConverter.Dither? noise = dither && !destination.Format.IsFloat() ? new SampleConverter.Dither() : null;
        try
        {
            for (int start = 0; start < count; start += chunk)
            {
                int n = Math.Min(chunk, count - start);
                for (int i = 0; i < inputs; i++)
                {
                    SampleConverter.ReadFloats(source, i, start, buffer.AsSpan(i * chunk, n));
                }

                Span<float> sum = buffer.AsSpan(inputs * chunk, n);
                for (int o = 0; o < outputs; o++)
                {
                    sum.Clear();
                    for (int i = 0; i < inputs; i++)
                    {
                        float w = _matrix[(o * inputs) + i];
                        if (w == 0)
                        {
                            continue;
                        }

                        MultiplyAdd(buffer.AsSpan(i * chunk, n), w, sum);
                    }

                    SampleConverter.WriteFloats(destination, o, start, sum, noise);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    /// <summary>sum += x * w, a vector at a time.</summary>
    internal static void MultiplyAdd(ReadOnlySpan<float> x, float w, Span<float> sum)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<float> weight = new(w);
            ReadOnlySpan<Vector<float>> xs = MemoryMarshal.Cast<float, Vector<float>>(x);
            Span<Vector<float>> sums = MemoryMarshal.Cast<float, Vector<float>>(sum);
            for (int v = 0; v < xs.Length; v++)
            {
                sums[v] += xs[v] * weight;
            }

            i = xs.Length * Vector<float>.Count;
        }

        for (; i < x.Length; i++)
        {
            sum[i] += x[i] * w;
        }
    }

    private static float[] DefaultMatrix(ChannelLayout input, ChannelLayout output, float lfeLevel)
    {
        int inputs = input.ChannelCount;
        float[] m = new float[output.ChannelCount * inputs];
        void Add(int o, int i, float w)
        {
            if (o >= 0)
            {
                m[(o * inputs) + i] += w;
            }
        }

        int Out(Speakers s) => output.IndexOf(s);
        bool Has(Speakers s) => output.IndexOf(s) >= 0;
        bool mono = output.IsPositioned && output.Speakers == Speakers.FrontCenter;
        int positionedIn = BitOperations.PopCount((uint)input.Speakers);
        int positionedOut = BitOperations.PopCount((uint)output.Speakers);

        for (int i = 0; i < inputs; i++)
        {
            if (i >= positionedIn)
            {
                // Unpositioned channels map by index among the unpositioned outputs.
                int o = positionedOut + (i - positionedIn);
                if (o < output.ChannelCount)
                {
                    Add(o, i, 1);
                }

                continue;
            }

            Speakers s = input.GetSpeaker(i);
            if (Has(s))
            {
                Add(Out(s), i, 1);
                continue;
            }

            if (mono)
            {
                float w = s switch
                {
                    Speakers.LowFrequency => lfeLevel,
                    Speakers.FrontLeft or Speakers.FrontRight or Speakers.FrontLeftOfCenter or Speakers.FrontRightOfCenter or Speakers.TopFrontLeft or Speakers.TopFrontRight => MinusThreeDecibels,
                    Speakers.TopCenter or Speakers.TopFrontCenter => MinusThreeDecibels,
                    _ => 0.5f,
                };
                Add(Out(Speakers.FrontCenter), i, w);
                continue;
            }

            switch (s)
            {
                case Speakers.FrontCenter:
                case Speakers.TopFrontCenter:
                case Speakers.TopCenter:
                    if (s != Speakers.FrontCenter && Has(Speakers.FrontCenter))
                    {
                        Add(Out(Speakers.FrontCenter), i, s == Speakers.TopCenter ? MinusThreeDecibels : 1);
                    }
                    else
                    {
                        Add(Out(Speakers.FrontLeft), i, MinusThreeDecibels);
                        Add(Out(Speakers.FrontRight), i, MinusThreeDecibels);
                    }

                    break;
                case Speakers.LowFrequency:
                    if (lfeLevel != 0)
                    {
                        if (Has(Speakers.FrontLeft) || Has(Speakers.FrontRight))
                        {
                            Add(Out(Speakers.FrontLeft), i, lfeLevel);
                            Add(Out(Speakers.FrontRight), i, lfeLevel);
                        }
                        else
                        {
                            Add(Out(Speakers.FrontCenter), i, lfeLevel);
                        }
                    }

                    break;
                case Speakers.FrontLeftOfCenter:
                case Speakers.FrontRightOfCenter:
                    {
                        Speakers side = s == Speakers.FrontLeftOfCenter ? Speakers.FrontLeft : Speakers.FrontRight;
                        Add(Out(side), i, Has(side) ? 1 : 0);
                        if (!Has(side))
                        {
                            Add(Out(Speakers.FrontCenter), i, MinusThreeDecibels);
                        }

                        break;
                    }

                case Speakers.BackLeft or Speakers.SideLeft or Speakers.BackRight or Speakers.SideRight:
                    {
                        bool left = s is Speakers.BackLeft or Speakers.SideLeft;
                        Speakers other = s switch
                        {
                            Speakers.BackLeft => Speakers.SideLeft,
                            Speakers.SideLeft => Speakers.BackLeft,
                            Speakers.BackRight => Speakers.SideRight,
                            _ => Speakers.BackRight,
                        };
                        if (Has(other))
                        {
                            // A pure rename (5.1 side to 5.1 back) passes at full level; folding a second pair in (7.1 to 5.1) at -3 dB.
                            Add(Out(other), i, input.IndexOf(other) >= 0 ? MinusThreeDecibels : 1);
                        }
                        else
                        {
                            Front(left, i, MinusThreeDecibels);
                        }

                        break;
                    }

                case Speakers.BackCenter:
                case Speakers.TopBackCenter:
                    if (Has(Speakers.BackLeft) && Has(Speakers.BackRight))
                    {
                        Add(Out(Speakers.BackLeft), i, MinusThreeDecibels);
                        Add(Out(Speakers.BackRight), i, MinusThreeDecibels);
                    }
                    else if (Has(Speakers.SideLeft) && Has(Speakers.SideRight))
                    {
                        Add(Out(Speakers.SideLeft), i, MinusThreeDecibels);
                        Add(Out(Speakers.SideRight), i, MinusThreeDecibels);
                    }
                    else
                    {
                        Front(true, i, 0.5f);
                        Front(false, i, 0.5f);
                    }

                    break;
                case Speakers.TopFrontLeft or Speakers.TopFrontRight:
                    Front(s == Speakers.TopFrontLeft, i, MinusThreeDecibels);
                    break;
                case Speakers.TopBackLeft or Speakers.TopBackRight:
                    {
                        bool left = s == Speakers.TopBackLeft;
                        Speakers back = left ? Speakers.BackLeft : Speakers.BackRight;
                        Speakers side = left ? Speakers.SideLeft : Speakers.SideRight;
                        if (Has(back) || Has(side))
                        {
                            Add(Out(Has(back) ? back : side), i, MinusThreeDecibels);
                        }
                        else
                        {
                            Front(left, i, 0.5f);
                        }

                        break;
                    }

                default:
                    // FrontLeft or FrontRight into an output without that speaker (mono is handled above): its center.
                    Add(Out(Speakers.FrontCenter), i, MinusThreeDecibels);
                    break;
            }
        }

        return m;

        void Front(bool left, int i, float w)
        {
            Speakers side = left ? Speakers.FrontLeft : Speakers.FrontRight;
            Add(Has(side) ? Out(side) : Out(Speakers.FrontCenter), i, w);
        }
    }
}
