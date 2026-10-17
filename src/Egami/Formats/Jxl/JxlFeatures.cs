namespace Egami.Formats.Jxl;

/// <summary>A frame's planes as floats, all one size: the color channels (three; gray is three equal planes) then the extra channels.</summary>
internal sealed class JxlPlanes(int width, int height, float[][] planes)
{
    internal int Width { get; } = width;

    internal int Height { get; } = height;

    internal float[][] Planes { get; } = planes;

    internal JxlPlanes Copy() => new(Width, Height, [.. Planes.Select(p => (float[])p.Clone())]);
}

/// <summary>A patch blending mode (K.3.1).</summary>
internal enum JxlPatchBlendMode
{
    None = 0,
    Replace = 1,
    Add = 2,
    Mul = 3,
    BlendAbove = 4,
    BlendBelow = 5,
    MulAddAbove = 6,
    MulAddBelow = 7,
}

/// <summary>How a patch blends into one channel.</summary>
internal readonly record struct JxlPatchBlending(JxlPatchBlendMode Mode, int AlphaChannel, bool Clamp);

/// <summary>Where a patch goes, and how it blends into each channel (color, then each extra channel).</summary>
internal readonly record struct JxlPatchTarget(int X, int Y, JxlPatchBlending[] Blending);

/// <summary>A rectangle of a reference frame and the places it is pasted.</summary>
internal sealed record JxlPatch(int Reference, int X0, int Y0, int Width, int Height, JxlPatchTarget[] Targets);

/// <summary>
/// The image features drawn on a frame after the restoration filters (K): patches copied from reference frames, splines, and
/// noise. Ported from jxl-oxide's jxl-frame and jxl-render (see THIRD-PARTY-NOTICES).
/// </summary>
internal static class JxlFeatures
{
    private const int MaxSplines = 1 << 24;
    private const int MaxControlPoints = 1 << 20;

    internal static int UnpackSigned(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

    /// <summary>Reads the patch dictionary (K.3.1).</summary>
    internal static JxlPatch[] ReadPatches(JxlBitReader r, JxlImageHeader image, JxlFrameHeader frame)
    {
        int extras = image.ExtraChannels.Count;
        int[] alphas = [.. image.ExtraChannels.Select((e, i) => (e, i)).Where(t => t.e.Type == JxlExtraChannelType.Alpha).Select(t => t.i)];
        JxlEntropyDecoder d = JxlEntropyDecoder.Read(r, 10);
        d.Begin(r);
        // libjxl's limits (dec_patch_dictionary), on the frame's size padded to whole blocks: djxl decodes up to these.
        (long codedWidth, long codedHeight) = frame.CodedSize;
        if (!frame.Modular)
        {
            int bw = 8 << (frame.JpegUpsampling.Any(u => u is 1 or 2) ? 1 : 0);
            int bh = 8 << (frame.JpegUpsampling.Any(u => u is 1 or 3) ? 1 : 0);
            (codedWidth, codedHeight) = ((codedWidth + bw - 1) / bw * bw, (codedHeight + bh - 1) / bh * bh);
        }

        long maxRefs = 1024 + (codedWidth * codedHeight / 4);
        long maxPatches = maxRefs * 4;
        long maxBlendings = maxPatches * 4;
        long nextSize = 1;
        uint count = d.ReadUint(r, 0);
        if (count > maxRefs)
        {
            throw Corrupt(r, "The frame has more patches than its size allows.");
        }

        JxlPatch[] patches = new JxlPatch[count];
        long total = 0;
        for (int p = 0; p < count; p++)
        {
            uint reference = d.ReadUint(r, 1);
            if (reference >= 4)
            {
                throw Corrupt(r, "A patch names a reference frame slot beyond 3.");
            }

            uint x0 = d.ReadUint(r, 3);
            uint y0 = d.ReadUint(r, 3);
            uint width = d.ReadUint(r, 2) + 1;
            uint height = d.ReadUint(r, 2) + 1;
            uint idCount = d.ReadUint(r, 7);
            if (idCount > maxPatches)
            {
                throw Corrupt(r, "The frame has more patches than its size allows.");
            }

            uint targets = idCount + 1;
            total += targets;
            if (nextSize < total)
            {
                nextSize = Math.Min(nextSize * 2, maxPatches);
            }

            if (total > maxPatches || nextSize * (extras + 1) > maxBlendings || x0 > int.MaxValue || y0 > int.MaxValue || width > int.MaxValue || height > int.MaxValue)
            {
                throw Corrupt(r, "The frame has more patches than its size allows.");
            }

            JxlPatchTarget[] list = new JxlPatchTarget[targets];
            long x = 0;
            long y = 0;
            for (int t = 0; t < targets; t++)
            {
                if (t == 0)
                {
                    x = d.ReadUint(r, 4);
                    y = d.ReadUint(r, 4);
                }
                else
                {
                    x += UnpackSigned(d.ReadUint(r, 6));
                    y += UnpackSigned(d.ReadUint(r, 6));
                }

                if (x is < int.MinValue or > int.MaxValue || y is < int.MinValue or > int.MaxValue)
                {
                    throw Corrupt(r, "A patch position overflows.");
                }

                JxlPatchBlending[] blending = new JxlPatchBlending[extras + 1];
                for (int c = 0; c <= extras; c++)
                {
                    uint mode = d.ReadUint(r, 5);
                    if (mode > 7)
                    {
                        throw Corrupt(r, "A patch has an unknown blend mode.");
                    }

                    int alpha = mode >= 4 && alphas.Length >= 2 ? (int)Math.Min(d.ReadUint(r, 8), int.MaxValue) : alphas.FirstOrDefault();
                    bool clamp = mode >= 3 && d.ReadUint(r, 9) != 0;
                    if (mode >= 4 && alpha >= extras)
                    {
                        throw Corrupt(r, "A patch blends by an alpha channel the image does not have.");
                    }

                    blending[c] = new JxlPatchBlending((JxlPatchBlendMode)mode, alpha, clamp);
                }

                list[t] = new JxlPatchTarget((int)x, (int)y, blending);
            }

            patches[p] = new JxlPatch((int)reference, (int)x0, (int)y0, (int)width, (int)height, list);
        }

        d.Finish(r);
        return patches;
    }

    /// <summary>Pastes the patches into <paramref name="frame"/> from the reference frames, which must be saved before the color transform.</summary>
    internal static void ApplyPatches(JxlPlanes frame, JxlPatch[] patches, JxlReference?[] references, JxlImageHeader image)
    {
        float[][] tmp = JxlBlend.Scratch(frame.Planes.Length, patches.Length == 0 ? 0 : patches.Max(p => p.Width));
        foreach (JxlPatch patch in patches)
        {
            JxlReference reference = references[patch.Reference]
                ?? throw new CorruptDataException(JxlFormat.Info.Name, -1, "A patch uses a reference frame slot that holds no frame.");
            JxlPlanes source = reference.Planes;
            if (!reference.BeforeColorTransform)
            {
                throw new CorruptDataException(JxlFormat.Info.Name, -1, "A patch uses a reference frame saved after the color transform.");
            }

            if ((long)patch.X0 + patch.Width > source.Width || (long)patch.Y0 + patch.Height > source.Height)
            {
                throw new CorruptDataException(JxlFormat.Info.Name, -1, "A patch reaches outside its reference frame.");
            }

            foreach (JxlPatchTarget target in patch.Targets)
            {
                // Clip the target to the frame.
                int left = Math.Max(0, -target.X);
                int top = Math.Max(0, -target.Y);
                int width = (int)Math.Min(patch.Width, (long)frame.Width - target.X) - left;
                int height = (int)Math.Min(patch.Height, (long)frame.Height - target.Y) - top;
                for (int dy = 0; dy < height && width > 0; dy++)
                {
                    int bi = ((target.Y + top + dy) * frame.Width) + target.X + left;
                    int ni = ((patch.Y0 + top + dy) * source.Width) + patch.X0 + left;
                    JxlBlend.Row(frame.Planes, bi, source.Planes, ni, width, target.Blending[0], target.Blending.AsSpan(1), image, tmp);
                }
            }
        }
    }

    /// <summary>Reads the splines (K.4).</summary>
    internal static JxlSplines ReadSplines(JxlBitReader r, JxlFrameHeader frame)
    {
        JxlEntropyDecoder d = JxlEntropyDecoder.Read(r, 6);
        d.Begin(r);
        long pixels = (long)frame.Width * frame.Height;
        uint count = d.ReadUint(r, 2);
        if (count >= Math.Min(MaxSplines, pixels / 4))
        {
            throw Corrupt(r, "The frame has more splines than its size allows.");
        }

        int splines = (int)count + 1;
        (long X, long Y)[] starts = new (long, long)[splines];
        starts[0] = (d.ReadUint(r, 1), d.ReadUint(r, 1));
        for (int i = 1; i < splines; i++)
        {
            long x = starts[i - 1].X + UnpackSigned(d.ReadUint(r, 1));
            long y = starts[i - 1].Y + UnpackSigned(d.ReadUint(r, 1));
            starts[i] = (x, y);
        }

        int quantAdjust = UnpackSigned(d.ReadUint(r, 0));
        JxlQuantSpline[] list = new JxlQuantSpline[splines];
        long points = 0;
        long maxPoints = Math.Min(MaxControlPoints, pixels / 2);
        for (int s = 0; s < splines; s++)
        {
            uint n = d.ReadUint(r, 3);
            points += n;
            if (points > maxPoints)
            {
                throw Corrupt(r, "The frame has more spline points than its size allows.");
            }

            (long X, long Y)[] control = new (long, long)[n + 1];
            control[0] = starts[s];
            long dx = 0;
            long dy = 0;
            long manhattan = 0;
            for (int i = 1; i <= n; i++)
            {
                dx += UnpackSigned(d.ReadUint(r, 4));
                dy += UnpackSigned(d.ReadUint(r, 4));
                manhattan += Math.Abs(dx) + Math.Abs(dy);
                control[i] = (control[i - 1].X + dx, control[i - 1].Y + dy);
                if (control[i] == control[i - 1])
                {
                    throw Corrupt(r, "Two consecutive spline control points are the same.");
                }

                if (Math.Abs(control[i].X) > 1L << 40 || Math.Abs(control[i].Y) > 1L << 40)
                {
                    throw Corrupt(r, "A spline control point overflows.");
                }
            }

            int[][] xyb = [new int[32], new int[32], new int[32]];
            foreach (int[] channel in xyb)
            {
                for (int i = 0; i < 32; i++)
                {
                    channel[i] = UnpackSigned(d.ReadUint(r, 5));
                }
            }

            int[] sigma = new int[32];
            for (int i = 0; i < 32; i++)
            {
                sigma[i] = UnpackSigned(d.ReadUint(r, 5));
            }

            list[s] = new JxlQuantSpline(control, manhattan, xyb, sigma);
        }

        d.Finish(r);
        return new JxlSplines(list, quantAdjust);
    }

    /// <summary>Checks the splines' estimated drawing area against the level 10 limit (K.4).</summary>
    internal static void CheckSplineArea(JxlSplines splines, float correlationX, float correlationB, long pixels)
    {
        ulong corrX = (ulong)MathF.Ceiling(MathF.Abs(correlationX));
        ulong corrB = (ulong)MathF.Ceiling(MathF.Abs(correlationB));
        ulong total = 0;
        foreach (JxlQuantSpline s in splines.Splines)
        {
            ulong[] color = new ulong[3];
            for (int c = 0; c < 3; c++)
            {
                foreach (int q in s.Xyb[c])
                {
                    color[c] += DivCeil((uint)Math.Abs((long)q), splines.QuantAdjust);
                }
            }

            color[0] += corrX * color[1];
            color[2] += corrB * color[1];
            ulong logColor = (ulong)Math.Ceiling(Math.Log2(1 + (double)color.Max()));
            ulong width = 0;
            foreach (int q in s.Sigma)
            {
                ulong weight = 1 + DivCeil((uint)Math.Abs((long)q), splines.QuantAdjust);
                width += weight * weight * logColor;
            }

            total += width * (ulong)s.Manhattan;
            if (total > Math.Min(1UL << 42, (1024UL * (ulong)pixels) + (1UL << 32)))
            {
                throw new CorruptDataException(JxlFormat.Info.Name, -1, "The frame's splines cover more area than level 10 allows.");
            }
        }
    }

    private static ulong DivCeil(uint dividend, int quantAdjust)
    {
        ulong v = dividend;
        return quantAdjust >= 0 ? ((8 * v) + 7 + (ulong)quantAdjust) / (8 + (ulong)quantAdjust) : v + (((v * (ulong)-quantAdjust) + 7) / 8);
    }

    /// <summary>Draws the splines into the X, Y and B planes.</summary>
    internal static void RenderSplines(JxlPlanes frame, JxlSplines splines, float correlationX, float correlationB, JxlParallel parallel)
    {
        ReadOnlySpan<float> weights = [0.0042f, 0.075f, 0.07f, 0.3333f];
        float qa = splines.QuantAdjust;
        float inverted = qa >= 0 ? 1 / (1 + (qa / 8)) : 1 - (qa / 8);
        // Every sample's place, size and color first, in drawing order; then bands of rows draw them all on several threads.
        // Each pixel gets its additions in the same order, so the output is the same at every thread count.
        List<SplineSample> drawn = [];
        foreach (JxlQuantSpline q in splines.Splines)
        {
            float[][] xyb = [new float[32], new float[32], new float[32]];
            float[] sigma = new float[32];
            for (int c = 0; c < 3; c++)
            {
                for (int i = 0; i < 32; i++)
                {
                    xyb[c][i] = q.Xyb[c][i] * weights[c] * inverted;
                }
            }

            for (int i = 0; i < 32; i++)
            {
                xyb[0][i] += correlationX * xyb[1][i];
                xyb[2][i] += correlationB * xyb[1][i];
                sigma[i] = q.Sigma[i] * weights[3] * inverted;
            }

            List<(float X, float Y, float Length)> samples = Samples(Upsampled(q.Points));
            float arclength = samples.Count - 2 + samples[^1].Length;
            for (int i = 0; i < samples.Count; i++)
            {
                (float px, float py, float length) = samples[i];
                float t = 31 * MathF.Min(1, i / arclength);
                float sig = ContinuousIdct(sigma, t);
                float v0 = ContinuousIdct(xyb[0], t) * length;
                float v1 = ContinuousIdct(xyb[1], t) * length;
                float v2 = ContinuousIdct(xyb[2], t) * length;
                float maxColor = MathF.Max(0.01f, MathF.Max(v0, MathF.Max(v1, v2)));
                float maxDistance = MathF.Sqrt(2 * ((MathF.Log(10) * 3) + maxColor)) * MathF.Abs(sig);
                int x0 = Math.Max(0, (int)MathF.Floor(px - maxDistance + 0.5f));
                int x1 = Math.Min(frame.Width, (int)MathF.Floor(px + maxDistance + 1.5f));
                int y0 = Math.Max(0, (int)MathF.Floor(py - maxDistance + 0.5f));
                int y1 = Math.Min(frame.Height, (int)MathF.Floor(py + maxDistance + 1.5f));
                if (x0 < x1 && y0 < y1)
                {
                    drawn.Add(new SplineSample(px, py, sig, v0, v1, v2, x0, x1, y0, y1));
                }
            }
        }

        float[] p0 = frame.Planes[0];
        float[] p1 = frame.Planes[1];
        float[] p2 = frame.Planes[2];
        int width = frame.Width;
        parallel.Rows(frame.Height, (top, bottom) =>
        {
            foreach (SplineSample d in drawn)
            {
                float invSigma = 1 / d.Sigma;
                for (int y = Math.Max(top, d.Y0); y < Math.Min(bottom, d.Y1); y++)
                {
                    float dy = y - d.Y;
                    for (int x = d.X0; x < d.X1; x++)
                    {
                        // The falloff is the same for the three channels, so it is computed once a pixel.
                        float dx = x - d.X;
                        float distance = MathF.Sqrt((dx * dx) + (dy * dy));
                        const float Sqrt0125 = 0.35355338f;
                        float factor = Erf(((0.5f * distance) + Sqrt0125) * invSigma) - Erf(((0.5f * distance) - Sqrt0125) * invSigma);
                        int at = (y * width) + x;
                        p0[at] += 0.25f * d.V0 * d.Sigma * factor * factor;
                        p1[at] += 0.25f * d.V1 * d.Sigma * factor * factor;
                        p2[at] += 0.25f * d.V2 * d.Sigma * factor * factor;
                    }
                }
            }
        });
    }

    /// <summary>Gets the points of the centripetal Catmull-Rom spline through the control points, 16 per segment.</summary>
    private static List<(float X, float Y)> Upsampled((long X, long Y)[] control)
    {
        (float X, float Y)[] s = [.. control.Select(p => ((float)p.X, (float)p.Y))];
        if (s.Length == 1)
        {
            return [s[0]];
        }

        List<(float X, float Y)> extended = [Mirror(s[1], s[0]), .. s, Mirror(s[^2], s[^1])];
        List<(float X, float Y)> upsampled = new((16 * (extended.Count - 3)) + 1);
        Span<float> t = stackalloc float[4];
        Span<(float X, float Y)> a = stackalloc (float, float)[3];
        Span<(float X, float Y)> b = stackalloc (float, float)[2];
        for (int i = 0; i < extended.Count - 3; i++)
        {
            (float X, float Y)[] p = [extended[i], extended[i + 1], extended[i + 2], extended[i + 3]];
            upsampled.Add(p[1]);
            t[0] = 0;
            for (int k = 1; k < 4; k++)
            {
                float ddx = p[k].X - p[k - 1].X;
                float ddy = p[k].Y - p[k - 1].Y;
                t[k] = t[k - 1] + MathF.Pow((ddx * ddx) + (ddy * ddy), 0.25f);
            }

            for (int step = 1; step < 16; step++)
            {
                float knot = t[1] + (step / 16f * (t[2] - t[1]));
                for (int k = 0; k < 3; k++)
                {
                    a[k] = Lerp(p[k], p[k + 1], (knot - t[k]) / (t[k + 1] - t[k]));
                }

                for (int k = 0; k < 2; k++)
                {
                    b[k] = Lerp(a[k], a[k + 1], (knot - t[k]) / (t[k + 2] - t[k]));
                }

                upsampled.Add(Lerp(b[0], b[1], (knot - t[1]) / (t[2] - t[1])));
            }
        }

        upsampled.Add(s[^1]);
        return upsampled;
    }

    private static (float X, float Y) Mirror((float X, float Y) p, (float X, float Y) center) => (center.X + center.X - p.X, center.Y + center.Y - p.Y);

    private static (float X, float Y) Lerp((float X, float Y) p, (float X, float Y) q, float f) => (p.X + ((q.X - p.X) * f), p.Y + ((q.Y - p.Y) * f));

    /// <summary>Gets points along the curve one pixel of arc length apart, each with the arc length it stands for.</summary>
    private static List<(float X, float Y, float Length)> Samples(List<(float X, float Y)> points)
    {
        (float X, float Y) current = points[0];
        int next = 0;
        List<(float X, float Y, float Length)> samples = [(current.X, current.Y, 1)];
        while (next < points.Count)
        {
            (float X, float Y) prev = current;
            float arclength = 0;
            while (true)
            {
                if (next >= points.Count)
                {
                    samples.Add((prev.X, prev.Y, arclength));
                    break;
                }

                (float X, float Y) p = points[next];
                float dx = p.X - prev.X;
                float dy = p.Y - prev.Y;
                float toNext = MathF.Sqrt((dx * dx) + (dy * dy));
                if (arclength + toNext >= 1)
                {
                    current = Lerp(prev, p, (1 - arclength) / toNext);
                    samples.Add((current.X, current.Y, 1));
                    break;
                }

                arclength += toNext;
                prev = p;
                next++;
            }
        }

        return samples;
    }

    private static float ContinuousIdct(float[] dct, float t)
    {
        float result = dct[0];
        for (int i = 1; i < 32; i++)
        {
            float theta = i * (MathF.PI / 32) * (t + 0.5f);
            result += MathF.Sqrt(2) * dct[i] * MathF.Cos(theta);
        }

        return result;
    }

    /// <summary>The error function, to within 7e-4.</summary>
    private static float Erf(float x)
    {
        float ax = MathF.Abs(x);
        float d1 = (ax * 7.77394369e-02f) + 2.05260015e-04f;
        float d2 = (d1 * ax) + 2.32120216e-01f;
        float d3 = (d2 * ax) + 2.77820801e-01f;
        float d4 = (d3 * ax) + 1;
        float d5 = d4 * d4;
        float inv = 1 / d5;
        float result = (-inv * inv) + 1;
        return x < 0 ? -result : result;
    }

    /// <summary>Reads the noise parameters (K.5): eight intensity levels.</summary>
    internal static float[] ReadNoise(JxlBitReader r)
    {
        float[] lut = new float[8];
        for (int i = 0; i < 8; i++)
        {
            lut[i] = r.ReadBits(10) / 1024f;
        }

        return lut;
    }

    /// <summary>
    /// Adds noise to the X, Y and B planes: per-group pseudo-random planes seeded by the frame's place in the sequence, filtered by a
    /// 5x5 high-pass kernel, scaled by intensity.
    /// </summary>
    internal static void RenderNoise(JxlPlanes frame, float[] parameters, int groupDim, int visible, int invisible, float correlationX, float correlationB)
    {
        int width = frame.Width;
        int height = frame.Height;
        float[][] noise = [new float[width * height], new float[width * height], new float[width * height]];
        ulong seed0 = ((ulong)visible << 32) + (ulong)invisible;
        Span<uint> bits = stackalloc uint[16];
        for (int gy = 0; gy < height; gy += groupDim)
        {
            for (int gx = 0; gx < width; gx += groupDim)
            {
                XorShift128Plus rng = new(seed0, ((ulong)gx << 32) + (ulong)gy);
                int gw = Math.Min(groupDim, width - gx);
                int gh = Math.Min(groupDim, height - gy);
                foreach (float[] plane in noise)
                {
                    for (int y = 0; y < gh; y++)
                    {
                        int row = ((gy + y) * width) + gx;
                        for (int x = 0; x < gw; x += 16)
                        {
                            rng.Fill(bits);
                            for (int k = 0; k < 16 && x + k < gw; k++)
                            {
                                plane[row + x + k] = BitConverter.UInt32BitsToSingle((bits[k] >> 9) | 0x3F800000);
                            }
                        }
                    }
                }
            }
        }

        float[] lut = [.. parameters, parameters[7]];
        float[] px = frame.Planes[0];
        float[] py = frame.Planes[1];
        float[] pb = frame.Planes[2];
        float[] rowX = new float[width];
        float[] rowY = new float[width];
        float[] rowB = new float[width];
        for (int y = 0; y < height; y++)
        {
            Convolve(noise[0], width, height, y, rowX);
            Convolve(noise[1], width, height, y, rowY);
            Convolve(noise[2], width, height, y, rowB);
            int at = y * width;
            for (int x = 0; x < width; x++, at++)
            {
                float inX = px[at] + py[at];
                float inY = py[at] - px[at];
                float sx = Strength(lut, inX);
                float sy = Strength(lut, inY);
                float nx = 0.22f * sx * ((0.0078125f * rowX[x]) + (0.9921875f * rowB[x]));
                float ny = 0.22f * sy * ((0.0078125f * rowY[x]) + (0.9921875f * rowB[x]));
                px[at] += (correlationX * (nx + ny)) + nx - ny;
                py[at] += nx + ny;
                pb[at] += correlationB * (nx + ny);
            }
        }
    }

    private static float Strength(float[] lut, float v)
    {
        float scaled = MathF.Max(0, v * 3);
        int i = (int)MathF.Min(scaled, 7);
        float frac = scaled - i;
        return ((lut[i + 1] - lut[i]) * frac) + lut[i];
    }

    /// <summary>Filters one row of a noise plane by the 5x5 kernel (0.16 everywhere, minus 4 at the center), mirroring at the edges.</summary>
    private static void Convolve(float[] plane, int width, int height, int y, float[] output)
    {
        for (int x = 0; x < width; x++)
        {
            float sum = 0;
            for (int dy = -2; dy <= 2; dy++)
            {
                int row = JxlFilters.Mirror(y + dy, height) * width;
                for (int dx = -2; dx <= 2; dx++)
                {
                    sum += plane[row + JxlFilters.Mirror(x + dx, width)];
                }
            }

            output[x] = (sum * 0.16f) - (plane[(y * width) + x] * 4);
        }
    }

    private static CorruptDataException Corrupt(JxlBitReader r, string message) => new(JxlFormat.Info.Name, r.BitPosition / 8, message);

    /// <summary>Eight xorshift128+ generators run side by side, as the noise synthesis specifies.</summary>
    private sealed class XorShift128Plus
    {
        private readonly ulong[] _s0 = new ulong[8];
        private readonly ulong[] _s1 = new ulong[8];

        internal XorShift128Plus(ulong seed0, ulong seed1)
        {
            _s0[0] = SplitMix64(seed0 + 0x9E3779B97F4A7C15);
            _s1[0] = SplitMix64(seed1 + 0x9E3779B97F4A7C15);
            for (int i = 1; i < 8; i++)
            {
                _s0[i] = SplitMix64(_s0[i - 1]);
                _s1[i] = SplitMix64(_s1[i - 1]);
            }
        }

        /// <summary>Fills 16 random 32-bit values: each generator's 64 bits, low half first.</summary>
        internal void Fill(Span<uint> output)
        {
            for (int i = 0; i < 8; i++)
            {
                ulong s1 = _s0[i];
                ulong s0 = _s1[i];
                ulong result = s1 + s0;
                _s0[i] = s0;
                s1 ^= s1 << 23;
                _s1[i] = s1 ^ (s0 ^ (s1 >> 18) ^ (s0 >> 5));
                output[2 * i] = (uint)result;
                output[(2 * i) + 1] = (uint)(result >> 32);
            }
        }

        private static ulong SplitMix64(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }
    }
}

/// <summary>A spline as coded: control points, the Manhattan length of the deltas, and the quantized DCT32 of X, Y, B and sigma.</summary>
internal sealed record JxlQuantSpline((long X, long Y)[] Points, long Manhattan, int[][] Xyb, int[] Sigma);

/// <summary>A frame's splines.</summary>
internal sealed record JxlSplines(JxlQuantSpline[] Splines, int QuantAdjust);

/// <summary>One drawn point of a spline: its place, width (sigma), color and the window of pixels it reaches.</summary>
internal readonly record struct SplineSample(float X, float Y, float Sigma, float V0, float V1, float V2, int X0, int X1, int Y0, int Y1);
