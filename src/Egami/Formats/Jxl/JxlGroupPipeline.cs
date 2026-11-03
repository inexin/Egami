using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <summary>
/// One thread's working memory for a pass group: the coefficients of the varblock being read, and the group's pixels after the
/// inverse transforms (X, Y and B, <see cref="Stride"/> samples a row, the group's top left corner at index 0).
/// </summary>
internal sealed class JxlGroupScratch
{
    // The largest varblock, 256×256.
    private const int MaxVarblock = 256 * 256;

    internal JxlGroupScratch(int groupDim)
    {
        Stride = groupDim;
        Planes = [Rent(groupDim * groupDim), Rent(groupDim * groupDim), Rent(groupDim * groupDim)];
        Coefficients = [RentZeroed(), RentZeroed(), RentZeroed()];
    }

    internal int Stride { get; }

    /// <summary>Gets the group's X, Y and B pixels.</summary>
    internal float[][] Planes { get; }

    /// <summary>Gets the varblock's coefficients, one plane a channel, 0 outside the varblock being read (each varblock clears its own).</summary>
    internal int[][] Coefficients { get; }

    /// <summary>Gets the group's first block column, in the frame.</summary>
    internal int Bx0 { get; private set; }

    internal int By0 { get; private set; }

    internal void Begin(int bx0, int by0) => (Bx0, By0) = (bx0, by0);

    internal void Return()
    {
        foreach (float[] plane in Planes)
        {
            ArrayPool<float>.Shared.Return(plane);
        }

        foreach (int[] coefficients in Coefficients)
        {
            ArrayPool<int>.Shared.Return(coefficients);
        }
    }

    private static float[] Rent(int length) => ArrayPool<float>.Shared.Rent(length);

    private static int[] RentZeroed()
    {
        int[] array = ArrayPool<int>.Shared.Rent(MaxVarblock);
        Array.Clear(array, 0, MaxVarblock);
        return array;
    }
}

/// <summary>
/// Decodes a single-pass VarDCT frame group by group, libjxl's way (roadmap 2.4.11, D1 and D2): each pass group is decoded,
/// dequantized and transformed into a per-thread buffer, then rendered there (Gaborish, the EPF steps, the color conversion and
/// the pixel type) straight into the output plane. The restoration filters reach <see cref="_border"/> pixels across, so a group
/// renders only the pixels at least that far from its neighbours; it keeps the 2 × border rows and columns next to each seam in
/// frame-wide strips, from which the seams are rendered once every group is in. No frame-sized float plane is made. Every step
/// does what the whole-frame path (<see cref="JxlVarDct.Reconstruct"/>, <see cref="JxlFilters"/>) does, in the same order with
/// the same arithmetic and the same mirroring at the frame's edges, so the pixels are the same.
/// </summary>
internal sealed class JxlGroupPipeline
{
    // The largest piece rendered at once: a group's width, and up to 128 rows, so a thread's buffers stay near its L2 cache.
    private const int TileRows = 128;

    // EPF tap offsets (x, y) and the patch offsets summed for each tap, by step.
    private static readonly int[] EpfKernel0 = [0, -2, -1, -1, 0, -1, 1, -1, -2, 0, -1, 0, 1, 0, 2, 0, -1, 1, 0, 1, 1, 1, 0, 2];
    private static readonly int[] EpfKernel12 = [0, -1, 0, 1, -1, 0, 1, 0];
    private static readonly int[] EpfDistance0 = [0, -1, 1, 0, 0, 0, -1, 0, 0, 1];
    private static readonly int[] EpfDistance1 = [0, -1, 0, 0, 0, 1, -1, 0, 1, 0];
    private static readonly int[] EpfDistance2 = [0, 0];

    private readonly JxlFrameDecoder _decoder;
    private readonly JxlFrameHeader _frame;
    private readonly JxlFile _file;
    private readonly int _width;
    private readonly int _height;
    private readonly int _groupDim;
    private readonly int _groupsX;
    private readonly int _groupsY;

    // The filter steps in order (-1 Gaborish, 0 to 2 the EPF steps), how far each reaches, and their sum.
    private readonly int[] _steps;
    private readonly int[] _reach;
    private readonly int _border;
    private readonly ConcurrentBag<Worker> _workers = [];
    private JxlVarDct? _varDct;

    // The pixels after the inverse transforms within 2 × border of each seam: rows of the whole width for the seams between
    // group rows, columns of the whole height for those between group columns.
    private float[][][] _rowStrips = [];
    private float[][][] _columnStrips = [];

    private JxlGroupPipeline(JxlFrameDecoder decoder, JxlFrameHeader frame, JxlFile file)
    {
        _decoder = decoder;
        _frame = frame;
        _file = file;
        (long w, long h) = frame.CodedSize;
        _width = (int)w;
        _height = (int)h;
        _groupDim = frame.GroupDim;
        _groupsX = (_width + _groupDim - 1) / _groupDim;
        _groupsY = (_height + _groupDim - 1) / _groupDim;
        List<int> steps = [];
        if (frame.Gaborish)
        {
            steps.Add(-1);
        }

        int iterations = frame.EpfIterations;
        if (iterations == 3)
        {
            steps.Add(0);
        }

        if (iterations >= 1)
        {
            steps.Add(1);
        }

        if (iterations >= 2)
        {
            steps.Add(2);
        }

        _steps = [.. steps];
        _reach = [.. steps.Select(s => s switch { -1 => 1, 0 => 3, 1 => 2, _ => 1 })];
        _border = _reach.Sum();
    }

    /// <summary>
    /// Gets a pipeline for the frame when it can take one: a VarDCT frame alone in the codestream, with XYB or plain color but
    /// no subsampled chroma, LF frame, patches, splines, noise, upsampling, crop or blending, and no extra channel other than
    /// straight alpha and channels the pixels do not show, none upsampled; decoded whole. Otherwise <see langword="null"/>, and
    /// the frame takes the whole-frame path.
    /// </summary>
    internal static JxlGroupPipeline? For(JxlFrameDecoder decoder, JxlFrameHeader f, JxlImageHeader h, JxlFile file, int frames)
    {
        bool fits = frames == 1 && f is { Modular: false, YCbCr: false, Upsampling: 1, HaveCrop: false, Type: JxlFrameType.Regular, IsLast: true }
            && (f.Flags & (0x01 | 0x02 | 0x10 | 0x20)) == 0 && f.Blending.Mode == JxlBlendMode.Replace
            && f.ExtraChannelBlending.All(b => b.Mode == JxlBlendMode.Replace) && f.ExtraChannelUpsampling.All(u => u == 1)
            && !h.ExtraChannels.Any(e => e.Type is JxlExtraChannelType.SpotColor or JxlExtraChannelType.Black || e.AlphaAssociated)
            && decoder.Needed is null && decoder.Arrived == long.MaxValue && !decoder.CoefficientsOnly;
        return fits ? new JxlGroupPipeline(decoder, f, file) : null;
    }

    /// <summary>Decodes the frame into <paramref name="output"/>, which is the frame's size.</summary>
    internal void Render<TPixel>(PixelPlane<TPixel> output)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        _varDct = _decoder.BeginGroups();
        int b2 = 2 * _border;
        if (_border > 0)
        {
            _rowStrips = new float[_groupsY - 1][][];
            for (int j = 0; j < _rowStrips.Length; j++)
            {
                int y = (j + 1) * _groupDim;
                int rows = Math.Min(_height, y + b2) - (y - b2);
                _rowStrips[j] = [new float[rows * _width], new float[rows * _width], new float[rows * _width]];
            }

            _columnStrips = new float[_groupsX - 1][][];
            for (int i = 0; i < _columnStrips.Length; i++)
            {
                int x = (i + 1) * _groupDim;
                int columns = Math.Min(_width, x + b2) - (x - b2);
                _columnStrips[i] = [new float[columns * _height], new float[columns * _height], new float[columns * _height]];
            }
        }

        try
        {
            // Each group: decoded, kept next to its seams, and its inner part rendered.
            _decoder.GroupParallel.For(_groupsX * _groupsY, g =>
            {
                Worker worker = Take();
                try
                {
                    _decoder.DecodeGroupPixels(g, worker.Group);
                    int gx = g % _groupsX;
                    int gy = g / _groupsX;
                    int x0 = gx * _groupDim;
                    int y0 = gy * _groupDim;
                    int x1 = Math.Min(_width, x0 + _groupDim);
                    int y1 = Math.Min(_height, y0 + _groupDim);
                    Window source = new(worker.Group.Planes, x0, y0, _groupDim);
                    KeepStrips(source, x0, y0, x1, y1);
                    int ix0 = gx > 0 ? x0 + _border : x0;
                    int iy0 = gy > 0 ? y0 + _border : y0;
                    int ix1 = gx < _groupsX - 1 ? x1 - _border : x1;
                    int iy1 = gy < _groupsY - 1 ? y1 - _border : y1;
                    RenderArea(source, ix0, iy0, ix1, iy1, worker, output);
                }
                finally
                {
                    _workers.Add(worker);
                }
            });
            _decoder.EndGroups();
            JxlStageTimes.Lap(JxlStage.Entropy);

            // The seams: the rows across every seam between group rows, then the columns across those between group columns.
            List<(int Strip, bool Rows, int X0, int Y0, int X1, int Y1)> seams = [];
            for (int j = 0; j < _rowStrips.Length; j++)
            {
                int y = (j + 1) * _groupDim;
                for (int x = 0; x < _width; x += _groupDim)
                {
                    seams.Add((j, true, x, y - _border, Math.Min(_width, x + _groupDim), Math.Min(_height, y + _border)));
                }
            }

            for (int i = 0; i < _columnStrips.Length; i++)
            {
                int x = (i + 1) * _groupDim;
                for (int gy = 0; gy < _groupsY; gy++)
                {
                    int y0 = (gy * _groupDim) + (gy > 0 ? _border : 0);
                    int y1 = Math.Min(_height, (gy + 1) * _groupDim) - (gy < _groupsY - 1 ? _border : 0);
                    if (y1 > y0)
                    {
                        seams.Add((i, false, x - _border, y0, Math.Min(_width, x + _border), y1));
                    }
                }
            }

            _decoder.Parallel.For(seams.Count, k =>
            {
                (int strip, bool rows, int x0, int y0, int x1, int y1) = seams[k];
                Window source = rows
                    ? new Window(_rowStrips[strip], 0, ((strip + 1) * _groupDim) - b2, _width)
                    : new Window(_columnStrips[strip], ((strip + 1) * _groupDim) - b2, 0, Math.Min(_width, ((strip + 1) * _groupDim) + b2) - (((strip + 1) * _groupDim) - b2));
                Worker worker = Take();
                try
                {
                    RenderArea(source, x0, y0, x1, y1, worker, output);
                }
                finally
                {
                    _workers.Add(worker);
                }
            }, minimumPerThread: 4);

            // Last, as every color step writes an alpha of 1.
            WriteAlpha(output);
            JxlStageTimes.Lap(JxlStage.Render);
        }
        finally
        {
            while (_workers.TryTake(out Worker? worker))
            {
                worker.Return();
            }
        }
    }

    /// <summary>Writes the alpha sample of every pixel from the alpha channel, as <c>JxlImageDecoder.Plane</c> does; the color steps left it 1.</summary>
    private void WriteAlpha<TPixel>(PixelPlane<TPixel> output)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        JxlImageHeader h = _file.Header;
        int index = h.AlphaIndex;
        PixelLayoutInfo layout = TPixel.Layout;
        if (index < 0 || !layout.HasAlpha)
        {
            return;
        }

        int[] data = _decoder.ExtraChannel(index).Data;
        JxlBitDepth depth = h.ExtraChannels[index].BitDepth;
        int samples = layout.ChannelCount;
        int width = _width;
        _decoder.Parallel.For(_height, y =>
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(output.Row(y));
            int at = y * width;
            for (int x = 0; x < width; x++)
            {
                float f = JxlImageDecoder.Float(data[at + x], depth);
                int i = (x * samples) + samples - 1;
                switch (layout.ChannelType)
                {
                    case PixelChannelType.Unorm8:
                        bytes[i] = (byte)MathF.Round(Math.Clamp(f, 0, 1) * 255);
                        break;
                    case PixelChannelType.Unorm16:
                        MemoryMarshal.Cast<byte, ushort>(bytes)[i] = (ushort)MathF.Round(Math.Clamp(f, 0, 1) * 65535);
                        break;
                    case PixelChannelType.Float16:
                        MemoryMarshal.Cast<byte, Half>(bytes)[i] = (Half)f;
                        break;
                    default:
                        MemoryMarshal.Cast<byte, float>(bytes)[i] = f;
                        break;
                }
            }
        }, minimumPerThread: 16);
    }

    private Worker Take() => _workers.TryTake(out Worker? worker) ? worker : new Worker(_groupDim, _border);

    /// <summary>Copies the part of a group's pixels within 2 × border of a seam into that seam's strip.</summary>
    private void KeepStrips(Window source, int x0, int y0, int x1, int y1)
    {
        int b2 = 2 * _border;
        for (int j = 0; j < _rowStrips.Length; j++)
        {
            int top = ((j + 1) * _groupDim) - b2;
            int from = Math.Max(y0, top);
            int to = Math.Min(y1, Math.Min(_height, top + (2 * b2)));
            for (int c = 0; c < 3; c++)
            {
                for (int y = from; y < to; y++)
                {
                    source.Planes[c].AsSpan(source.Index(x0, y), x1 - x0).CopyTo(_rowStrips[j][c].AsSpan(((y - top) * _width) + x0));
                }
            }
        }

        for (int i = 0; i < _columnStrips.Length; i++)
        {
            int left = ((i + 1) * _groupDim) - b2;
            int stride = Math.Min(_width, left + (2 * b2)) - left;
            int from = Math.Max(x0, left);
            int to = Math.Min(x1, left + stride);
            if (to <= from)
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                for (int y = y0; y < y1; y++)
                {
                    source.Planes[c].AsSpan(source.Index(from, y), to - from).CopyTo(_columnStrips[i][c].AsSpan((y * stride) + from - left));
                }
            }
        }
    }

    /// <summary>Renders the pixels from (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>, <paramref name="y1"/>) into the output, in tiles.</summary>
    private void RenderArea<TPixel>(Window source, int x0, int y0, int x1, int y1, Worker worker, PixelPlane<TPixel> output)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int ty = y0; ty < y1; ty += TileRows)
        {
            for (int tx = x0; tx < x1; tx += _groupDim)
            {
                RenderTile(source, tx, ty, Math.Min(x1, tx + _groupDim), Math.Min(y1, ty + TileRows), worker, output);
            }
        }
    }

    /// <summary>
    /// Renders one tile: its pixels and the border around it fetched from <paramref name="source"/> (mirrored past the frame's
    /// edges), each filter step over a smaller area, then color and pixel type.
    /// </summary>
    private void RenderTile<TPixel>(Window source, int x0, int y0, int x1, int y1, Worker worker, PixelPlane<TPixel> output)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int b = _border;
        Area area = new(x0 - b, y0 - b, x1 - x0 + (2 * b), y1 - y0 + (2 * b));
        worker.Ensure(area.Stride * area.Rows);
        float[][] input = worker.Input;
        Fetch(source, input, area, x0 - b, y0 - b, x1 + b, y1 + b);
        int rem = b;
        for (int k = 0; k < _steps.Length; k++)
        {
            rem -= _reach[k];
            int ax0 = x0 - rem, ay0 = y0 - rem, ax1 = x1 + rem, ay1 = y1 + rem;
            float[][] result = worker.Output;
            int fx0 = Math.Max(0, ax0), fy0 = Math.Max(0, ay0), fx1 = Math.Min(_width, ax1), fy1 = Math.Min(_height, ay1);
            if (_steps[k] < 0)
            {
                Gaborish(input, result, area, fx0, fy0, fx1, fy1);
            }
            else
            {
                Epf(_steps[k], input, result, area, fx0, fy0, fx1, fy1, worker);
            }

            MirrorEdges(result, area, ax0, ay0, ax1, ay1);
            worker.Swap();
            input = worker.Input;
        }

        // Color, a row at a time, then the pixel type.
        JxlOutput outputSpace = _file.Output!;
        float[][] color = worker.Color;
        (color[0], color[1], color[2]) = (input[0], input[1], input[2]);
        for (int y = y0; y < y1; y++)
        {
            int at = area.Index(x0, y);
            JxlImageDecoder.ToColorSpace(color, at, at + x1 - x0, _file);
        }

        if (outputSpace.Transform?.Target.Model == Colors.ColorModel.Gray)
        {
            color[1] = color[0];
            color[2] = color[0];
        }
        else if (outputSpace.Transform is null && outputSpace.Gray)
        {
            color[0] = color[1];
            color[2] = color[1];
        }

        for (int y = y0; y < y1; y++)
        {
            WriteRow(output.Row(y).Slice(x0, x1 - x0), color, area.Index(x0, y));
        }
    }

    /// <summary>Fills an area's buffer with the source's pixels; places past the frame's edges take the pixel mirrored into it.</summary>
    private void Fetch(Window source, float[][] buffer, Area area, int x0, int y0, int x1, int y1)
    {
        int inX0 = Math.Max(0, x0);
        int inX1 = Math.Min(_width, x1);
        for (int c = 0; c < 3; c++)
        {
            float[] from = source.Planes[c];
            float[] to = buffer[c];
            for (int y = y0; y < y1; y++)
            {
                int sy = JxlFilters.Mirror(y, _height);
                int row = area.Index(0, y);
                from.AsSpan(source.Index(inX0, sy), inX1 - inX0).CopyTo(to.AsSpan(row + inX0));
                for (int x = x0; x < inX0; x++)
                {
                    to[row + x] = from[source.Index(JxlFilters.Mirror(x, _width), sy)];
                }

                for (int x = inX1; x < x1; x++)
                {
                    to[row + x] = from[source.Index(JxlFilters.Mirror(x, _width), sy)];
                }
            }
        }
    }

    /// <summary>Fills the places of (x0, y0) to (x1, y1) past the frame's edges with the pixels mirrored into it, as the whole-frame filters read them.</summary>
    private void MirrorEdges(float[][] buffer, Area area, int x0, int y0, int x1, int y1)
    {
        int inX0 = Math.Max(0, x0);
        int inX1 = Math.Min(_width, x1);
        int inY0 = Math.Max(0, y0);
        int inY1 = Math.Min(_height, y1);
        if (inX0 == x0 && inX1 == x1 && inY0 == y0 && inY1 == y1)
        {
            return;
        }

        for (int c = 0; c < 3; c++)
        {
            float[] p = buffer[c];
            for (int y = inY0; y < inY1; y++)
            {
                int row = area.Index(0, y);
                for (int x = x0; x < inX0; x++)
                {
                    p[row + x] = p[row + JxlFilters.Mirror(x, _width)];
                }

                for (int x = inX1; x < x1; x++)
                {
                    p[row + x] = p[row + JxlFilters.Mirror(x, _width)];
                }
            }

            for (int y = y0; y < y1; y++)
            {
                if (y >= inY0 && y < inY1)
                {
                    continue;
                }

                p.AsSpan(area.Index(x0, JxlFilters.Mirror(y, _height)), x1 - x0).CopyTo(p.AsSpan(area.Index(x0, y)));
            }
        }
    }

    /// <summary>Gaborish (J.2) on (x0, y0) to (x1, y1): <see cref="JxlFilters.Gaborish"/>'s arithmetic, the neighbours read from the buffer's border.</summary>
    private void Gaborish(float[][] input, float[][] output, Area area, int x0, int y0, int x1, int y1)
    {
        float[] weights = _frame.LoopFilter.GaborishWeights;
        int lanes = Vector<float>.Count;
        for (int c = 0; c < 3; c++)
        {
            float w0 = weights[2 * c];
            float w1 = weights[(2 * c) + 1];
            float norm = 1 / (1 + (4 * w0) + (4 * w1));
            Vector<float> vw0 = new(w0);
            Vector<float> vw1 = new(w1);
            Vector<float> vnorm = new(norm);
            ref float p = ref MemoryMarshal.GetArrayDataReference(input[c]);
            ref float o = ref MemoryMarshal.GetArrayDataReference(output[c]);
            for (int y = y0; y < y1; y++)
            {
                int row = area.Index(x0, y);
                int up = row - area.Stride;
                int down = row + area.Stride;
                int n = x1 - x0;
                int x = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    for (; x + lanes <= n; x += lanes)
                    {
                        Vector<float> side = Load(ref p, up + x) + Load(ref p, down + x) + Load(ref p, row + x - 1) + Load(ref p, row + x + 1);
                        Vector<float> diag = Load(ref p, up + x - 1) + Load(ref p, up + x + 1) + Load(ref p, down + x - 1) + Load(ref p, down + x + 1);
                        Store(ref o, row + x, (Load(ref p, row + x) + (side * vw0) + (diag * vw1)) * vnorm);
                    }
                }

                for (; x < n; x++)
                {
                    float side = Unsafe.Add(ref p, up + x) + Unsafe.Add(ref p, down + x) + Unsafe.Add(ref p, row + x - 1) + Unsafe.Add(ref p, row + x + 1);
                    float diag = Unsafe.Add(ref p, up + x - 1) + Unsafe.Add(ref p, up + x + 1) + Unsafe.Add(ref p, down + x - 1) + Unsafe.Add(ref p, down + x + 1);
                    Unsafe.Add(ref o, row + x) = (Unsafe.Add(ref p, row + x) + (side * w0) + (diag * w1)) * norm;
                }
            }
        }
    }

    /// <summary>
    /// One EPF step (J.3) on (x0, y0) to (x1, y1): <see cref="JxlFilters.Epf"/>'s arithmetic, eight rows at a time with each
    /// tap's weighted patch difference worked out once for the rows' patches, the neighbours read from the buffer's border.
    /// </summary>
    private void Epf(int step, float[][] input, float[][] output, Area area, int x0, int y0, int x1, int y1, Worker worker)
    {
        JxlLoopFilter lf = _frame.LoopFilter;
        int[] kernel = step == 0 ? EpfKernel0 : EpfKernel12;
        int[] dist = step switch { 0 => EpfDistance0, 1 => EpfDistance1, _ => EpfDistance2 };
        float stepMultiplier = step == 0 ? lf.Pass0SigmaScale : step == 2 ? lf.Pass2SigmaScale : 1;
        float border = lf.BorderSadMul;
        float k0 = lf.ChannelScale[0];
        float k1 = lf.ChannelScale[1];
        float k2 = lf.ChannelScale[2];
        int taps = kernel.Length / 2;
        int n = dist.Length / 2;
        int reach = step == 2 ? 0 : 1;
        float[] sigma = _varDct!.EpfSigma;
        int blocksX = _varDct.BlocksX;
        float scaleFactor = 6.6f * (MathF.Sqrt(0.5f) - 1);
        float inner = stepMultiplier;
        float edge = stepMultiplier * border;
        int width = x1 - x0;
        int lanes = Vector<float>.Count;
        float[] p0 = input[0];
        float[] p1 = input[1];
        float[] p2 = input[2];

        // The differences cover the band's rows and columns and one more each way where the patches reach.
        int dWidth = width + (2 * reach);
        Span<int> around = stackalloc int[n * taps];
        Span<int> tapOffsets = stackalloc int[taps];
        for (int band = y0; band < y1; band += 8)
        {
            int bandEnd = Math.Min(y1, band + 8);
            int dRows = bandEnd - band + (2 * reach);
            int dPlane = dRows * dWidth;
            float[] w = worker.Differences(dPlane * taps);
            for (int k = 0; k < taps; k++)
            {
                int tx = kernel[2 * k];
                int ty = kernel[(2 * k) + 1];
                int shift = (ty * area.Stride) + tx;
                for (int r = 0; r < dRows; r++)
                {
                    int b = area.Index(x0 - reach, band - reach + r);
                    Differences(p0, p1, p2, w.AsSpan((k * dPlane) + (r * dWidth), dWidth), b, b + shift, k0, k1, k2);
                }
            }

            for (int k = 0; k < taps; k++)
            {
                for (int i = 0; i < n; i++)
                {
                    around[(k * n) + i] = (k * dPlane) + ((reach + dist[(2 * i) + 1]) * dWidth) + reach + dist[2 * i];
                }

                tapOffsets[k] = (kernel[(2 * k) + 1] * area.Stride) + kernel[2 * k];
            }

            Span<float> factor = worker.Factor(width);
            for (int y = band; y < bandEnd; y++)
            {
                // The same values as 6.6 (√0.5 − 1) / sigma · sm, worked out once a block.
                bool yBorder = ((y + 1) & 6) == 0;
                for (int x = x0; x < x1;)
                {
                    int bx = x >> 3;
                    float s = sigma[((y >> 3) * blocksX) + bx];
                    float t = scaleFactor / s;
                    float fi = s < 0.3f ? float.NaN : t * (yBorder ? edge : inner);
                    float fe = s < 0.3f ? float.NaN : t * edge;
                    int end = Math.Min(x1, (bx + 1) * 8);
                    for (; x < end; x++)
                    {
                        factor[x - x0] = (x & 7) is 0 or 7 ? fe : fi;
                    }
                }

                int row = (y - band) * dWidth;
                int at = area.Index(x0, y);
                int px = 0;
                if (Vector.IsHardwareAccelerated)
                {
                    ref float wr = ref MemoryMarshal.GetArrayDataReference(w);
                    ref float r0 = ref MemoryMarshal.GetArrayDataReference(p0);
                    ref float r1 = ref MemoryMarshal.GetArrayDataReference(p1);
                    ref float r2 = ref MemoryMarshal.GetArrayDataReference(p2);
                    for (; px + lanes <= width; px += lanes)
                    {
                        Vector<float> f = new(factor[px..]);
                        Vector<float> c0 = Load(ref r0, at + px);
                        Vector<float> c1 = Load(ref r1, at + px);
                        Vector<float> c2 = Load(ref r2, at + px);
                        Vector<float> s0 = c0;
                        Vector<float> s1 = c1;
                        Vector<float> s2 = c2;
                        Vector<float> sumW = Vector<float>.One;
                        for (int k = 0; k < taps; k++)
                        {
                            int o = k * n;
                            Vector<float> d = Load(ref wr, around[o] + row + px);
                            for (int i = 1; i < n; i++)
                            {
                                d += Load(ref wr, around[o + i] + row + px);
                            }

                            Vector<float> weight = Vector.Max(Vector<float>.Zero, Vector<float>.One + (d * f));
                            int t = at + px + tapOffsets[k];
                            sumW += weight;
                            s0 += weight * Load(ref r0, t);
                            s1 += weight * Load(ref r1, t);
                            s2 += weight * Load(ref r2, t);
                        }

                        // Pixels the filter leaves as they are (a NaN factor) keep their input.
                        Vector<int> filtered = Vector.Equals(f, f);
                        Vector.ConditionalSelect(filtered, s0 / sumW, c0).CopyTo(output[0], at + px);
                        Vector.ConditionalSelect(filtered, s1 / sumW, c1).CopyTo(output[1], at + px);
                        Vector.ConditionalSelect(filtered, s2 / sumW, c2).CopyTo(output[2], at + px);
                    }
                }

                for (; px < width; px++)
                {
                    int a = at + px;
                    float f = factor[px];
                    if (float.IsNaN(f))
                    {
                        output[0][a] = p0[a];
                        output[1][a] = p1[a];
                        output[2][a] = p2[a];
                        continue;
                    }

                    float s0 = p0[a];
                    float s1 = p1[a];
                    float s2 = p2[a];
                    float sumW = 1;
                    for (int k = 0; k < taps; k++)
                    {
                        int o = k * n;
                        float d = w[around[o] + row + px];
                        for (int i = 1; i < n; i++)
                        {
                            d += w[around[o + i] + row + px];
                        }

                        float weight = MathF.Max(0, 1 + (d * f));
                        int t = a + tapOffsets[k];
                        sumW += weight;
                        s0 += weight * p0[t];
                        s1 += weight * p1[t];
                        s2 += weight * p2[t];
                    }

                    output[0][a] = s0 / sumW;
                    output[1][a] = s1 / sumW;
                    output[2][a] = s2 / sumW;
                }
            }
        }
    }

    /// <summary>Writes the weighted three-channel difference |p(z + tap) − p(z)| for a row of places z starting at <paramref name="b"/> (z + tap at <paramref name="a"/>).</summary>
    private static void Differences(float[] p0, float[] p1, float[] p2, Span<float> w, int b, int a, float k0, float k1, float k2)
    {
        int z = 0;
        if (Vector.IsHardwareAccelerated)
        {
            ref float r0 = ref MemoryMarshal.GetArrayDataReference(p0);
            ref float r1 = ref MemoryMarshal.GetArrayDataReference(p1);
            ref float r2 = ref MemoryMarshal.GetArrayDataReference(p2);
            ref float wr = ref MemoryMarshal.GetReference(w);
            Vector<float> v0 = new(k0);
            Vector<float> v1 = new(k1);
            Vector<float> v2 = new(k2);
            int lanes = Vector<float>.Count;
            for (; z + lanes <= w.Length; z += lanes)
            {
                Vector<float> d = (v0 * Vector.Abs(Load(ref r0, a + z) - Load(ref r0, b + z))) + (v1 * Vector.Abs(Load(ref r1, a + z) - Load(ref r1, b + z))) + (v2 * Vector.Abs(Load(ref r2, a + z) - Load(ref r2, b + z)));
                Store(ref wr, z, d);
            }
        }

        for (; z < w.Length; z++)
        {
            w[z] = (k0 * MathF.Abs(p0[a + z] - p0[b + z])) + (k1 * MathF.Abs(p1[a + z] - p1[b + z])) + (k2 * MathF.Abs(p2[a + z] - p2[b + z]));
        }
    }

    /// <summary>
    /// Writes a row of pixels from the color planes at <paramref name="at"/>: what <c>JxlImageDecoder.Plane</c> writes for float
    /// color with no alpha channel coded (alpha 1), clamped, scaled and rounded to nearest even for integer types.
    /// </summary>
    private static void WriteRow<TPixel>(Span<TPixel> row, float[][] color, int at)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelLayoutInfo layout = TPixel.Layout;
        int samples = layout.ChannelCount;
        bool alpha = layout.HasAlpha;
        int colorSamples = alpha ? samples - 1 : samples;
        int width = row.Length;
        Span<byte> bytes = MemoryMarshal.AsBytes(row);
        if (layout.ChannelType == PixelChannelType.Unorm8 && (samples is 1 or 3 ? !alpha : samples == 4 && alpha) && System.Runtime.Intrinsics.X86.Avx2.IsSupported)
        {
            WriteBytes(bytes, color, at, samples);
            return;
        }

        for (int s = 0; s < samples; s++)
        {
            float[]? plane = alpha && s == samples - 1 ? null : color[Math.Min(s, colorSamples - 1) % 3];
            switch (layout.ChannelType)
            {
                case PixelChannelType.Unorm8:
                    {
                        int x0 = 0;
                        if (plane is not null && System.Runtime.Intrinsics.X86.Avx.IsSupported)
                        {
                            Span<int> rounded = stackalloc int[8];
                            Vector256<float> zero = Vector256<float>.Zero;
                            Vector256<float> one = Vector256<float>.One;
                            Vector256<float> max = Vector256.Create(255f);
                            for (; x0 + 8 <= width; x0 += 8)
                            {
                                Vector256<float> f = Vector256.Create(plane.AsSpan(at + x0, 8));
                                f = Vector256.Min(Vector256.Max(f, zero), one) * max;
                                System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Int32(System.Runtime.Intrinsics.X86.Avx.RoundToNearestInteger(f)).CopyTo(rounded);
                                for (int k = 0; k < 8; k++)
                                {
                                    bytes[((x0 + k) * samples) + s] = (byte)rounded[k];
                                }
                            }
                        }

                        for (int x = x0; x < width; x++)
                        {
                            float f = plane is null ? 1f : plane[at + x];
                            bytes[(x * samples) + s] = (byte)MathF.Round(Math.Clamp(f, 0, 1) * 255);
                        }

                        break;
                    }

                case PixelChannelType.Unorm16:
                    {
                        Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(bytes);
                        for (int x = 0; x < width; x++)
                        {
                            float f = plane is null ? 1f : plane[at + x];
                            words[(x * samples) + s] = (ushort)MathF.Round(Math.Clamp(f, 0, 1) * 65535);
                        }

                        break;
                    }

                case PixelChannelType.Float16:
                    {
                        Span<Half> halves = MemoryMarshal.Cast<byte, Half>(bytes);
                        for (int x = 0; x < width; x++)
                        {
                            halves[(x * samples) + s] = (Half)(plane is null ? 1f : plane[at + x]);
                        }

                        break;
                    }

                default:
                    {
                        Span<float> floats = MemoryMarshal.Cast<byte, float>(bytes);
                        for (int x = 0; x < width; x++)
                        {
                            floats[(x * samples) + s] = plane is null ? 1f : plane[at + x];
                        }

                        break;
                    }
            }
        }
    }

    /// <summary>
    /// Writes a row of 8-bit gray, RGB or RGBA (alpha 255), eight pixels at a time: each sample clamped, scaled and rounded to nearest even as
    /// <see cref="WriteRow"/> does, then narrowed to bytes and the three channels interleaved with byte shuffles.
    /// </summary>
    private static void WriteBytes(Span<byte> bytes, float[][] color, int at, int samples)
    {
        int width = bytes.Length / samples;
        ref byte to = ref MemoryMarshal.GetReference(bytes);
        float[] p0 = color[0];
        float[] p1 = color[samples == 1 ? 0 : 1];
        float[] p2 = color[samples == 1 ? 0 : 2];
        int x = 0;
        if (samples == 3)
        {
            Vector128<byte> lowR = Vector128.Create((byte)0, 8, 0x80, 1, 9, 0x80, 2, 10, 0x80, 3, 11, 0x80, 4, 12, 0x80, 5);
            Vector128<byte> lowB = Vector128.Create((byte)0x80, 0x80, 0, 0x80, 0x80, 1, 0x80, 0x80, 2, 0x80, 0x80, 3, 0x80, 0x80, 4, 0x80);
            Vector128<byte> highR = Vector128.Create((byte)13, 0x80, 6, 14, 0x80, 7, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            Vector128<byte> highB = Vector128.Create((byte)0x80, 5, 0x80, 0x80, 6, 0x80, 0x80, 7, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            for (; x + 8 <= width; x += 8)
            {
                Vector256<short> rg = Vector256.Narrow(Round8(p0, at + x), Round8(p1, at + x));
                Vector256<short> b = Vector256.Narrow(Round8(p2, at + x), Vector256<int>.Zero);
                Vector256<byte> all = Vector256.Narrow(rg.AsUInt16(), b.AsUInt16());
                Vector128<byte> first = all.GetLower();
                Vector128<byte> second = all.GetUpper();
                Vector128<byte> out0 = System.Runtime.Intrinsics.X86.Ssse3.Shuffle(first, lowR) | System.Runtime.Intrinsics.X86.Ssse3.Shuffle(second, lowB);
                Vector128<byte> out1 = System.Runtime.Intrinsics.X86.Ssse3.Shuffle(first, highR) | System.Runtime.Intrinsics.X86.Ssse3.Shuffle(second, highB);
                out0.StoreUnsafe(ref to, (nuint)(x * 3));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, (x * 3) + 16), out1.AsUInt64().ToScalar());
            }
        }
        else if (samples == 4)
        {
            // R, G, B and an alpha of 255 (the alpha channel, if any, is written later).
            Vector128<byte> lowRg = Vector128.Create((byte)0, 8, 0x80, 0x80, 1, 9, 0x80, 0x80, 2, 10, 0x80, 0x80, 3, 11, 0x80, 0x80);
            Vector128<byte> lowBa = Vector128.Create((byte)0x80, 0x80, 0, 8, 0x80, 0x80, 1, 9, 0x80, 0x80, 2, 10, 0x80, 0x80, 3, 11);
            Vector128<byte> highRg = Vector128.Create((byte)4, 12, 0x80, 0x80, 5, 13, 0x80, 0x80, 6, 14, 0x80, 0x80, 7, 15, 0x80, 0x80);
            Vector128<byte> highBa = Vector128.Create((byte)0x80, 0x80, 4, 12, 0x80, 0x80, 5, 13, 0x80, 0x80, 6, 14, 0x80, 0x80, 7, 15);
            Vector256<int> opaque = Vector256.Create(255);
            for (; x + 8 <= width; x += 8)
            {
                Vector256<short> rg = Vector256.Narrow(Round8(p0, at + x), Round8(p1, at + x));
                Vector256<short> ba = Vector256.Narrow(Round8(p2, at + x), opaque);
                Vector256<byte> all = Vector256.Narrow(rg.AsUInt16(), ba.AsUInt16());
                Vector128<byte> first = all.GetLower();
                Vector128<byte> second = all.GetUpper();
                (System.Runtime.Intrinsics.X86.Ssse3.Shuffle(first, lowRg) | System.Runtime.Intrinsics.X86.Ssse3.Shuffle(second, lowBa)).StoreUnsafe(ref to, (nuint)(x * 4));
                (System.Runtime.Intrinsics.X86.Ssse3.Shuffle(first, highRg) | System.Runtime.Intrinsics.X86.Ssse3.Shuffle(second, highBa)).StoreUnsafe(ref to, (nuint)((x * 4) + 16));
            }
        }
        else
        {
            for (; x + 8 <= width; x += 8)
            {
                Vector256<short> v = Vector256.Narrow(Round8(p0, at + x), Vector256<int>.Zero);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, x), Vector256.Narrow(v.AsUInt16(), Vector256<ushort>.Zero).GetLower().AsUInt64().ToScalar());
            }
        }

        for (; x < width; x++)
        {
            bytes[x * samples] = (byte)MathF.Round(Math.Clamp(p0[at + x], 0, 1) * 255);
            if (samples >= 3)
            {
                bytes[(x * samples) + 1] = (byte)MathF.Round(Math.Clamp(p1[at + x], 0, 1) * 255);
                bytes[(x * samples) + 2] = (byte)MathF.Round(Math.Clamp(p2[at + x], 0, 1) * 255);
            }

            if (samples == 4)
            {
                bytes[(x * 4) + 3] = 255;
            }
        }
    }

    /// <summary>Eight samples clamped to 0 to 1, scaled to 255 and rounded to nearest even.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Round8(float[] plane, int at)
    {
        Vector256<float> f = Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(plane), (nuint)at);
        f = Vector256.Min(Vector256.Max(f, Vector256<float>.Zero), Vector256<float>.One) * Vector256.Create(255f);
        return System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Int32(System.Runtime.Intrinsics.X86.Avx.RoundToNearestInteger(f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Load(ref float origin, int at) => Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref origin, at)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref float origin, int at, Vector<float> value) => Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref origin, at)), value);

    /// <summary>Planes holding a rectangle of the frame from (<see cref="X0"/>, <see cref="Y0"/>), <see cref="Stride"/> samples a row.</summary>
    private readonly record struct Window(float[][] Planes, int X0, int Y0, int Stride)
    {
        internal int Index(int x, int y) => ((y - Y0) * Stride) + x - X0;
    }

    /// <summary>A tile's buffers: the frame from (<see cref="X0"/>, <see cref="Y0"/>), <see cref="Stride"/> by <see cref="Rows"/> samples.</summary>
    private readonly record struct Area(int X0, int Y0, int Stride, int Rows)
    {
        internal int Index(int x, int y) => ((y - Y0) * Stride) + x - X0;
    }

    /// <summary>One thread's buffers: a group's scratch, a tile's input and output planes (swapped after each filter step) and the EPF's.</summary>
    private sealed class Worker
    {
        private float[] _differences = [];
        private float[] _factor = [];

        internal Worker(int groupDim, int border)
        {
            Group = new JxlGroupScratch(groupDim);
            int size = (groupDim + (2 * border)) * (Math.Min(groupDim, TileRows) + (2 * border));
            Input = [Rent(size), Rent(size), Rent(size)];
            Output = [Rent(size), Rent(size), Rent(size)];
        }

        internal JxlGroupScratch Group { get; }

        internal float[][] Input { get; private set; }

        internal float[][] Output { get; private set; }

        /// <summary>Gets three plane references for the color step to rearrange without touching <see cref="Input"/>.</summary>
        internal float[][] Color { get; } = new float[3][];

        internal void Ensure(int size)
        {
            if (Input[0].Length < size)
            {
                Return(Input);
                Return(Output);
                Input = [Rent(size), Rent(size), Rent(size)];
                Output = [Rent(size), Rent(size), Rent(size)];
            }
        }

        internal void Swap() => (Input, Output) = (Output, Input);

        internal float[] Differences(int size)
        {
            if (_differences.Length < size)
            {
                Release(_differences);
                _differences = Rent(size);
            }

            return _differences;
        }

        internal Span<float> Factor(int size)
        {
            if (_factor.Length < size)
            {
                Release(_factor);
                _factor = Rent(size);
            }

            return _factor.AsSpan(0, size);
        }

        internal void Return()
        {
            Group.Return();
            Return(Input);
            Return(Output);
            Release(_differences);
            Release(_factor);
        }

        private static void Release(float[] array)
        {
            if (array.Length > 0)
            {
                ArrayPool<float>.Shared.Return(array);
            }
        }

        private static float[] Rent(int size) => ArrayPool<float>.Shared.Rent(size);

        private static void Return(float[][] planes)
        {
            foreach (float[] plane in planes)
            {
                ArrayPool<float>.Shared.Return(plane);
            }
        }
    }
}
