namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Highlight handling on white-balanced camera RGB, where a channel the sensor clipped is at its white-balance multiplier or
/// above. Clip cuts every channel at 1, the level of the least amplified one, so clipped areas turn white. Blend also lifts
/// what clipping took off as an equal amount on every channel. Rebuild estimates a clipped channel from the ones that are not,
/// with the colour ratios of the unclipped surroundings spread into the clipped area through a push-pull pyramid.
/// </summary>
internal static class RawHighlights
{
    private const int Cell = 8;

    public static void Apply(Memory<float> rgb, int width, int height, double[] multipliers, RawHighlightMode mode, ParallelOptions parallel)
    {
        float[] clip = [.. multipliers.Select(m => (float)m * 0.995f)];
        if (mode == RawHighlightMode.Rebuild)
        {
            Rebuild(rgb, width, height, clip, parallel);
        }

        Parallel.For(0, height, parallel, y =>
        {
            Span<float> row = rgb.Span.Slice(y * width * 3, width * 3);
            for (int i = 0; i < row.Length; i += 3)
            {
                float r = row[i], g = row[i + 1], b = row[i + 2];
                if (r <= 1 && g <= 1 && b <= 1)
                {
                    continue;
                }

                if (mode == RawHighlightMode.Clip)
                {
                    row[i] = MathF.Min(r, 1);
                    row[i + 1] = MathF.Min(g, 1);
                    row[i + 2] = MathF.Min(b, 1);
                    continue;
                }

                if (mode == RawHighlightMode.Rebuild)
                {
                    // Rebuilt values stay above 1 for the exposure to bring down; nothing more to do here.
                    continue;
                }

                float cr = MathF.Min(r, 1), cg = MathF.Min(g, 1), cb = MathF.Min(b, 1);
                float lost = MathF.Min((r + g + b) / 3, 1) - ((cr + cg + cb) / 3);
                row[i] = MathF.Min(cr + lost, 1);
                row[i + 1] = MathF.Min(cg + lost, 1);
                row[i + 2] = MathF.Min(cb + lost, 1);
            }
        });
    }

    private static void Rebuild(Memory<float> rgb, int width, int height, float[] clip, ParallelOptions parallel)
    {
        int cw = (width + Cell - 1) / Cell, ch = (height + Cell - 1) / Cell;

        // Colour shares (each channel over the sum) of pixels well below clipping, averaged per cell.
        float[] share = new float[cw * ch * 3];
        float[] weight = new float[cw * ch];
        ReadOnlySpan<float> all = rgb.Span;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 3;
                float r = all[i], g = all[i + 1], b = all[i + 2];
                float sum = r + g + b;
                if (r >= clip[0] * 0.9f || g >= clip[1] * 0.9f || b >= clip[2] * 0.9f || sum < 0.03f)
                {
                    continue;
                }

                int c = ((y / Cell) * cw) + (x / Cell);
                float w = sum;
                share[c * 3] += r / sum * w;
                share[(c * 3) + 1] += g / sum * w;
                share[(c * 3) + 2] += b / sum * w;
                weight[c] += w;
            }
        }

        Fill(share, weight, cw, ch);
        Parallel.For(0, height, parallel, y =>
        {
            Span<float> row = rgb.Span.Slice(y * width * 3, width * 3);
            Span<float> q = stackalloc float[3];
            for (int x = 0; x < width; x++)
            {
                int i = x * 3;
                bool cr = row[i] >= clip[0], cg = row[i + 1] >= clip[1], cb = row[i + 2] >= clip[2];
                if (!(cr || cg || cb) || (cr && cg && cb))
                {
                    continue;
                }

                // Bilinear share at this pixel.
                float fx = Math.Clamp(((x + 0.5f) / Cell) - 0.5f, 0, cw - 1), fy = Math.Clamp(((y + 0.5f) / Cell) - 0.5f, 0, ch - 1);
                int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, cw - 1), y1 = Math.Min(y0 + 1, ch - 1);
                float tx = fx - x0, ty = fy - y0;
                for (int c = 0; c < 3; c++)
                {
                    q[c] = ((share[(((y0 * cw) + x0) * 3) + c] * (1 - tx)) + (share[(((y0 * cw) + x1) * 3) + c] * tx)) * (1 - ty)
                        + (((share[(((y1 * cw) + x0) * 3) + c] * (1 - tx)) + (share[(((y1 * cw) + x1) * 3) + c] * tx)) * ty);
                }

                float known = 0, knownShare = 0;
                if (!cr)
                {
                    known += row[i];
                    knownShare += q[0];
                }

                if (!cg)
                {
                    known += row[i + 1];
                    knownShare += q[1];
                }

                if (!cb)
                {
                    known += row[i + 2];
                    knownShare += q[2];
                }

                if (knownShare <= 1e-4f)
                {
                    continue;
                }

                float scale = known / knownShare;
                if (cr)
                {
                    row[i] = MathF.Max(row[i], scale * q[0]);
                }

                if (cg)
                {
                    row[i + 1] = MathF.Max(row[i + 1], scale * q[1]);
                }

                if (cb)
                {
                    row[i + 2] = MathF.Max(row[i + 2], scale * q[2]);
                }
            }
        });
    }

    /// <summary>Normalizes the weighted shares and fills cells without any from coarser levels of a push-pull pyramid.</summary>
    private static void Fill(float[] share, float[] weight, int w, int h)
    {
        if (w <= 1 && h <= 1)
        {
            Normalize(share, weight);
            return;
        }

        int w2 = (w + 1) / 2, h2 = (h + 1) / 2;
        float[] coarse = new float[w2 * h2 * 3];
        float[] coarseWeight = new float[w2 * h2];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int c = (y * w) + x, d = ((y / 2) * w2) + (x / 2);
                coarseWeight[d] += weight[c];
                for (int k = 0; k < 3; k++)
                {
                    coarse[(d * 3) + k] += share[(c * 3) + k];
                }
            }
        }

        Fill(coarse, coarseWeight, w2, h2);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int c = (y * w) + x, d = ((y / 2) * w2) + (x / 2);
                if (weight[c] > 0)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        share[(c * 3) + k] /= weight[c];
                    }
                }
                else
                {
                    for (int k = 0; k < 3; k++)
                    {
                        share[(c * 3) + k] = coarse[(d * 3) + k];
                    }
                }

                weight[c] = 1;
            }
        }
    }

    private static void Normalize(float[] share, float[] weight)
    {
        for (int c = 0; c < weight.Length; c++)
        {
            for (int k = 0; k < 3; k++)
            {
                share[(c * 3) + k] = weight[c] > 0 ? share[(c * 3) + k] / weight[c] : 1f / 3;
            }

            weight[c] = 1;
        }
    }
}
