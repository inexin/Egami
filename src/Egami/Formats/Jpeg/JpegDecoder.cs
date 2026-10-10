using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Decodes a Huffman-coded JPEG. A sequential file whose first scan holds every component is decoded one MCU row at a time:
/// entropy decoding, dequantization and IDCT into a strip of samples per component, then upsampling and color conversion of the
/// rows above into the raster, so memory is the raster plus a few rows. Progressive files and sequential files with a scan per
/// component are decoded into a buffer of every coefficient first, scan by scan, and converted the same way after the last scan.
/// </summary>
internal sealed class JpegDecoder<TPixel> : IDisposable
    where TPixel : unmanaged, IPixelType<TPixel>
{
    /// <summary>Images with fewer pixels than this decode on one thread; the pipeline only pays off on large ones.</summary>
    private const long ParallelPixels = 1 << 20;

    /// <summary>Gets or sets whether a parallel decode uses the pipeline whatever the image size, so tests can run it on small files.</summary>
    internal bool ForcePipeline { get; set; }

    /// <summary>MCU rows of coefficients between the entropy decoder and the rest of the pipeline.</summary>
    private const int PipelineSlots = 4;

    /// <summary>The most scans a file may have. Real progressive files have about 10; more only slows a hostile file down.</summary>
    internal const int MaxScans = 1000;

    private readonly JpegHeader _header;
    private readonly ReaderOptions _options;
    private readonly Raster<TPixel> _raster;
    private readonly Component[] _components;
    private readonly int _maxH;
    private readonly int _maxV;
    private readonly int _mcusX;
    private readonly int _mcusY;
    private readonly int _mcuRows;
    private readonly int _blockSize;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _fast;
    private readonly int _channels;
    private byte[] _rowBuffer;
    private byte[] _sampleBuffer;
    private short[] _sums;
    private readonly List<(JpegHuffmanSpec Spec, JpegHuffmanTable Table)> _tables = [];
    private bool _warned;

    /// <summary>Creates a decoder whose output is the image scaled by 1/<paramref name="scale"/> (1, 2, 4 or 8).</summary>
    internal JpegDecoder(JpegHeader header, ReaderOptions options, Raster<TPixel> raster, bool fastUpsampling, int scale = 1)
    {
        _header = header;
        _options = options;
        _raster = raster;
        _fast = fastUpsampling;
        bool single = header.Components.Length == 1;

        // A one-component frame is coded non-interleaved: its MCU is one block whatever its sampling factors say.
        _maxH = single ? 1 : header.MaxH;
        _maxV = single ? 1 : header.MaxV;
        _mcusX = (header.Width + (8 * _maxH) - 1) / (8 * _maxH);
        _mcusY = (header.Height + (8 * _maxV) - 1) / (8 * _maxV);

        // At 1/n scale a full-resolution block becomes 8/n samples a side.
        _blockSize = 8 / scale;
        _width = ScaledSize(header.Width, scale);
        _height = ScaledSize(header.Height, scale);
        _mcuRows = _blockSize * _maxV;
        _channels = single ? 1 : 3;
        _components = new Component[header.Components.Length];
        for (int i = 0; i < _components.Length; i++)
        {
            JpegFrameComponent c = header.Components[i];
            int h = single ? 1 : c.H;
            int v = single ? 1 : c.V;
            if (_maxH % h != 0 || _maxV % v != 0)
            {
                // As in libjpeg: a component sampled at, say, 3/4 of another has no whole upsampling ratio.
                throw new UnsupportedFormatException($"Fractional chroma sampling ({c.H}×{c.V} against {_maxH}×{_maxV}) is not supported.");
            }

            // Subsampled components are scaled up in the IDCT where they can be, a doubling at a time while it still divides
            // evenly in both directions, so a scaled decode upsamples less or not at all; this is libjpeg's choice too.
            int size = _blockSize;
            while (size < 8 && (_maxH * _blockSize) % (h * size * 2) == 0 && (_maxV * _blockSize) % (v * size * 2) == 0)
            {
                size *= 2;
            }

            _components[i] = new Component(
                h, v, size, _maxH * _blockSize, _maxV * _blockSize, _mcusX * h * size,
                (int)(((long)header.Width * h * size) + (_maxH * 8) - 1) / (_maxH * 8), (int)(((long)header.Height * v * size) + (_maxV * 8) - 1) / (_maxV * 8));
        }

        _rowBuffer = ArrayPool<byte>.Shared.Rent(header.Width * Math.Max(_channels, _components.Length));
        _sampleBuffer = ArrayPool<byte>.Shared.Rent(header.Width * _components.Length);
        _sums = ArrayPool<short>.Shared.Rent(header.Width + 2);
    }

    /// <summary>Returns the output size of a dimension at 1/<paramref name="scale"/> scale, rounded up as libjpeg does.</summary>
    internal static int ScaledSize(int size, int scale)
    {
        return (size + scale - 1) / scale;
    }

    /// <summary>Decodes the image data from the first scan, whose header has just been read, to the end, filling the raster.</summary>
    internal void Decode(EgamiInput input)
    {
        using JpegBitReader reader = new(input);
        JpegScan scan = _header.FirstScan!;
        if (!_header.Progressive && scan.Components.Length == _components.Length)
        {
            DecodeStreaming(input, reader, scan);
            return;
        }

        foreach (Component c in _components)
        {
            c.AllocateCoefficients(input.Memory, _mcusX * c.H, _mcusY * c.V);
        }

        int scans = 0;
        while (true)
        {
            if (++scans > MaxScans)
            {
                throw new CorruptDataException(JpegFormat.Info.Name, -1, string.Create(CultureInfo.InvariantCulture, $"The file has more than {MaxScans:N0} scans."));
            }

            input.CancellationToken.ThrowIfCancellationRequested();

            // At 1/8 scale a block is its DC term alone, so a progressive AC scan of such a component is skipped unread.
            bool skip = _header.Progressive && scan.SpectralStart > 0 && _components[scan.Components[0].ComponentIndex].BlockSize == 1;
            if (skip)
            {
                reader.BeginScan();
            }

            if ((!skip && !DecodeBufferedScan(reader, scan)) || NextScan(input, reader) is not JpegScan next)
            {
                break;
            }

            scan = next;
        }

        for (int i = 0; i < _components.Length; i++)
        {
            // A component no scan reached is all zeros, mid-gray, and still needs a table.
            LatchQuant(i);
        }

        Span<short> natural = stackalloc short[64];

        for (int my = 0; my < _mcusY; my++)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            foreach (Component c in _components)
            {
                Span<byte> strip = c.Strip(my);
                for (int v = 0; v < c.V; v++)
                {
                    int by = (my * c.V) + v;
                    Span<short> row = c.BlockRow(by);
                    for (int bx = 0; bx < _mcusX * c.H; bx++)
                    {
                        Span<short> block = row.Slice(bx * 64, 64);
                        Span<byte> target = strip[(((v * c.StripWidth) + bx) * c.BlockSize)..];
                        ulong nonzero = JpegBlock.NonZero(block);
                        if (c.BlockSize > 1 && (nonzero & ~1UL) == 0)
                        {
                            JpegIdct.FillDc(block[0] * c.Quant![0], target, c.StripWidth, c.BlockSize);
                            continue;
                        }

                        // Back to natural order, visiting only the nonzero coefficients.
                        natural.Clear();
                        for (; nonzero != 0; nonzero &= nonzero - 1)
                        {
                            int z = BitOperations.TrailingZeroCount(nonzero);
                            natural[JpegTables.ZigZag[z]] = block[z];
                        }

                        c.Idct(natural, target);
                    }
                }
            }

            if (my > 0)
            {
                EmitRows(my - 1);
            }
        }

        EmitRows(_mcusY - 1);
    }

    public void Dispose()
    {
        foreach (Component c in _components)
        {
            c.Dispose();
        }

        foreach ((_, JpegHuffmanTable table) in _tables)
        {
            table.Dispose();
        }

        _tables.Clear();

        ArrayPool<byte>.Shared.Return(Interlocked.Exchange(ref _rowBuffer, null!));
        ArrayPool<byte>.Shared.Return(Interlocked.Exchange(ref _sampleBuffer, null!));
        ArrayPool<short>.Shared.Return(Interlocked.Exchange(ref _sums, null!));
    }

    /// <summary>Converts a quantization table from zigzag to natural order.</summary>
    private static ushort[] Natural(ushort[] zigzag)
    {
        ushort[] natural = new ushort[64];
        for (int k = 0; k < 64; k++)
        {
            natural[JpegTables.ZigZag[k]] = zigzag[k];
        }

        return natural;
    }

    private static CorruptDataException Missing(string kind, int slot)
    {
        return new CorruptDataException(JpegFormat.Info.Name, -1, $"The scan uses {kind} Huffman table {slot}, which is missing.");
    }

    /// <summary>Decodes one block's coefficients into natural order (T.81 F.2.2).</summary>
    /// <returns>Whether any AC coefficient is nonzero; a block without one is flat.</returns>
    private static bool DecodeBlock(JpegBitReader reader, JpegHuffmanTable dc, JpegHuffmanTable ac, ref int predictor, Span<short> block)
    {
        block.Clear();
        bool any = false;

        // The bit buffer lives in locals for the block. Each symbol needs at most 16 bits of code and 15 of value, so one refill
        // to more than 32 bits covers both.
        reader.Load(out ulong bits, out int count);
        if (count < 32)
        {
            reader.Refill(ref bits, ref count);
        }

        int s = dc.Decode(ref bits, ref count) & 15;
        predictor += Receive(ref bits, ref count, s);
        block[0] = (short)predictor;
        ref byte zigzag = ref MemoryMarshal.GetReference(JpegTables.ZigZag);
        ref int fast = ref MemoryMarshal.GetArrayDataReference(ac.FastAc);
        for (int k = 1; k < 64; k++)
        {
            if (count < 32)
            {
                reader.Refill(ref bits, ref count);
            }

            int entry = Unsafe.Add(ref fast, (nint)(bits >> 55));
            if (entry != 0)
            {
                int used = entry & 0xFF;
                bits <<= used;
                count -= used;
                k += (entry >> 8) & 15;
                if (k < 64)
                {
                    block[Unsafe.Add(ref zigzag, k)] = (short)(entry >> 16);
                    any = true;
                }

                continue;
            }

            int rs = ac.Decode(ref bits, ref count);
            int r = rs >> 4;
            s = rs & 15;
            if (s == 0)
            {
                if (r != 15)
                {
                    break;
                }

                k += 15;
                continue;
            }

            k += r;
            int value = Receive(ref bits, ref count, s);
            if (k < 64)
            {
                block[Unsafe.Add(ref zigzag, k)] = (short)value;
                any = true;
            }
        }

        reader.Store(bits, count);
        return any;
    }

    /// <summary>Takes <paramref name="size"/> bits (0 to 15) and extends them to a signed value (T.81 figure F.12).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Receive(ref ulong bits, ref int count, int size)
    {
        if (size == 0)
        {
            return 0;
        }

        int value = (int)(bits >> (64 - size));
        bits <<= size;
        count -= size;
        return value - ((((value >> (size - 1)) & 1) - 1) & ((1 << size) - 1));
    }

    /// <summary>The first DC scan of a progressive file (T.81 G.1.2.1): the DC difference, scaled up by the point transform.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeDcFirst(JpegBitReader reader, ref ulong bits, ref int count, JpegHuffmanTable dc, ref int predictor, Span<short> block, int al)
    {
        if (count < 32)
        {
            reader.Refill(ref bits, ref count);
        }

        int s = dc.Decode(ref bits, ref count) & 15;
        predictor += Receive(ref bits, ref count, s);
        block[0] = (short)(predictor << al);
    }

    /// <summary>A DC refinement scan: one more bit of the DC coefficient.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeDcRefine(JpegBitReader reader, ref ulong bits, ref int count, Span<short> block, int al)
    {
        if (TakeBit(reader, ref bits, ref count))
        {
            block[0] |= (short)(1 << al);
        }
    }

    /// <summary>The first scan of an AC band (T.81 G.1.2.2), with end-of-band runs that span blocks.</summary>
    private static void DecodeAcFirst(JpegBitReader reader, ref ulong bits, ref int count, JpegHuffmanTable ac, ref int eobRun, Span<short> block, int ss, int se, int al)
    {
        if (eobRun > 0)
        {
            eobRun--;
            return;
        }

        ref int fast = ref MemoryMarshal.GetArrayDataReference(ac.FastAc);
        for (int k = ss; k <= se; k++)
        {
            if (count < 32)
            {
                reader.Refill(ref bits, ref count);
            }

            // Most coefficients: the code and value together in the one-step lookup.
            int entry = Unsafe.Add(ref fast, (nint)(bits >> 55));
            if (entry != 0)
            {
                int used = entry & 0xFF;
                bits <<= used;
                count -= used;
                k += (entry >> 8) & 15;
                if (k <= se)
                {
                    block[k] = (short)((entry >> 16) << al);
                }

                continue;
            }

            int rs = ac.Decode(ref bits, ref count);
            int r = rs >> 4;
            int s = rs & 15;
            if (s != 0)
            {
                k += r;
                int value = Receive(ref bits, ref count, s);
                if (k <= se)
                {
                    block[k] = (short)(value << al);
                }
            }
            else if (r == 15)
            {
                k += 15;
            }
            else
            {
                // EOBn: this block and 2^r + extra bits - 1 more end here.
                eobRun = (1 << r) - 1 + Take(ref bits, ref count, r);
                break;
            }
        }
    }

    /// <summary>
    /// An AC refinement scan (T.81 G.1.2.3): one correction bit for each coefficient already nonzero, and new coefficients of
    /// magnitude one placed by runs that count only the coefficients still zero. The block is in zigzag order, and a bit mask of
    /// its nonzero coefficients finds the next zero, and the nonzero ones passed on the way, without visiting each position.
    /// </summary>
    private static void DecodeAcRefine(JpegBitReader reader, ref ulong bits, ref int count, JpegHuffmanTable ac, ref int eobRun, Span<short> block, int ss, int se, int al)
    {
        int p1 = 1 << al;
        int m1 = -1 << al;
        ulong nonzero = JpegBlock.NonZero(block);
        int k = ss;
        if (eobRun == 0)
        {
            while (k <= se)
            {
                if (count < 32)
                {
                    reader.Refill(ref bits, ref count);
                }

                int rs = ac.Decode(ref bits, ref count);
                int r = rs >> 4;
                int s = rs & 15;
                int value = 0;
                if (s != 0)
                {
                    // The size is always 1 here; the next bit is the sign.
                    value = Take(ref bits, ref count, 1) != 0 ? p1 : m1;
                }
                else if (r != 15)
                {
                    eobRun = (1 << r) + Take(ref bits, ref count, r);
                    break;
                }

                // The run ends at the (r + 1)th zero coefficient from k (for ZRL, the 16th), or past the band if there are fewer.
                ulong zeros = ~nonzero & Range(k, se);
                for (int z = 0; z < r && zeros != 0; z++)
                {
                    zeros &= zeros - 1;
                }

                int end = zeros == 0 ? se + 1 : BitOperations.TrailingZeroCount(zeros);
                RefineAll(reader, ref bits, ref count, block, nonzero & Range(k, end - 1), p1, m1);
                k = end;
                if (value != 0 && k <= 63)
                {
                    block[k] = (short)value;
                    nonzero |= 1UL << k;
                }

                k++;
            }
        }

        if (eobRun > 0)
        {
            // The band ends in this block: only the corrections of the nonzero coefficients remain.
            if (k <= se)
            {
                RefineAll(reader, ref bits, ref count, block, nonzero & Range(k, se), p1, m1);
            }

            eobRun--;
        }
    }

    /// <summary>Applies a correction bit to each coefficient in <paramref name="positions"/>, in order, taking up to 32 bits at once.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RefineAll(JpegBitReader reader, ref ulong bits, ref int count, Span<short> block, ulong positions, int p1, int m1)
    {
        while (positions != 0)
        {
            if (count < 32)
            {
                reader.Refill(ref bits, ref count);
            }

            int n = Math.Min(BitOperations.PopCount(positions), 32);
            ulong taken = bits;
            bits <<= n;
            count -= n;
            for (int t = 0; t < n; t++)
            {
                ref short coefficient = ref block[BitOperations.TrailingZeroCount(positions)];
                if ((long)taken < 0 && (coefficient & p1) == 0)
                {
                    coefficient = (short)(coefficient + (coefficient >= 0 ? p1 : m1));
                }

                taken <<= 1;
                positions &= positions - 1;
            }
        }
    }

    /// <summary>Returns the bits <paramref name="first"/> to <paramref name="last"/> inclusive, or none if <paramref name="last"/> is smaller.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Range(int first, int last)
    {
        if (last < first)
        {
            return 0;
        }

        ulong upTo = last >= 63 ? ulong.MaxValue : (1UL << (last + 1)) - 1;
        return upTo & (ulong.MaxValue << first);
    }

    /// <summary>Takes one bit, refilling first if the buffer is empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TakeBit(JpegBitReader reader, ref ulong bits, ref int count)
    {
        if (count < 1)
        {
            reader.Refill(ref bits, ref count);
        }

        bool bit = (long)bits < 0;
        bits <<= 1;
        count--;
        return bit;
    }

    /// <summary>Takes <paramref name="n"/> bits (0 to 16) as an unsigned number; the buffer must hold them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Take(ref ulong bits, ref int count, int n)
    {
        if (n == 0)
        {
            return 0;
        }

        int value = (int)(bits >> (64 - n));
        bits <<= n;
        count -= n;
        return value;
    }

    /// <summary>
    /// Decodes a sequential file whose first scan holds every component, one MCU row at a time: the entropy decoder fills a slot
    /// of coefficients for a row, then the row is transformed into the strips and the rows above it are written out. With
    /// <see cref="EgamiContext.MaxParallelism"/> above 1 and a large image, the two halves run on two threads with a few slots
    /// between them, so the IDCT, upsampling and color conversion overlap the entropy decoding.
    /// </summary>
    private void DecodeStreaming(EgamiInput input, JpegBitReader reader, JpegScan scan)
    {
        int count = scan.Components.Length;
        JpegHuffmanTable[] dc = new JpegHuffmanTable[count];
        JpegHuffmanTable[] ac = new JpegHuffmanTable[count];
        Component[] order = new Component[count];
        int[] firstBlock = new int[count];
        int blocks = 0;
        for (int i = 0; i < count; i++)
        {
            JpegScanComponent sc = scan.Components[i];
            order[i] = _components[sc.ComponentIndex];
            LatchQuant(sc.ComponentIndex);
            dc[i] = Table(_header.DcTables[sc.DcTable] ?? throw Missing("DC", sc.DcTable));
            ac[i] = Table(_header.AcTables[sc.AcTable] ?? throw Missing("AC", sc.AcTable));
            firstBlock[i] = blocks;
            blocks += _mcusX * order[i].H * order[i].V;
        }

        reader.BeginScan();
        EntropyState state = new(count);
        // A scaled decode leaves too little work after the entropy decoder for a second thread to pay off.
        bool parallel = input.Context.MaxParallelism > 1 && _mcusY >= 4 && (ForcePipeline || (_blockSize == 8 && (long)_header.Width * _header.Height >= ParallelPixels));
        int slots = parallel ? PipelineSlots : 1;
        short[] coefficients = ArrayPool<short>.Shared.Rent(slots * blocks * 64);
        byte[] hasAc = ArrayPool<byte>.Shared.Rent(slots * blocks);
        try
        {
            RowSlot Slot(int my)
            {
                int slot = my % slots;
                return new RowSlot(coefficients, slot * blocks * 64, hasAc, slot * blocks, firstBlock);
            }

            if (!parallel)
            {
                for (int my = 0; my < _mcusY; my++)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    DecodeRow(reader, dc, ac, order, state, Slot(my));
                    TransformRow(my, order, Slot(my));
                    if (my > 0)
                    {
                        EmitRows(my - 1);
                    }
                }

                EmitRows(_mcusY - 1);
                return;
            }

            using SemaphoreSlim free = new(slots);
            using SemaphoreSlim filled = new(0);
            bool stop = false;
            Task consumer = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        for (int my = 0; my < _mcusY; my++)
                        {
                            filled.Wait();
                            if (Volatile.Read(ref stop))
                            {
                                return;
                            }

                            TransformRow(my, order, Slot(my));
                            if (my > 0)
                            {
                                EmitRows(my - 1);
                            }

                            free.Release();
                        }

                        EmitRows(_mcusY - 1);
                    }
                    catch
                    {
                        // Let the decoding side see the failure instead of waiting for a free slot forever.
                        Volatile.Write(ref stop, true);
                        free.Release();
                        throw;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            try
            {
                for (int my = 0; my < _mcusY; my++)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    free.Wait();
                    if (Volatile.Read(ref stop))
                    {
                        break;
                    }

                    DecodeRow(reader, dc, ac, order, state, Slot(my));
                    filled.Release();
                }
            }
            catch
            {
                Volatile.Write(ref stop, true);
                filled.Release();
                try
                {
                    consumer.Wait();
                }
                catch (AggregateException)
                {
                    // The decoding side's exception is the one to report.
                }

                throw;
            }

            consumer.GetAwaiter().GetResult();
        }
        finally
        {
            ArrayPool<short>.Shared.Return(coefficients);
            ArrayPool<byte>.Shared.Return(hasAc);
        }
    }

    /// <summary>Entropy-decodes MCU row the next of the scan into <paramref name="slot"/>.</summary>
    private void DecodeRow(JpegBitReader reader, JpegHuffmanTable[] dc, JpegHuffmanTable[] ac, Component[] order, EntropyState state, RowSlot slot)
    {
        int restart = _header.RestartInterval;
        for (int mx = 0; mx < _mcusX; mx++, state.Mcu++)
        {
            if (restart > 0 && state.Mcu > 0 && state.Mcu % restart == 0 && !state.Ended)
            {
                int next = state.NextRestart;
                state.Ended = !RestartInterval(reader, ref next);
                state.NextRestart = next;
                Array.Clear(state.Predictors);
            }

            for (int i = 0; i < order.Length; i++)
            {
                Component c = order[i];
                int across = _mcusX * c.H;
                for (int v = 0; v < c.V; v++)
                {
                    for (int h = 0; h < c.H; h++)
                    {
                        int b = slot.FirstBlock[i] + (v * across) + (mx * c.H) + h;
                        Span<short> block = slot.Block(b);
                        if (state.Ended)
                        {
                            // After the data ends, the rest of the image is mid-gray: every coefficient 0.
                            block.Clear();
                            slot.HasAc[slot.FlagStart + b] = 0;
                        }
                        else
                        {
                            slot.HasAc[slot.FlagStart + b] = DecodeBlock(reader, dc[i], ac[i], ref state.Predictors[i], block) ? (byte)1 : (byte)0;
                        }
                    }
                }
            }

            state.Ended = state.Ended || EndedEarly(reader, "The image data ends early. The rest of the image is gray.");
        }
    }

    /// <summary>Dequantizes and transforms the blocks of MCU row <paramref name="my"/> into the strips.</summary>
    private void TransformRow(int my, Component[] order, RowSlot slot)
    {
        for (int i = 0; i < order.Length; i++)
        {
            Component c = order[i];
            Span<byte> strip = c.Strip(my);
            int across = _mcusX * c.H;
            for (int v = 0; v < c.V; v++)
            {
                for (int bx = 0; bx < across; bx++)
                {
                    int b = slot.FirstBlock[i] + (v * across) + bx;
                    Span<byte> target = strip[(((v * c.StripWidth) + bx) * c.BlockSize)..];
                    Span<short> block = slot.Block(b);
                    if (slot.HasAc[slot.FlagStart + b] == 0 && c.BlockSize > 1)
                    {
                        JpegIdct.FillDc(block[0] * c.Quant![0], target, c.StripWidth, c.BlockSize);
                    }
                    else
                    {
                        c.Idct(block, target);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Decodes one scan into the coefficient buffer. Returns false if the data ended early, so no scan can follow; the coefficients
    /// so far are kept, as libjpeg does.
    /// </summary>
    private bool DecodeBufferedScan(JpegBitReader reader, JpegScan scan)
    {
        int count = scan.Components.Length;
        int ss = scan.SpectralStart;
        int se = scan.SpectralEnd;
        int ah = scan.ApproxHigh;
        int al = scan.ApproxLow;
        bool progressive = _header.Progressive;
        if (progressive && ((ss == 0 && se != 0) || (ss > 0 && count != 1) || (ah != 0 && al != ah - 1)))
        {
            throw new CorruptDataException(JpegFormat.Info.Name, -1, $"A progressive scan has an invalid band ({ss} to {se}) or bit position ({ah}, {al}).");
        }

        // Which kind of scan: 0 sequential, 1 DC first, 2 DC refine, 3 AC first, 4 AC refine.
        int kind = !progressive ? 0 : ss == 0 ? (ah == 0 ? 1 : 2) : (ah == 0 ? 3 : 4);
        JpegHuffmanTable?[] dc = new JpegHuffmanTable?[count];
        JpegHuffmanTable?[] ac = new JpegHuffmanTable?[count];
        Component[] order = new Component[count];
        for (int i = 0; i < count; i++)
        {
            JpegScanComponent sc = scan.Components[i];
            order[i] = _components[sc.ComponentIndex];
            LatchQuant(sc.ComponentIndex);
            if (kind is 0 or 1)
            {
                dc[i] = Table(_header.DcTables[sc.DcTable] ?? throw Missing("DC", sc.DcTable));
            }

            if (kind is 0 or 3 or 4)
            {
                ac[i] = Table(_header.AcTables[sc.AcTable] ?? throw Missing("AC", sc.AcTable));
            }
        }

        reader.BeginScan();
        Span<int> predictors = stackalloc int[count];
        Span<short> natural = stackalloc short[64];
        int eobRun = 0;
        int restart = _header.RestartInterval;
        int nextRestart = 0;
        int mcu = 0;

        // One component is coded non-interleaved: an MCU is one block, over the blocks the component really covers. Several are
        // interleaved in whole MCUs, padding blocks included.
        bool single = count == 1;
        int unitsX = single ? NonInterleavedBlocks(_header.Width, order[0].H, _maxH) : _mcusX;
        int unitsY = single ? NonInterleavedBlocks(_header.Height, order[0].V, _maxV) : _mcusY;
        const string Ended = "The image data ends early. The scans so far were decoded; the rest of the image has less detail or is gray.";
        reader.Load(out ulong bits, out int available);
        for (int uy = 0; uy < unitsY; uy++)
        {
            // A non-interleaved scan walks one block row of its component.
            Span<short> row = single ? order[0].BlockRow(uy) : default;
            for (int ux = 0; ux < unitsX; ux++, mcu++)
            {
                if (restart > 0 && mcu > 0 && mcu % restart == 0)
                {
                    reader.Store(bits, available);
                    if (!RestartInterval(reader, ref nextRestart))
                    {
                        return false;
                    }

                    reader.Load(out bits, out available);
                    predictors.Clear();
                    eobRun = 0;
                }

                for (int i = 0; i < count; i++)
                {
                    Component c = order[i];
                    int blocksX = single ? 1 : c.H;
                    int blocksY = single ? 1 : c.V;
                    for (int v = 0; v < blocksY; v++)
                    {
                        for (int h = 0; h < blocksX; h++)
                        {
                            Span<short> block = single ? row.Slice(ux * 64, 64) : c.Block((ux * c.H) + h, (uy * c.V) + v);
                            switch (kind)
                            {
                                case 0:
                                    reader.Store(bits, available);
                                    _ = DecodeBlock(reader, dc[i]!, ac[i]!, ref predictors[i], natural);
                                    reader.Load(out bits, out available);
                                    for (int z = 0; z < 64; z++)
                                    {
                                        block[z] = natural[JpegTables.ZigZag[z]];
                                    }

                                    break;
                                case 1:
                                    DecodeDcFirst(reader, ref bits, ref available, dc[i]!, ref predictors[i], block, al);
                                    break;
                                case 2:
                                    DecodeDcRefine(reader, ref bits, ref available, block, al);
                                    break;
                                case 3:
                                    DecodeAcFirst(reader, ref bits, ref available, ac[i]!, ref eobRun, block, ss, se, al);
                                    break;
                                default:
                                    DecodeAcRefine(reader, ref bits, ref available, ac[i]!, ref eobRun, block, ss, se, al);
                                    break;
                            }
                        }
                    }
                }

                if (reader.PastEndWith(available) && !reader.AtRestartMarker)
                {
                    reader.Store(bits, available);
                    Problem(Ended);
                    return false;
                }
            }
        }

        reader.Store(bits, available);
        return true;
    }

    /// <summary>
    /// Reads the segments after a scan up to the next scan header and returns it, or null at the end of the image. Tables and the
    /// restart interval may change between scans; anything else is skipped.
    /// </summary>
    private JpegScan? NextScan(EgamiInput input, JpegBitReader reader)
    {
        int marker = reader.EndScan();
        while (true)
        {
            if (marker == JpegBitReader.EndOfFile)
            {
                Problem("The file ends before its end-of-image marker; the scans so far were decoded.");
                return null;
            }

            if (marker == JpegMarker.Eoi)
            {
                return null;
            }

            if (!JpegMarker.IsStandalone((byte)marker))
            {
                int high = reader.ReadSegmentByte();
                int low = reader.ReadSegmentByte();
                int length = (high << 8) + low - 2;
                if (low < 0 || length < 0)
                {
                    Problem("A segment between scans is cut off or has an invalid length.");
                    return null;
                }

                byte[] payload = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    int b = reader.ReadSegmentByte();
                    if (b < 0)
                    {
                        Problem("A segment between scans is cut off.");
                        return null;
                    }

                    payload[i] = (byte)b;
                }

                switch (marker)
                {
                    case JpegMarker.Sos:
                        return JpegSegmentReader.ParseScan(input, _header, payload, -1);
                    case JpegMarker.Dht:
                        JpegSegmentReader.ReadHuffmanTables(input, _header, payload, -1);
                        break;
                    case JpegMarker.Dqt:
                        JpegSegmentReader.ReadQuantTables(input, _header, payload, -1);
                        break;
                    case JpegMarker.Dri:
                        _header.RestartInterval = payload.Length >= 2 ? (payload[0] << 8) | payload[1] : throw input.Corrupt(-1, "The restart interval segment is too short.");
                        break;
                    case JpegMarker.Dnl:
                        break;
                    case >= JpegMarker.Sof0 and <= JpegMarker.Sof15 when marker is not (JpegMarker.Dht or JpegMarker.Dac or 0xC8):
                        throw input.Corrupt(-1, "The file has more than one frame header (SOF).");
                }
            }

            marker = reader.NextMarker(out bool junk);
            if (junk)
            {
                Problem("There are stray bytes between segments after a scan; they were skipped.");
            }
        }
    }

    /// <summary>Returns the blocks a non-interleaved scan codes across a dimension: those the full-size component covers.</summary>
    private static int NonInterleavedBlocks(int size, int factor, int max)
    {
        long samples = (((long)size * factor) + max - 1) / max;
        return (int)((samples + 7) / 8);
    }

    /// <summary>Returns the decoding table for a table definition, built once however many scans use it.</summary>
    private JpegHuffmanTable Table(JpegHuffmanSpec spec)
    {
        foreach ((JpegHuffmanSpec known, JpegHuffmanTable table) in _tables)
        {
            if (ReferenceEquals(known, spec))
            {
                return table;
            }
        }

        JpegHuffmanTable built = new(spec);
        _tables.Add((spec, built));
        return built;
    }

    /// <summary>Uses the quantization table a component has when it first appears in a scan, as libjpeg does.</summary>
    private void LatchQuant(int index)
    {
        Component c = _components[index];
        if (c.Quant is null)
        {
            int slot = _header.Components[index].QuantTable;
            ushort[] quant = _header.QuantTables[slot]
                ?? throw new CorruptDataException(JpegFormat.Info.Name, -1, $"Component {_header.Components[index].Id} uses quantization table {slot}, which is missing.");
            c.Quant = Natural(quant);
        }
    }

    /// <summary>Handles a restart interval boundary. Returns false if the data ended instead.</summary>
    private bool RestartInterval(JpegBitReader reader, ref int nextRestart)
    {
        bool ended = false;
        if (!reader.Restart(nextRestart))
        {
            Problem("A restart marker is missing or out of order; the data was resynchronized at the next one.");
            // An out-of-order restart marker was consumed and decoding resumes; any other marker ends the data.
            ended = reader.Marker != 0;
        }

        nextRestart = reader.LastRestart >= 0 ? reader.LastRestart + 1 : nextRestart + 1;
        return !ended;
    }

    /// <summary>
    /// Returns whether the data ran out within the MCU just decoded. Running into a restart marker early only loses the rest of
    /// its interval; any other marker or the end of the file ends the data.
    /// </summary>
    private bool EndedEarly(JpegBitReader reader, string message)
    {
        if (reader.PastEnd && !reader.AtRestartMarker)
        {
            Problem(message);
            return true;
        }

        return false;
    }

    private void Problem(string message)
    {
        if (_options.Recovery == RecoveryMode.Strict)
        {
            throw new CorruptDataException(JpegFormat.Info.Name, -1, message);
        }

        if (!_warned)
        {
            _raster.Metadata.WarningList.Add(message);
            _warned = true;
        }
    }

    /// <summary>Upsamples, converts and writes the output rows of MCU row <paramref name="my"/>, using its neighbors for context.</summary>
    private void EmitRows(int my)
    {
        int first = my * _mcuRows;
        int last = Math.Min(_height, first + _mcuRows);
        int count = _components.Length;
        for (int y = first; y < last; y++)
        {
            ReadOnlySpan<byte> a = SampleRow(0, y);
            ReadOnlySpan<byte> b = count > 1 ? SampleRow(1, y) : default;
            ReadOnlySpan<byte> c = count > 2 ? SampleRow(2, y) : default;
            ReadOnlySpan<byte> k = count > 3 ? SampleRow(3, y) : default;
            WriteRow(y, a, b, c, k);
        }
    }

    /// <summary>
    /// Returns one full-width row of component <paramref name="index"/> for output row <paramref name="y"/>: the strip row
    /// itself when the component is not subsampled, else the row upsampled into a buffer.
    /// </summary>
    private ReadOnlySpan<byte> SampleRow(int index, int y)
    {
        Component c = _components[index];
        int hr = c.HorizontalRatio;
        int vr = c.VerticalRatio;
        int width = _width;
        int row = y / vr;
        ReadOnlySpan<byte> cur = c.Row(row);
        if (hr == 1 && vr == 1)
        {
            return cur[..width];
        }

        Span<byte> output = _sampleBuffer.AsSpan(index * width, width);

        // libjpeg also replicates at 1/8 scale, where a block is one sample and there is nothing to interpolate within it.
        if (_fast || _blockSize == 1 || (hr != 1 && hr != 2) || (vr != 1 && vr != 2) || !c.WholeRatios)
        {
            JpegUpsample.Replicate(cur, hr, output);
            return output;
        }

        int n = c.Width;
        Span<short> sums = _sums.AsSpan(0, n + 2);
        if (vr == 2)
        {
            // Triangle filter vertically: 3/4 of the nearer row, 1/4 of the next one out (replicated past the edges).
            bool upper = (y & 1) == 0;
            ReadOnlySpan<byte> near = c.Row(Math.Clamp(upper ? row - 1 : row + 1, 0, c.Height - 1));
            if (hr == 1)
            {
                JpegUpsample.Vertical(cur[..width], near[..width], output, upper ? 1 : 2);
                return output;
            }

            // Both directions at once, on column sums, as libjpeg's h2v2 filter does.
            JpegUpsample.ColumnSums(cur, near, n, sums);
            JpegUpsample.Double(sums, n, output, 8, 7, 4);
            return output;
        }

        // Horizontal only (4:2:2): 3/4 of the sample and 1/4 of its neighbor on the side of the output pixel.
        JpegUpsample.Widen(cur, n, sums);
        JpegUpsample.Double(sums, n, output, 1, 2, 2);
        return output;
    }

    /// <summary>Converts one row of component samples to RGB or gray and stores it in the raster.</summary>
    private void WriteRow(int y, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, ReadOnlySpan<byte> k)
    {
        Span<TPixel> target = _raster.Row(y);
        int width = _width;
        if (_channels == 1)
        {
            if (typeof(TPixel) == typeof(Gray8))
            {
                a.CopyTo(MemoryMarshal.AsBytes(target));
            }
            else
            {
                PixelConvert.Convert(MemoryMarshal.Cast<byte, Gray8>(a), target);
            }

            return;
        }

        if (_header.Details.ColorSpace is JpegColorSpace.Cmyk or JpegColorSpace.Ycck && CmykInk.Is<TPixel>())
        {
            Span<byte> inks = typeof(TPixel) == typeof(Cmyk8) ? MemoryMarshal.AsBytes(target) : _rowBuffer.AsSpan(0, width * 4);
            JpegColor.ToInk(_header.Details.ColorSpace, a, b, c, k, inks);
            if (typeof(TPixel) != typeof(Cmyk8))
            {
                PixelConvert.Convert(MemoryMarshal.Cast<byte, Cmyk8>(inks), target);
            }

            return;
        }

        Span<byte> rgb = typeof(TPixel) == typeof(Rgb8) ? MemoryMarshal.AsBytes(target) : _rowBuffer.AsSpan(0, width * 3);
        JpegColor.ToRgb(_header.Details.ColorSpace, a, b, c, k, rgb);
        if (typeof(TPixel) != typeof(Rgb8))
        {
            PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgb8>(rgb), target);
        }
    }

    /// <summary>Where the entropy decoder is in the scan, carried from one MCU row to the next.</summary>
    private sealed class EntropyState(int components)
    {
        internal int[] Predictors { get; } = new int[components];

        internal int NextRestart { get; set; }

        internal int Mcu { get; set; }

        internal bool Ended { get; set; }
    }

    /// <summary>One MCU row of coefficients in the pipeline, with a flag per block saying whether it has AC terms.</summary>
    private readonly struct RowSlot(short[] coefficients, int start, byte[] hasAc, int flagStart, int[] firstBlock)
    {
        internal byte[] HasAc => hasAc;

        internal int FlagStart => flagStart;

        /// <summary>Gets the index of each scan component's first block in the row.</summary>
        internal int[] FirstBlock => firstBlock;

        internal Span<short> Block(int index)
        {
            return coefficients.AsSpan(start + (index * 64), 64);
        }
    }

    /// <summary>
    /// One component's sampling, quantization table and the ring of three MCU-row strips of samples, plus, for a buffered decode,
    /// every coefficient in zigzag order, in chunks of whole block rows that each fit one memory block.
    /// </summary>
    private sealed unsafe class Component(int h, int v, int blockSize, int mcuWidth, int mcuHeight, int stripWidth, int width, int height) : IDisposable
    {
        private readonly int _stripHeight = v * blockSize;
        private byte[] _strips = ArrayPool<byte>.Shared.Rent(3 * stripWidth * v * blockSize);
        private IMemoryOwner<byte>?[] _chunks = [];
        private MemoryHandle[] _pins = [];
        private int _blocksX;
        private int _rowsPerChunk = 1;

        internal int H => h;

        internal int V => v;

        /// <summary>Gets or sets the quantization table in natural order, fixed when the component first appears in a scan.</summary>
        internal ushort[]? Quant { get; set; }

        internal int StripWidth => stripWidth;

        /// <summary>Gets the side of a block in samples after the IDCT: 8 at full scale, less in a scaled decode.</summary>
        internal int BlockSize => blockSize;

        /// <summary>Gets how many output pixels across one sample covers.</summary>
        internal int HorizontalRatio => mcuWidth / (h * blockSize);

        /// <summary>Gets how many output rows one sample row covers.</summary>
        internal int VerticalRatio => mcuHeight / (v * blockSize);

        /// <summary>Gets whether both ratios are whole numbers; odd sampling factors such as 3 can leave a remainder.</summary>
        internal bool WholeRatios => mcuWidth % (h * blockSize) == 0 && mcuHeight % (v * blockSize) == 0;

        /// <summary>Gets the component's real width in samples, before MCU padding.</summary>
        internal int Width => width;

        /// <summary>Gets the component's real height in samples, before MCU padding.</summary>
        internal int Height => height;

        /// <summary>Gets the strip that holds MCU row <paramref name="my"/>.</summary>
        internal Span<byte> Strip(int my)
        {
            return _strips.AsSpan(my % 3 * stripWidth * _stripHeight, stripWidth * _stripHeight);
        }

        /// <summary>Gets sample row <paramref name="row"/>, which must be in one of the three strips held.</summary>
        internal ReadOnlySpan<byte> Row(int row)
        {
            return Strip(row / _stripHeight).Slice(row % _stripHeight * stripWidth, stripWidth);
        }

        /// <summary>Dequantizes and transforms one block into <paramref name="target"/>, at this component's block size.</summary>
        internal void Idct(ReadOnlySpan<short> block, Span<byte> target)
        {
            switch (blockSize)
            {
                case 8:
                    JpegIdct.Transform(block, Quant, target, stripWidth);
                    break;
                case 4:
                    JpegIdct.Transform4(block, Quant, target, stripWidth);
                    break;
                case 2:
                    JpegIdct.Transform2(block, Quant, target, stripWidth);
                    break;
                default:
                    JpegIdct.Transform1(block, Quant, target, stripWidth);
                    break;
            }
        }

        /// <summary>Allocates zeroed coefficients for <paramref name="blocksX"/> × <paramref name="blocksY"/> blocks, within the load's memory budget.</summary>
        internal void AllocateCoefficients(BudgetedMemorySource memory, int blocksX, int blocksY)
        {
            int rowBytes = blocksX * 64 * sizeof(short);
            memory.Check((long)rowBytes * blocksY);
            _blocksX = blocksX;
            _rowsPerChunk = Math.Clamp(memory.MaxBlockBytes / rowBytes, 1, blocksY);
            _chunks = new IMemoryOwner<byte>?[(blocksY + _rowsPerChunk - 1) / _rowsPerChunk];
            _pins = new MemoryHandle[_chunks.Length];
            for (int i = 0; i < _chunks.Length; i++)
            {
                int rows = Math.Min(_rowsPerChunk, blocksY - (i * _rowsPerChunk));
                _chunks[i] = memory.Allocate(rows * rowBytes, ClearMode.Zero);

                // Pinned for the whole decode, so a block is a pointer away; getting a span from the memory per block was a tenth
                // of a progressive decode.
                _pins[i] = _chunks[i]!.Memory.Pin();
            }
        }

        /// <summary>Gets the coefficients of block row <paramref name="by"/>, 64 per block.</summary>
        internal Span<short> BlockRow(int by)
        {
            return new Span<short>(CoefficientRow(by), _blocksX * 64);
        }

        /// <summary>Gets the 64 coefficients of block (<paramref name="bx"/>, <paramref name="by"/>).</summary>
        internal Span<short> Block(int bx, int by)
        {
            if ((uint)bx >= (uint)_blocksX)
            {
                throw new ArgumentOutOfRangeException(nameof(bx));
            }

            return new Span<short>(CoefficientRow(by) + (bx * 64), 64);
        }

        private short* CoefficientRow(int by)
        {
            // The pins array bounds-checks the row's chunk.
            return (short*)_pins[by / _rowsPerChunk].Pointer + ((long)(by % _rowsPerChunk) * _blocksX * 64);
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Interlocked.Exchange(ref _strips, null!));
            foreach (MemoryHandle pin in _pins)
            {
                pin.Dispose();
            }

            foreach (IMemoryOwner<byte>? chunk in _chunks)
            {
                chunk?.Dispose();
            }

            _pins = [];
            _chunks = [];
        }
    }
}

/// <summary>Color conversion from a JPEG's components to RGB, in the fixed point libjpeg uses (16 fraction bits, rounded).</summary>
internal static class JpegColor
{
    private const int ScaleBits = 16;
    private const int Half = 1 << (ScaleBits - 1);

    private static readonly int CrToRFactor = Factor(1.40200);
    private static readonly int CbToBFactor = Factor(1.77200);
    private static readonly int CrToGFactor = Factor(-0.71414);
    private static readonly int CbToGFactor = Factor(-0.34414);
    private static readonly int[] CrToR = Table(CrToRFactor, Half);
    private static readonly int[] CbToB = Table(CbToBFactor, Half);
    private static readonly int[] CrToG = Table(CrToGFactor, 0, shift: false);
    private static readonly int[] CbToG = Table(CbToGFactor, Half, shift: false);

    /// <summary>
    /// Converts a row of planar component samples to packed RGB, as wide as <paramref name="rgb"/> holds. YCbCr uses the
    /// full-range BT.601 (JFIF) equations; CMYK and YCCK as Adobe stores them (inverted) use R = C × K / 255.
    /// </summary>
    internal static void ToRgb(JpegColorSpace space, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, ReadOnlySpan<byte> k, Span<byte> rgb)
    {
        int width = rgb.Length / 3;
        switch (space)
        {
            case JpegColorSpace.Rgb:
                for (int x = 0; x < width; x++)
                {
                    rgb[3 * x] = a[x];
                    rgb[(3 * x) + 1] = b[x];
                    rgb[(3 * x) + 2] = c[x];
                }

                break;
            case JpegColorSpace.Cmyk or JpegColorSpace.Ycck:
                for (int x = 0; x < width; x++)
                {
                    int c0 = a[x], m0 = b[x], y0 = c[x];
                    if (space == JpegColorSpace.Ycck)
                    {
                        // YCCK holds the inverted CMY as YCbCr: convert, then invert back.
                        int r = Clamp(a[x] + CrToR[c[x]]);
                        int g = Clamp(a[x] + ((CbToG[b[x]] + CrToG[c[x]]) >> ScaleBits));
                        int bl = Clamp(a[x] + CbToB[b[x]]);
                        c0 = 255 - r;
                        m0 = 255 - g;
                        y0 = 255 - bl;
                    }

                    rgb[3 * x] = (byte)(((c0 * k[x]) + 127) / 255);
                    rgb[(3 * x) + 1] = (byte)(((m0 * k[x]) + 127) / 255);
                    rgb[(3 * x) + 2] = (byte)(((y0 * k[x]) + 127) / 255);
                }

                break;
            default:
                int done = Avx2.IsSupported ? YCbCrAvx2(a, b, c, rgb) : 0;
                YCbCrScalar(a, b, c, rgb, done);
                break;
        }
    }

    /// <summary>
    /// Converts a row of CMYK or YCCK samples, stored inverted as Adobe writes them, to ink amounts (0 = no ink), four bytes per
    /// pixel. Converting these inks to RGB with the plain formula gives exactly what <see cref="ToRgb"/> gives.
    /// </summary>
    internal static void ToInk(JpegColorSpace space, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, ReadOnlySpan<byte> k, Span<byte> cmyk)
    {
        int width = cmyk.Length / 4;
        for (int x = 0; x < width; x++)
        {
            int c0 = a[x], m0 = b[x], y0 = c[x];
            if (space == JpegColorSpace.Ycck)
            {
                c0 = 255 - Clamp(a[x] + CrToR[c[x]]);
                m0 = 255 - Clamp(a[x] + ((CbToG[b[x]] + CrToG[c[x]]) >> ScaleBits));
                y0 = 255 - Clamp(a[x] + CbToB[b[x]]);
            }

            cmyk[4 * x] = (byte)(255 - c0);
            cmyk[(4 * x) + 1] = (byte)(255 - m0);
            cmyk[(4 * x) + 2] = (byte)(255 - y0);
            cmyk[(4 * x) + 3] = (byte)(255 - k[x]);
        }
    }

    /// <summary>The scalar YCbCr conversion from pixel <paramref name="start"/> on.</summary>
    internal static void YCbCrScalar(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> blue, ReadOnlySpan<byte> red, Span<byte> rgb, int start)
    {
        int width = rgb.Length / 3;
        for (int x = start; x < width; x++)
        {
            int y = luma[x], cb = blue[x], cr = red[x];
            rgb[3 * x] = Clamp(y + CrToR[cr]);
            rgb[(3 * x) + 1] = Clamp(y + ((CbToG[cb] + CrToG[cr]) >> ScaleBits));
            rgb[(3 * x) + 2] = Clamp(y + CbToB[cb]);
        }
    }

    /// <summary>
    /// Converts 16 pixels at a time with AVX2, computing the table entries in 32-bit lanes instead of looking them up, so the
    /// bytes equal the scalar ones. Returns how many pixels it did.
    /// </summary>
    internal static int YCbCrAvx2(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> blue, ReadOnlySpan<byte> red, Span<byte> rgb)
    {
        int width = rgb.Length / 3;
        ref byte yRef = ref MemoryMarshal.GetReference(luma);
        ref byte cbRef = ref MemoryMarshal.GetReference(blue);
        ref byte crRef = ref MemoryMarshal.GetReference(red);
        ref byte o = ref MemoryMarshal.GetReference(rgb);

        // Within each 128-bit lane: bytes 0-7 red and 8-15 green of eight pixels, and blue in a second vector; the masks
        // interleave them to 24 bytes, 16 from the first shuffle pair and 8 from the second.
        Vector256<byte> rg0 = Lanes(0, 8, 255, 1, 9, 255, 2, 10, 255, 3, 11, 255, 4, 12, 255, 5);
        Vector256<byte> b0 = Lanes(255, 255, 0, 255, 255, 1, 255, 255, 2, 255, 255, 3, 255, 255, 4, 255);
        Vector256<byte> rg1 = Lanes(13, 255, 6, 14, 255, 7, 15, 255, 255, 255, 255, 255, 255, 255, 255, 255);
        Vector256<byte> b1 = Lanes(255, 5, 255, 255, 6, 255, 255, 7, 255, 255, 255, 255, 255, 255, 255, 255);
        int x = 0;
        for (; x + 16 <= width && x + 16 <= luma.Length && x + 16 <= blue.Length && x + 16 <= red.Length; x += 16)
        {
            Vector256<short> r = Channel(ref yRef, ref cbRef, ref crRef, x, 0);
            Vector256<short> g = Channel(ref yRef, ref cbRef, ref crRef, x, 1);
            Vector256<short> bl = Channel(ref yRef, ref cbRef, ref crRef, x, 2);
            Vector256<byte> redGreen = Avx2.PackUnsignedSaturate(r, g);
            Vector256<byte> blues = Avx2.PackUnsignedSaturate(bl, bl);
            Vector256<byte> first = Avx2.Shuffle(redGreen, rg0) | Avx2.Shuffle(blues, b0);
            Vector256<byte> second = Avx2.Shuffle(redGreen, rg1) | Avx2.Shuffle(blues, b1);
            ref byte at = ref Unsafe.Add(ref o, 3 * x);
            first.GetLower().StoreUnsafe(ref at);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, 16), second.AsUInt64().GetElement(0));
            first.GetUpper().StoreUnsafe(ref at, 24);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, 40), second.AsUInt64().GetElement(2));
        }

        return x;
    }

    /// <summary>One output channel (0 red, 1 green, 2 blue) of 16 pixels as shorts in pixel order, before the clamp.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Channel(ref byte luma, ref byte blue, ref byte red, int x, int channel)
    {
        Vector256<int> low = Channel8(ref luma, ref blue, ref red, x, channel);
        Vector256<int> high = Channel8(ref luma, ref blue, ref red, x + 8, channel);

        // The pack interleaves 128-bit lanes; the permute puts the pixels back in order.
        return Avx2.Permute4x64(Avx2.PackSignedSaturate(low, high).AsInt64(), 0b11_01_10_00).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Channel8(ref byte luma, ref byte blue, ref byte red, int x, int channel)
    {
        Vector256<int> y = Avx2.ConvertToVector256Int32(Vector64.LoadUnsafe(ref luma, (nuint)x).ToVector128Unsafe());
        Vector256<int> offset = Vector256.Create(-128);
        Vector256<int> half = Vector256.Create(Half);
        if (channel == 0)
        {
            Vector256<int> cr = Avx2.ConvertToVector256Int32(Vector64.LoadUnsafe(ref red, (nuint)x).ToVector128Unsafe()) + offset;
            return y + (((cr * Vector256.Create(CrToRFactor)) + half) >> ScaleBits);
        }

        if (channel == 2)
        {
            Vector256<int> cb = Avx2.ConvertToVector256Int32(Vector64.LoadUnsafe(ref blue, (nuint)x).ToVector128Unsafe()) + offset;
            return y + (((cb * Vector256.Create(CbToBFactor)) + half) >> ScaleBits);
        }

        Vector256<int> b = Avx2.ConvertToVector256Int32(Vector64.LoadUnsafe(ref blue, (nuint)x).ToVector128Unsafe()) + offset;
        Vector256<int> r = Avx2.ConvertToVector256Int32(Vector64.LoadUnsafe(ref red, (nuint)x).ToVector128Unsafe()) + offset;
        return y + (((b * Vector256.Create(CbToGFactor)) + half + (r * Vector256.Create(CrToGFactor))) >> ScaleBits);
    }

    private static Vector256<byte> Lanes(params ReadOnlySpan<byte> mask)
    {
        Vector128<byte> lane = Vector128.Create(mask);
        return Vector256.Create(lane, lane);
    }

    private static int Factor(double factor)
    {
        return (int)((factor * (1 << ScaleBits)) + (factor < 0 ? -0.5 : 0.5));
    }

    private static int[] Table(int f, int add, bool shift = true)
    {
        int[] table = new int[256];
        for (int i = 0; i < 256; i++)
        {
            int value = (f * (i - 128)) + add;
            table[i] = shift ? value >> ScaleBits : value;
        }

        return table;
    }

    private static byte Clamp(int value)
    {
        return (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
    }
}
