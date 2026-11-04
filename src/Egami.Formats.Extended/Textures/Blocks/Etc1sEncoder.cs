using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>One 2D image an ETC1S encode takes: RGBA pixels in rows, and the mip level it belongs to.</summary>
internal sealed record Etc1sImage(byte[] Rgba, int Width, int Height, int Level);

/// <summary>What an ETC1S encode makes: the BasisLZ global data and each mip level's slices, as KTX 2 stores them.</summary>
internal sealed record Etc1sEncoded(byte[] GlobalData, byte[][] Levels, int EndpointCount, int SelectorCount);

/// <summary>
/// Encodes images to Basis Universal ETC1S in BasisLZ form (KTX 2.0 Specification, Appendices C and D) at a quality of 1 to 255
/// and an effort of 0 to 6. Every block of every image (and of the alpha channel, as gray blocks, when there is alpha) shares
/// one codebook of endpoints (a 5-bit colour and an intensity table) and one of selectors (16 two-bit modifier choices); the
/// quality sets the selector codebook's size, from 16 entries at 1 to 16128 at 255, and the endpoint codebook gets three times
/// as many (at most 16128). Endpoints are found by vector quantization of each block's mean colour and spread (splitting the
/// cluster with the largest error), fitted to their blocks' pixels, and refined by moving each block to the endpoint near its
/// mean colour that codes it best and fitting again. Below quality 255 a block then takes the endpoint of its left, upper or
/// upper left neighbour, which BasisLZ codes as a cheap prediction, when that costs little more error (up to 1.5 times at
/// quality 128 and below). Selectors are quantized from each block's best modifiers under its endpoint, weighting each block by
/// its variance, then refitted texel by texel to their blocks' pixels while every block moves to the selector with the least
/// real error (a selector's error is four lookups in tables of the block's per-row errors; with a large codebook a block
/// searches only the groups of similar selectors whose representatives suit it best). Last the endpoints are refitted to the
/// settled selectors. The slices then code each block's two indices with BasisLZ's predictions, history buffer and run
/// lengths, and the Huffman tables are built from the counts. Every step runs in a fixed order or sums integers, so the output
/// does not depend on the number of threads.
/// </summary>
internal static class Etc1sEncoder
{
    private const int MaxCodebook = 16128;
    private const int HistorySize = 64;
    private const int PredRepeatSymbol = 256;
    private const int GroupSize = 32;

    /// <summary>The endpoint codebook's size as a multiple of the selector codebook's (at most <see cref="MaxCodebook"/>).</summary>
    private const int EndpointsPerSelector = 3;

    /// <summary>Returns the codebook size quality <paramref name="quality"/> (1 to 255) asks for, before the number of blocks caps it.</summary>
    public static int CodebookSize(int quality) => (int)Math.Min(MaxCodebook, Math.Round(Math.Pow(2, 4 + (9.98 * (Math.Clamp(quality, 1, 255) - 1) / 254))));

    /// <summary>
    /// Encodes <paramref name="images"/>, in KTX 2's order (mip level, then layer, face and depth slice), to ETC1S; with
    /// <paramref name="alpha"/> each image also gets an alpha slice. <paramref name="threads"/> caps the threads (−1: no cap).
    /// </summary>
    public static Etc1sEncoded Encode(IReadOnlyList<Etc1sImage> images, bool alpha, int quality, int effort, int threads = -1)
    {
        ParallelOptions options = new() { MaxDegreeOfParallelism = threads };
        effort = Math.Clamp(effort, 0, 6);
        List<(int BlocksX, int BlocksY, int Rgb, int Alpha)> layout = [];
        int total = 0;
        foreach (Etc1sImage image in images)
        {
            int bx = (image.Width + 3) / 4;
            int by = (image.Height + 3) / 4;
            int rgb = total;
            total += bx * by;
            int a = -1;
            if (alpha)
            {
                a = total;
                total += bx * by;
            }

            layout.Add((bx, by, rgb, a));
        }

        // Every block's pixels, 48 bytes of RGB (an alpha block as gray), edges repeated into partial blocks.
        byte[] pixels = new byte[total * 48];
        for (int i = 0; i < images.Count; i++)
        {
            Gather(images[i], layout[i].BlocksX, layout[i].BlocksY, layout[i].Rgb, false, pixels);
            if (alpha)
            {
                Gather(images[i], layout[i].BlocksX, layout[i].BlocksY, layout[i].Alpha, true, pixels);
            }
        }

        List<(int First, int BlocksX, int BlocksY)> slices = [];
        foreach ((int bx, int by, int rgb, int a) in layout)
        {
            slices.Add((rgb, bx, by));
            if (a >= 0)
            {
                slices.Add((a, bx, by));
            }
        }

        Codebooks books = Quantize(pixels, total, slices, Math.Clamp(quality, 1, 255), effort, options);
        return Write(books, layout, images);
    }

    /// <summary>The codebooks and each block's two indices.</summary>
    private sealed record Codebooks(Endpoint[] Endpoints, uint[] Selectors, int[] BlockEndpoint, int[] BlockSelector);

    /// <summary>An ETC1S endpoint: a colour of 5 bits a channel and an intensity table.</summary>
    private readonly record struct Endpoint(byte R, byte G, byte B, byte Inten);

    private static void Gather(Etc1sImage image, int blocksX, int blocksY, int first, bool alpha, byte[] pixels)
    {
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int at = (first + (by * blocksX) + bx) * 48;
                for (int t = 0; t < 16; t++)
                {
                    int x = Math.Min((bx * 4) + (t & 3), image.Width - 1);
                    int y = Math.Min((by * 4) + (t >> 2), image.Height - 1);
                    int p = ((y * image.Width) + x) * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        pixels[at + (t * 3) + c] = alpha ? image.Rgba[p + 3] : image.Rgba[p + c];
                    }
                }
            }
        }
    }

    /// <summary>Fills the four colours of an endpoint, 3 values each: selectors 0 to 3 are −b, −a, +a and +b.</summary>
    private static void Palette(Endpoint e, Span<int> palette)
    {
        int a = Etc.Modifiers[e.Inten * 2];
        int b = Etc.Modifiers[(e.Inten * 2) + 1];
        int r = (e.R << 3) | (e.R >> 2);
        int g = (e.G << 3) | (e.G >> 2);
        int bl = (e.B << 3) | (e.B >> 2);
        Span<int> mods = [-b, -a, a, b];
        for (int s = 0; s < 4; s++)
        {
            palette[s * 3] = Math.Clamp(r + mods[s], 0, 255);
            palette[(s * 3) + 1] = Math.Clamp(g + mods[s], 0, 255);
            palette[(s * 3) + 2] = Math.Clamp(bl + mods[s], 0, 255);
        }
    }

    private static int TexelError(ReadOnlySpan<byte> px, int t, ReadOnlySpan<int> palette, int s)
    {
        int dr = px[t * 3] - palette[s * 3];
        int dg = px[(t * 3) + 1] - palette[(s * 3) + 1];
        int db = px[(t * 3) + 2] - palette[(s * 3) + 2];
        return (dr * dr) + (dg * dg) + (db * db);
    }

    /// <summary>Returns a block's error under an endpoint with each texel's best selector, and those selectors (2 bits each, texel 0 lowest).</summary>
    private static long BestSelectors(ReadOnlySpan<byte> px, ReadOnlySpan<int> palette, out uint selectors)
    {
        long error = 0;
        selectors = 0;
        for (int t = 0; t < 16; t++)
        {
            int best = int.MaxValue;
            int pick = 0;
            for (int s = 0; s < 4; s++)
            {
                int e = TexelError(px, t, palette, s);
                if (e < best)
                {
                    best = e;
                    pick = s;
                }
            }

            error += best;
            selectors |= (uint)pick << (t * 2);
        }

        return error;
    }

    /// <summary>Returns a block's error under an endpoint with fixed selectors.</summary>
    private static long FixedError(ReadOnlySpan<byte> px, ReadOnlySpan<int> palette, uint selectors)
    {
        long error = 0;
        for (int t = 0; t < 16; t++)
        {
            error += TexelError(px, t, palette, (int)((selectors >> (t * 2)) & 3));
        }

        return error;
    }

    private static Codebooks Quantize(byte[] pixels, int blocks, List<(int First, int BlocksX, int BlocksY)> slices, int quality, int effort, ParallelOptions options)
    {
        int size = CodebookSize(quality);
        Plan plan = Plan.For(effort);

        // Endpoint features: the mean colour (as sums of 16 texels) and the spread, as the small modifier of the block's best
        // table; and the mean colour at 5 bits, where the search for a block's endpoint starts.
        int[] features = new int[blocks * 4];
        int[] meanColor = new int[blocks];
        Parallel.For(0, blocks, options, i =>
        {
            ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
            int r = 0, g = 0, b = 0;
            for (int t = 0; t < 48; t += 3)
            {
                r += px[t];
                g += px[t + 1];
                b += px[t + 2];
            }

            Span<int> palette = stackalloc int[12];
            long best = long.MaxValue;
            int inten = 0;
            for (int t = 0; t < 8; t++)
            {
                Palette(new Endpoint(Q5(r), Q5(g), Q5(b), (byte)t), palette);
                long e = BestSelectors(px, palette, out _);
                if (e < best)
                {
                    best = e;
                    inten = t;
                }
            }

            features[i * 4] = r;
            features[(i * 4) + 1] = g;
            features[(i * 4) + 2] = b;
            features[(i * 4) + 3] = Etc.Modifiers[inten * 2] * 32;
            meanColor[i] = (Q5(r) << 10) | (Q5(g) << 5) | Q5(b);
        });

        int endpointCount = Math.Min(Math.Min(MaxCodebook, size * EndpointsPerSelector), blocks);
        int[] blockEndpoint = Vq(features, null, blocks, 4, endpointCount, plan.EndpointLloyd, options, out int clusters);
        Endpoint[] endpoints = new Endpoint[clusters];
        FitEndpoints(pixels, blockEndpoint, endpoints, plan.Steps, plan.IntenReach, options);

        // Move each block to the endpoint near its mean colour that codes it best, and refit the endpoints to their new blocks.
        for (int round = 0; round < plan.EndpointRounds; round++)
        {
            if (!ReassignEndpoints(pixels, meanColor, endpoints, blockEndpoint, plan.Reach, options))
            {
                break;
            }

            FitEndpoints(pixels, blockEndpoint, endpoints, plan.Steps, plan.IntenReach, options);
        }

        // Rate-distortion: from quality 128 down, a block shares a neighbour's endpoint at up to 1.5 times its own error; above
        // it the allowance shrinks to none at 255.
        double rdo = 1 + (0.5 * Math.Min(1, (255 - quality) / 127.0));
        if (rdo > 1)
        {
            ShareEndpoints(pixels, endpoints, blockEndpoint, slices, rdo, options);
        }

        // Each block's best selectors under its endpoint, quantized to the selector codebook.
        uint[] ideal = new uint[blocks];
        Parallel.For(0, blocks, options, i =>
        {
            Span<int> palette = stackalloc int[12];
            Palette(endpoints[blockEndpoint[i]], palette);
            BestSelectors(pixels.AsSpan(i * 48, 48), palette, out ideal[i]);
        });

        // Selector training weights: the block's variance, so the codebook spends its entries where selectors matter.
        int[] selectorWeights = new int[blocks];
        Parallel.For(0, blocks, options, i =>
        {
            ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
            long squares = 0, r = 0, g = 0, b = 0;
            for (int t = 0; t < 48; t += 3)
            {
                r += px[t];
                g += px[t + 1];
                b += px[t + 2];
                squares += (px[t] * px[t]) + (px[t + 1] * px[t + 1]) + (px[t + 2] * px[t + 2]);
            }

            long variance = squares - (((r * r) + (g * g) + (b * b)) / 16);
            selectorWeights[i] = (int)Math.Clamp(variance / 64, 1, 4096);
        });

        int[] selectorFeatures = new int[blocks * 16];
        for (int i = 0; i < blocks; i++)
        {
            for (int t = 0; t < 16; t++)
            {
                selectorFeatures[(i * 16) + t] = (int)((ideal[i] >> (t * 2)) & 3) * 16;
            }
        }

        int selectorCount = Math.Min(size, blocks);
        int[] blockSelector = Vq(selectorFeatures, selectorWeights, blocks, 16, selectorCount, 0, options, out int selectorClusters);
        uint[] selectors = new uint[selectorClusters];

        // Refit selectors texel by texel to their blocks, then move each block to the selector that codes it best, and repeat.
        for (int round = 0; round < plan.SelectorRounds; round++)
        {
            List<int>[] members = Members(blockSelector, selectorClusters);
            Parallel.For(0, selectorClusters, options, k => selectors[k] = FitSelector(pixels, members[k], blockEndpoint, endpoints, ideal, selectors[k]));
            ReassignSelectors(pixels, endpoints, blockEndpoint, selectors, blockSelector, plan.Probe, options);
        }

        // With the selectors settled, refit each endpoint to them.
        if (plan.Refit)
        {
            List<int>[] members = Members(blockEndpoint, endpoints.Length);
            Parallel.For(0, endpoints.Length, options, k => endpoints[k] = RefitEndpoint(pixels, members[k], endpoints[k], blockSelector, selectors, plan.Tables));
            if (plan.FinalReassign)
            {
                ReassignSelectors(pixels, endpoints, blockEndpoint, selectors, blockSelector, plan.Probe, options);
            }
        }

        return Compact(endpoints, selectors, blockEndpoint, blockSelector);
    }

    /// <summary>How hard each effort searches.</summary>
    private readonly record struct Plan(int EndpointLloyd, int Steps, int IntenReach, int EndpointRounds, int Reach, int SelectorRounds, int Probe, bool Refit, int Tables, bool FinalReassign)
    {
        public static Plan For(int effort) => effort switch
        {
            0 => new(0, 0, 0, 0, 0, 1, 2, false, 0, false),
            1 => new(0, 0, 0, 1, 1, 1, 2, false, 0, false),
            2 => new(0, 0, 0, 1, 1, 1, 3, true, 1, false),
            3 => new(0, 1, 0, 1, 1, 2, 3, true, 2, false),
            4 => new(0, 1, 0, 1, 1, 2, 4, true, 2, true),
            5 => new(1, 3, 1, 2, 2, 3, 8, true, 3, true),
            _ => new(2, 4, 1, 3, 2, 4, 0, true, 4, true),
        };
    }

    /// <summary>Fits every endpoint to its blocks' pixels; an endpoint with no blocks stays as it is.</summary>
    private static void FitEndpoints(byte[] pixels, int[] blockEndpoint, Endpoint[] endpoints, int steps, int intenReach, ParallelOptions options)
    {
        List<int>[] members = Members(blockEndpoint, endpoints.Length);
        Parallel.For(0, endpoints.Length, options, k =>
        {
            if (members[k].Count > 0)
            {
                endpoints[k] = FitEndpoint(pixels, members[k], steps, intenReach);
            }
        });
    }

    /// <summary>
    /// Moves each block to the endpoint with the least error (each texel taking its best selector) among those whose colour lies
    /// within <paramref name="reach"/> steps of the block's mean colour in every channel, keeping its own on a tie. Returns
    /// whether any block moved.
    /// </summary>
    private static bool ReassignEndpoints(byte[] pixels, int[] meanColor, Endpoint[] endpoints, int[] blockEndpoint, int reach, ParallelOptions options)
    {
        // The endpoints by colour, as lists in colour order, and their palettes.
        int[] start = new int[(32 * 32 * 32) + 1];
        foreach (Endpoint e in endpoints)
        {
            start[((e.R << 10) | (e.G << 5) | e.B) + 1]++;
        }

        for (int c = 0; c < 32 * 32 * 32; c++)
        {
            start[c + 1] += start[c];
        }

        int[] byColor = new int[endpoints.Length];
        int[] fill = start[..^1];
        int[] palettes = new int[endpoints.Length * 12];
        for (int k = 0; k < endpoints.Length; k++)
        {
            Endpoint e = endpoints[k];
            byColor[fill[(e.R << 10) | (e.G << 5) | e.B]++] = k;
            Palette(e, palettes.AsSpan(k * 12, 12));
        }

        int moved = 0;
        Parallel.For(0, blockEndpoint.Length, options, () => 0, (i, _, local) =>
        {
            ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
            int current = blockEndpoint[i];
            int pick = current;
            long best = BestError(px, palettes.AsSpan(current * 12, 12), long.MaxValue);
            int mean = meanColor[i];
            int r0 = mean >> 10, g0 = (mean >> 5) & 31, b0 = mean & 31;
            for (int r = Math.Max(0, r0 - reach); r <= Math.Min(31, r0 + reach) && best > 0; r++)
            {
                for (int g = Math.Max(0, g0 - reach); g <= Math.Min(31, g0 + reach); g++)
                {
                    for (int b = Math.Max(0, b0 - reach); b <= Math.Min(31, b0 + reach); b++)
                    {
                        int cell = (r << 10) | (g << 5) | b;
                        for (int j = start[cell]; j < start[cell + 1]; j++)
                        {
                            int k = byColor[j];
                            long e = BestError(px, palettes.AsSpan(k * 12, 12), best);
                            if (e < best)
                            {
                                best = e;
                                pick = k;
                            }
                        }
                    }
                }
            }

            blockEndpoint[i] = pick;
            return local + (pick != current ? 1 : 0);
        }, local => Interlocked.Add(ref moved, local));

        return moved > 0;
    }

    /// <summary>
    /// Rate-distortion pass over the endpoints, in each slice's raster order: a block takes the endpoint of the block to its
    /// left, above or above left (which code as a prediction rather than a delta) when that costs at most
    /// <paramref name="threshold"/> times its own endpoint's error.
    /// </summary>
    private static void ShareEndpoints(byte[] pixels, Endpoint[] endpoints, int[] blockEndpoint, List<(int First, int BlocksX, int BlocksY)> slices, double threshold, ParallelOptions options)
    {
        int[] palettes = new int[endpoints.Length * 12];
        for (int k = 0; k < endpoints.Length; k++)
        {
            Palette(endpoints[k], palettes.AsSpan(k * 12, 12));
        }

        Parallel.For(0, slices.Count, options, n =>
        {
            (int first, int blocksX, int blocksY) = slices[n];
            for (int y = 0; y < blocksY; y++)
            {
                for (int x = 0; x < blocksX; x++)
                {
                    int i = first + (y * blocksX) + x;
                    ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
                    int own = blockEndpoint[i];
                    long limit = (long)(BestError(px, palettes.AsSpan(own * 12, 12), long.MaxValue) * threshold) + 1;
                    long best = limit;
                    int pick = own;
                    for (int c = 0; c < 3; c++)
                    {
                        int nx = x - (c == 1 ? 0 : 1);
                        int ny = y - (c == 0 ? 0 : 1);
                        if (nx < 0 || ny < 0)
                        {
                            continue;
                        }

                        int k = blockEndpoint[first + (ny * blocksX) + nx];
                        if (k == own)
                        {
                            pick = own;
                            break;
                        }

                        long e = BestError(px, palettes.AsSpan(k * 12, 12), best);
                        if (e < best)
                        {
                            best = e;
                            pick = k;
                        }
                    }

                    blockEndpoint[i] = pick;
                }
            }
        });
    }

    /// <summary>Returns a block's error with each texel's best selector, stopping once it reaches <paramref name="limit"/>.</summary>
    private static long BestError(ReadOnlySpan<byte> px, ReadOnlySpan<int> palette, long limit)
    {
        long error = 0;
        for (int t = 0; t < 48 && error < limit; t += 3)
        {
            int r = px[t], g = px[t + 1], b = px[t + 2];
            int best = int.MaxValue;
            for (int s = 0; s < 12; s += 3)
            {
                int dr = r - palette[s], dg = g - palette[s + 1], db = b - palette[s + 2];
                best = Math.Min(best, (dr * dr) + (dg * dg) + (db * db));
            }

            error += best;
        }

        return error;
    }

    /// <summary>
    /// Moves each block to the selector of the codebook with the least error under its endpoint, keeping its own on a tie. A
    /// block's errors are first summed per row of four texels for all 256 selector bytes, so a selector costs four lookups.
    /// With <paramref name="probe"/> above 0 and a large codebook, the codebook is grouped by vector quantization into groups of
    /// about 32, each with a representative (each texel's rounded mean selector), and a block searches only the
    /// <paramref name="probe"/> groups whose representatives suit it best.
    /// </summary>
    private static void ReassignSelectors(byte[] pixels, Endpoint[] endpoints, int[] blockEndpoint, uint[] selectors, int[] blockSelector, int probe, ParallelOptions options)
    {
        int groups = probe > 0 ? selectors.Length / GroupSize : 0;
        int[] start = [];
        int[] order = [];
        uint[] representatives = [];
        if (groups > probe)
        {
            int[] features = new int[selectors.Length * 16];
            for (int k = 0; k < selectors.Length; k++)
            {
                for (int t = 0; t < 16; t++)
                {
                    features[(k * 16) + t] = (int)((selectors[k] >> (t * 2)) & 3) * 16;
                }
            }

            int[] groupOf = Vq(features, null, selectors.Length, 16, groups, 2, options, out groups);
            if (groups <= probe)
            {
                groups = 0;
            }
            start = new int[groups + 1];
            foreach (int g in groupOf)
            {
                start[g + 1]++;
            }

            for (int g = 0; g < groups; g++)
            {
                start[g + 1] += start[g];
            }

            order = new int[selectors.Length];
            int[] fill = start[..^1];
            int[] sums = new int[groups * 16];
            for (int k = 0; k < selectors.Length; k++)
            {
                int g = groupOf[k];
                order[fill[g]++] = k;
                for (int t = 0; t < 16; t++)
                {
                    sums[(g * 16) + t] += (int)((selectors[k] >> (t * 2)) & 3);
                }
            }

            representatives = new uint[groups];
            for (int g = 0; g < groups; g++)
            {
                int n = start[g + 1] - start[g];
                uint r = 0;
                for (int t = 0; t < 16; t++)
                {
                    r |= (uint)(((2 * sums[(g * 16) + t]) + n) / (2 * n)) << (t * 2);
                }

                representatives[g] = r;
            }

            if (groups <= probe)
            {
                groups = 0;
            }
        }
        else
        {
            groups = 0;
        }

        Parallel.For(0, blockEndpoint.Length, options, () => new int[1024 + 64 + 12], (i, _, scratch) =>
        {
            Span<int> palette = scratch.AsSpan(1024 + 64, 12);
            Span<int> texel = scratch.AsSpan(1024, 64);
            Palette(endpoints[blockEndpoint[i]], palette);
            ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
            for (int t = 0; t < 16; t++)
            {
                for (int s = 0; s < 4; s++)
                {
                    texel[(t * 4) + s] = TexelError(px, t, palette, s);
                }
            }

            Span<int> pair = stackalloc int[32];
            for (int row = 0; row < 4; row++)
            {
                ReadOnlySpan<int> e = texel.Slice(row * 16, 16);
                for (int x = 0; x < 16; x++)
                {
                    pair[x] = e[x & 3] + e[4 + (x >> 2)];
                    pair[16 + x] = e[8 + (x & 3)] + e[12 + (x >> 2)];
                }

                Span<int> table = scratch.AsSpan(row * 256, 256);
                for (int x = 0; x < 256; x++)
                {
                    table[x] = pair[x & 15] + pair[16 + (x >> 4)];
                }
            }

            ref int t0 = ref scratch[0];
            ref uint sel = ref selectors[0];
            int pick = blockSelector[i];
            int best = Cost(ref t0, selectors[pick]);
            if (groups == 0)
            {
                for (int k = 0; k < selectors.Length; k++)
                {
                    int e = Cost(ref t0, Unsafe.Add(ref sel, k));
                    if (e < best)
                    {
                        best = e;
                        pick = k;
                    }
                }
            }
            else
            {
                // The probe groups with the cheapest representatives, cheapest first.
                Span<int> top = stackalloc int[probe];
                Span<int> topCost = stackalloc int[probe];
                topCost.Fill(int.MaxValue);
                for (int g = 0; g < groups; g++)
                {
                    int e = Cost(ref t0, representatives[g]);
                    if (e >= topCost[probe - 1])
                    {
                        continue;
                    }

                    int j = probe - 1;
                    for (; j > 0 && topCost[j - 1] > e; j--)
                    {
                        top[j] = top[j - 1];
                        topCost[j] = topCost[j - 1];
                    }

                    top[j] = g;
                    topCost[j] = e;
                }

                for (int j = 0; j < probe; j++)
                {
                    int g = top[j];
                    for (int at = start[g]; at < start[g + 1]; at++)
                    {
                        int k = order[at];
                        int e = Cost(ref t0, Unsafe.Add(ref sel, k));
                        if (e < best)
                        {
                            best = e;
                            pick = k;
                        }
                    }
                }
            }

            blockSelector[i] = pick;
            return scratch;
        }, _ => { });

        static int Cost(ref int tables, uint s) =>
            Unsafe.Add(ref tables, (int)(s & 255)) + Unsafe.Add(ref tables, 256 + (int)((s >> 8) & 255))
            + Unsafe.Add(ref tables, 512 + (int)((s >> 16) & 255)) + Unsafe.Add(ref tables, 768 + (int)(s >> 24));
    }

    private static byte Q5(int sum16) => (byte)Math.Clamp(((sum16 * 31) + (255 * 8)) / (255 * 16), 0, 31);

    private static List<int>[] Members(int[] assignment, int clusters)
    {
        List<int>[] members = new List<int>[clusters];
        for (int k = 0; k < clusters; k++)
        {
            members[k] = [];
        }

        for (int i = 0; i < assignment.Length; i++)
        {
            members[assignment[i]].Add(i);
        }

        return members;
    }

    /// <summary>
    /// Vector quantization of <paramref name="n"/> integer vectors of <paramref name="dims"/> values, each with a weight in
    /// <paramref name="weights"/> (or 1), into at most <paramref name="k"/> clusters: the cluster with the largest weighted error
    /// splits at its weighted mean along its widest dimension until there are <paramref name="k"/>, then
    /// <paramref name="iterations"/> (unweighted) Lloyd iterations move vectors to their nearest centroid.
    /// Returns each vector's cluster.
    /// </summary>
    internal static int[] Vq(int[] features, int[]? weights, int n, int dims, int k, int iterations, ParallelOptions options, out int clusters)
    {
        int[] assignment = new int[n];
        List<int[]> groups = [Enumerable.Range(0, n).ToArray()];
        PriorityQueue<int, (double, int)> queue = new();
        queue.Enqueue(0, (-Spread(features, weights, groups[0], dims, out _, out _, out _), 0));
        while (groups.Count < k && queue.Count > 0)
        {
            queue.TryDequeue(out int g, out (double Error, int Order) key);
            if (key.Error == 0)
            {
                break;
            }

            int[] group = groups[g];
            Spread(features, weights, group, dims, out int dim, out long sum, out long weight);
            int[] left = [.. group.Where(i => (long)features[(i * dims) + dim] * weight < sum)];
            int[] right = [.. group.Where(i => (long)features[(i * dims) + dim] * weight >= sum)];
            if (left.Length == 0 || right.Length == 0)
            {
                continue;
            }

            groups[g] = left;
            groups.Add(right);
            queue.Enqueue(g, (-Spread(features, weights, left, dims, out _, out _, out _), g));
            queue.Enqueue(groups.Count - 1, (-Spread(features, weights, right, dims, out _, out _, out _), groups.Count - 1));
        }

        for (int g = 0; g < groups.Count; g++)
        {
            foreach (int i in groups[g])
            {
                assignment[i] = g;
            }
        }

        clusters = groups.Count;
        for (int iteration = 0; iteration < iterations && clusters > 1; iteration++)
        {
            long[] sums = new long[clusters * dims];
            int[] counts = new int[clusters];
            for (int i = 0; i < n; i++)
            {
                int c = assignment[i];
                counts[c]++;
                for (int d = 0; d < dims; d++)
                {
                    sums[(c * dims) + d] += features[(i * dims) + d];
                }
            }

            int[] centroids = new int[clusters * dims];
            int live = 0;
            for (int c = 0; c < clusters; c++)
            {
                if (counts[c] == 0)
                {
                    continue;
                }

                for (int d = 0; d < dims; d++)
                {
                    centroids[(live * dims) + d] = (int)((sums[(c * dims) + d] + (counts[c] / 2)) / counts[c]);
                }

                live++;
            }

            int changed = 0;
            int[] next = new int[n];
            Parallel.For(0, n, options, () => 0, (i, _, local) =>
            {
                long best = long.MaxValue;
                int pick = 0;
                for (int c = 0; c < live; c++)
                {
                    long distance = 0;
                    for (int d = 0; d < dims && distance < best; d++)
                    {
                        long v = features[(i * dims) + d] - centroids[(c * dims) + d];
                        distance += v * v;
                    }

                    if (distance < best)
                    {
                        best = distance;
                        pick = c;
                    }
                }

                next[i] = pick;
                return local + (pick != assignment[i] ? 1 : 0);
            }, local => Interlocked.Add(ref changed, local));

            assignment = next;
            clusters = Renumber(assignment, live);
            if (changed == 0)
            {
                break;
            }
        }

        return assignment;
    }

    /// <summary>Renumbers clusters so the used ones are 0 to count − 1 in order; returns the count.</summary>
    private static int Renumber(int[] assignment, int clusters)
    {
        int[] map = new int[clusters];
        Array.Fill(map, -1);
        int count = 0;
        for (int c = 0; c < clusters; c++)
        {
            map[c] = -1;
        }

        bool[] used = new bool[clusters];
        foreach (int c in assignment)
        {
            used[c] = true;
        }

        for (int c = 0; c < clusters; c++)
        {
            if (used[c])
            {
                map[c] = count++;
            }
        }

        for (int i = 0; i < assignment.Length; i++)
        {
            assignment[i] = map[assignment[i]];
        }

        return count;
    }

    /// <summary>Returns a group's weighted squared error about its mean, its widest dimension, that dimension's weighted sum, and the group's weight.</summary>
    private static double Spread(int[] features, int[]? weights, int[] group, int dims, out int widest, out long widestSum, out long weight)
    {
        widest = 0;
        widestSum = 0;
        weight = 0;
        foreach (int i in group)
        {
            weight += weights is null ? 1 : weights[i];
        }

        double total = 0;
        double widestVariance = -1;
        for (int d = 0; d < dims; d++)
        {
            long sum = 0;
            double squares = 0;
            foreach (int i in group)
            {
                long v = features[(i * dims) + d];
                long w = weights is null ? 1 : weights[i];
                sum += v * w;
                squares += (double)(v * v * w);
            }

            double variance = squares - ((double)sum * sum / Math.Max(weight, 1));
            total += variance;
            if (variance > widestVariance)
            {
                widestVariance = variance;
                widest = d;
                widestSum = sum;
            }
        }

        return total;
    }

    /// <summary>
    /// Finds the endpoint with the least error over a cluster's blocks, each texel taking its best selector: the mean colour
    /// with each table, then up to <paramref name="steps"/> moves to the best of the 26 colours around the best so far (with
    /// its table and, with <paramref name="intenReach"/>, the tables that near it), stopping when none is better. A candidate
    /// stops being summed once it is worse than the best so far.
    /// </summary>
    private static Endpoint FitEndpoint(byte[] pixels, List<int> blocks, int steps, int intenReach)
    {
        ReadOnlySpan<int> members = CollectionsMarshal.AsSpan(blocks);
        long sr = 0, sg = 0, sb = 0;
        foreach (int i in members)
        {
            ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
            for (int t = 0; t < 48; t += 3)
            {
                sr += px[t];
                sg += px[t + 1];
                sb += px[t + 2];
            }
        }

        long count = members.Length * 16L;
        Span<int> palette = stackalloc int[12];
        byte r0 = Q5Mean(sr, count), g0 = Q5Mean(sg, count), b0 = Q5Mean(sb, count);
        Endpoint best = default;
        long bestError = long.MaxValue;
        for (int inten = 0; inten < 8; inten++)
        {
            Endpoint e = new(r0, g0, b0, (byte)inten);
            long error = Error(pixels, members, e, palette, bestError);
            if (error < bestError)
            {
                bestError = error;
                best = e;
            }
        }

        for (int step = 0; step < steps; step++)
        {
            Endpoint centre = best;
            for (int di = -intenReach; di <= intenReach; di++)
            {
                int inten = centre.Inten + di;
                if ((uint)inten > 7)
                {
                    continue;
                }

                for (int dr = -1; dr <= 1; dr++)
                {
                    for (int dg = -1; dg <= 1; dg++)
                    {
                        for (int db = -1; db <= 1; db++)
                        {
                            int r = centre.R + dr, g = centre.G + dg, b = centre.B + db;
                            if ((dr | dg | db | di) == 0 || (uint)r > 31 || (uint)g > 31 || (uint)b > 31)
                            {
                                continue;
                            }

                            Endpoint e = new((byte)r, (byte)g, (byte)b, (byte)inten);
                            long error = Error(pixels, members, e, palette, bestError);
                            if (error < bestError)
                            {
                                bestError = error;
                                best = e;
                            }
                        }
                    }
                }
            }

            if (best == centre)
            {
                break;
            }
        }

        return best;

        static long Error(byte[] pixels, ReadOnlySpan<int> members, Endpoint e, Span<int> palette, long limit)
        {
            Palette(e, palette);
            long error = 0;
            foreach (int i in members)
            {
                error += BestError(pixels.AsSpan(i * 48, 48), palette, limit - error);
                if (error >= limit)
                {
                    break;
                }
            }

            return error;
        }
    }

    private static byte Q5Mean(long sum, long count) => (byte)Math.Clamp(((sum * 31) + (count * 255 / 2)) / (count * 255), 0, 31);

    /// <summary>
    /// Refits an endpoint to its blocks with their selectors fixed. Ignoring clamping, a table's best colour is the mean of texel
    /// minus modifier and its error follows from per-selector sums; the <paramref name="tables"/> tables with the least such
    /// error are then tried exactly at the 5-bit colours either side of that mean in each channel.
    /// </summary>
    private static Endpoint RefitEndpoint(byte[] pixels, List<int> blocks, Endpoint current, int[] blockSelector, uint[] selectors, int tables)
    {
        ReadOnlySpan<int> members = CollectionsMarshal.AsSpan(blocks);
        if (members.IsEmpty)
        {
            return current;
        }

        // Per selector value: the texel count and channel sums; and the sum of squares.
        Span<long> counts = stackalloc long[4];
        Span<long> sums = stackalloc long[12];
        counts.Clear();
        sums.Clear();
        foreach (int i in members)
        {
            uint s = selectors[blockSelector[i]];
            ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
            for (int t = 0; t < 16; t++)
            {
                int k = (int)((s >> (t * 2)) & 3);
                counts[k]++;
                sums[k * 3] += px[t * 3];
                sums[(k * 3) + 1] += px[(t * 3) + 1];
                sums[(k * 3) + 2] += px[(t * 3) + 2];
            }
        }

        long n = members.Length * 16L;
        Span<double> approx = stackalloc double[8];
        Span<double> mean = stackalloc double[24];
        Span<int> mods = stackalloc int[4];
        for (int inten = 0; inten < 8; inten++)
        {
            int a = Etc.Modifiers[inten * 2];
            int b = Etc.Modifiers[(inten * 2) + 1];
            mods[0] = -b;
            mods[1] = -a;
            mods[2] = a;
            mods[3] = b;

            // Σ(p − m)² − (Σ(p − m))² / n per channel, less the Σp² every table shares.
            double error = 0;
            for (int c = 0; c < 3; c++)
            {
                double sum = 0;
                double cross = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += sums[(k * 3) + c] - (counts[k] * mods[k]);
                    cross += (counts[k] * (double)mods[k] * mods[k]) - (2.0 * mods[k] * sums[(k * 3) + c]);
                }

                mean[(inten * 3) + c] = sum / n;
                error += cross - (sum * sum / n);
            }

            approx[inten] = error;
        }

        Span<int> palette = stackalloc int[12];
        Endpoint best = current;
        long bestError = Error(current, palette, long.MaxValue);
        Span<int> lo = stackalloc int[3];
        Span<int> hi = stackalloc int[3];
        for (int pass = 0; pass < tables; pass++)
        {
            int inten = 0;
            for (int t = 1; t < 8; t++)
            {
                if (approx[t] < approx[inten])
                {
                    inten = t;
                }
            }

            approx[inten] = double.MaxValue;
            for (int c = 0; c < 3; c++)
            {
                double m = Math.Clamp(mean[(inten * 3) + c], 0, 255);
                int v = Math.Clamp((int)(m * 31 / 255), 0, 31);
                lo[c] = v;
                hi[c] = Math.Min(31, v + 1);
            }

            for (int corner = 0; corner < 8; corner++)
            {
                int r = (corner & 1) == 0 ? lo[0] : hi[0];
                int g = (corner & 2) == 0 ? lo[1] : hi[1];
                int bl = (corner & 4) == 0 ? lo[2] : hi[2];
                Endpoint e = new((byte)r, (byte)g, (byte)bl, (byte)inten);
                long error = Error(e, palette, bestError);
                if (error < bestError)
                {
                    bestError = error;
                    best = e;
                }
            }
        }

        return best;

        long Error(Endpoint e, Span<int> palette, long limit)
        {
            Palette(e, palette);
            long error = 0;
            foreach (int i in CollectionsMarshal.AsSpan(blocks))
            {
                error += FixedError(pixels.AsSpan(i * 48, 48), palette, selectors[blockSelector[i]]);
                if (error >= limit)
                {
                    break;
                }
            }

            return error;
        }
    }

    /// <summary>Fits a selector texel by texel: each texel takes the modifier with the least error summed over the cluster's blocks.</summary>
    private static uint FitSelector(byte[] pixels, List<int> blocks, int[] blockEndpoint, Endpoint[] endpoints, uint[] ideal, uint current)
    {
        if (blocks.Count == 0)
        {
            return current;
        }

        if (blocks.Count == 1)
        {
            return ideal[blocks[0]];
        }

        Span<long> errors = stackalloc long[64];
        errors.Clear();
        Span<int> palette = stackalloc int[12];
        foreach (int i in blocks)
        {
            Palette(endpoints[blockEndpoint[i]], palette);
            ReadOnlySpan<byte> px = pixels.AsSpan(i * 48, 48);
            for (int t = 0; t < 16; t++)
            {
                for (int s = 0; s < 4; s++)
                {
                    errors[(t * 4) + s] += TexelError(px, t, palette, s);
                }
            }
        }

        uint selector = 0;
        for (int t = 0; t < 16; t++)
        {
            int pick = 0;
            for (int s = 1; s < 4; s++)
            {
                if (errors[(t * 4) + s] < errors[(t * 4) + pick])
                {
                    pick = s;
                }
            }

            selector |= (uint)pick << (t * 2);
        }

        return selector;
    }

    /// <summary>Merges identical codebook entries, drops unused ones and sorts both codebooks so their deltas code small.</summary>
    private static Codebooks Compact(Endpoint[] endpoints, uint[] selectors, int[] blockEndpoint, int[] blockSelector)
    {
        int[] endpointMap = Sort(endpoints, blockEndpoint, e => (e.G << 24) | (e.R << 16) | (e.B << 8) | e.Inten, out Endpoint[] sortedEndpoints);
        int[] selectorMap = Sort(selectors, blockSelector, s => s, out uint[] sortedSelectors);
        int[] be = [.. blockEndpoint.Select(e => endpointMap[e])];
        int[] bs = [.. blockSelector.Select(s => selectorMap[s])];
        return new Codebooks(sortedEndpoints, sortedSelectors, be, bs);

        static int[] Sort<T, TKey>(T[] entries, int[] used, Func<T, TKey> key, out T[] sorted)
            where T : notnull
            where TKey : IComparable<TKey>
        {
            bool[] live = new bool[entries.Length];
            foreach (int u in used)
            {
                live[u] = true;
            }

            List<T> distinct = [.. entries.Where((_, i) => live[i]).Distinct().OrderBy(key)];
            Dictionary<T, int> index = [];
            for (int i = 0; i < distinct.Count; i++)
            {
                index[distinct[i]] = i;
            }

            sorted = [.. distinct];
            int[] map = new int[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                map[i] = live[i] ? index[entries[i]] : 0;
            }

            return map;
        }
    }

    /// <summary>Codes the codebooks and slices into BasisLZ global data and level data.</summary>
    private static Etc1sEncoded Write(Codebooks books, List<(int BlocksX, int BlocksY, int Rgb, int Alpha)> layout, IReadOnlyList<Etc1sImage> images)
    {
        byte[] endpoints = WriteEndpoints(books.Endpoints);
        byte[] selectors = WriteSelectors(books.Selectors);

        // Slices: first the symbols, with the decoder's state followed exactly, then the codes built from their counts.
        List<List<Symbol>> slices = [];
        long[] predCounts = new long[PredRepeatSymbol + 1];
        long[] deltaCounts = new long[books.Endpoints.Length];
        long[] selectorCounts = new long[books.Selectors.Length + HistorySize + 1];
        long[] rleCounts = new long[64];
        foreach ((int bx, int by, int rgb, int alpha) in layout)
        {
            slices.Add(Slice(books, rgb, bx, by));
            if (alpha >= 0)
            {
                slices.Add(Slice(books, alpha, bx, by));
            }
        }

        foreach (List<Symbol> slice in slices)
        {
            foreach (Symbol s in slice)
            {
                long[]? counts = s.Table switch { 0 => predCounts, 1 => deltaCounts, 2 => selectorCounts, 3 => rleCounts, _ => null };
                if (counts is not null)
                {
                    counts[s.Value]++;
                }
            }
        }

        BasisHuffmanCode[] codes = [new(predCounts), new(deltaCounts), new(selectorCounts), new(rleCounts)];
        BasisBitWriter tableBits = new();
        foreach (BasisHuffmanCode code in codes)
        {
            code.WriteTable(tableBits);
        }

        tableBits.Write(HistorySize, 13);
        byte[] tables = tableBits.ToArray();

        int levelCount = images.Count == 0 ? 0 : images.Max(i => i.Level) + 1;
        List<byte>[] levels = new List<byte>[levelCount];
        for (int l = 0; l < levelCount; l++)
        {
            levels[l] = [];
        }

        List<BasisImage> descriptors = [];
        int next = 0;
        for (int i = 0; i < images.Count; i++)
        {
            List<byte> level = levels[images[i].Level];
            byte[] rgb = Code(slices[next++], codes);
            uint rgbOffset = (uint)level.Count;
            level.AddRange(rgb);
            uint alphaOffset = 0;
            uint alphaLength = 0;
            if (layout[i].Alpha >= 0)
            {
                byte[] a = Code(slices[next++], codes);
                alphaOffset = (uint)level.Count;
                alphaLength = (uint)a.Length;
                level.AddRange(a);
            }

            descriptors.Add(new BasisImage(false, rgbOffset, (uint)rgb.Length, alphaOffset, alphaLength));
        }

        byte[] global = BasisLzLayout.GlobalData(books.Endpoints.Length, books.Selectors.Length, descriptors, endpoints, selectors, tables);
        return new Etc1sEncoded(global, [.. levels.Select(l => l.ToArray())], books.Endpoints.Length, books.Selectors.Length);
    }

    /// <summary>One coded item of a slice: a Huffman symbol of table 0 to 3, or (table −1) raw VLC bits.</summary>
    private readonly record struct Symbol(int Table, int Value, int VlcBits = 0);

    private static byte[] Code(List<Symbol> slice, BasisHuffmanCode[] codes)
    {
        BasisBitWriter bits = new();
        foreach (Symbol s in slice)
        {
            if (s.Table < 0)
            {
                bits.Vlc((uint)s.Value, s.VlcBits);
            }
            else
            {
                codes[s.Table].Write(bits, s.Value);
            }
        }

        return bits.ToArray();
    }

    /// <summary>Chooses each block's predictions and selector symbols, as the decoder (Appendix D.5) will read them.</summary>
    private static List<Symbol> Slice(Codebooks books, int first, int blocksX, int blocksY)
    {
        List<Symbol> symbols = [];
        int selectorCount = books.Selectors.Length;
        int[] history = new int[HistorySize];
        int rover = HistorySize / 2;
        int prevEndpoint = 0;
        int prevPredSymbol = 0;
        int[] preds = new int[blocksX * blocksY];

        // Each block's prediction: 0 left, 1 above, 2 upper left, 3 a coded delta.
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int e = books.BlockEndpoint[first + (by * blocksX) + bx];
                int pred = 3;
                if (bx > 0 && books.BlockEndpoint[first + (by * blocksX) + bx - 1] == e)
                {
                    pred = 0;
                }
                else if (by > 0 && books.BlockEndpoint[first + ((by - 1) * blocksX) + bx] == e)
                {
                    pred = 1;
                }
                else if (bx > 0 && by > 0 && books.BlockEndpoint[first + ((by - 1) * blocksX) + bx - 1] == e)
                {
                    pred = 2;
                }

                preds[(by * blocksX) + bx] = pred;
            }
        }

        // The 2 × 2 groups' symbols, in the order the decoder reads them (on even block rows), with runs of repeats.
        List<int> groups = [];
        for (int by = 0; by < blocksY; by += 2)
        {
            for (int bx = 0; bx < blocksX; bx += 2)
            {
                int symbol = 0;
                for (int k = 0; k < 4; k++)
                {
                    int x = bx + (k & 1);
                    int y = by + (k >> 1);
                    int pred = x < blocksX && y < blocksY ? preds[(y * blocksX) + x] : 0;
                    symbol |= pred << (k * 2);
                }

                groups.Add(symbol);
            }
        }

        // The block loop: group symbols where the decoder reads them, then each block's delta and selector.
        int groupIndex = 0;
        int repeat = 0;
        int rle = 0;
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                if ((bx & 1) == 0 && (by & 1) == 0)
                {
                    int symbol = groups[groupIndex];
                    if (repeat > 0)
                    {
                        repeat--;
                    }
                    else
                    {
                        int run = 0;
                        while (groupIndex + run < groups.Count && groups[groupIndex + run] == prevPredSymbol && run < 1 << 20)
                        {
                            run++;
                        }

                        if (run >= 3 && groupIndex > 0)
                        {
                            symbols.Add(new Symbol(0, PredRepeatSymbol));
                            symbols.Add(new Symbol(-1, run - 3, 4));
                            repeat = run - 1;
                        }
                        else
                        {
                            symbols.Add(new Symbol(0, symbol));
                            prevPredSymbol = symbol;
                        }
                    }

                    groupIndex++;
                }

                int block = first + (by * blocksX) + bx;
                int endpoint = books.BlockEndpoint[block];
                if (preds[(by * blocksX) + bx] == 3)
                {
                    int delta = endpoint - prevEndpoint;
                    if (delta < 0)
                    {
                        delta += books.Endpoints.Length;
                    }

                    symbols.Add(new Symbol(1, delta));
                }

                prevEndpoint = endpoint;

                // The selector: inside a run, history entry 0; else a run of history entry 0, a history entry or a codebook index.
                int selector = books.BlockSelector[block];
                if (rle > 0)
                {
                    rle--;
                    continue;
                }

                int found = Array.IndexOf(history, selector);
                if (found == 0)
                {
                    int run = 1;
                    int at = block + 1;
                    int lastBlock = first + (blocksX * blocksY);
                    while (at < lastBlock && books.BlockSelector[at] == selector)
                    {
                        run++;
                        at++;
                    }

                    if (run >= 3)
                    {
                        symbols.Add(new Symbol(2, selectorCount + HistorySize));
                        if (run - 3 >= 63)
                        {
                            symbols.Add(new Symbol(3, 63));
                            symbols.Add(new Symbol(-1, run - 3, 7));
                        }
                        else
                        {
                            symbols.Add(new Symbol(3, run - 3));
                        }

                        rle = run - 1;
                        continue;
                    }
                }

                if (found >= 0)
                {
                    symbols.Add(new Symbol(2, selectorCount + found));
                    if (found != 0)
                    {
                        (history[found / 2], history[found]) = (history[found], history[found / 2]);
                    }
                }
                else
                {
                    symbols.Add(new Symbol(2, selector));
                    history[rover++] = selector;
                    if (rover == HistorySize)
                    {
                        rover = HistorySize / 2;
                    }
                }
            }
        }

        return symbols;
    }

    /// <summary>Codes the endpoint codebook (Appendix D.2): four Huffman tables, the grayscale flag, then DPCM deltas.</summary>
    private static byte[] WriteEndpoints(Endpoint[] endpoints)
    {
        bool gray = endpoints.All(e => e.R == e.G && e.G == e.B);
        long[][] colorCounts = [new long[32], new long[32], new long[32]];
        long[] intenCounts = new long[8];
        List<(int Model, int Delta, bool Inten)> deltas = [];
        int prevInten = 0;
        Span<int> prev = [16, 16, 16];
        foreach (Endpoint e in endpoints)
        {
            int di = (e.Inten - prevInten) & 7;
            intenCounts[di]++;
            deltas.Add((-1, di, true));
            prevInten = e.Inten;
            Span<int> value = [e.R, e.G, e.B];
            for (int c = 0; c < (gray ? 1 : 3); c++)
            {
                int model = prev[c] <= 9 ? 0 : prev[c] <= 21 ? 1 : 2;
                int d = (value[c] - prev[c]) & 31;
                colorCounts[model][d]++;
                deltas.Add((model, d, false));
                prev[c] = value[c];
            }
        }

        BasisHuffmanCode[] color = [new(colorCounts[0]), new(colorCounts[1]), new(colorCounts[2])];
        BasisHuffmanCode inten = new(intenCounts);
        BasisBitWriter bits = new();
        foreach (BasisHuffmanCode code in color)
        {
            code.WriteTable(bits);
        }

        inten.WriteTable(bits);
        bits.Write(gray ? 1u : 0, 1);
        foreach ((int model, int delta, bool isInten) in deltas)
        {
            if (isInten)
            {
                inten.Write(bits, delta);
            }
            else
            {
                color[model].Write(bits, delta);
            }
        }

        return bits.ToArray();
    }

    /// <summary>Codes the selector codebook (Appendix D.3), XOR deltas with Huffman coding or raw, whichever is smaller.</summary>
    private static byte[] WriteSelectors(uint[] selectors)
    {
        // A selector's 2-bit texels, texel t at bit 2t, are BasisLZ's four rows of bytes with the left texel lowest.
        uint[] rows = selectors;
        BasisBitWriter raw = new();
        raw.Write(0, 2);
        raw.Write(1, 1);
        foreach (uint r in rows)
        {
            raw.Write(r, 32);
        }

        long[] counts = new long[256];
        for (int i = 1; i < rows.Length; i++)
        {
            uint x = rows[i] ^ rows[i - 1];
            for (int j = 0; j < 4; j++)
            {
                counts[(x >> (j * 8)) & 0xFF]++;
            }
        }

        BasisHuffmanCode delta = new(counts);
        BasisBitWriter coded = new();
        coded.Write(0, 3);
        delta.WriteTable(coded);
        coded.Write(rows[0], 32);
        for (int i = 1; i < rows.Length; i++)
        {
            uint x = rows[i] ^ rows[i - 1];
            for (int j = 0; j < 4; j++)
            {
                delta.Write(coded, (int)((x >> (j * 8)) & 0xFF));
            }
        }

        byte[] a = raw.ToArray();
        byte[] b = coded.ToArray();
        return b.Length < a.Length ? b : a;
    }
}
