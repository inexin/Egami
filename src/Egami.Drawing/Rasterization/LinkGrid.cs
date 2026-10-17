using System.Runtime.CompilerServices;

using Egami.Colors;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// A transform of three inputs from 0 to 1 (RGB) sampled on a 33-point grid and interpolated tetrahedrally, as Little CMS
/// optimises a device link: an ink canvas converts images and shaded paints a pixel at a time, where the full pipeline costs
/// hundreds of nanoseconds. Inputs outside 0 to 1 go through the transform itself.
/// </summary>
internal sealed class LinkGrid
{
    private const int Points = 33;
    private const int Last = Points - 1;

    private static readonly ConditionalWeakTable<ColorTransform, LinkGrid> Cache = [];

    private readonly ColorTransform _transform;
    private readonly float[] _table;
    private readonly int _outputs;

    private LinkGrid(ColorTransform transform)
    {
        _transform = transform;
        _outputs = transform.OutputChannels;
        float[] inputs = new float[Points * Points * Points * 3];
        int at = 0;
        for (int r = 0; r < Points; r++)
        {
            for (int g = 0; g < Points; g++)
            {
                for (int b = 0; b < Points; b++)
                {
                    inputs[at++] = r / (float)Last;
                    inputs[at++] = g / (float)Last;
                    inputs[at++] = b / (float)Last;
                }
            }
        }

        _table = new float[Points * Points * Points * _outputs];
        transform.Apply(inputs, _table);
    }

    /// <summary>Returns the grid of a three-input transform, built once per transform.</summary>
    internal static LinkGrid For(ColorTransform transform)
    {
        if (transform.InputChannels != 3)
        {
            throw new ArgumentException("A link grid takes three inputs.", nameof(transform));
        }

        return Cache.GetValue(transform, t => new LinkGrid(t));
    }

    /// <summary>Converts colours of three values each into <see cref="ColorTransform.OutputChannels"/> values each.</summary>
    internal void Apply(ReadOnlySpan<float> input, Span<float> output)
    {
        int n = input.Length / 3, k = _outputs;
        ReadOnlySpan<float> table = _table;
        for (int i = 0; i < n; i++)
        {
            float r = input[3 * i], g = input[(3 * i) + 1], b = input[(3 * i) + 2];
            Span<float> to = output.Slice(i * k, k);
            if (!(r >= 0 && r <= 1 && g >= 0 && g <= 1 && b >= 0 && b <= 1))
            {
                _transform.Apply(input.Slice(3 * i, 3), to);
                continue;
            }

            float fr = r * Last, fg = g * Last, fb = b * Last;
            int x0 = Math.Min((int)fr, Last - 1), y0 = Math.Min((int)fg, Last - 1), z0 = Math.Min((int)fb, Last - 1);
            float rx = fr - x0, ry = fg - y0, rz = fb - z0;
            int sx = Points * Points * k, sy = Points * k, sz = k;
            int c000 = (x0 * sx) + (y0 * sy) + (z0 * sz);

            // The tetrahedron holding the point, by the order of its fractions; c1, c2 the corners after c000.
            int c1, c2;
            float w0, w1, w2, w3;
            if (rx >= ry)
            {
                if (ry >= rz)
                {
                    (c1, c2, w0, w1, w2, w3) = (sx, sx + sy, 1 - rx, rx - ry, ry - rz, rz);
                }
                else if (rx >= rz)
                {
                    (c1, c2, w0, w1, w2, w3) = (sx, sx + sz, 1 - rx, rx - rz, rz - ry, ry);
                }
                else
                {
                    (c1, c2, w0, w1, w2, w3) = (sz, sx + sz, 1 - rz, rz - rx, rx - ry, ry);
                }
            }
            else if (rz >= ry)
            {
                (c1, c2, w0, w1, w2, w3) = (sz, sy + sz, 1 - rz, rz - ry, ry - rx, rx);
            }
            else if (rz >= rx)
            {
                (c1, c2, w0, w1, w2, w3) = (sy, sy + sz, 1 - ry, ry - rz, rz - rx, rx);
            }
            else
            {
                (c1, c2, w0, w1, w2, w3) = (sy, sx + sy, 1 - ry, ry - rx, rx - rz, rz);
            }

            int c111 = sx + sy + sz;
            for (int c = 0; c < k; c++)
            {
                int o = c000 + c;
                to[c] = (w0 * table[o]) + (w1 * table[o + c1]) + (w2 * table[o + c2]) + (w3 * table[o + c111]);
            }
        }
    }
}
