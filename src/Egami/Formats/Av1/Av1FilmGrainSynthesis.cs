using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Av1;

/// <summary>
/// Film grain synthesis, after dav1d's filmgrain_tmpl.c and fg_apply_tmpl.c (BSD 2-clause, see THIRD-PARTY-NOTICES), with
/// libaom's Gaussian sequence: autoregressive grain templates, piecewise linear scaling, and 32x32 blocks of grain at random
/// offsets blended where they overlap. Applied to a copy of the frame for output; the frame itself stays the reference.
/// </summary>
internal static class Av1FilmGrainSynthesis
{
    private const int GrainWidth = 82;
    private const int GrainHeight = 73;
    private const int BlockSize = 32;
    private const int SubGrainWidth = 44;
    private const int SubGrainHeight = 38;

    /// <summary>
    /// Returns the frame's planes with grain added, as 16-bit planes, or the planes themselves (of the frame's sample type) when
    /// the frame applies none.
    /// </summary>
    internal static Array[] Apply(Av1Frame frame)
    {
        Av1FilmGrain g = frame.Header.FilmGrain;
        if (!g.ApplyGrain)
        {
            return frame.Planes!;
        }

        ushort[][] input = frame.WidePlanes();

        Av1SequenceHeader seq = frame.Sequence;
        int bd = seq.BitDepth;
        int width = frame.Header.UpscaledWidth, height = frame.Header.FrameHeight;
        int ssx = seq.SubsamplingX, ssy = seq.SubsamplingY;
        bool mono = seq.Monochrome;
        ushort[][] output = [.. input.Select(p => (ushort[])p.Clone())];
        Data d = new(g);

        int[] lumaGrain = new int[GrainHeight * GrainWidth];
        GenerateLuma(lumaGrain, d, bd);
        int[][] chromaGrain = new int[2][];
        int[][] scaling = new int[3][];
        for (int uv = 0; uv < 2 && !mono; uv++)
        {
            if (d.UvPoints[uv].Length > 0 || d.ChromaScalingFromLuma)
            {
                chromaGrain[uv] = new int[GrainHeight * GrainWidth];
                GenerateChroma(chromaGrain[uv], lumaGrain, d, uv, ssx, ssy, bd);
            }
        }

        if (d.YPoints.Length > 0 || d.ChromaScalingFromLuma)
        {
            scaling[0] = Scaling(bd, d.YPoints);
        }

        for (int uv = 0; uv < 2; uv++)
        {
            if (d.UvPoints[uv].Length > 0)
            {
                scaling[uv + 1] = Scaling(bd, d.UvPoints[uv]);
            }
        }

        int rows = (height + BlockSize - 1) / BlockSize;
        int lumaStride = frame.Strides[0];
        bool isIdentity = seq.MatrixCoefficients == 0;
        // Each band of 32 rows seeds its own random numbers, so the bands add grain on their own.
        frame.For(rows, row =>
        {
            if (d.YPoints.Length > 0)
            {
                int bh = Math.Min(height - (row * BlockSize), BlockSize);
                ApplyLuma(output[0], input[0], row * BlockSize * lumaStride, lumaStride, d, width, scaling[0], lumaGrain, bh, row, bd);
            }

            if (mono || (d.UvPoints[0].Length == 0 && d.UvPoints[1].Length == 0 && !d.ChromaScalingFromLuma))
            {
                return;
            }

            int cbh = (Math.Min(height - (row * BlockSize), BlockSize) + ssy) >> ssy;
            int lumaRow = row * BlockSize * lumaStride;
            if ((width & ssx) != 0)
            {
                // Repeat the last luma column for the chroma sample past the frame's odd width.
                for (int y = 0, at = lumaRow; y < cbh; y++, at += lumaStride << ssy)
                {
                    input[0][at + width] = input[0][at + width - 1];
                }
            }

            int chromaStride = frame.Strides[1];
            int cpw = (width + ssx) >> ssx;
            int uvOffset = (row * BlockSize * chromaStride) >> ssy;
            for (int uv = 0; uv < 2; uv++)
            {
                if (d.ChromaScalingFromLuma || d.UvPoints[uv].Length > 0)
                {
                    ApplyChroma(output[1 + uv], input[1 + uv], uvOffset, chromaStride, d, cpw, d.ChromaScalingFromLuma ? scaling[0] : scaling[1 + uv],
                        chromaGrain[uv], cbh, row, input[0], lumaRow, lumaStride, uv, isIdentity, ssx, ssy, bd);
                }
            }
        });

        return output;
    }

    private static int RandomNumber(int bits, ref uint state)
    {
        uint r = state;
        uint bit = ((r >> 0) ^ (r >> 1) ^ (r >> 3) ^ (r >> 12)) & 1;
        state = (r >> 1) | (bit << 15);
        return (int)((state >> (16 - bits)) & ((1u << bits) - 1));
    }

    private static int Round2(int x, int shift) => (x + ((1 << shift) >> 1)) >> shift;

    private static void GenerateLuma(int[] buf, Data d, int bd)
    {
        uint seed = (uint)d.Seed;
        int shift = 4 - (bd - 8) + d.GrainScaleShift;
        int grainMin = -(128 << (bd - 8)), grainMax = (128 << (bd - 8)) - 1;
        for (int y = 0; y < GrainHeight; y++)
        {
            for (int x = 0; x < GrainWidth; x++)
            {
                buf[(y * GrainWidth) + x] = Round2(Av1Tables.GaussianSequence[RandomNumber(11, ref seed)], shift);
            }
        }

        int lag = d.ArCoeffLag;
        for (int y = 3; y < GrainHeight; y++)
        {
            for (int x = 3; x < GrainWidth - 3; x++)
            {
                int sum = 0, coeff = 0;
                for (int dy = -lag; dy <= 0; dy++)
                {
                    for (int dx = -lag; dx <= lag; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            break;
                        }

                        sum += d.ArCoeffsY[coeff++] * buf[((y + dy) * GrainWidth) + x + dx];
                    }
                }

                int grain = buf[(y * GrainWidth) + x] + Round2(sum, d.ArCoeffShift);
                buf[(y * GrainWidth) + x] = Math.Clamp(grain, grainMin, grainMax);
            }
        }
    }

    private static void GenerateChroma(int[] buf, int[] lumaBuf, Data d, int uv, int ssx, int ssy, int bd)
    {
        uint seed = (uint)(d.Seed ^ (uv != 0 ? 0x49d8 : 0xb524));
        int shift = 4 - (bd - 8) + d.GrainScaleShift;
        int grainMin = -(128 << (bd - 8)), grainMax = (128 << (bd - 8)) - 1;
        int chromaW = ssx != 0 ? SubGrainWidth : GrainWidth, chromaH = ssy != 0 ? SubGrainHeight : GrainHeight;
        for (int y = 0; y < chromaH; y++)
        {
            for (int x = 0; x < chromaW; x++)
            {
                buf[(y * GrainWidth) + x] = Round2(Av1Tables.GaussianSequence[RandomNumber(11, ref seed)], shift);
            }
        }

        int lag = d.ArCoeffLag;
        int[] coeffs = d.ArCoeffsUv[uv];
        for (int y = 3; y < chromaH; y++)
        {
            for (int x = 3; x < chromaW - 3; x++)
            {
                int sum = 0, coeff = 0;
                for (int dy = -lag; dy <= 0; dy++)
                {
                    for (int dx = -lag; dx <= lag; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            if (d.YPoints.Length == 0)
                            {
                                break;
                            }

                            int luma = 0;
                            int lumaX = ((x - 3) << ssx) + 3, lumaY = ((y - 3) << ssy) + 3;
                            for (int i = 0; i <= ssy; i++)
                            {
                                for (int j = 0; j <= ssx; j++)
                                {
                                    luma += lumaBuf[((lumaY + i) * GrainWidth) + lumaX + j];
                                }
                            }

                            luma = Round2(luma, ssx + ssy);
                            sum += luma * coeffs[coeff];
                            break;
                        }

                        sum += coeffs[coeff++] * buf[((y + dy) * GrainWidth) + x + dx];
                    }
                }

                int grain = buf[(y * GrainWidth) + x] + Round2(sum, d.ArCoeffShift);
                buf[(y * GrainWidth) + x] = Math.Clamp(grain, grainMin, grainMax);
            }
        }
    }

    /// <summary>generate_scaling: the piecewise linear scaling function, at every sample value.</summary>
    private static int[] Scaling(int bd, (int Value, int Scaling)[] points)
    {
        int shiftX = bd - 8, size = 1 << bd;
        int[] scaling = new int[Math.Max(size, 256)];
        int num = points.Length;
        if (num == 0)
        {
            return scaling;
        }

        scaling.AsSpan(0, points[0].Value << shiftX).Fill(points[0].Scaling);
        for (int i = 0; i < num - 1; i++)
        {
            int bx = points[i].Value, by = points[i].Scaling, ex = points[i + 1].Value, ey = points[i + 1].Scaling;
            int dx = ex - bx, dy = ey - by;
            int delta = dy * ((0x10000 + (dx >> 1)) / dx);
            for (int x = 0, dd = 0x8000; x < dx; x++)
            {
                scaling[(bx + x) << shiftX] = by + (dd >> 16);
                dd += delta;
            }
        }

        int n = points[num - 1].Value << shiftX;
        scaling.AsSpan(n, size - n).Fill(points[num - 1].Scaling);
        if (shiftX > 0)
        {
            int pad = 1 << shiftX, rnd = pad >> 1;
            for (int i = 0; i < num - 1; i++)
            {
                int bx = points[i].Value << shiftX, ex = points[i + 1].Value << shiftX;
                for (int x = 0; x < ex - bx; x += pad)
                {
                    int range = scaling[bx + x + pad] - scaling[bx + x];
                    for (int k = 1, r = rnd; k < pad; k++)
                    {
                        r += range;
                        scaling[bx + x + k] = scaling[bx + x] + (r >> shiftX);
                    }
                }
            }
        }

        return scaling;
    }

    /// <summary>Where in the grain template the block's sample (0, 0) is.</summary>
    private static int SampleLutAt(int[,] offsets, int subx, int suby, int bx, int by)
    {
        int randval = offsets[bx, by];
        int offx = 3 + ((2 >> subx) * (3 + (randval >> 4)));
        int offy = 3 + ((2 >> suby) * (3 + (randval & 0xF)));
        return ((offy + ((BlockSize >> suby) * by)) * GrainWidth) + offx + ((BlockSize >> subx) * bx);
    }

    private static void ApplyLuma(ushort[] dst, ushort[] src, int rowStart, int stride, Data d, int pw, int[] scaling, int[] lut, int bh, int rowNum, int bd)
    {
        int rows = 1 + (d.OverlapFlag && rowNum > 0 ? 1 : 0);
        int grainMin = -(128 << (bd - 8)), grainMax = (128 << (bd - 8)) - 1;
        int minValue = d.ClipToRestrictedRange ? 16 << (bd - 8) : 0;
        int maxValue = d.ClipToRestrictedRange ? 235 << (bd - 8) : (1 << bd) - 1;
        Span<uint> seed = stackalloc uint[2];
        for (int i = 0; i < rows; i++)
        {
            seed[i] = (uint)d.Seed;
            seed[i] ^= (uint)(((((rowNum - i) * 37) + 178) & 0xFF) << 8);
            seed[i] ^= (uint)((((rowNum - i) * 173) + 105) & 0xFF);
        }

        int[,] offsets = new int[2, 2];
        ReadOnlySpan<int> w0 = [27, 17], w1 = [17, 27];
        for (int bx = 0; bx < pw; bx += BlockSize)
        {
            int bw = Math.Min(BlockSize, pw - bx);
            if (d.OverlapFlag && bx != 0)
            {
                for (int i = 0; i < rows; i++)
                {
                    offsets[1, i] = offsets[0, i];
                }
            }

            for (int i = 0; i < rows; i++)
            {
                offsets[0, i] = RandomNumber(8, ref seed[i]);
            }

            int ystart = d.OverlapFlag && rowNum != 0 ? Math.Min(2, bh) : 0;
            int xstart = d.OverlapFlag && bx != 0 ? Math.Min(2, bw) : 0;
            int lutBase = SampleLutAt(offsets, 0, 0, 0, 0);
            // Where the blocks the overlaps blend with start (read only where there are overlaps).
            int lutLeft = SampleLutAt(offsets, 0, 0, 1, 0), lutTop = SampleLutAt(offsets, 0, 0, 0, 1), lutTopLeft = SampleLutAt(offsets, 0, 0, 1, 1);
            int shift = d.ScalingShift;
            for (int y = ystart; y < bh; y++)
            {
                ReadOnlySpan<int> grains = lut.AsSpan(lutBase + (y * GrainWidth) + xstart, bw - xstart);
                Span<ushort> line = dst.AsSpan(rowStart + (y * stride) + bx + xstart, bw - xstart);
                ReadOnlySpan<ushort> source = src.AsSpan(rowStart + (y * stride) + bx + xstart, line.Length);
                LumaRow(grains, source, line, scaling, shift, minValue, maxValue);
            }

            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < (y < ystart ? bw : xstart); x++)
                {
                    int grain = lut[lutBase + (y * GrainWidth) + x];
                    if (x < xstart)
                    {
                        int old = lut[lutLeft + (y * GrainWidth) + x];
                        grain = Math.Clamp(Round2((old * (x == 0 ? w0 : w1)[0]) + (grain * (x == 0 ? w0 : w1)[1]), 5), grainMin, grainMax);
                    }

                    if (y < ystart)
                    {
                        int top = lut[lutTop + (y * GrainWidth) + x];
                        if (x < xstart)
                        {
                            int oldTop = lut[lutTopLeft + (y * GrainWidth) + x];
                            top = Math.Clamp(Round2((oldTop * (x == 0 ? w0 : w1)[0]) + (top * (x == 0 ? w0 : w1)[1]), 5), grainMin, grainMax);
                        }

                        grain = Math.Clamp(Round2((top * (y == 0 ? w0 : w1)[0]) + (grain * (y == 0 ? w0 : w1)[1]), 5), grainMin, grainMax);
                    }

                    int at = rowStart + (y * stride) + bx + x;
                    int noise = Round2(scaling[src[at]] * grain, d.ScalingShift);
                    dst[at] = (ushort)Math.Clamp(src[at] + noise, minValue, maxValue);
                }
            }
        }
    }

    /// <summary>Adds grain to a row of luma: each sample plus Round2(scaling[sample] * grain, shift), clamped; eight a step on AVX2.</summary>
    private static unsafe void LumaRow(ReadOnlySpan<int> grains, ReadOnlySpan<ushort> source, Span<ushort> line, int[] scaling, int shift, int minValue, int maxValue)
    {
        int half = (1 << shift) >> 1;
        int x = 0;
        if (Avx2.IsSupported)
        {
            ref int g0 = ref MemoryMarshal.GetReference(grains);
            ref ushort s0 = ref MemoryMarshal.GetReference(source), d0 = ref MemoryMarshal.GetReference(line);
            Vector256<int> halfV = Vector256.Create(half), minV = Vector256.Create(minValue), maxV = Vector256.Create(maxValue);
            fixed (int* table = scaling)
            {
                for (; x + 8 <= line.Length; x += 8)
                {
                    Vector256<int> value = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref s0, (nuint)x));
                    Vector256<int> noise = Vector256.ShiftRightArithmetic((Avx2.GatherVector256(table, value, 4) * Vector256.LoadUnsafe(ref g0, (nuint)x)) + halfV, shift);
                    Vector256<int> result = Vector256.Min(Vector256.Max(value + noise, minV), maxV);
                    Sse41.PackUnsignedSaturate(result.GetLower(), result.GetUpper()).StoreUnsafe(ref d0, (nuint)x);
                }
            }
        }

        for (; x < line.Length; x++)
        {
            int value = source[x];
            line[x] = (ushort)Math.Clamp(value + (((scaling[value] * grains[x]) + half) >> shift), minValue, maxValue);
        }
    }

    private static void ApplyChroma(ushort[] dst, ushort[] src, int rowStart, int stride, Data d, int pw, int[] scaling, int[] lut, int bh, int rowNum,
        ushort[] luma, int lumaRow, int lumaStride, int uv, bool isIdentity, int sx, int sy, int bd)
    {
        int rows = 1 + (d.OverlapFlag && rowNum > 0 ? 1 : 0);
        int grainMin = -(128 << (bd - 8)), grainMax = (128 << (bd - 8)) - 1;
        int minValue = d.ClipToRestrictedRange ? 16 << (bd - 8) : 0;
        int maxValue = d.ClipToRestrictedRange ? (isIdentity ? 235 : 240) << (bd - 8) : (1 << bd) - 1;
        int pixelMax = (1 << bd) - 1;
        Span<uint> seed = stackalloc uint[2];
        for (int i = 0; i < rows; i++)
        {
            seed[i] = (uint)d.Seed;
            seed[i] ^= (uint)(((((rowNum - i) * 37) + 178) & 0xFF) << 8);
            seed[i] ^= (uint)((((rowNum - i) * 173) + 105) & 0xFF);
        }

        int[,] offsets = new int[2, 2];
        for (int bx = 0; bx < pw; bx += BlockSize >> sx)
        {
            int bw = Math.Min(BlockSize >> sx, pw - bx);
            if (d.OverlapFlag && bx != 0)
            {
                for (int i = 0; i < rows; i++)
                {
                    offsets[1, i] = offsets[0, i];
                }
            }

            for (int i = 0; i < rows; i++)
            {
                offsets[0, i] = RandomNumber(8, ref seed[i]);
            }

            int ystart = d.OverlapFlag && rowNum != 0 ? Math.Min(2 >> sy, bh) : 0;
            int xstart = d.OverlapFlag && bx != 0 ? Math.Min(2 >> sx, bw) : 0;
            int lutBase = SampleLutAt(offsets, sx, sy, 0, 0);
            // Where the blocks the overlaps blend with start (read only where there are overlaps).
            int lutLeft = SampleLutAt(offsets, sx, sy, 1, 0), lutTop = SampleLutAt(offsets, sx, sy, 0, 1), lutTopLeft = SampleLutAt(offsets, sx, sy, 1, 1);
            for (int y = ystart; y < bh; y++)
            {
                // As Add does for each sample, over a row's spans: the checks once a row, and a loop for each way of getting the value.
                ReadOnlySpan<int> grains = lut.AsSpan(lutBase + (y * GrainWidth) + xstart, bw - xstart);
                ReadOnlySpan<ushort> source = src.AsSpan(rowStart + (y * stride) + bx + xstart, bw - xstart);
                Span<ushort> line = dst.AsSpan(rowStart + (y * stride) + bx + xstart, bw - xstart);
                ReadOnlySpan<ushort> lumaLine = luma.AsSpan(lumaRow + ((y << sy) * lumaStride) + ((bx + xstart) << sx), (bw - xstart) << sx);
                AddRow(grains, source, line, lumaLine, sx, d.ChromaScalingFromLuma, d.UvLumaMult[uv], d.UvMult[uv], d.UvOffset[uv] * (1 << (bd - 8)),
                    pixelMax, scaling, d.ScalingShift, minValue, maxValue);
            }

            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < (y < ystart ? bw : xstart); x++)
                {
                    int grain = lut[lutBase + (y * GrainWidth) + x];
                    if (x < xstart)
                    {
                        int old = lut[lutLeft + (y * GrainWidth) + x];
                        grain = Math.Clamp(Round2((old * Weight(sx, x, 0)) + (grain * Weight(sx, x, 1)), 5), grainMin, grainMax);
                    }

                    if (y < ystart)
                    {
                        int top = lut[lutTop + (y * GrainWidth) + x];
                        if (x < xstart)
                        {
                            int oldTop = lut[lutTopLeft + (y * GrainWidth) + x];
                            top = Math.Clamp(Round2((oldTop * Weight(sx, x, 0)) + (top * Weight(sx, x, 1)), 5), grainMin, grainMax);
                        }

                        grain = Math.Clamp(Round2((top * Weight(sy, y, 0)) + (grain * Weight(sy, y, 1)), 5), grainMin, grainMax);
                    }

                    Add(grain, x, y);
                }
            }

            void Add(int grain, int x, int y)
            {
                int lumaAt = lumaRow + ((y << sy) * lumaStride) + ((bx + x) << sx);
                int average = luma[lumaAt];
                if (sx != 0)
                {
                    average = (average + luma[lumaAt + 1] + 1) >> 1;
                }

                int at = rowStart + (y * stride) + bx + x;
                int value = average;
                if (!d.ChromaScalingFromLuma)
                {
                    int combined = (average * d.UvLumaMult[uv]) + (src[at] * d.UvMult[uv]);
                    value = Math.Clamp((combined >> 6) + (d.UvOffset[uv] * (1 << (bd - 8))), 0, pixelMax);
                }

                int noise = Round2(scaling[value] * grain, d.ScalingShift);
                dst[at] = (ushort)Math.Clamp(src[at] + noise, minValue, maxValue);
            }
        }

        static void AddRow(ReadOnlySpan<int> grains, ReadOnlySpan<ushort> source, Span<ushort> line, ReadOnlySpan<ushort> lumaLine, int sx,
            bool fromLuma, int lumaMult, int mult, int offset, int pixelMax, int[] scaling, int shift, int minValue, int maxValue)
        {
            int half = (1 << shift) >> 1;
            int x = 0;
            if (Avx2.IsSupported)
            {
                x = AddRowVector(grains, source, line, lumaLine, sx, fromLuma, lumaMult, mult, offset, pixelMax, scaling, shift, minValue, maxValue);
            }

            for (; x < line.Length; x++)
            {
                int average = sx != 0 ? (lumaLine[2 * x] + lumaLine[(2 * x) + 1] + 1) >> 1 : lumaLine[x];
                int value = fromLuma ? average : Math.Clamp((((average * lumaMult) + (source[x] * mult)) >> 6) + offset, 0, pixelMax);
                line[x] = (ushort)Math.Clamp(source[x] + (((scaling[value] * grains[x]) + half) >> shift), minValue, maxValue);
            }
        }

        // Eight samples a step: the luma average, the value, the scaling gathered from its table, then as the scalar loop.
        static unsafe int AddRowVector(ReadOnlySpan<int> grains, ReadOnlySpan<ushort> source, Span<ushort> line, ReadOnlySpan<ushort> lumaLine, int sx,
            bool fromLuma, int lumaMult, int mult, int offset, int pixelMax, int[] scaling, int shift, int minValue, int maxValue)
        {
            ref int g0 = ref MemoryMarshal.GetReference(grains);
            ref ushort s0 = ref MemoryMarshal.GetReference(source), l0 = ref MemoryMarshal.GetReference(lumaLine), d0 = ref MemoryMarshal.GetReference(line);
            Vector256<int> halfV = Vector256.Create((1 << shift) >> 1), minV = Vector256.Create(minValue), maxV = Vector256.Create(maxValue);
            Vector256<int> lumaMultV = Vector256.Create(lumaMult), multV = Vector256.Create(mult), offsetV = Vector256.Create(offset);
            Vector256<int> pixelMaxV = Vector256.Create(pixelMax), low = Vector256.Create(0xFFFF), one = Vector256.Create(1);
            int x = 0;
            fixed (int* table = scaling)
            {
                for (; x + 8 <= line.Length; x += 8)
                {
                    Vector256<int> average;
                    if (sx != 0)
                    {
                        // Each 32-bit lane holds a pair of luma samples.
                        Vector256<int> pairs = Vector256.LoadUnsafe(ref Unsafe.As<ushort, int>(ref Unsafe.Add(ref l0, 2 * x)));
                        average = Vector256.ShiftRightArithmetic((pairs & low) + Vector256.ShiftRightLogical(pairs, 16) + one, 1);
                    }
                    else
                    {
                        average = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref l0, (nuint)x));
                    }

                    Vector256<int> src = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref s0, (nuint)x));
                    Vector256<int> value = fromLuma ? average
                        : Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic((average * lumaMultV) + (src * multV), 6) + offsetV, Vector256<int>.Zero), pixelMaxV);
                    Vector256<int> noise = Vector256.ShiftRightArithmetic((Avx2.GatherVector256(table, value, 4) * Vector256.LoadUnsafe(ref g0, (nuint)x)) + halfV, shift);
                    Vector256<int> result = Vector256.Min(Vector256.Max(src + noise, minV), maxV);
                    Sse41.PackUnsignedSaturate(result.GetLower(), result.GetUpper()).StoreUnsafe(ref d0, (nuint)x);
                }
            }

            return x;
        }

        // w[sub][off][i]: { { 27, 17 }, { 17, 27 } } without subsampling, { 23, 22 } with it.
        static int Weight(int sub, int offset, int i) => sub != 0 ? (i == 0 ? 23 : 22) : offset == 0 ? (i == 0 ? 27 : 17) : (i == 0 ? 17 : 27);
    }

    /// <summary>The grain parameters as dav1d holds them: coefficients and multipliers centred, shifts with their offsets added.</summary>
    private sealed class Data
    {
        internal Data(Av1FilmGrain g)
        {
            Seed = g.GrainSeed;
            YPoints = g.PointsY;
            UvPoints = [g.PointsCb, g.PointsCr];
            ChromaScalingFromLuma = g.ChromaScalingFromLuma;
            ScalingShift = g.GrainScalingMinus8 + 8;
            ArCoeffLag = g.ArCoeffLag;
            // Coefficients a frame doesn't code (luma's without luma points, a chroma plane's without its points) are 0.
            int positions = 2 * g.ArCoeffLag * (g.ArCoeffLag + 1);
            ArCoeffsY = Coefficients(g.ArCoeffsYPlus128, positions);
            ArCoeffsUv = [Coefficients(g.ArCoeffsCbPlus128, positions + 1), Coefficients(g.ArCoeffsCrPlus128, positions + 1)];
            ArCoeffShift = g.ArCoeffShiftMinus6 + 6;
            GrainScaleShift = g.GrainScaleShift;
            UvMult = [g.CbMult - 128, g.CrMult - 128];
            UvLumaMult = [g.CbLumaMult - 128, g.CrLumaMult - 128];
            UvOffset = [g.CbOffset - 256, g.CrOffset - 256];
            OverlapFlag = g.OverlapFlag;
            ClipToRestrictedRange = g.ClipToRestrictedRange;
        }

        private static int[] Coefficients(int[] plus128, int count)
        {
            int[] coeffs = new int[count];
            for (int i = 0; i < Math.Min(plus128.Length, count); i++)
            {
                coeffs[i] = plus128[i] - 128;
            }

            return coeffs;
        }

        internal int Seed { get; }

        internal (int Value, int Scaling)[] YPoints { get; }

        internal (int Value, int Scaling)[][] UvPoints { get; }

        internal bool ChromaScalingFromLuma { get; }

        internal int ScalingShift { get; }

        internal int ArCoeffLag { get; }

        internal int[] ArCoeffsY { get; }

        internal int[][] ArCoeffsUv { get; }

        internal int ArCoeffShift { get; }

        internal int GrainScaleShift { get; }

        internal int[] UvMult { get; }

        internal int[] UvLumaMult { get; }

        internal int[] UvOffset { get; }

        internal bool OverlapFlag { get; }

        internal bool ClipToRestrictedRange { get; }
    }
}
