namespace Egami.Heic.Hevc;

/// <summary>
/// The adaptive quantizer: a QP for each quantization group from the activity of its luma samples. Busy, textured groups hide
/// quantization noise, so they take a higher QP; flat ones show it, so they take a lower one. The offset is a log of the group's
/// variance against the picture's mean of those logs, so the mean QP stays the slice's.
/// </summary>
internal static class HevcAdaptiveQp
{
    /// <summary>Gets or sets the QP change per doubling of a group's variance against the mean.</summary>
    internal static double Strength { get; set; } = 1.0;

    /// <summary>The largest change either way.</summary>
    internal const int MaxOffset = 6;

    /// <summary>Gets the QP of each quantization group of 2^<paramref name="log2Group"/> luma samples, in raster order.</summary>
    internal static int[] GroupQps(HevcEncoderSettings settings, HevcSource source, int log2Group)
    {
        int size = 1 << log2Group;
        int width = (settings.CodedWidth + size - 1) >> log2Group, height = (settings.CodedHeight + size - 1) >> log2Group;
        int[] qps = new int[width * height];
        Array.Fill(qps, settings.Qp);
        if (!settings.AdaptiveQp || settings.Lossless)
        {
            return qps;
        }

        double[] logs = new double[qps.Length];
        double sum = 0;
        ushort[] plane = source.Planes[0];
        int stride = source.Widths[0];
        int shift = settings.BitDepth - 8;
        for (int gy = 0; gy < height; gy++)
        {
            for (int gx = 0; gx < width; gx++)
            {
                // The mean variance of the group's 8x8 blocks, in 8-bit units.
                double variance = 0;
                int blocks = 0;
                for (int by = gy * size; by < Math.Min((gy + 1) * size, settings.CodedHeight); by += 8)
                {
                    for (int bx = gx * size; bx < Math.Min((gx + 1) * size, settings.CodedWidth); bx += 8)
                    {
                        long s = 0, s2 = 0;
                        for (int y = 0; y < 8; y++)
                        {
                            for (int x = 0; x < 8; x++)
                            {
                                int v = plane[((by + y) * stride) + bx + x] >> shift;
                                s += v;
                                s2 += v * v;
                            }
                        }

                        variance += (s2 - (s * (double)s / 64)) / 64;
                        blocks++;
                    }
                }

                double log = Math.Log2((variance / Math.Max(blocks, 1)) + 1);
                logs[(gy * width) + gx] = log;
                sum += log;
            }
        }

        int min = -6 * (settings.BitDepth - 8);
        if (settings.VideoQuantizer is { } video)
        {
            VideoQps(settings, video, qps, logs, log2Group, width, height, min);
            return qps;
        }

        double mean = sum / logs.Length;
        for (int i = 0; i < qps.Length; i++)
        {
            int offset = (int)Math.Round(Math.Clamp(Strength * (logs[i] - mean), -MaxOffset, MaxOffset));
            qps[i] = Math.Clamp(settings.Qp + offset, min, 51);
        }

        return qps;
    }

    /// <summary>
    /// The video encoder's group QPs: the adaptive offsets against the mean of the groups that are not flat (all of them when
    /// every group is), plus the block tree's mean offset over each group's 16x16 blocks.
    /// </summary>
    private static void VideoQps(HevcEncoderSettings settings, HevcVideoQuantizer video, int[] qps, double[] logs, int log2Group, int width, int height, int min)
    {
        double flatLog = Math.Log2(video.FlatVariance + 1), sum = 0, all = 0;
        int counted = 0;
        foreach (double log in logs)
        {
            all += log;
            if (log >= flatLog)
            {
                sum += log;
                counted++;
            }
        }

        double mean = counted > 0 ? sum / counted : all / logs.Length;
        double[]? tree = video.TreeOffsets;
        int blockColumns = (settings.Width + 15) >> 4, blockRows = (settings.Height + 15) >> 4;
        if (tree is not null && tree.Length != blockColumns * blockRows)
        {
            tree = null;
        }

        int per = Math.Max(1, 1 << (log2Group - 4));
        for (int gy = 0; gy < height; gy++)
        {
            for (int gx = 0; gx < width; gx++)
            {
                int i = (gy * width) + gx;
                double offset = video.Strength == 0 ? 0 : Math.Clamp(Strength * video.Strength * (logs[i] - mean), -MaxOffset, MaxOffset);
                if (tree is not null)
                {
                    // The group's 16x16 blocks inside the picture (a 16x16 group, or smaller, takes its block's).
                    double t = 0;
                    int n = 0;
                    int bx0 = (gx << log2Group) >> 4, by0 = (gy << log2Group) >> 4;
                    for (int by = by0; by < Math.Min(by0 + per, blockRows); by++)
                    {
                        for (int bx = bx0; bx < Math.Min(bx0 + per, blockColumns); bx++)
                        {
                            t += tree[(by * blockColumns) + bx];
                            n++;
                        }
                    }

                    offset += n > 0 ? 6 * t / n : 0;
                }

                qps[i] = Math.Clamp(settings.Qp + (int)Math.Round(offset), min, 51);
            }
        }
    }
}
