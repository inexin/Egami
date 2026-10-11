using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.ExceptionServices;

namespace Egami.Formats.Jpeg2000;

/// <summary>
/// Decoded JPEG 2000 samples: one plane per component, at each component's own (reduced) size, holding the component rows from
/// <see cref="RowOffsets"/> on: the whole image, or the rows of one row of tiles.
/// </summary>
internal sealed class J2kImage
{
    internal required int[][] Planes { get; init; }

    /// <summary>Gets each component's first row held, counting from the component's first row.</summary>
    internal required int[] RowOffsets { get; init; }

    internal required int[] Widths { get; init; }

    internal required int[] Heights { get; init; }

    internal required J2kComponent[] Components { get; init; }

    /// <summary>Gets or sets whether the codestream ended early; what it held was decoded.</summary>
    internal bool Truncated { get; set; }

    /// <summary>Gets or sets the number of tiles that had no data.</summary>
    internal int MissingTiles { get; set; }
}

/// <summary>A rectangle on the reference grid after the levels dropped, to which a decode may limit its work.</summary>
internal readonly record struct J2kWindow(int X0, int Y0, int X1, int Y1);

/// <summary>
/// Decodes a JPEG 2000 codestream (T.800) tile by tile: tier 2 gathers each code-block's passes, tier 1 decodes the code-blocks
/// in parallel, then dequantization, the inverse wavelet and component transforms, the DC level shift and clamping (Annex G).
/// </summary>
internal sealed class J2kDecoder
{
    private readonly J2kCodestream _stream;
    private readonly int _reduce;
    private readonly int _keepLayers;
    private readonly int _parallelism;
    private readonly CancellationToken _cancellationToken;
    private long _structures;

    internal J2kDecoder(J2kCodestream stream, int reduce, int keepLayers, int parallelism, CancellationToken cancellationToken)
    {
        _stream = stream;
        _reduce = reduce;
        _keepLayers = keepLayers <= 0 ? int.MaxValue : keepLayers;
        _parallelism = Math.Max(1, parallelism);
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets which components to decode, or <see langword="null"/> for all: the ones the pixels use, and the first three when the
    /// component transform combines them. The rest are still parsed, as their packets lie between the others'.
    /// </summary>
    internal bool[]? Needed { get; init; }

    /// <summary>Gets the size of component <paramref name="c"/> after dropping <paramref name="reduce"/> resolution levels.</summary>
    internal static (int Width, int Height) ComponentSize(J2kCodestream stream, int c, int reduce)
    {
        J2kComponent component = stream.Components[c];
        int x0 = J2kTier2.CeilShift(J2kTier2.CeilDiv(stream.OffsetX, component.Dx), reduce);
        int y0 = J2kTier2.CeilShift(J2kTier2.CeilDiv(stream.OffsetY, component.Dy), reduce);
        int x1 = J2kTier2.CeilShift(J2kTier2.CeilDiv(stream.Width, component.Dx), reduce);
        int y1 = J2kTier2.CeilShift(J2kTier2.CeilDiv(stream.Height, component.Dy), reduce);
        return (x1 - x0, y1 - y0);
    }

    internal J2kImage Decode() => Decode(_stream.OffsetY, _stream.Height, null);

    /// <summary>
    /// Decodes the tiles covering reference grid rows <paramref name="y0"/> to <paramref name="y1"/>, those inside
    /// <paramref name="window"/> when it is given, and only the code-blocks the window needs, into planes holding those rows.
    /// </summary>
    internal J2kImage Decode(int y0, int y1, J2kWindow? window)
    {
        int count = _stream.Components.Length;
        int[][] planes = new int[count][];
        int[] widths = new int[count];
        int[] heights = new int[count];
        int[] offsets = new int[count];
        for (int c = 0; c < count; c++)
        {
            J2kComponent component = _stream.Components[c];
            int origin = J2kTier2.CeilShift(J2kTier2.CeilDiv(_stream.OffsetY, component.Dy), _reduce);
            offsets[c] = J2kTier2.CeilShift(J2kTier2.CeilDiv(y0, component.Dy), _reduce) - origin;
            heights[c] = J2kTier2.CeilShift(J2kTier2.CeilDiv(y1, component.Dy), _reduce) - origin - offsets[c];
            widths[c] = ComponentSize(_stream, c, _reduce).Width;
            planes[c] = Needed is null || Needed[c] ? new int[(long)widths[c] * heights[c]] : [];
        }

        J2kImage image = new() { Planes = planes, RowOffsets = offsets, Widths = widths, Heights = heights, Components = _stream.Components };
        _stream.SplitPackedHeaders();
        for (int t = 0; t < _stream.TileCount; t++)
        {
            (int tx0, int ty0, int tx1, int ty1) = _stream.TileRect(t);
            if (ty1 <= y0 || ty0 >= y1 || (window is { } w && !Touches(w, tx0, ty0, tx1, ty1)))
            {
                continue;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            J2kTileData? tile = _stream.Tiles[t];
            if (tile is null)
            {
                image.MissingTiles++;
                continue;
            }

            // Every tile builds each component's resolutions, bands and precincts: a codestream of many components and many
            // tiny tiles would build far more of them than the image has samples.
            _structures += (long)_stream.Components.Length * (_stream.CodingStyle(tile, 0).Levels + 2);
            if (_structures > (Samples() / 4) + (1 << 20))
            {
                throw new InvalidDataException("The codestream has far more tiles and components than its samples can fill.");
            }

            if (DecodeTile(tile, image, window))
            {
                image.Truncated = true;
            }
        }

        image.Truncated |= _stream.Truncated;
        return image;
    }

    private long Samples()
    {
        long samples = 0;
        for (int c = 0; c < _stream.Components.Length; c++)
        {
            (int w, int h) = ComponentSize(_stream, c, 0);
            samples += (long)w * h;
        }

        return samples;
    }

    /// <summary>Returns whether a tile's rectangle, reduced, meets the window.</summary>
    private bool Touches(J2kWindow window, int tx0, int ty0, int tx1, int ty1)
    {
        return J2kTier2.CeilShift(tx0, _reduce) < window.X1 && J2kTier2.CeilShift(tx1, _reduce) > window.X0
            && J2kTier2.CeilShift(ty0, _reduce) < window.Y1 && J2kTier2.CeilShift(ty1, _reduce) > window.Y0;
    }

    /// <summary>
    /// Returns whether a code-block of a band <paramref name="halvings"/> levels below the decoded resolution can affect the
    /// window, given in that resolution's coordinates: its rectangle, scaled up, widened by the synthesis filters' reach.
    /// </summary>
    private static bool Reaches(J2kCodeBlock block, int halvings, (int X0, int Y0, int X1, int Y1) window)
    {
        // Each synthesis level reaches at most three samples either side of a sample's position at that level, so a band
        // sample reaches no further than six of its own steps; eight is a safe margin.
        const int Margin = 8;
        return block.X0 - Margin < (window.X1 >> halvings) + 1 && block.X1 + Margin > window.X0 >> halvings
            && block.Y0 - Margin < (window.Y1 >> halvings) + 1 && block.Y1 + Margin > window.Y0 >> halvings;
    }

    /// <summary>Decodes one tile into the image's planes; returns whether its data ended early.</summary>
    private bool DecodeTile(J2kTileData tile, J2kImage image, J2kWindow? window)
    {
        J2kTier2 tier2 = new(_stream, tile, _keepLayers);
        tier2.ReadPackets(_cancellationToken);
        J2kTileComponent[] components = tier2.Components;
        int count = components.Length;
        int[]?[] integers = new int[count][];
        float[]?[] floats = new float[count][];
        int[] tops = new int[count];
        List<(J2kCodeBlock Block, J2kBand Band, int Component, int Offset)> work = [];
        for (int c = 0; c < count; c++)
        {
            J2kTileComponent tc = components[c];
            int top = tc.Style.Levels - _reduce;
            if (top < 0)
            {
                throw new InvalidDataException($"A tile-component has {tc.Style.Levels} decomposition levels, fewer than the {_reduce} to drop.");
            }

            tops[c] = top;
            if (Needed is not null && !Needed[c])
            {
                continue;
            }

            J2kResolution highest = tc.Resolutions[top];
            J2kComponent component = _stream.Components[c];
            (int X0, int Y0, int X1, int Y1)? local = window is { } w
                ? (FloorDiv(w.X0, component.Dx) - 1, FloorDiv(w.Y0, component.Dy) - 1, J2kTier2.CeilDiv(w.X1, component.Dx) + 1, J2kTier2.CeilDiv(w.Y1, component.Dy) + 1)
                : null;
            int stride = highest.Width;
            long size = (long)highest.Width * highest.Height;
            if (tc.Style.Reversible)
            {
                integers[c] = new int[size];
            }
            else
            {
                floats[c] = new float[size];
            }

            for (int r = 0; r <= top; r++)
            {
                J2kResolution res = tc.Resolutions[r];
                foreach (J2kPrecinctBand[]? precinct in res.Precincts)
                {
                    if (precinct is null)
                    {
                        continue;
                    }

                    for (int b = 0; b < precinct.Length; b++)
                    {
                        J2kBand band = res.Bands[b];
                        foreach (J2kCodeBlock block in precinct[b].Blocks)
                        {
                            if (block.Bytes == 0)
                            {
                                continue;
                            }

                            // The band's level below the decoded resolution: LL at resolution 0 lies top levels down, a
                            // detail band of resolution r one level below resolution r.
                            int halvings = r == 0 ? top : top - r + 1;
                            if (local is { } l && !Reaches(block, halvings, l))
                            {
                                continue;
                            }

                            int offset = ((band.BufferY + block.Y0 - band.Y0) * stride) + band.BufferX + block.X0 - band.X0;
                            work.Add((block, band, c, offset));
                        }
                    }
                }
            }
        }

        RunTier1(work, components, integers, floats, tops);
        ForEach(count, c =>
        {
            if (integers[c] is null && floats[c] is null)
            {
                return;
            }

            J2kTileComponent tc = components[c];
            J2kResolution highest = tc.Resolutions[tops[c]];
            if (integers[c] is { } ints)
            {
                J2kWavelet.Reverse53(ints, highest.Width, tc.Resolutions, tops[c]);
            }
            else
            {
                J2kWavelet.Reverse97(floats[c]!, highest.Width, tc.Resolutions, tops[c]);
            }
        });

        bool transform = components[0].Style.Mct == 1 && count >= 3 && SameSize(components, tops);
        if (transform)
        {
            if (integers[0] is not null && integers[1] is not null && integers[2] is not null)
            {
                InverseRct(integers[0]!, integers[1]!, integers[2]!);
            }
            else if (floats[0] is not null && floats[1] is not null && floats[2] is not null)
            {
                InverseIct(floats[0]!, floats[1]!, floats[2]!);
            }
            else
            {
                throw new InvalidDataException("The component transform mixes reversible and irreversible components.");
            }
        }

        for (int c = 0; c < count; c++)
        {
            if (integers[c] is not null || floats[c] is not null)
            {
                Store(components[c], tops[c], c, integers[c], floats[c], image);
            }
        }

        return tier2.Truncated;
    }

    private static bool SameSize(J2kTileComponent[] components, int[] tops)
    {
        J2kResolution a = components[0].Resolutions[tops[0]];
        for (int c = 1; c < 3; c++)
        {
            J2kResolution b = components[c].Resolutions[tops[c]];
            if (a.Width != b.Width || a.Height != b.Height)
            {
                return false;
            }
        }

        return true;
    }

    private void RunTier1(List<(J2kCodeBlock Block, J2kBand Band, int Component, int Offset)> work, J2kTileComponent[] components, int[]?[] integers, float[]?[] floats, int[] tops)
    {
        void Range(J2kTier1 tier1, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                (J2kCodeBlock block, J2kBand band, int c, int offset) = work[i];
                J2kTileComponent tc = components[c];
                int stride = tc.Resolutions[tops[c]].Width;
                tier1.Decode(block, band, tc, integers[c], floats[c], offset, stride);
            }
        }

        int threads = Math.Min(_parallelism, Math.Max(1, work.Count / 16));
        if (threads <= 1)
        {
            Range(new J2kTier1(), 0, work.Count);
            return;
        }

        ParallelOptions options = new() { MaxDegreeOfParallelism = threads, CancellationToken = _cancellationToken };
        try
        {
            Parallel.ForEach(Partitioner.Create(0, work.Count, Math.Max(4, work.Count / (threads * 8))), options, () => new J2kTier1(), (range, _, tier1) =>
            {
                Range(tier1, range.Item1, range.Item2);
                return tier1;
            }, _ => { });
        }
        catch (AggregateException e) when (e.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
        }
    }

    private void ForEach(int count, Action<int> action)
    {
        if (_parallelism <= 1 || count == 1)
        {
            for (int i = 0; i < count; i++)
            {
                action(i);
            }

            return;
        }

        ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Min(_parallelism, count), CancellationToken = _cancellationToken };
        try
        {
            Parallel.For(0, count, options, action);
        }
        catch (AggregateException e) when (e.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
        }
    }

    /// <summary>The inverse reversible component transform (G.2.2).</summary>
    private static void InverseRct(int[] y0, int[] y1, int[] y2)
    {
        for (int i = 0; i < y0.Length; i++)
        {
            int g = y0[i] - ((y2[i] + y1[i]) >> 2);
            int r = y2[i] + g;
            int b = y1[i] + g;
            y0[i] = r;
            y1[i] = g;
            y2[i] = b;
        }
    }

    /// <summary>The inverse irreversible component transform (G.3.2).</summary>
    private static void InverseIct(float[] y0, float[] y1, float[] y2)
    {
        for (int i = 0; i < y0.Length; i++)
        {
            float y = y0[i];
            float cb = y1[i];
            float cr = y2[i];
            y0[i] = y + (1.402f * cr);
            y1[i] = y - (0.34413f * cb) - (0.71414f * cr);
            y2[i] = y + (1.772f * cb);
        }
    }

    /// <summary>Level-shifts, rounds and clamps a tile-component (G.1.2) into its place in the component's plane.</summary>
    private void Store(J2kTileComponent tc, int top, int c, int[]? integers, float[]? floats, J2kImage image)
    {
        J2kComponent component = _stream.Components[c];
        J2kResolution res = tc.Resolutions[top];
        int originX = J2kTier2.CeilShift(J2kTier2.CeilDiv(_stream.OffsetX, component.Dx), _reduce);
        int originY = J2kTier2.CeilShift(J2kTier2.CeilDiv(_stream.OffsetY, component.Dy), _reduce) + image.RowOffsets[c];
        int planeWidth = image.Widths[c];
        int[] plane = image.Planes[c];
        int precision = Math.Min(component.Precision, 30);
        long shift = component.Signed ? 0 : 1L << (precision - 1);
        long min = component.Signed ? -(1L << (precision - 1)) : 0;
        long max = component.Signed ? (1L << (precision - 1)) - 1 : (1L << precision) - 1;
        int w = res.Width;
        int from = Math.Max(0, originY - res.Y0);
        int to = Math.Min(res.Height, originY + image.Heights[c] - res.Y0);
        for (int y = from; y < to; y++)
        {
            Span<int> target = plane.AsSpan(((res.Y0 - originY + y) * planeWidth) + res.X0 - originX, w);
            if (integers is not null)
            {
                StoreRow(integers.AsSpan(y * w, w), target, (int)shift, (int)min, (int)max);
            }
            else
            {
                StoreRow(floats!.AsSpan(y * w, w), target, (int)shift, (int)min, (int)max);
            }
        }
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>Level-shifts and clamps a row of integer samples.</summary>
    private static void StoreRow(ReadOnlySpan<int> source, Span<int> target, int shift, int min, int max)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<int> add = new(shift);
            Vector<int> low = new(min);
            Vector<int> high = new(max);
            for (; x + Vector<int>.Count <= source.Length; x += Vector<int>.Count)
            {
                Vector.Min(Vector.Max(new Vector<int>(source[x..]) + add, low), high).CopyTo(target[x..]);
            }
        }

        for (; x < source.Length; x++)
        {
            target[x] = Math.Clamp(source[x] + shift, min, max);
        }
    }

    /// <summary>Rounds (half to even), level-shifts and clamps a row of float samples.</summary>
    private static void StoreRow(ReadOnlySpan<float> source, Span<int> target, int shift, int min, int max)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<int> add = new(shift);
            Vector<int> low = new(min);
            Vector<int> high = new(max);
            Vector<float> lowF = new(min - shift);
            Vector<float> highF = new(max - shift);
            for (; x + Vector<int>.Count <= source.Length; x += Vector<int>.Count)
            {
                Vector<float> v = Vector.Min(Vector.Max(Vector.Round(new Vector<float>(source[x..])), lowF), highF);
                (Vector.ConvertToInt32(v) + add).CopyTo(target[x..]);
            }
        }

        for (; x < source.Length; x++)
        {
            target[x] = (int)Math.Clamp(MathF.Round(source[x]) + (long)shift, min, max);
        }
    }
}
