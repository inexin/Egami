using Egami.Drawing.Geometry;

namespace Egami.Drawing;

/// <summary>
/// The dashes of a stroke: lengths of drawn and skipped stretches in turn, in path units, and where in the pattern each contour
/// starts, as PDF's <c>d</c>, SVG's <c>stroke-dasharray</c> and <c>stroke-dashoffset</c>, and Skia's dash effect define them. An
/// odd number of lengths repeats once to make an even one, as SVG says, and a pattern whose lengths add up to 0 draws a solid line.
/// Each dash is an open piece of the path that follows its curves and gets the stroke's caps; the pattern starts afresh on each
/// contour.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="dashes"/>
/// </example>
public sealed record class DashPattern
{
    /// <summary>More dashes than this on one path are not drawn as dashes: the stroke is solid instead, as in Skia.</summary>
    internal const int MaxDashes = 1_000_000;

    private readonly float[] _intervals;

    /// <summary>Initializes a new instance of the <see cref="DashPattern"/> class.</summary>
    /// <param name="intervals">The lengths: drawn, skipped, drawn, skipped and so on.</param>
    /// <param name="phase">How far into the pattern each contour starts; negative values count back from the pattern's end.</param>
    /// <exception cref="ArgumentException"><paramref name="intervals"/> is empty, or a length is negative or not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="phase"/> is not finite.</exception>
    public DashPattern(ReadOnlySpan<float> intervals, float phase = 0)
    {
        if (intervals.IsEmpty)
        {
            throw new ArgumentException("A dash pattern needs at least one length.", nameof(intervals));
        }

        foreach (float length in intervals)
        {
            if (!(length >= 0 && float.IsFinite(length)))
            {
                throw new ArgumentException("Dash lengths must be finite and not negative.", nameof(intervals));
            }
        }

        if (!float.IsFinite(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "The phase must be finite.");
        }

        _intervals = intervals.ToArray();
        Phase = phase;
    }

    /// <summary>Gets the lengths as given: drawn, skipped, drawn, skipped and so on.</summary>
    public IReadOnlyList<float> Intervals => Array.AsReadOnly(_intervals);

    /// <summary>Gets how far into the pattern each contour starts.</summary>
    public float Phase { get; }

    /// <summary>
    /// Returns the pattern with every length and the phase multiplied by a factor: give lengths in stroke widths and scale by the
    /// width, as Office's dash styles measure them.
    /// </summary>
    /// <param name="factor">The factor, such as the stroke's width.</param>
    /// <returns>The scaled pattern.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is negative or not finite.</exception>
    public DashPattern Scaled(float factor)
    {
        if (!(factor >= 0 && float.IsFinite(factor)))
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The factor must be finite and not negative.");
        }

        float[] scaled = new float[_intervals.Length];
        for (int i = 0; i < scaled.Length; i++)
        {
            scaled[i] = _intervals[i] * factor;
        }

        return new DashPattern(scaled, Phase * factor);
    }

    /// <inheritdoc/>
    public bool Equals(DashPattern? other) => other is not null && Phase.Equals(other.Phase) && _intervals.AsSpan().SequenceEqual(other._intervals);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(Phase);
        foreach (float length in _intervals)
        {
            hash.Add(length);
        }

        return hash.ToHashCode();
    }

    /// <summary>Returns the path cut into its dashes, each an open contour; the path itself when the pattern is solid or would make too many.</summary>
    internal Path Apply(Path path)
    {
        // An odd count repeats to make an even one; lengths add up in double so a long pattern keeps its precision.
        int count = _intervals.Length % 2 == 0 ? _intervals.Length : 2 * _intervals.Length;
        double[] lengths = new double[count];
        double period = 0;
        for (int i = 0; i < count; i++)
        {
            lengths[i] = _intervals[i % _intervals.Length];
            period += lengths[i];
        }

        if (!(period > 0) || path.IsEmpty)
        {
            return path;
        }

        PathMeasure measure = new(path);
        double total = 0;
        foreach (ContourMeasure contour in measure.Contours)
        {
            total += Math.Ceiling(contour.ExactLength / period) * (count / 2);
        }

        if (total > MaxDashes)
        {
            return path;
        }

        double phase = Phase % period;
        if (phase < 0)
        {
            phase += period;
        }

        PathBuilder builder = new();
        foreach (ContourMeasure contour in measure.Contours)
        {
            // Find where the phase falls in the pattern.
            int index = 0;
            double left = phase;
            while (left > 0 && left >= lengths[index])
            {
                left -= lengths[index];
                index = (index + 1) % count;
            }

            double distance = -left;
            double length = contour.ExactLength;
            while (distance <= length)
            {
                double end = distance + lengths[index];
                if (index % 2 == 0 && end >= 0)
                {
                    double from = Math.Max(0, distance), to = Math.Min(length, end);
                    if (to > from)
                    {
                        contour.AddSegment(builder, from, to, startWithMove: true);
                    }
                    else if (lengths[index] == 0)
                    {
                        // A zero-length dash is a dot, which round and square caps draw.
                        System.Numerics.Vector2 dot = contour.GetPosition((float)from);
                        builder.MoveTo(dot).LineTo(dot);
                    }
                }

                distance = end;
                index = (index + 1) % count;
            }
        }

        return builder.ToPathUnchecked(path.FillRule);
    }
}
