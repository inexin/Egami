using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// The byte path of the box passes (Phase 2.5.7), for pixel types whose channels are whole bytes stored straight. Both axes run
/// as one streaming sweep per line (<see cref="LaneBoxes"/>), all passes chained through small rings, so nothing but the image
/// leaves the cache. Across: <see cref="Band"/> rows at a time, transposed so each vector lane is a row, premultiplied as they
/// load. Down: <see cref="Group"/> columns at a time, dividing alpha out as rows are stored. Between the sweeps an opaque image
/// holds its values as bytes in the destination, and any other as 16-bit values in a plane of its own, since a small alpha
/// would magnify an 8-bit rounding of a premultiplied color many times; the one rounding keeps the result within a level of the
/// float path. Every line runs whole from the image's edge, so the result is the same at any thread count.
/// </summary>
internal static class ByteBlur
{
    /// <summary>The rows in a band of the horizontal sweep: one per vector lane, two vectors per channel.</summary>
    internal const int Band = 32;

    /// <summary>The rows in a band of a one-value plane's horizontal sweep: eight vectors, as many floats as a band of pixels.</summary>
    internal const int PlaneBand = 64;

    /// <summary>The columns in a group of the vertical sweep.</summary>
    internal const int Group = 64;

    private static readonly bool Fast = Avx2.IsSupported && Vector256.IsHardwareAccelerated;

    /// <summary>Gets whether <typeparamref name="TPixel"/> takes the byte path: whole straight bytes, not CMYK inks.</summary>
    internal static bool Supports<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return StoredRows<TPixel>.Bytes && !CmykInk.Is<TPixel>();
    }

    /// <summary>
    /// Blurs <paramref name="source"/> into <paramref name="destination"/>, <paramref name="x"/> across and <paramref name="y"/>
    /// down, in linear light through <paramref name="curve"/> when there is one.
    /// </summary>
    internal static void Run<TPixel>(PixelPlane<TPixel> source, PixelPlane<TPixel> destination, BoxCascade x, BoxCascade y, Colors.LinearCurve? curve, IPixelMemorySource memory, int maxParallelism, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = source.Width, height = source.Height, channels = Unsafe.SizeOf<TPixel>(), alpha = StoredRows<TPixel>.AlphaByte;
        int[] translucent = ArrayPool<int>.Shared.Rent(height + 1);
        translucent.AsSpan(0, height + 1).Clear();

        // Linear light needs the 16-bit plane too: a dark color's linear value is a small fraction of a level.
        bool deep = curve is not null;
        for (int v = 0; v < height && alpha >= 0 && !deep; v++)
        {
            deep = !Opaque(MemoryMarshal.AsBytes(source.Row(v)), channels, alpha);
        }

        PixelPlane<Gray16>? plane = deep ? PixelPlane.Allocate<Gray16>(width * channels, height, memory) : null;
        try
        {
            Layout layout = new(width, channels, alpha, curve?.LinearFrom8Times255, curve);
            int bands = (height + Band - 1) / Band;
            Chunks.Run(bands, (long)width * height * channels, maxParallelism, (start, end) =>
            {
                using LaneBoxes lanes = new(x, channels * Band);
                byte[] rows = ArrayPool<byte>.Shared.Rent(Band * layout.RowBytes * 3);
                try
                {
                    for (int b = start; b < end; b++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Across(lanes, layout, source, destination, plane, b * Band, rows, translucent);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rows);
                }
            }, cancellationToken);

            // translucent[v + 1] becomes the count of translucent rows up to v, so a window's count is a difference.
            for (int v = 0; v < height; v++)
            {
                translucent[v + 1] += translucent[v];
            }

            int groups = (width + Group - 1) / Group;
            Chunks.Run(groups, (long)width * height * channels, maxParallelism, (start, end) =>
            {
                using LaneBoxes lanes = new(y, channels * Group);
                for (int g = start; g < end; g++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Down(lanes, layout, destination, plane, g * Group, translucent);
                }
            }, cancellationToken);
        }
        finally
        {
            plane?.Dispose();
            ArrayPool<int>.Shared.Return(translucent);
        }
    }

    /// <summary>
    /// Blurs a float plane of one value per pixel in place, <paramref name="x"/> across and <paramref name="y"/> down, with the
    /// same two sweeps (DropShadow's alpha). Edges repeat the edge value.
    /// </summary>
    internal static void Run(PixelPlane<GrayF32> plane, BoxCascade x, BoxCascade y, int maxParallelism, CancellationToken cancellationToken)
    {
        int width = plane.Width, height = plane.Height;
        int bands = (height + PlaneBand - 1) / PlaneBand;
        Chunks.Run(bands, (long)width * height * 4, maxParallelism, (start, end) =>
        {
            using LaneBoxes lanes = new(x, PlaneBand);
            float[] scratch = ArrayPool<float>.Shared.Rent(PlaneBand * width);
            try
            {
                for (int b = start; b < end; b++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AcrossPlane(lanes, plane, b * PlaneBand, scratch);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }, cancellationToken);

        int groups = (width + Group - 1) / Group;
        Chunks.Run(groups, (long)width * height * 4, maxParallelism, (start, end) =>
        {
            using LaneBoxes lanes = new(y, Group);
            for (int g = start; g < end; g++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DownPlane(lanes, plane, g * Group);
            }
        }, cancellationToken);
    }

    /// <summary>The horizontal sweep of <see cref="Run(PixelPlane{GrayF32}, BoxCascade, BoxCascade, int, CancellationToken)"/> over the band from <paramref name="y0"/>, in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void AcrossPlane(LaneBoxes lanes, PixelPlane<GrayF32> plane, int y0, float[] scratch)
    {
        int width = plane.Width, rows = Math.Min(PlaneBand, plane.Height - y0);
        Rows<GrayF32> access = new(plane);
        Span<float> band = MemoryMarshal.Cast<byte, float>(rows == PlaneBand ? access.Band(y0, PlaneBand) : default);
        bool direct = !band.IsEmpty;
        if (!direct)
        {
            band = scratch.AsSpan(0, PlaneBand * width);
            for (int j = 0; j < PlaneBand; j++)
            {
                MemoryMarshal.Cast<byte, float>(access[y0 + Math.Min(j, rows - 1)]).CopyTo(band[(j * width)..]);
            }
        }

        // Eight columns at a time, column i, row j at i × PlaneBand + j: written back in place, as a column is stored only after every
        // column it reads was loaded.
        Span<float> columns = stackalloc float[8 * PlaneBand];
        Span<float> made = stackalloc float[8 * PlaneBand];
        int loaded = int.MinValue;
        lanes.Start();
        for (int s = -lanes.Reach; s < width + lanes.Reach; s++)
        {
            int x = Math.Clamp(s, 0, width - 1), block = x >> 3;
            if (block != loaded)
            {
                TransposeColumns(band, width, block * 8, Math.Min(8, width - (block * 8)), columns, load: true);
                loaded = block;
            }

            Copy(columns.Slice((x & 7) * PlaneBand, PlaneBand), lanes.Input());
            if (lanes.Advance(out int o))
            {
                int i = o & 7;
                Copy(lanes.Output, made.Slice(i * PlaneBand, PlaneBand));
                if (i == 7 || o == width - 1)
                {
                    TransposeColumns(band, width, o - i, i + 1, made, load: false);
                }
            }
        }

        if (!direct)
        {
            for (int j = 0; j < rows; j++)
            {
                band.Slice(j * width, width).CopyTo(MemoryMarshal.Cast<byte, float>(access[y0 + j]));
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="count"/> columns from <paramref name="x0"/> between the band's rows and <paramref name="columns"/>
    /// (column i, row j at i × <see cref="PlaneBand"/> + j): into the columns when <paramref name="load"/>, back into the rows otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void TransposeColumns(Span<float> band, int width, int x0, int count, Span<float> columns, bool load)
    {
        if (Fast && count == 8)
        {
            if (band.Length < ((PlaneBand - 1) * width) + x0 + 8 || columns.Length < 8 * PlaneBand)
            {
                throw new ArgumentException("The band is too short.", nameof(band));
            }

            ref float rows = ref Unsafe.Add(ref MemoryMarshal.GetReference(band), x0);
            ref float cells = ref MemoryMarshal.GetReference(columns);
            for (int half = 0; half < PlaneBand; half += 8)
            {
                ref float from = ref Unsafe.Add(ref rows, half * width);
                if (load)
                {
                    Vector256<float> m0 = Vector256.LoadUnsafe(ref from), m1 = Vector256.LoadUnsafe(ref from, (nuint)width);
                    Vector256<float> m2 = Vector256.LoadUnsafe(ref from, (nuint)(2 * width)), m3 = Vector256.LoadUnsafe(ref from, (nuint)(3 * width));
                    Vector256<float> m4 = Vector256.LoadUnsafe(ref from, (nuint)(4 * width)), m5 = Vector256.LoadUnsafe(ref from, (nuint)(5 * width));
                    Vector256<float> m6 = Vector256.LoadUnsafe(ref from, (nuint)(6 * width)), m7 = Vector256.LoadUnsafe(ref from, (nuint)(7 * width));
                    Transpose(ref m0, ref m1, ref m2, ref m3, ref m4, ref m5, ref m6, ref m7);
                    ref float to = ref Unsafe.Add(ref cells, half);
                    m0.StoreUnsafe(ref to);
                    m1.StoreUnsafe(ref to, PlaneBand);
                    m2.StoreUnsafe(ref to, 2 * PlaneBand);
                    m3.StoreUnsafe(ref to, 3 * PlaneBand);
                    m4.StoreUnsafe(ref to, 4 * PlaneBand);
                    m5.StoreUnsafe(ref to, 5 * PlaneBand);
                    m6.StoreUnsafe(ref to, 6 * PlaneBand);
                    m7.StoreUnsafe(ref to, 7 * PlaneBand);
                }
                else
                {
                    ref float at = ref Unsafe.Add(ref cells, half);
                    Vector256<float> m0 = Vector256.LoadUnsafe(ref at), m1 = Vector256.LoadUnsafe(ref at, PlaneBand);
                    Vector256<float> m2 = Vector256.LoadUnsafe(ref at, 2 * PlaneBand), m3 = Vector256.LoadUnsafe(ref at, 3 * PlaneBand);
                    Vector256<float> m4 = Vector256.LoadUnsafe(ref at, 4 * PlaneBand), m5 = Vector256.LoadUnsafe(ref at, 5 * PlaneBand);
                    Vector256<float> m6 = Vector256.LoadUnsafe(ref at, 6 * PlaneBand), m7 = Vector256.LoadUnsafe(ref at, 7 * PlaneBand);
                    Transpose(ref m0, ref m1, ref m2, ref m3, ref m4, ref m5, ref m6, ref m7);
                    m0.StoreUnsafe(ref from);
                    m1.StoreUnsafe(ref from, (nuint)width);
                    m2.StoreUnsafe(ref from, (nuint)(2 * width));
                    m3.StoreUnsafe(ref from, (nuint)(3 * width));
                    m4.StoreUnsafe(ref from, (nuint)(4 * width));
                    m5.StoreUnsafe(ref from, (nuint)(5 * width));
                    m6.StoreUnsafe(ref from, (nuint)(6 * width));
                    m7.StoreUnsafe(ref from, (nuint)(7 * width));
                }
            }

            return;
        }

        for (int j = 0; j < PlaneBand; j++)
        {
            for (int i = 0; i < count; i++)
            {
                ref float cell = ref columns[(i * PlaneBand) + j];
                ref float pixel = ref band[(j * width) + x0 + i];
                if (load)
                {
                    cell = pixel;
                }
                else
                {
                    pixel = cell;
                }
            }
        }
    }

    /// <summary>The vertical sweep of <see cref="Run(PixelPlane{GrayF32}, BoxCascade, BoxCascade, int, CancellationToken)"/> over the group of columns from <paramref name="x0"/>, in place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DownPlane(LaneBoxes lanes, PixelPlane<GrayF32> plane, int x0)
    {
        int height = plane.Height, reach = lanes.Reach, count = Math.Min(Group, plane.Width - x0);
        Rows<GrayF32> reader = new(plane), writer = new(plane), ahead = new(plane);
        lanes.Start();
        for (int s = -reach; s < height + reach; s++)
        {
            int v = Math.Clamp(s, 0, height - 1);
            if (Sse.IsSupported)
            {
                Prefetch(ahead[Math.Min(v + Ahead, height - 1)].Slice(x0 * 4, count * 4));
            }

            Span<float> target = lanes.Input();
            MemoryMarshal.Cast<byte, float>(reader[v]).Slice(x0, count).CopyTo(target);
            target[count..].Clear();
            if (lanes.Advance(out int o))
            {
                lanes.Output[..count].CopyTo(MemoryMarshal.Cast<byte, float>(writer[o]).Slice(x0, count));
            }
        }
    }

    /// <summary>
    /// The horizontal sweep over the band of rows from <paramref name="y0"/>. A position is one column of the band: a vector pair
    /// per channel, each lane a row. Columns load and store eight at a time, transposed. The band's rows are read and written in
    /// place when the planes are contiguous, else through <paramref name="scratch"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Across<TPixel>(LaneBoxes lanes, Layout layout, PixelPlane<TPixel> source, PixelPlane<TPixel> destination, PixelPlane<Gray16>? plane, int y0, byte[] scratch, int[] translucent)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = layout.Width, channels = layout.Channels, alpha = layout.Alpha, rowBytes = layout.RowBytes, size = Band * rowBytes;
        int rows = Math.Min(Band, source.Height - y0);
        Rows<TPixel> sourceRows = new(source), destinationRows = new(destination);
        ReadOnlySpan<byte> input = rows == Band ? sourceRows.Band(y0, Band) : default;
        if (input.IsEmpty)
        {
            // The band's rows are copied, the last real one repeated past the bottom.
            Span<byte> copy = scratch.AsSpan(0, size);
            for (int j = 0; j < Band; j++)
            {
                sourceRows[y0 + Math.Min(j, rows - 1)].CopyTo(copy[(j * rowBytes)..]);
            }

            input = copy;
        }

        bool premultiply = false;
        if (plane is not null && alpha >= 0)
        {
            for (int j = 0; j < rows; j++)
            {
                if (!Opaque(input.Slice(j * rowBytes, rowBytes), channels, alpha))
                {
                    translucent[y0 + j + 1] = 1;
                    premultiply = true;
                }
            }
        }

        Span<byte> output = plane is null && rows == Band ? destinationRows.Band(y0, Band) : default;
        bool direct = !output.IsEmpty;
        output = direct ? output : scratch.AsSpan(size, size);
        Span<ushort> deep = plane is null ? default : MemoryMarshal.Cast<byte, ushort>(scratch.AsSpan(size, 2 * size));

        // Eight columns of the band at a time, channel-major: column i, channel k, row j at ((i × channels) + k) × Band + j.
        int stride = channels * Band;
        Span<float> columns = stackalloc float[8 * stride];
        Span<float> made = stackalloc float[8 * stride];
        int loaded = int.MinValue;
        lanes.Start();
        for (int s = -lanes.Reach; s < width + lanes.Reach; s++)
        {
            int x = Math.Clamp(s, 0, width - 1), block = x >> 3;
            if (block != loaded)
            {
                if (Sse.IsSupported)
                {
                    // A band reads and writes more rows at once than the hardware follows; ask for the lines a few blocks on.
                    int ahead = Math.Min(((block * 8) + PrefetchColumns) * channels, rowBytes - 1);
                    PrefetchColumn(input, ahead, rowBytes);
                    PrefetchColumn(output, ahead, rowBytes);
                }

                LoadColumns(layout, input, block * 8, Math.Min(8, width - (block * 8)), premultiply, columns);
                loaded = block;
            }

            Copy(columns.Slice((x & 7) * stride, stride), lanes.Input());
            if (lanes.Advance(out int o))
            {
                int i = o & 7;
                Copy(lanes.Output, made.Slice(i * stride, stride));
                if (i == 7 || o == width - 1)
                {
                    StoreColumns(layout, output, deep, o - i, i + 1, made);
                }
            }
        }

        if (direct)
        {
            return;
        }

        for (int j = 0; j < rows; j++)
        {
            if (plane is null)
            {
                output.Slice(j * rowBytes, rowBytes).CopyTo(destinationRows[y0 + j]);
            }
            else
            {
                deep.Slice(j * rowBytes, rowBytes).CopyTo(MemoryMarshal.Cast<Gray16, ushort>(plane.Row(y0 + j)));
            }
        }
    }

    /// <summary>How many columns ahead the horizontal sweep asks for its lines.</summary>
    private const int PrefetchColumns = 48;

    /// <summary>Asks the cache for the line at <paramref name="at"/> in each of the band's rows.</summary>
    private static unsafe void PrefetchColumn(ReadOnlySpan<byte> band, int at, int rowBytes)
    {
        ref byte start = ref Unsafe.Add(ref MemoryMarshal.GetReference(band), at);
        for (int j = 0; j < Band; j++)
        {
            Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref start, j * rowBytes)));
        }
    }

    /// <summary>Copies whole vectors of floats.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy(ReadOnlySpan<float> from, Span<float> to)
    {
        if (to.Length < from.Length)
        {
            throw new ArgumentException("The target is too short.", nameof(to));
        }

        ref float f = ref MemoryMarshal.GetReference(from);
        ref float t = ref MemoryMarshal.GetReference(to);
        for (nuint i = 0; i < (nuint)from.Length; i += (nuint)Vector256<float>.Count)
        {
            Vector256.LoadUnsafe(ref f, i).StoreUnsafe(ref t, i);
        }
    }

    /// <summary>Loads <paramref name="count"/> columns of the band from <paramref name="x0"/>, channel-major, premultiplied if asked.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void LoadColumns(Layout layout, ReadOnlySpan<byte> band, int x0, int count, bool premultiply, Span<float> columns)
    {
        int channels = layout.Channels, alpha = layout.Alpha, rowBytes = layout.RowBytes, stride = channels * Band;
        float[]? linear = layout.Linear;
        if (Fast && channels == 4 && count == 8 && linear is not null)
        {
            LoadLinear(band, x0, rowBytes, stride, alpha, premultiply, linear, columns);
            return;
        }

        if (Fast && channels == 4 && count == 8)
        {
            // Eight rows of eight pixels at a time, transposed: vector i is pixel i of every row, one 32-bit pixel per lane.
            if (band.Length < ((Band - 1) * rowBytes) + (x0 * 4) + 32 || columns.Length < 8 * stride)
            {
                throw new ArgumentException("The band is too short.", nameof(band));
            }

            ref byte from = ref Unsafe.Add(ref MemoryMarshal.GetReference(band), x0 * 4);
            ref float to = ref MemoryMarshal.GetReference(columns);
            Vector256<int> low = Vector256.Create(0xFF);
            Vector256<float> unit = Vector256.Create(1f / 255);
            for (int half = 0; half < Band; half += 8)
            {
                ref byte rows = ref Unsafe.Add(ref from, half * rowBytes);
                Vector256<float> m0 = Vector256.LoadUnsafe(ref rows).AsSingle();
                Vector256<float> m1 = Vector256.LoadUnsafe(ref rows, (nuint)rowBytes).AsSingle();
                Vector256<float> m2 = Vector256.LoadUnsafe(ref rows, (nuint)(2 * rowBytes)).AsSingle();
                Vector256<float> m3 = Vector256.LoadUnsafe(ref rows, (nuint)(3 * rowBytes)).AsSingle();
                Vector256<float> m4 = Vector256.LoadUnsafe(ref rows, (nuint)(4 * rowBytes)).AsSingle();
                Vector256<float> m5 = Vector256.LoadUnsafe(ref rows, (nuint)(5 * rowBytes)).AsSingle();
                Vector256<float> m6 = Vector256.LoadUnsafe(ref rows, (nuint)(6 * rowBytes)).AsSingle();
                Vector256<float> m7 = Vector256.LoadUnsafe(ref rows, (nuint)(7 * rowBytes)).AsSingle();
                Transpose(ref m0, ref m1, ref m2, ref m3, ref m4, ref m5, ref m6, ref m7);
                Split(m0, ref Unsafe.Add(ref to, half), premultiply, alpha, low, unit);
                Split(m1, ref Unsafe.Add(ref to, stride + half), premultiply, alpha, low, unit);
                Split(m2, ref Unsafe.Add(ref to, (2 * stride) + half), premultiply, alpha, low, unit);
                Split(m3, ref Unsafe.Add(ref to, (3 * stride) + half), premultiply, alpha, low, unit);
                Split(m4, ref Unsafe.Add(ref to, (4 * stride) + half), premultiply, alpha, low, unit);
                Split(m5, ref Unsafe.Add(ref to, (5 * stride) + half), premultiply, alpha, low, unit);
                Split(m6, ref Unsafe.Add(ref to, (6 * stride) + half), premultiply, alpha, low, unit);
                Split(m7, ref Unsafe.Add(ref to, (7 * stride) + half), premultiply, alpha, low, unit);
            }

            return;
        }

        for (int j = 0; j < Band; j++)
        {
            ReadOnlySpan<byte> row = band.Slice((j * rowBytes) + (x0 * channels), count * channels);
            for (int i = 0; i < count; i++)
            {
                float a = premultiply ? row[(i * channels) + alpha] * (1f / 255) : 1;
                for (int k = 0; k < channels; k++)
                {
                    int b = row[(i * channels) + k];
                    float v = k == alpha || linear is null ? b : linear[b];
                    columns[(i * stride) + (k * Band) + j] = k == alpha || !premultiply ? v : v * a;
                }
            }
        }
    }

    /// <summary>
    /// <see cref="LoadColumns"/>'s vector path for linear light: eight rows of eight pixels transposed, colors looked up in
    /// <paramref name="linear"/> eight at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static unsafe void LoadLinear(ReadOnlySpan<byte> band, int x0, int rowBytes, int stride, int alpha, bool premultiply, float[] linear, Span<float> columns)
    {
        if (band.Length < ((Band - 1) * rowBytes) + (x0 * 4) + 32 || columns.Length < 8 * stride)
        {
            throw new ArgumentException("The band is too short.", nameof(band));
        }

        ref byte from = ref Unsafe.Add(ref MemoryMarshal.GetReference(band), x0 * 4);
        ref float to = ref MemoryMarshal.GetReference(columns);
        Vector256<int> low = Vector256.Create(0xFF);
        Vector256<float> unit = Vector256.Create(1f / 255);
        Span<Vector256<float>> m = stackalloc Vector256<float>[8];
        fixed (float* table = linear)
        {
            for (int half = 0; half < Band; half += 8)
            {
                ref byte rows = ref Unsafe.Add(ref from, half * rowBytes);
                Vector256<float> m0 = Vector256.LoadUnsafe(ref rows).AsSingle();
                Vector256<float> m1 = Vector256.LoadUnsafe(ref rows, (nuint)rowBytes).AsSingle();
                Vector256<float> m2 = Vector256.LoadUnsafe(ref rows, (nuint)(2 * rowBytes)).AsSingle();
                Vector256<float> m3 = Vector256.LoadUnsafe(ref rows, (nuint)(3 * rowBytes)).AsSingle();
                Vector256<float> m4 = Vector256.LoadUnsafe(ref rows, (nuint)(4 * rowBytes)).AsSingle();
                Vector256<float> m5 = Vector256.LoadUnsafe(ref rows, (nuint)(5 * rowBytes)).AsSingle();
                Vector256<float> m6 = Vector256.LoadUnsafe(ref rows, (nuint)(6 * rowBytes)).AsSingle();
                Vector256<float> m7 = Vector256.LoadUnsafe(ref rows, (nuint)(7 * rowBytes)).AsSingle();
                Transpose(ref m0, ref m1, ref m2, ref m3, ref m4, ref m5, ref m6, ref m7);
                m[0] = m0;
                m[1] = m1;
                m[2] = m2;
                m[3] = m3;
                m[4] = m4;
                m[5] = m5;
                m[6] = m6;
                m[7] = m7;
                for (int i = 0; i < 8; i++)
                {
                    Vector256<int> pixel = m[i].AsInt32();
                    ref float at = ref Unsafe.Add(ref to, (i * stride) + half);
                    Vector256<float> a = Vector256.ConvertToSingle((pixel >>> (alpha * 8)) & low);
                    Vector256<float> share = a * unit;
                    for (int k = 0; k < 4; k++)
                    {
                        Vector256<float> c = k == alpha ? a : Avx2.GatherVector256(table, (pixel >>> (k * 8)) & low, 4);
                        (k == alpha || !premultiply ? c : c * share).StoreUnsafe(ref at, (nuint)(k * Band));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Stores <paramref name="count"/> columns from <paramref name="x0"/> into the band's rows: bytes over the copy of the source,
    /// or 16-bit values into <paramref name="deep"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void StoreColumns(Layout layout, Span<byte> band, Span<ushort> deep, int x0, int count, ReadOnlySpan<float> columns)
    {
        int channels = layout.Channels, rowBytes = layout.RowBytes, stride = channels * Band;
        if (!deep.IsEmpty && Fast && channels == 4 && count == 8)
        {
            StoreDeep(deep, x0, rowBytes, stride, columns);
            return;
        }

        if (!deep.IsEmpty)
        {
            for (int j = 0; j < Band; j++)
            {
                Span<ushort> row = deep.Slice((j * rowBytes) + (x0 * channels), count * channels);
                for (int i = 0; i < count; i++)
                {
                    for (int k = 0; k < channels; k++)
                    {
                        row[(i * channels) + k] = Round16(columns[(i * stride) + (k * Band) + j]);
                    }
                }
            }

            return;
        }

        if (Fast && channels == 4 && count == 8)
        {
            // Each column's four channels packed into one 32-bit pixel per row, then transposed back to rows.
            if (band.Length < ((Band - 1) * rowBytes) + (x0 * 4) + 32 || columns.Length < 8 * stride)
            {
                throw new ArgumentException("The band is too short.", nameof(band));
            }

            ref float from = ref MemoryMarshal.GetReference(columns);
            ref byte to = ref Unsafe.Add(ref MemoryMarshal.GetReference(band), x0 * 4);
            for (int half = 0; half < Band; half += 8)
            {
                Vector256<float> m0 = Pack(ref Unsafe.Add(ref from, half));
                Vector256<float> m1 = Pack(ref Unsafe.Add(ref from, stride + half));
                Vector256<float> m2 = Pack(ref Unsafe.Add(ref from, (2 * stride) + half));
                Vector256<float> m3 = Pack(ref Unsafe.Add(ref from, (3 * stride) + half));
                Vector256<float> m4 = Pack(ref Unsafe.Add(ref from, (4 * stride) + half));
                Vector256<float> m5 = Pack(ref Unsafe.Add(ref from, (5 * stride) + half));
                Vector256<float> m6 = Pack(ref Unsafe.Add(ref from, (6 * stride) + half));
                Vector256<float> m7 = Pack(ref Unsafe.Add(ref from, (7 * stride) + half));
                Transpose(ref m0, ref m1, ref m2, ref m3, ref m4, ref m5, ref m6, ref m7);
                ref byte rows = ref Unsafe.Add(ref to, half * rowBytes);
                m0.AsByte().StoreUnsafe(ref rows);
                m1.AsByte().StoreUnsafe(ref rows, (nuint)rowBytes);
                m2.AsByte().StoreUnsafe(ref rows, (nuint)(2 * rowBytes));
                m3.AsByte().StoreUnsafe(ref rows, (nuint)(3 * rowBytes));
                m4.AsByte().StoreUnsafe(ref rows, (nuint)(4 * rowBytes));
                m5.AsByte().StoreUnsafe(ref rows, (nuint)(5 * rowBytes));
                m6.AsByte().StoreUnsafe(ref rows, (nuint)(6 * rowBytes));
                m7.AsByte().StoreUnsafe(ref rows, (nuint)(7 * rowBytes));
            }

            return;
        }

        for (int j = 0; j < Band; j++)
        {
            Span<byte> row = band.Slice((j * rowBytes) + (x0 * channels), count * channels);
            for (int i = 0; i < count; i++)
            {
                for (int k = 0; k < channels; k++)
                {
                    row[(i * channels) + k] = Round(columns[(i * stride) + (k * Band) + j]);
                }
            }
        }
    }

    /// <summary>
    /// <see cref="StoreColumns"/>'s vector path for 16-bit values: each column's four channels rounded to v × 256, saturating,
    /// and interleaved into one 64-bit pixel per row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void StoreDeep(Span<ushort> deep, int x0, int rowBytes, int stride, ReadOnlySpan<float> columns)
    {
        if (deep.Length < ((Band - 1) * rowBytes) + (x0 * 4) + 32 || columns.Length < 8 * stride)
        {
            throw new ArgumentException("The band is too short.", nameof(deep));
        }

        ref float from = ref MemoryMarshal.GetReference(columns);
        ref byte to = ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref MemoryMarshal.GetReference(deep), x0 * 4));
        Vector256<float> scale = Vector256.Create(256f), half = Vector256.Create(0.5f);
        for (int i = 0; i < 8; i++)
        {
            for (int part = 0; part < Band; part += 8)
            {
                ref float at = ref Unsafe.Add(ref from, (i * stride) + part);
                Vector256<int> c0 = Vector256.ConvertToInt32((Vector256.LoadUnsafe(ref at) * scale) + half);
                Vector256<int> c1 = Vector256.ConvertToInt32((Vector256.LoadUnsafe(ref at, Band) * scale) + half);
                Vector256<int> c2 = Vector256.ConvertToInt32((Vector256.LoadUnsafe(ref at, 2 * Band) * scale) + half);
                Vector256<int> c3 = Vector256.ConvertToInt32((Vector256.LoadUnsafe(ref at, 3 * Band) * scale) + half);
                Vector256<ushort> top = Vector256.Create((ushort)(255 << 8));
                Vector256<ushort> p01 = Vector256.Min(Avx2.PackUnsignedSaturate(c0, c1), top), p23 = Vector256.Min(Avx2.PackUnsignedSaturate(c2, c3), top);

                // Per 128-bit half: rows 0 to 3 (4 to 7) of channels 0 and 1, then of 2 and 3; two unpacks interleave each row's four.
                Vector256<ushort> even = Avx2.UnpackLow(p01, p23), odd = Avx2.UnpackHigh(p01, p23);
                Vector256<ulong> rows01 = Avx2.UnpackLow(even, odd).AsUInt64(), rows23 = Avx2.UnpackHigh(even, odd).AsUInt64();
                ref byte pixel = ref Unsafe.Add(ref to, ((part * rowBytes) + (i * 4)) * 2);
                int step = rowBytes * 2;
                Unsafe.WriteUnaligned(ref pixel, rows01.GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pixel, step), rows01.GetElement(1));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pixel, 2 * step), rows23.GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pixel, 3 * step), rows23.GetElement(1));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pixel, 4 * step), rows01.GetElement(2));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pixel, 5 * step), rows01.GetElement(3));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pixel, 6 * step), rows23.GetElement(2));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref pixel, 7 * step), rows23.GetElement(3));
            }
        }
    }

    /// <summary>
    /// The vertical sweep over the group of columns from <paramref name="x0"/>, in place on the destination. A position is one row
    /// of the group, its values as floats in order. Rows are stored straight, alpha divided out where a translucent row is in reach.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Down<TPixel>(LaneBoxes lanes, Layout layout, PixelPlane<TPixel> image, PixelPlane<Gray16>? plane, int x0, int[] translucent)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int height = image.Height, channels = layout.Channels, alpha = layout.Alpha, reach = lanes.Reach;
        int count = Math.Min(Group, layout.Width - x0), length = count * channels, at = x0 * channels;
        Rows<TPixel> reader = new(image), writer = new(image), ahead = new(image);
        Rows<Gray16> deep = plane is null ? default : new(plane), deepAhead = plane is null ? default : new(plane);

        // Rows are written back in place: a row is stored only after every row it reads was loaded, and rows past the bottom
        // all load the last row, which is stored last.
        lanes.Start();
        for (int s = -reach; s < height + reach; s++)
        {
            int v = Math.Clamp(s, 0, height - 1);
            if (Sse.IsSupported)
            {
                // The rows a group reads are a row apart in memory, too far for the hardware to guess; ask for them ahead.
                int next = Math.Min(v + Ahead, height - 1);
                Prefetch(plane is null ? ahead[next].Slice(at, length) : deepAhead[next].Slice(at * 2, length * 2));
            }

            Span<float> target = lanes.Input();
            if (plane is null)
            {
                Widen(reader[v].Slice(at, length), target);
            }
            else
            {
                Widen(MemoryMarshal.Cast<byte, ushort>(deep[v]).Slice(at, length), target);
            }

            if (lanes.Advance(out int o))
            {
                bool opaque = alpha < 0 || translucent[Math.Min(height, o + reach + 1)] == translucent[Math.Max(0, o - reach)];
                if (layout.Curve is { } curve)
                {
                    Encode(lanes.Output, writer[o].Slice(at, length), channels, opaque ? -1 : alpha, alpha, curve);
                }
                else
                {
                    Narrow(lanes.Output, writer[o].Slice(at, length), channels, opaque ? -1 : alpha, alpha);
                }
            }
        }
    }

    /// <summary>How many rows ahead the vertical sweep asks for its rows.</summary>
    private const int Ahead = 16;

    /// <summary>Asks the cache for every line of <paramref name="bytes"/>.</summary>
    private static unsafe void Prefetch(ReadOnlySpan<byte> bytes)
    {
        ref byte start = ref MemoryMarshal.GetReference(bytes);
        for (int i = 0; i < bytes.Length; i += 64)
        {
            Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref start, i)));
        }
    }

    /// <summary>Loads bytes as floats; the rest of <paramref name="target"/> is cleared.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Widen(ReadOnlySpan<byte> row, Span<float> target)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref byte from = ref MemoryMarshal.GetReference(row);
            ref float to = ref MemoryMarshal.GetReference(target);
            for (; i <= row.Length - 16; i += 16)
            {
                (Vector256<ushort> words, _) = Vector256.Widen(Vector128.LoadUnsafe(ref from, (nuint)i).ToVector256Unsafe());
                (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(words);
                Vector256.ConvertToSingle(lo.AsInt32()).StoreUnsafe(ref to, (nuint)i);
                Vector256.ConvertToSingle(hi.AsInt32()).StoreUnsafe(ref to, (nuint)(i + 8));
            }
        }

        for (; i < row.Length; i++)
        {
            target[i] = row[i];
        }

        target[row.Length..].Clear();
    }

    /// <summary>Loads 16-bit values (v × 256) as floats of 0..255; the rest of <paramref name="target"/> is cleared.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Widen(ReadOnlySpan<ushort> row, Span<float> target)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref ushort from = ref MemoryMarshal.GetReference(row);
            ref float to = ref MemoryMarshal.GetReference(target);
            Vector256<float> unit = Vector256.Create(1f / 256);
            for (; i <= row.Length - 16; i += 16)
            {
                (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(Vector256.LoadUnsafe(ref from, (nuint)i));
                (Vector256.ConvertToSingle(lo.AsInt32()) * unit).StoreUnsafe(ref to, (nuint)i);
                (Vector256.ConvertToSingle(hi.AsInt32()) * unit).StoreUnsafe(ref to, (nuint)(i + 8));
            }
        }

        for (; i < row.Length; i++)
        {
            target[i] = row[i] * (1f / 256);
        }

        target[row.Length..].Clear();
    }

    /// <summary>
    /// Stores floats of 0..255 as bytes. With <paramref name="divide"/> at the alpha channel, colors are divided by alpha;
    /// otherwise every value is rounded, and an <paramref name="alpha"/> channel is set opaque.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Narrow(ReadOnlySpan<float> values, Span<byte> row, int channels, int divide, int alpha)
    {
        if (divide < 0)
        {
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                ref float from = ref MemoryMarshal.GetReference(values);
                ref byte to = ref MemoryMarshal.GetReference(row);
                for (; i <= row.Length - 16; i += 16)
                {
                    Vector256<ushort> words = Vector256.Narrow(Round(Vector256.LoadUnsafe(ref from, (nuint)i)).AsUInt32(), Round(Vector256.LoadUnsafe(ref from, (nuint)(i + 8))).AsUInt32());
                    Vector256.Narrow(words, words).GetLower().StoreUnsafe(ref to, (nuint)i);
                }
            }

            for (; i < row.Length; i++)
            {
                row[i] = Round(values[i]);
            }

            if (alpha >= 0)
            {
                for (int p = alpha; p < row.Length; p += channels)
                {
                    row[p] = 255;
                }
            }

            return;
        }

        for (int p = 0; p < row.Length; p += channels)
        {
            float a = values[p + divide];
            byte a8 = Round(a);
            if (a8 == 0)
            {
                row.Slice(p, channels).Clear();
                continue;
            }

            float scale = 255f / a;
            for (int k = 0; k < channels; k++)
            {
                row[p + k] = k == divide ? a8 : Round(values[p + k] * scale);
            }
        }
    }

    /// <summary>
    /// Stores linear floats of 0..255 as encoded bytes through <paramref name="curve"/>, as <see cref="Narrow"/> does: colors
    /// divided by alpha when <paramref name="divide"/> is the alpha channel, else alpha set opaque.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Encode(ReadOnlySpan<float> values, Span<byte> row, int channels, int divide, int alpha, Colors.LinearCurve curve)
    {
        byte[] table = curve.EncodeFrom16;
        const float Top = 255 << 8;
        for (int p = 0; p < row.Length; p += channels)
        {
            // Linear values of 0..255 times 256 index the table; colors over alpha are scaled to 0..255 first.
            float scale = 256;
            byte a8 = 255;
            if (divide >= 0)
            {
                float a = values[p + divide];
                a8 = Round(a);
                if (a8 == 0)
                {
                    row.Slice(p, channels).Clear();
                    continue;
                }

                scale = Top / a;
            }

            for (int k = 0; k < channels; k++)
            {
                row[p + k] = k == alpha ? a8 : table[(int)(Math.Clamp(values[p + k] * scale, 0f, Top) + 0.5f)];
            }
        }
    }

    /// <summary>Rounds a value of 0..255 (with float overshoot) to a byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Round(float value) => (byte)Math.Clamp((int)(value + 0.5f), 0, 255);

    /// <summary>Rounds a value of 0..255 (with float overshoot) to 16-bit fixed point, v × 256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Round16(float value) => (ushort)Math.Clamp((int)((value * 256) + 0.5f), 0, 255 << 8);

    /// <summary>Rounds eight values of 0..255 (with float overshoot) to whole numbers 0 to 255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Round(Vector256<float> value)
    {
        return Vector256.Min(Vector256.Max(Vector256.ConvertToInt32(value + Vector256.Create(0.5f)), Vector256<int>.Zero), Vector256.Create(255));
    }

    private static bool Opaque(ReadOnlySpan<byte> bytes, int channels, int alpha)
    {
        if (channels == 4 && Vector256.IsHardwareAccelerated)
        {
            ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(bytes);
            uint bits = 0xFFu << (alpha * 8);
            Vector256<uint> mask = Vector256.Create(bits);
            int i = 0;
            for (; i <= pixels.Length - Vector256<uint>.Count; i += Vector256<uint>.Count)
            {
                if ((Vector256.Create(pixels[i..]) & mask) != mask)
                {
                    return false;
                }
            }

            for (; i < pixels.Length; i++)
            {
                if ((pixels[i] & bits) != bits)
                {
                    return false;
                }
            }

            return true;
        }

        for (int p = alpha; p < bytes.Length; p += channels)
        {
            if (bytes[p] != 255)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Transposes eight vectors of eight 32-bit lanes in place: lane j of vector i becomes lane i of vector j.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector256<float> m0, ref Vector256<float> m1, ref Vector256<float> m2, ref Vector256<float> m3, ref Vector256<float> m4, ref Vector256<float> m5, ref Vector256<float> m6, ref Vector256<float> m7)
    {
        Vector256<float> t0 = Avx.UnpackLow(m0, m1), t1 = Avx.UnpackHigh(m0, m1);
        Vector256<float> t2 = Avx.UnpackLow(m2, m3), t3 = Avx.UnpackHigh(m2, m3);
        Vector256<float> t4 = Avx.UnpackLow(m4, m5), t5 = Avx.UnpackHigh(m4, m5);
        Vector256<float> t6 = Avx.UnpackLow(m6, m7), t7 = Avx.UnpackHigh(m6, m7);
        Vector256<float> u0 = Avx.Shuffle(t0, t2, 0x44), u1 = Avx.Shuffle(t0, t2, 0xEE);
        Vector256<float> u2 = Avx.Shuffle(t1, t3, 0x44), u3 = Avx.Shuffle(t1, t3, 0xEE);
        Vector256<float> u4 = Avx.Shuffle(t4, t6, 0x44), u5 = Avx.Shuffle(t4, t6, 0xEE);
        Vector256<float> u6 = Avx.Shuffle(t5, t7, 0x44), u7 = Avx.Shuffle(t5, t7, 0xEE);
        m0 = Avx.Permute2x128(u0, u4, 0x20);
        m1 = Avx.Permute2x128(u1, u5, 0x20);
        m2 = Avx.Permute2x128(u2, u6, 0x20);
        m3 = Avx.Permute2x128(u3, u7, 0x20);
        m4 = Avx.Permute2x128(u0, u4, 0x31);
        m5 = Avx.Permute2x128(u1, u5, 0x31);
        m6 = Avx.Permute2x128(u2, u6, 0x31);
        m7 = Avx.Permute2x128(u3, u7, 0x31);
    }

    /// <summary>Splits eight 32-bit pixels into four channel vectors at <paramref name="to"/>, <see cref="Band"/> floats apart, premultiplied if asked.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Split(Vector256<float> pixels, ref float to, bool premultiply, int alpha, Vector256<int> low, Vector256<float> unit)
    {
        Vector256<int> pixel = pixels.AsInt32();
        Vector256<float> c0 = Vector256.ConvertToSingle(pixel & low), c1 = Vector256.ConvertToSingle((pixel >>> 8) & low);
        Vector256<float> c2 = Vector256.ConvertToSingle((pixel >>> 16) & low), c3 = Vector256.ConvertToSingle(pixel >>> 24);
        if (premultiply)
        {
            Vector256<float> a = (alpha == 0 ? c0 : alpha == 1 ? c1 : alpha == 2 ? c2 : c3) * unit;
            c0 = alpha == 0 ? c0 : c0 * a;
            c1 = alpha == 1 ? c1 : c1 * a;
            c2 = alpha == 2 ? c2 : c2 * a;
            c3 = alpha == 3 ? c3 : c3 * a;
        }

        c0.StoreUnsafe(ref to);
        c1.StoreUnsafe(ref to, Band);
        c2.StoreUnsafe(ref to, 2 * Band);
        c3.StoreUnsafe(ref to, 3 * Band);
    }

    /// <summary>
    /// Rounds four channel vectors at <paramref name="from"/>, <see cref="Band"/> floats apart, to bytes and packs them into one
    /// 32-bit pixel per lane, the packs saturating to 0..255.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Pack(ref float from)
    {
        Vector256<float> half = Vector256.Create(0.5f);
        Vector256<int> c0 = Vector256.ConvertToInt32(Vector256.LoadUnsafe(ref from) + half);
        Vector256<int> c1 = Vector256.ConvertToInt32(Vector256.LoadUnsafe(ref from, Band) + half);
        Vector256<int> c2 = Vector256.ConvertToInt32(Vector256.LoadUnsafe(ref from, 2 * Band) + half);
        Vector256<int> c3 = Vector256.ConvertToInt32(Vector256.LoadUnsafe(ref from, 3 * Band) + half);

        // Per 128-bit half: channel 0 of four lanes, then channels 1, 2 and 3; the shuffle gathers each lane's four channels.
        Vector256<byte> bytes = Avx2.PackUnsignedSaturate(Avx2.PackSignedSaturate(c0, c1), Avx2.PackSignedSaturate(c2, c3));
        return Avx2.Shuffle(bytes, Vector256.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15, 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15)).AsSingle();
    }

    /// <summary>
    /// The shape of a row of the image: its width, bytes per pixel and alpha byte (−1 for none), and for linear light the curve
    /// and each encoded byte's linear value × 255.
    /// </summary>
    internal readonly record struct Layout(int Width, int Channels, int Alpha, float[]? Linear = null, Colors.LinearCurve? Curve = null)
    {
        public int RowBytes => Width * Channels;
    }

    /// <summary>
    /// A plane's rows as bytes, through the run of rows stored together that holds the last one asked for, so rows asked for in
    /// order cost a slice each.
    /// </summary>
    private ref struct Rows<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly PixelPlane<TPixel> _plane;
        private readonly int _rowBytes;
        private Span<byte> _run;
        private int _first;
        private int _count;

        internal Rows(PixelPlane<TPixel> plane)
        {
            _plane = plane;
            _rowBytes = plane.Width * Unsafe.SizeOf<TPixel>();
        }

        internal Span<byte> this[int y]
        {
            get
            {
                Fetch(y);
                return _run.Slice((y - _first) * _rowBytes, _rowBytes);
            }
        }

        /// <summary>
        /// Returns <paramref name="band"/> rows from <paramref name="y0"/> as one span, when they are all there and stored together;
        /// otherwise an empty span.
        /// </summary>
        internal Span<byte> Band(int y0, int band)
        {
            Fetch(y0);
            return y0 + band <= _first + _count ? _run.Slice((y0 - _first) * _rowBytes, band * _rowBytes) : default;
        }

        private void Fetch(int y)
        {
            if ((uint)(y - _first) >= (uint)_count)
            {
                _run = MemoryMarshal.AsBytes(_plane.RowRun(y, out _first, out _count));
            }
        }
    }
}

/// <summary>
/// A <see cref="BoxCascade"/>'s passes along a line of positions as one streaming sweep: the caller fills <see cref="Input"/>
/// with positions −<see cref="Reach"/> to count − 1 + <see cref="Reach"/> in order, the edge repeated past either end, and after
/// each one <see cref="Advance"/> runs every pass that can run and says when <see cref="Output"/> holds a final position. A
/// position is <c>width</c> floats (whole vectors) filtered independently, so a vector lane can be a row, a column or a channel.
/// The passes run from the line's start, so a line's result depends on nothing else.
/// </summary>
internal sealed class LaneBoxes : IDisposable
{
    private readonly BoxCascade _cascade;
    private readonly int _width;
    private readonly int _passes;
    private readonly float[][] _rings;
    private readonly int[] _slots;
    private readonly int[] _heads;
    private readonly int[] _lag;
    private readonly int[] _after;
    private readonly float[][] _sums;
    private readonly float[] _output;
    private int _source;

    /// <summary>Rents a ring per pass for positions of <paramref name="width"/> floats.</summary>
    internal LaneBoxes(BoxCascade cascade, int width)
    {
        if (width % Vector256<float>.Count != 0)
        {
            throw new ArgumentException("A position must be whole vectors.", nameof(width));
        }

        _cascade = cascade;
        _width = width;
        _passes = cascade.Radii.Length;
        _rings = new float[_passes][];
        _slots = new int[_passes];
        _heads = new int[_passes];
        _lag = new int[_passes];
        _after = new int[_passes];
        _sums = new float[_passes][];
        for (int p = 0, ahead = 0; p < _passes; p++)
        {
            // Pass p reads 2r + 3 positions of its input at a time, which is the whole ring. It makes position s − lag once
            // source position s is in, and makes positions from −after, after being how far the passes after it read ahead.
            int r = cascade.Radii[p];
            _slots[p] = (2 * r) + 3;
            _rings[p] = ArrayPool<float>.Shared.Rent(_slots[p] * width);
            _sums[p] = ArrayPool<float>.Shared.Rent(width);
            ahead += r + 1;
            _lag[p] = ahead;
            _after[p] = cascade.Reach - ahead;
        }

        _output = ArrayPool<float>.Shared.Rent(width);
    }

    /// <summary>Gets how far the passes together read past a position.</summary>
    internal int Reach => _cascade.Reach;

    /// <summary>Gets the final position <see cref="Advance"/> last made.</summary>
    internal ReadOnlySpan<float> Output => _output.AsSpan(0, _width);

    /// <summary>Starts a line: the next <see cref="Input"/> is position −<see cref="Reach"/>.</summary>
    internal void Start()
    {
        _source = -Reach - 1;
        for (int p = 0; p < _passes; p++)
        {
            _heads[p] = _slots[p] - 1;
        }
    }

    /// <summary>Returns the slot to fill with the next source position.</summary>
    internal Span<float> Input()
    {
        _source++;
        return Next(0);
    }

    /// <summary>Runs every pass the new source position lets run.</summary>
    /// <param name="position">The final position made, when there is one.</param>
    /// <returns><see langword="true"/> if <see cref="Output"/> holds final position <paramref name="position"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal bool Advance(out int position)
    {
        for (int p = 0; p < _passes; p++)
        {
            int o = _source - _lag[p];
            if (o < -_after[p])
            {
                break;
            }

            bool last = p == _passes - 1;
            Step(p, o == -_after[p], last ? _output.AsSpan(0, _width) : Next(p + 1));
            if (last)
            {
                position = o;
                return true;
            }
        }

        position = 0;
        return false;
    }

    public void Dispose()
    {
        for (int p = 0; p < _passes; p++)
        {
            ArrayPool<float>.Shared.Return(_rings[p]);
            ArrayPool<float>.Shared.Return(_sums[p]);
        }

        ArrayPool<float>.Shared.Return(_output);
    }

    /// <summary>Moves pass <paramref name="p"/>'s ring on to the slot for its next input position, the oldest one, and returns it.</summary>
    private Span<float> Next(int p)
    {
        int head = _heads[p] + 1;
        head = head == _slots[p] ? 0 : head;
        _heads[p] = head;
        return _rings[p].AsSpan(head * _width, _width);
    }

    /// <summary>
    /// Makes pass <paramref name="p"/>'s next position o, norm × Σ in[o − r .. o + r] + αnorm × (in[o − r − 1] + in[o + r + 1]),
    /// then slides the window sum on to o + 1. The ring holds exactly in[o − r − 1 .. o + r + 1], the newest at the head, so the
    /// oldest is the slot after it. The first position sums its window from scratch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Step(int p, bool first, Span<float> target)
    {
        int slots = _slots[p], head = _heads[p], width = _width;
        int oldest = head + 1 == slots ? 0 : head + 1, leaving = oldest + 1 == slots ? 0 : oldest + 1;
        float norm = _cascade.Norms[p], outer = _cascade.Alphas[p] * norm;
        ref float ring = ref MemoryMarshal.GetArrayDataReference(_rings[p]);
        ref float s = ref MemoryMarshal.GetArrayDataReference(_sums[p]);
        if (first)
        {
            // in[o − r .. o + r]: every slot but the oldest and the head.
            _sums[p].AsSpan(0, width).Clear();
            for (int k = 0, slot = leaving; k < slots - 2; k++, slot = slot + 1 == slots ? 0 : slot + 1)
            {
                ref float row = ref Unsafe.Add(ref ring, slot * width);
                for (nuint i = 0; i < (nuint)width; i += (nuint)Vector256<float>.Count)
                {
                    (Vector256.LoadUnsafe(ref s, i) + Vector256.LoadUnsafe(ref row, i)).StoreUnsafe(ref s, i);
                }
            }
        }

        if (target.Length < width)
        {
            throw new ArgumentException("The target is too short.", nameof(target));
        }

        ref float u = ref Unsafe.Add(ref ring, oldest * width);
        ref float b = ref Unsafe.Add(ref ring, head * width);
        ref float l = ref Unsafe.Add(ref ring, leaving * width);
        ref float t = ref MemoryMarshal.GetReference(target);
        Vector256<float> vn = Vector256.Create(norm), vo = Vector256.Create(outer);
        for (nuint i = 0; i < (nuint)width; i += (nuint)Vector256<float>.Count)
        {
            Vector256<float> window = Vector256.LoadUnsafe(ref s, i), next = Vector256.LoadUnsafe(ref b, i);
            ((window * vn) + ((Vector256.LoadUnsafe(ref u, i) + next) * vo)).StoreUnsafe(ref t, i);
            (window + (next - Vector256.LoadUnsafe(ref l, i))).StoreUnsafe(ref s, i);
        }
    }
}
