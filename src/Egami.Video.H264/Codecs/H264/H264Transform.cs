namespace Egami.Video.Codecs.H264;

/// <summary>The inverse 4x4 and 8x8 transforms (8.5.12.2, 8.5.13.2), added to the prediction in a plane.</summary>
internal static class H264Transform
{
    /// <summary>Transforms scaled coefficients d (raster order, row by row) and adds (r + 32) &gt;&gt; 6 to the 4x4 block at <paramref name="at"/>.</summary>
    internal static void Add4x4(Span<int> d, ushort[] plane, int at, int stride, int max)
    {
        bool dcOnly = true;
        for (int i = 1; i < 16 && dcOnly; i++)
        {
            dcOnly = d[i] == 0;
        }

        if (dcOnly)
        {
            int dc = (d[0] + 32) >> 6;
            for (int y = 0; y < 4; y++)
            {
                int row = at + (y * stride);
                for (int x = 0; x < 4; x++)
                {
                    plane[row + x] = (ushort)Math.Clamp(plane[row + x] + dc, 0, max);
                }
            }

            return;
        }

        Span<int> f = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int d0 = d[(i * 4) + 0], d1 = d[(i * 4) + 1], d2 = d[(i * 4) + 2], d3 = d[(i * 4) + 3];
            int e0 = d0 + d2, e1 = d0 - d2, e2 = (d1 >> 1) - d3, e3 = d1 + (d3 >> 1);
            f[(i * 4) + 0] = e0 + e3;
            f[(i * 4) + 1] = e1 + e2;
            f[(i * 4) + 2] = e1 - e2;
            f[(i * 4) + 3] = e0 - e3;
        }

        for (int j = 0; j < 4; j++)
        {
            int f0 = f[j], f1 = f[4 + j], f2 = f[8 + j], f3 = f[12 + j];
            int g0 = f0 + f2, g1 = f0 - f2, g2 = (f1 >> 1) - f3, g3 = f1 + (f3 >> 1);
            Add(plane, at + j, max, g0 + g3);
            Add(plane, at + stride + j, max, g1 + g2);
            Add(plane, at + (2 * stride) + j, max, g1 - g2);
            Add(plane, at + (3 * stride) + j, max, g0 - g3);
        }
    }

    /// <summary>Transforms an 8x8 block of scaled coefficients (raster order) and adds it to the plane.</summary>
    internal static void Add8x8(Span<int> d, ushort[] plane, int at, int stride, int max)
    {
        Span<int> m = stackalloc int[64];
        for (int i = 0; i < 8; i++)
        {
            Transform8(d.Slice(i * 8, 8), m.Slice(i * 8, 8));
        }

        Span<int> column = stackalloc int[8];
        Span<int> result = stackalloc int[8];
        for (int j = 0; j < 8; j++)
        {
            for (int i = 0; i < 8; i++)
            {
                column[i] = m[(i * 8) + j];
            }

            Transform8(column, result);
            for (int i = 0; i < 8; i++)
            {
                Add(plane, at + (i * stride) + j, max, result[i]);
            }
        }
    }

    private static void Transform8(Span<int> d, Span<int> o)
    {
        int e0 = d[0] + d[4];
        int e1 = -d[3] + d[5] - d[7] - (d[7] >> 1);
        int e2 = d[0] - d[4];
        int e3 = d[1] + d[7] - d[3] - (d[3] >> 1);
        int e4 = (d[2] >> 1) - d[6];
        int e5 = -d[1] + d[7] + d[5] + (d[5] >> 1);
        int e6 = d[2] + (d[6] >> 1);
        int e7 = d[3] + d[5] + d[1] + (d[1] >> 1);
        int f0 = e0 + e6;
        int f1 = e1 + (e7 >> 2);
        int f2 = e2 + e4;
        int f3 = e3 + (e5 >> 2);
        int f4 = e2 - e4;
        int f5 = (e3 >> 2) - e5;
        int f6 = e0 - e6;
        int f7 = e7 - (e1 >> 2);
        o[0] = f0 + f7;
        o[1] = f2 + f5;
        o[2] = f4 + f3;
        o[3] = f6 + f1;
        o[4] = f6 - f1;
        o[5] = f4 - f3;
        o[6] = f2 - f5;
        o[7] = f0 - f7;
    }

    private static void Add(ushort[] plane, int at, int max, int h) => plane[at] = (ushort)Math.Clamp(plane[at] + ((h + 32) >> 6), 0, max);
}
