namespace Egami.Video;

/// <summary>
/// HDR10+ dynamic metadata for one frame (SMPTE ST 2094-40, application version 0 or 1), as WebM carries it in each block's
/// BlockAdditional with BlockAddID 4 (an ITU-T T.35 payload). The values are the first processing window's, which covers the whole
/// frame; luminances are in cd/m². Egami.Video reads and keeps it but does not tone map with it: HDR to SDR conversion uses the
/// static metadata (<see cref="VideoFrame.Hdr"/>).
/// </summary>
/// <example>
/// <code language="csharp">
/// using var reader = VideoReader.Open("hdr10plus.webm");
/// foreach (YuvVideoFrame frame in reader.ReadFrames())
///     using (frame)
///         if (frame.Hdr10Plus is { } meta)
///             Console.WriteLine($"{frame.Timestamp}: MaxSCL {string.Join(", ", meta.MaxScl)} cd/m², target {meta.TargetDisplayMaxLuminance} cd/m²");
/// </code>
/// </example>
public sealed record Hdr10PlusMetadata
{
    /// <summary>Gets the ST 2094-40 application version (0 or 1).</summary>
    public int ApplicationVersion { get; init; }

    /// <summary>Gets the peak luminance of the display the metadata was made for (targeted_system_display_maximum_luminance), in cd/m².</summary>
    public double TargetDisplayMaxLuminance { get; init; }

    /// <summary>Gets the brightest linear red, green and blue in the scene (maxscl), in cd/m².</summary>
    public IReadOnlyList<double> MaxScl { get; init; } = [];

    /// <summary>Gets the average of each pixel's brightest linear component (average_maxrgb), in cd/m².</summary>
    public double AverageMaxRgb { get; init; }

    /// <summary>
    /// Gets the distribution of each pixel's brightest component: for each listed percentage of pixels, the level that many are at
    /// or below (distribution_maxrgb_percentages and percentiles), in cd/m².
    /// </summary>
    public IReadOnlyList<Hdr10PlusPercentile> DistributionMaxRgb { get; init; } = [];

    /// <summary>Gets the share of pixels the brightest percentile counts (fraction_bright_pixels), from 0 to 1.023.</summary>
    public double FractionBrightPixels { get; init; }

    /// <summary>Gets the tone mapping curve for the target display, or <see langword="null"/> when there is none.</summary>
    public Hdr10PlusToneCurve? ToneCurve { get; init; }

    /// <summary>Gets the T.35 payload as stored, kept so a remux writes it unchanged.</summary>
    public ReadOnlyMemory<byte> Raw { get; init; }

    /// <summary>Reads an ITU-T T.35 HDR10+ payload, or returns <see langword="null"/> when it is not one or is cut off.</summary>
    internal static Hdr10PlusMetadata? Parse(ReadOnlyMemory<byte> payload)
    {
        BitReader r = new(payload.Span);
        try
        {
            if (r.Read(8) != 0xB5 || r.Read(16) != 0x003C || r.Read(16) != 0x0001 || r.Read(8) != 4)
            {
                return null;
            }

            int version = r.Read(8);
            if (version > 1)
            {
                return null;
            }

            int windows = r.Read(2);
            for (int w = 1; w < windows; w++)
            {
                // Window geometry (corners, ellipse center, angle and axes, overlap option), which applies to windows past the first.
                r.Skip((6 * 16) + 8 + (3 * 16) + 1);
            }

            double target = r.Read(27);
            if (r.Read(1) != 0)
            {
                SkipLuminanceMatrix(ref r);
            }

            double[] maxScl = new double[3];
            double average = 0, bright = 0;
            List<Hdr10PlusPercentile> distribution = [];
            for (int w = 0; w < Math.Max(windows, 1); w++)
            {
                double[] scl = [r.Read(17) / 10.0, r.Read(17) / 10.0, r.Read(17) / 10.0];
                double avg = r.Read(17) / 10.0;
                int percentiles = r.Read(4);
                List<Hdr10PlusPercentile> dist = [];
                for (int i = 0; i < percentiles; i++)
                {
                    byte percentage = (byte)r.Read(7);
                    dist.Add(new Hdr10PlusPercentile(percentage, r.Read(17) / 10.0));
                }

                double fraction = r.Read(10) / 1000.0;
                if (w == 0)
                {
                    maxScl = scl;
                    average = avg;
                    distribution = dist;
                    bright = fraction;
                }
            }

            if (r.Read(1) != 0)
            {
                SkipLuminanceMatrix(ref r);
            }

            Hdr10PlusToneCurve? curve = null;
            for (int w = 0; w < Math.Max(windows, 1); w++)
            {
                if (r.Read(1) != 0)
                {
                    double kneeX = r.Read(12) / 4095.0, kneeY = r.Read(12) / 4095.0;
                    int anchors = r.Read(4);
                    double[] values = new double[anchors];
                    for (int i = 0; i < anchors; i++)
                    {
                        values[i] = r.Read(10) / 1023.0;
                    }

                    if (w == 0)
                    {
                        curve = new Hdr10PlusToneCurve(kneeX, kneeY, values);
                    }
                }

                if (r.Read(1) != 0)
                {
                    r.Skip(6);
                }
            }

            return new Hdr10PlusMetadata
            {
                ApplicationVersion = version,
                TargetDisplayMaxLuminance = target,
                MaxScl = maxScl,
                AverageMaxRgb = average,
                DistributionMaxRgb = distribution,
                FractionBrightPixels = bright,
                ToneCurve = curve,
                Raw = payload,
            };
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static void SkipLuminanceMatrix(ref BitReader r)
    {
        int rows = r.Read(5), cols = r.Read(5);
        r.Skip(rows * cols * 4);
    }

    /// <summary>Reads bits most significant first.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bit;

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int at = _bit >> 3;
                if (at >= _data.Length)
                {
                    throw new EndOfStreamException();
                }

                value = (value << 1) | ((_data[at] >> (7 - (_bit & 7))) & 1);
                _bit++;
            }

            return value;
        }

        public void Skip(int count)
        {
            _bit += count;
            if ((_bit + 7) >> 3 > _data.Length)
            {
                throw new EndOfStreamException();
            }
        }
    }
}

/// <summary>
/// One point of HDR10+'s distribution of each pixel's brightest component: <see cref="Percentage"/> percent of the pixels are at or
/// below <see cref="Value"/> cd/m².
/// </summary>
/// <param name="Percentage">The share of pixels, in percent.</param>
/// <param name="Value">The level, in cd/m².</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-hdr"/>
/// </example>
public readonly record struct Hdr10PlusPercentile(byte Percentage, double Value);

/// <summary>An HDR10+ tone mapping curve for the target display: a knee point, then a Bezier curve through its anchors.</summary>
/// <param name="KneeX">The knee point's input level, 0 to 1.</param>
/// <param name="KneeY">The knee point's output level, 0 to 1.</param>
/// <param name="Anchors">The Bezier curve's anchors, 0 to 1.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-hdr"/>
/// </example>
public sealed record Hdr10PlusToneCurve(double KneeX, double KneeY, IReadOnlyList<double> Anchors);
